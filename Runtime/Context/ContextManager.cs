/*
┌────────────────────────────┐
│　Description: 对话上下文管理器
│　Remark: 轮数/token 双模式阈值 + 分区
│　　　　　 折叠 + LLM 摘要 + 归档
│　ClassName: ContextManager
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Storage;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Pool;

namespace LLM.Runtime
{
    public class ContextManager
    {
        private readonly CompressionConfig_SO config;
        private readonly List<ConversationRound> rounds = new();
        private readonly ContextCompressor compressor;
        private readonly string sessionId;
        private readonly bool enableDebug;

        private bool historyDirty = true;
        private List<LLMMessage> cachedHistory;
        private float tokPerChar = 0.25f;
        private int consecutiveCompacts;
        private bool compactStuck;
        private readonly SemaphoreSlim _compactLock = new(1, 1);

        private int accumulatedChars;
        private int accumulatedTokens;

        // system 段（人设 + 上下文块 + 每轮注入）的字符数：它不进 rounds，但每轮都真实占着窗口
        private int fixedOverheadChars;

        // 上一次请求里"估算覆盖不到的部分"（工具声明 JSON + 工具返回 + 协议开销），拿真实回包反推
        private int unaccountedTokens;

        public int RoundCount => rounds.Count;

        public bool ArchiveEnabled => config?.EnableArchive ?? true;

        /// <summary>尾部 token 预算，供外部修剪超长工具结果时对齐同一口径</summary>
        public int TailTokenBudget => config?.TailTokenBudget ?? 4096;

        /// <summary>展示用的上下文规模：对话轮 + system 段 + 上次请求反推出的"工具声明与工具返回"残量</summary>
        public int EstimatedTokens => EstimateTotalTokens() + unaccountedTokens;

        /// <summary>上下文窗口上限，0 = 轮数模式（此时没有"占多少比例"这个口径）</summary>
        public int ContextWindowTokens => config?.ContextWindowTokens ?? 0;

        /// <summary>由 LLMSession 每次建请求时回报 system 段长度（压缩阈值与占用读数都靠它才不漏算）</summary>
        public void SetFixedOverheadChars(int chars)
        {
            fixedOverheadChars = chars > 0 ? chars : 0;
        }

        public ContextManager(CompressionConfig_SO config = null, string sessionId = null)
        {
            this.config = config;
            this.sessionId = sessionId;
            compressor = new ContextCompressor(config);
            enableDebug = config?.EnableCompressionDebug ?? false;
        }

        private int MaxRounds => config?.SlidingWindowRounds ?? 6;
        private int CompressThreshold => config?.CompressThreshold ?? 12;
        private bool UseTokenMode => config != null && config.UseTokenMode;

        public void AddRound(string userMessage, string assistantMessage)
        {
            lock (rounds)
            {
                rounds.Add(new ConversationRound
                {
                    UserMessage = userMessage,
                    AssistantMessage = assistantMessage,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
                historyDirty = true;
            }

            MaybeCompact();
        }

        /// <summary>
        /// 用 API 响应的真实 prompt_tokens 校准 tokPerChar。
        /// 在流式收尾 chunk 或 LLMDispatcher.OnRequestCompleted 回调里调用。
        /// </summary>
        public void FeedActualTokenCount(int promptTokens, int charCount)
        {
            if (promptTokens <= 0 || charCount <= 0) return;

            // 真实 prompt 减去"轮次 + system"算得出的部分，剩下的就是工具声明与工具返回占的量；
            // 只喂给展示口径，压缩阈值仍走纯估算（拿上一轮的残量做触发会滞后一轮）
            int residual = promptTokens - EstimateTotalTokens();
            unaccountedTokens = residual > 0 ? residual : 0;

            accumulatedTokens += promptTokens;
            accumulatedChars += charCount;
            if (accumulatedChars <= 500) return;

            float newRatio = (float)accumulatedTokens / accumulatedChars;
            if (newRatio > 0.05f && newRatio < 2f)
                tokPerChar = newRatio;

            accumulatedChars = 0;
            accumulatedTokens = 0;
        }

        /// <summary>导出可直接送 Provider 的历史消息（不含 system）</summary>
        public List<LLMMessage> GetHistoryMessages()
        {
            lock (rounds)
            {
                if (!historyDirty && cachedHistory != null)
                    return new List<LLMMessage>(cachedHistory);
            }

            var activeRounds = GetActiveRounds();
            {
                cachedHistory ??= new List<LLMMessage>();
                cachedHistory.Clear();

                for (int i = 0; i < activeRounds.Count; i++)
                {
                    var round = activeRounds[i];
                    long timestamp = round.Timestamp;

                    if (round.IsSummary)
                    {
                        cachedHistory.Add(new LLMMessage("user",
                            ContextCompressor.WrapSummary(round.UserMessage ?? ""))
                        {
                            Timestamp = timestamp
                        });
                        continue;
                    }

                    if (!string.IsNullOrEmpty(round.UserMessage))
                        cachedHistory.Add(new LLMMessage("user", round.UserMessage) { Timestamp = timestamp });
                    if (!string.IsNullOrEmpty(round.AssistantMessage))
                        cachedHistory.Add(new LLMMessage("assistant", round.AssistantMessage) { Timestamp = timestamp });
                }

                lock (rounds)
                {
                    historyDirty = false;
                }

                return new List<LLMMessage>(cachedHistory);
            }
        }

        private List<ConversationRound> GetActiveRounds()
        {
            return UseTokenMode ? GetActiveRoundsByToken() : GetActiveRoundsByCount();
        }

        private List<ConversationRound> GetActiveRoundsByCount()
        {
            // 不用 ListPool：它线程不安全，而本类的方法会被并发调用（C5 并发用例），
            // 两个线程从池里拿到同一个 List 再双重释放，会把静态池打穿并污染全进程
            var result = new List<ConversationRound>();

            lock (rounds)
            {
                if (rounds.Count <= MaxRounds)
                {
                    result.AddRange(rounds);
                    return result;
                }

                int head = PinnedPrefixLen();
                for (int i = 0; i < head; i++)
                    result.Add(rounds[i]);

                int slots = MaxRounds - head;
                int start = Math.Max(head, rounds.Count - Math.Max(slots, 0));
                for (int i = start; i < rounds.Count; i++)
                    result.Add(rounds[i]);
            }

            return result;
        }

        private List<ConversationRound> GetActiveRoundsByToken()
        {
            var result = new List<ConversationRound>();

            lock (rounds)
            {
                int budget = config.TailTokenBudget;
                int maxByWindow = (int)(config.ContextWindowTokens * config.CompactTargetRatio);
                if (maxByWindow < budget) budget = maxByWindow;

                int head = PinnedPrefixLen();
                int start = TailStartByToken(head, budget);

                for (int i = 0; i < head && i < rounds.Count; i++)
                    result.Add(rounds[i]);
                for (int i = start; i < rounds.Count; i++)
                    result.Add(rounds[i]);
            }

            return result;
        }

        private int TailStartByToken(int head, int budgetTokens)
        {
            int start = rounds.Count;
            int acc = 0;

            // 必须含 head 本身：写 > 会把 rounds[head] 整条排除在尾区之外，而头区只加到 head-1，
            // 于是 head=0（没有摘要可钉）时第一轮对话永远不进 prompt
            for (int i = rounds.Count - 1; i >= head; i--)
            {
                int c = EstimateRoundTokens(rounds[i]);
                if (rounds.Count - i > (config?.MinRecentKeep ?? 2) && acc + c > budgetTokens)
                    break;
                acc += c;
                start = i;
            }

            return start;
        }

        private int EstimateRoundTokens(ConversationRound round)
        {
            int chars = (round.UserMessage?.Length ?? 0) + (round.AssistantMessage?.Length ?? 0);
            return (int)(chars * tokPerChar);
        }

        /// <summary>头部连续的"摘要 + 短提问"区域长度，这部分永远不参与折叠</summary>
        private int PinnedPrefixLen()
        {
            int i = 0;
            for (; i < rounds.Count; i++)
            {
                if (ContextCompressor.IsCompactionSummary(rounds[i])) continue;
                if (ContextCompressor.IsPinnableUserTurn(rounds[i], config?.ContextWindowTokens ?? 0, tokPerChar))
                    i++;
                break;
            }

            while (i < rounds.Count && ContextCompressor.IsCompactionSummary(rounds[i]))
                i++;

            return i;
        }

        #region 压缩

        private void MaybeCompact()
        {
            if (UseTokenMode) MaybeCompactByToken();
            else MaybeCompactByCount();
        }

        private void MaybeCompactByCount()
        {
            if (rounds.Count <= CompressThreshold || compactStuck) return;

            CompactRounds("auto").Forget();
        }

        private void MaybeCompactByToken()
        {
            if (config == null || config.ContextWindowTokens <= 0 || compactStuck) return;

            int totalTokens = EstimateTotalTokens();
            int window = config.ContextWindowTokens;
            int softThreshold = (int)(window * config.SoftCompactRatio);
            int highThreshold = (int)(window * config.CompactRatio);

            if (totalTokens >= softThreshold && totalTokens < highThreshold)
            {
                // 软阈值只做零成本的大内容修剪：够省就不必惊动 LLM 摘要。
                // 计数器复位仍交给下面的统一分支，免得把 consecutiveCompacts 的熔断口径改掉
                PruneStaleLargeContent();

                if (enableDebug)
                    Log.Debug(nameof(ContextManager),
                        ZString.Format("上下文达到 {0:F0}% 窗口，暂不压缩", config.SoftCompactRatio * 100));
            }

            if (totalTokens < highThreshold)
            {
                consecutiveCompacts = 0;
                compactStuck = false;
                return;
            }

            CompactRounds("auto").Forget();
        }

        private int EstimateTotalTokens()
        {
            int total = (int)(fixedOverheadChars * tokPerChar);
            lock (rounds)
            {
                for (int i = 0; i < rounds.Count; i++)
                    total += EstimateRoundTokens(rounds[i]);
            }
            return total;
        }

        private bool FoldEconomics(List<ConversationRound> fold)
        {
            int minFoldTokens = config?.MinFoldTokens ?? 200;
            int total = 0;
            for (int i = 0; i < fold.Count; i++)
                total += EstimateRoundTokens(fold[i]);
            return total >= minFoldTokens;
        }

        private List<ConversationRound> PartitionFold()
        {
            var fold = new List<ConversationRound>();

            lock (rounds)
            {
                int head = PinnedPrefixLen();
                int tailStart = UseTokenMode
                    ? TailStartByToken(head, config.TailTokenBudget)
                    : Math.Max(head, rounds.Count - (config?.MinRecentKeep ?? 2));

                for (int i = head; i < tailStart && i < rounds.Count; i++)
                {
                    var round = rounds[i];
                    if (!ContextCompressor.IsCompactionSummary(round))
                        fold.Add(round);
                }
            }

            return fold;
        }

        private async UniTask CompactRounds(string trigger)
        {
            await _compactLock.WaitAsync();

            try
            {
                int totalTokens = EstimateTotalTokens();
                if (UseTokenMode)
                {
                    int threshold = (int)(config.ContextWindowTokens * config.CompactRatio);
                    if (totalTokens < threshold || compactStuck) return;
                }
                else if (rounds.Count <= CompressThreshold || compactStuck)
                {
                    return;
                }

                var fold = PartitionFold();
                bool force = UseTokenMode &&
                    totalTokens >= (int)(config.ContextWindowTokens * config.ForceCompactRatio);
                if (fold.Count < (config?.MinCompactRounds ?? 2) ||
                    (UseTokenMode && !force && !FoldEconomics(fold)))
                    return;

                if (enableDebug)
                    Log.Debug(nameof(ContextManager),
                        ZString.Format("触发压缩: {0} 轮, 折叠 {1} 轮", rounds.Count, fold.Count));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int foldedChars = 0;
                for (int i = 0; i < fold.Count; i++)
                    foldedChars += (fold[i].UserMessage?.Length ?? 0) +
                                   (fold[i].AssistantMessage?.Length ?? 0);

                var compactEvent = new CompressionEvent
                {
                    Trigger = trigger,
                    SessionId = sessionId,
                    FoldedRounds = fold.Count,
                    FoldedChars = foldedChars,
                    EstimatedTokens = totalTokens
                };

                ContextCompressor.EmitCompactStarted(compactEvent);

                if (config?.EnableArchive ?? true)
                    compactEvent.ArchivePath = ArchiveRounds(fold);

                var result = await compressor.CompressAsync(fold, sessionId);
                if (string.IsNullOrEmpty(result.Summary))
                    result = new CompressionResult(
                        ZString.Concat(fold.Count, " earlier round(s) folded to free context."), false);

                long timestamp = fold[0].Timestamp;
                ReplaceFoldedRounds(fold, result.Summary, timestamp);

                historyDirty = true;
                consecutiveCompacts++;
                if (consecutiveCompacts >= 2)
                {
                    compactStuck = true;
                    Log.Warning(nameof(ContextManager),
                        ZString.Format("连续压缩 {0} 次，窗口过小，暂停自动压缩", consecutiveCompacts));
                }

                sw.Stop();
                compactEvent.RemainingRounds = rounds.Count;
                compactEvent.Summary = result.Summary;
                compactEvent.SummaryChars = result.Summary?.Length ?? 0;
                compactEvent.IsLLMGenerated = result.IsLLMGenerated;
                compactEvent.ElapsedMs = sw.ElapsedMilliseconds;
                ContextCompressor.EmitCompactCompleted(compactEvent);

                if (enableDebug)
                    Log.Debug(nameof(ContextManager),
                        ZString.Format("压缩完成: {0} 轮→摘要, 剩余 {1} 轮, LLM={2}",
                            fold.Count, rounds.Count, result.IsLLMGenerated));
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(ContextManager), $"压缩失败: {ex.Message}");
            }
            finally
            {
                _compactLock.Release();
            }
        }

        private void ReplaceFoldedRounds(List<ConversationRound> fold, string summary, long timestamp)
        {
            lock (rounds)
            {
                int insertAt = -1;
                for (int i = rounds.Count - 1; i >= 0; i--)
                {
                    if (!fold.Contains(rounds[i])) continue;
                    insertAt = i;
                    rounds.RemoveAt(i);
                }

                if (insertAt < 0) return;

                rounds.Insert(insertAt, new ConversationRound
                {
                    UserMessage = summary,
                    Timestamp = timestamp,
                    IsSummary = true,
                    IsPinned = true
                });
            }
        }

        #endregion

        #region 修剪

        /// <summary>把折叠区里超长的旧回复换成占位符，比摘要更省的一次预处理</summary>
        public int PruneStaleLargeContent()
        {
            if (!UseTokenMode || config == null) return 0;

            int pruned = 0;
            List<ConversationRound> victims = null;

            lock (rounds)
            {
                int head = PinnedPrefixLen();
                int tailStart = TailStartByToken(head, config.TailTokenBudget);
                int minBytes = config.MinPruneBytes;

                for (int i = head; i < tailStart && i < rounds.Count; i++)
                {
                    var round = rounds[i];
                    if (round.IsSummary || round.IsPinned) continue;
                    if (string.IsNullOrEmpty(round.AssistantMessage)) continue;
                    if (round.AssistantMessage.Length < minBytes) continue;
                    if (round.AssistantMessage.StartsWith("[elided", StringComparison.Ordinal)) continue;

                    string original = round.AssistantMessage;
                    string placeholder = ZString.Format(
                        "[elided — {0} bytes dropped to save context]", original.Length);
                    pruned += original.Length - placeholder.Length;
                    round.AssistantMessage = placeholder;

                    // 存副本而不是原对象：原对象已被就地改掉，归档了也只剩占位符
                    (victims ??= new List<ConversationRound>()).Add(new ConversationRound
                    {
                        UserMessage = round.UserMessage,
                        AssistantMessage = original,
                        Timestamp = round.Timestamp,
                        IsPinned = round.IsPinned
                    });
                }
            }

            if (pruned <= 0) return pruned;

            // 替换是不可逆的，而归档只在真压缩时发生：先落一份原文，否则事后无从追查
            if (config.EnableArchive)
                ArchiveRounds(victims);

            historyDirty = true;
            if (enableDebug)
                Log.Debug(nameof(ContextManager), ZString.Format("修剪了 {0} 字符的旧大内容", pruned));
            return pruned;
        }

        #endregion

        #region 归档

        private string ArchiveRounds(List<ConversationRound> roundsToArchive)
        {
            try
            {
                string dir = GetArchiveDirectory(sessionId);
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir,
                    ZString.Concat(DateTime.Now.ToString("yyyyMMdd-HHmmss.fff"), ".json"));
                File.WriteAllText(path, JsonConvert.SerializeObject(roundsToArchive, Formatting.Indented));

                if (enableDebug)
                    Log.Debug(nameof(ContextManager),
                        ZString.Format("归档 {0} 轮到 {1}", roundsToArchive.Count, path));
                return path;
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(ContextManager), $"归档失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        internal static string GetArchiveDirectory(string id)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.persistentDataPath, "LLMArchive"));
                string path = Path.GetFullPath(Path.Combine(root, id ?? "default"));
                string prefix = ZString.Concat(root.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar);
                return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void ClearArchive()
        {
            string path = GetArchiveDirectory(sessionId);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(ContextManager), ZString.Format("清理压缩归档失败: {0}", ex.Message));
            }
        }

        public void Clear()
        {
            lock (rounds)
            {
                rounds.Clear();
                cachedHistory?.Clear();
                historyDirty = true;
            }

            consecutiveCompacts = 0;
            compactStuck = false;
        }

        /// <summary>只读快照，供宿主把既有对话渲染出来。内部 List 会被压缩就地改写，所以必须复制。</summary>
        public List<ConversationRound> SnapshotRounds()
        {
            lock (rounds)
            {
                return new List<ConversationRound>(rounds);
            }
        }

        /// <summary>返回当前请求滑动窗口之外、但尚未被压缩移除的旧轮次。</summary>
        public List<ConversationRound> SnapshotHiddenRounds()
        {
            var active = new HashSet<ConversationRound>(GetActiveRounds());
            var hidden = new List<ConversationRound>();

            lock (rounds)
            {
                for (int i = 0; i < rounds.Count; i++)
                    if (!active.Contains(rounds[i])) hidden.Add(rounds[i]);
            }

            return hidden;
        }

        public string ExportRoundsJson()
        {
            lock (rounds)
            {
                return JsonConvert.SerializeObject(rounds);
            }
        }

        public AgentRoundsBlob ExportSnapshot()
        {
            var snapshot = new AgentRoundsBlob { Rounds = new List<ConversationRound>() };
            lock (rounds)
            {
                for (int i = 0; i < rounds.Count; i++)
                    if (rounds[i] is not null)
                        snapshot.Rounds.Add(CloneRound(rounds[i]));
            }

            return snapshot;
        }

        public void ImportSnapshot(AgentRoundsBlob snapshot)
        {
            lock (rounds)
            {
                rounds.Clear();
                if (snapshot?.Rounds != null)
                {
                    for (int i = 0; i < snapshot.Rounds.Count; i++)
                    {
                        var round = snapshot.Rounds[i];
                        if (round is not null)
                            rounds.Add(CloneRound(round));
                    }
                }

                cachedHistory?.Clear();
                historyDirty = true;
            }
        }

        public void ImportRoundsJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            try
            {
                var imported = JsonConvert.DeserializeObject<List<ConversationRound>>(json);
                if (imported == null || imported.Count == 0) return;

                lock (rounds)
                {
                    rounds.Clear();
                    rounds.AddRange(imported);
                    historyDirty = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(ContextManager), $"导入对话轮次失败: {ex.Message}");
            }
        }

        private static ConversationRound CloneRound(ConversationRound round)
        {
            return new ConversationRound
            {
                UserMessage = round.UserMessage,
                AssistantMessage = round.AssistantMessage,
                Timestamp = round.Timestamp,
                IsSummary = round.IsSummary,
                IsPinned = round.IsPinned
            };
        }
    }
}
