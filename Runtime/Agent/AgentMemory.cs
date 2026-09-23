/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 记忆：事实槽与记忆动作   │
 * │ Remark      : 只做结构化事实槽，不做向量检索 │
 * │               与反思式记忆（ADR-005）        │
 * │ ClassName   : AgentMemory                   │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Storage;
using Newtonsoft.Json;
using UnityEngine.Pool;

namespace LLM.Runtime.Agent
{
    [Serializable]
    public class AgentFact
    {
        public string Key;
        public string Value;
        public long Timestamp;
    }

    /// <summary>
    /// 每个 agent 一份，内存态；落盘经 AgentStateStores（ADR-014），本类自己不做文件 IO。
    /// 列表顺序即新旧顺序：命中更新会移到末尾，超容量从头部淘汰。
    /// </summary>
    public sealed class AgentMemory
    {
        private const string k_blockHeader = "## 已知事实";

        private readonly List<AgentFact> facts = new();

        private int maxCount = 60;

        public int Count => facts.Count;

        public IReadOnlyList<AgentFact> Facts => facts;

        public void Configure(int maxCount)
        {
            this.maxCount = Math.Max(1, maxCount);
        }

        public void Write(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;

            for (int i = 0; i < facts.Count; i++)
            {
                if (facts[i].Key != key) continue;

                var existing = facts[i];
                existing.Value = value;
                existing.Timestamp = NowSeconds();
                facts.RemoveAt(i);
                facts.Add(existing);
                return;
            }

            facts.Add(new AgentFact { Key = key, Value = value, Timestamp = NowSeconds() });
            Trim();
        }

        public bool Forget(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            for (int i = 0; i < facts.Count; i++)
            {
                if (facts[i].Key != key) continue;
                facts.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>注入用的整块文本。事实槽没有原位更新一说，每轮整体重拼</summary>
        public string RenderBlock()
        {
            if (facts.Count == 0) return "";

            using var sb = ZString.CreateStringBuilder();
            sb.Append(k_blockHeader);

            for (int i = 0; i < facts.Count; i++)
                sb.Append(ZString.Format("\n- {0}：{1}", facts[i].Key, facts[i].Value));

            return sb.ToString();
        }

        public string ExportJson()
        {
            return JsonConvert.SerializeObject(facts);
        }

        public AgentFactsBlob ExportSnapshot()
        {
            var snapshot = new AgentFactsBlob { Facts = new List<AgentFact>(facts.Count) };
            for (int i = 0; i < facts.Count; i++)
            {
                var fact = facts[i];
                snapshot.Facts.Add(new AgentFact
                {
                    Key = fact.Key,
                    Value = fact.Value,
                    Timestamp = fact.Timestamp
                });
            }

            return snapshot;
        }

        public void ImportSnapshot(AgentFactsBlob snapshot)
        {
            facts.Clear();
            if (snapshot?.Facts == null) return;

            for (int i = 0; i < snapshot.Facts.Count; i++)
            {
                var fact = snapshot.Facts[i];
                if (fact is null || string.IsNullOrEmpty(fact.Key)) continue;

                facts.Add(new AgentFact
                {
                    Key = fact.Key,
                    Value = fact.Value,
                    Timestamp = fact.Timestamp
                });
            }

            Trim();
        }

        public void ImportJson(string json)
        {
            facts.Clear();
            if (string.IsNullOrWhiteSpace(json)) return;

            List<AgentFact> imported;
            try
            {
                imported = JsonConvert.DeserializeObject<List<AgentFact>>(json);
            }
            catch (Exception ex)
            {
                // 存档是可被 Prefs 窗口手改的外部数据：坏档按空处理，不能让宿主在 OnEnable 建 agent 时炸掉
                Log.Warning(nameof(AgentMemory),
                    ZString.Format("事实槽存档解析失败，按空记忆继续: {0}", ex.Message));
                return;
            }

            if (imported == null) return;

            for (int i = 0; i < imported.Count; i++)
                if (!string.IsNullOrEmpty(imported[i]?.Key))
                    facts.Add(imported[i]);

            Trim();
        }

        public void Clear() => facts.Clear();

        private void Trim()
        {
            while (facts.Count > maxCount)
                facts.RemoveAt(0);
        }

        private static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// 记忆三件套必须是 [AgentAction] 而不是 [Tool]：静态的 [Tool] 拿不到上下文，
    /// 多个 NPC 会写进同一份内存。故一律经 AgentActionContext.Agent 定位到该实例的 AgentMemory。
    /// </summary>
    public static class AgentMemoryActions
    {
        private const long k_archiveReadBudget = 2 * 1024 * 1024;
        private const int k_maxQueryLength = 200;
        private const int k_maxTerms = 8;
        private const int k_maxPreviewLength = 500;

        private static readonly char[] querySeparators =
        {
            ' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '，', '。', '；', '：', '！', '？',
            '、', '/', '\\', '|', '(', ')', '[', ']', '{', '}', '【', '】', '（', '）', '"', '\''
        };

        [AgentAction("write_fact", "记住一条长期有效的事实（key 短名，value 一句话）。只记以后还会用到的稳定信息。", Idempotent = true, NameRepeatLimit = 3)]
        public static UniTask<AgentActionResult> WriteFact(AgentActionContext ctx, string key, string value)
        {
            if (ctx?.Agent == null || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                return new UniTask<AgentActionResult>(AgentActionResult.Failure("key 与 value 都不能为空"));

            ctx.Agent.Memory.Write(key, value);
            SaveFacts(ctx.Agent);
            return new UniTask<AgentActionResult>(
                AgentActionResult.Success(ZString.Format("已记住 {0}", key)));
        }

        [AgentAction("forget_fact", "删除一条已记住的事实（按 key 删除）。", Idempotent = true, NameRepeatLimit = 3)]
        public static UniTask<AgentActionResult> ForgetFact(AgentActionContext ctx, string key)
        {
            if (ctx?.Agent == null || string.IsNullOrEmpty(key))
                return new UniTask<AgentActionResult>(AgentActionResult.Failure("key 不能为空"));

            if (!ctx.Agent.Memory.Forget(key))
                return new UniTask<AgentActionResult>(
                    AgentActionResult.Failure(ZString.Format("没有记过 {0}", key)));

            SaveFacts(ctx.Agent);
            return new UniTask<AgentActionResult>(
                AgentActionResult.Success(ZString.Format("已遗忘 {0}", key)));
        }

        [AgentAction("search_past_conversation", "按关键词搜索当前上下文之外的旧对话和压缩归档。", Idempotent = true, NameRepeatLimit = 3)]
        public static UniTask<AgentActionResult> SearchPastConversation(
            AgentActionContext ctx, string query, int maxResults = 5)
        {
            if (ctx?.Agent == null)
                return new UniTask<AgentActionResult>(AgentActionResult.Failure("无记忆上下文"));
            if (string.IsNullOrWhiteSpace(query))
                return new UniTask<AgentActionResult>(AgentActionResult.Failure("query 不能为空"));
            if (query.Length > k_maxQueryLength)
                return new UniTask<AgentActionResult>(AgentActionResult.Failure("query 不能超过 200 个字符"));

            int limit = Math.Max(1, Math.Min(8, maxResults));
            string phrase = query.Trim().ToLowerInvariant();
            var terms = Tokenize(phrase);
            var candidates = ListPool<MemoryCandidate>.Get();
            // 池给的 HashSet 用默认比较器；字符串的默认比较器就是 Ordinal，与原写法同语义
            var seen = HashSetPool<string>.Get();
            int sequence = 0;

            AddRounds(ctx.Agent.Session.Context.SnapshotHiddenRounds(), phrase, terms,
                candidates, seen, ref sequence);

            bool archiveTruncated = false;
            bool archiveFailed = false;
            if (ctx.Agent.Session.Context.ArchiveEnabled)
                AddArchiveRounds(ctx.Agent.SessionId, phrase, terms, candidates, seen,
                    ref sequence, out archiveTruncated, out archiveFailed);

            candidates.Sort(MemoryCandidateComparer.Instance);
            using var sb = ZString.CreateStringBuilder();
            int count = Math.Min(limit, candidates.Count);
            if (count == 0)
                sb.Append("没有找到匹配的记忆。");
            else
                for (int i = 0; i < count; i++)
                    sb.Append(ZString.Format("{0}. [{1} | {2}] {3}\n", i + 1,
                        candidates[i].Source, FormatTimestamp(candidates[i].Timestamp),
                        Preview(candidates[i].Text)));

            if (!ctx.Agent.Session.Context.ArchiveEnabled)
                sb.Append("\n压缩归档未启用：已压缩的原始对话不可搜索。");
            else if (archiveTruncated)
                sb.Append("\n归档读取已达 2 MiB 上限，结果可能不完整。");
            else if (archiveFailed)
                sb.Append("\n部分压缩归档读取失败。");

            string report = sb.ToString().TrimEnd();
            ListPool<MemoryCandidate>.Release(candidates);
            HashSetPool<string>.Release(seen);
            return new UniTask<AgentActionResult>(AgentActionResult.Success(report));
        }

        private static List<string> Tokenize(string query)
        {
            var result = new List<string>();
            var parts = query.Split(querySeparators, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && result.Count < k_maxTerms; i++)
            {
                string term = parts[i].Trim();
                if (term.Length == 0 || result.Contains(term)) continue;
                result.Add(term);
            }

            return result;
        }

        private static void AddRounds(List<ConversationRound> rounds, string phrase, List<string> terms,
            List<MemoryCandidate> candidates, HashSet<string> seen, ref int sequence)
        {
            for (int i = 0; i < rounds.Count; i++)
            {
                var round = rounds[i];
                if (round.IsSummary)
                {
                    AddText(round.UserMessage, round.Timestamp, 4, "summary", phrase, terms,
                        candidates, seen, ref sequence);
                    continue;
                }

                AddText(round.UserMessage, round.Timestamp, 2, "user", phrase, terms,
                    candidates, seen, ref sequence);
                AddText(round.AssistantMessage, round.Timestamp, 3, "assistant", phrase, terms,
                    candidates, seen, ref sequence);
            }
        }

        private static void AddArchiveRounds(string sessionId, string phrase, List<string> terms,
            List<MemoryCandidate> candidates, HashSet<string> seen, ref int sequence,
            out bool truncated, out bool failed)
        {
            truncated = false;
            failed = false;
            string directory = ContextManager.GetArchiveDirectory(sessionId);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            try
            {
                var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.Ordinal);
                long readBytes = 0;

                for (int i = files.Length - 1; i >= 0; i--)
                {
                    long length = new FileInfo(files[i]).Length;
                    if (readBytes + length > k_archiveReadBudget)
                    {
                        truncated = true;
                        break;
                    }

                    readBytes += length;
                    var archived = JsonConvert.DeserializeObject<List<ConversationRound>>(
                        File.ReadAllText(files[i]));
                    if (archived != null)
                        AddRounds(archived, phrase, terms, candidates, seen, ref sequence);
                }
            }
            catch (Exception ex)
            {
                failed = true;
                Log.Warning(nameof(AgentMemoryActions),
                    ZString.Format("读取压缩归档失败: {0}", ex.Message));
            }
        }

        private static void AddText(string text, long timestamp, int sourcePriority, string source,
            string phrase, List<string> terms, List<MemoryCandidate> candidates,
            HashSet<string> seen, ref int sequence)
        {
            if (string.IsNullOrEmpty(text)) return;

            string search = text.ToLowerInvariant();
            bool fullPhrase = search.Contains(phrase);
            int hits = CountHits(search, terms);
            if (!fullPhrase && hits == 0) return;

            AddCandidate(candidates, seen, text, timestamp, fullPhrase, hits,
                sourcePriority, source, ref sequence);
        }

        private static int CountHits(string text, List<string> terms)
        {
            int count = 0;
            for (int i = 0; i < terms.Count; i++)
                if (text.Contains(terms[i])) count++;
            return count;
        }

        private static void AddCandidate(List<MemoryCandidate> candidates, HashSet<string> seen,
            string text, long timestamp, bool fullPhrase, int termHits, int sourcePriority,
            string source, ref int sequence)
        {
            string normalized = text.Trim().ToLowerInvariant();
            string key = ZString.Format("{0}:{1}", timestamp, normalized);
            if (!seen.Add(key)) return;

            candidates.Add(new MemoryCandidate
            {
                Text = text,
                Timestamp = timestamp,
                FullPhrase = fullPhrase,
                TermHits = termHits,
                SourcePriority = sourcePriority,
                Source = source,
                Sequence = sequence++
            });
        }

        private static string Preview(string text)
        {
            string singleLine = text.Replace("\r", " ").Replace("\n", " ");
            return singleLine.Length <= k_maxPreviewLength
                ? singleLine
                : ZString.Concat(singleLine.Substring(0, k_maxPreviewLength), "...");
        }

        private static string FormatTimestamp(long timestamp)
        {
            if (timestamp <= 0) return "unknown";
            return DateTimeOffset.FromUnixTimeSeconds(timestamp)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss");
        }

        private sealed class MemoryCandidate
        {
            public string Text;
            public long Timestamp;
            public bool FullPhrase;
            public int TermHits;
            public int SourcePriority;
            public string Source;
            public int Sequence;
        }

        private sealed class MemoryCandidateComparer : IComparer<MemoryCandidate>
        {
            public static readonly MemoryCandidateComparer Instance = new();

            public int Compare(MemoryCandidate x, MemoryCandidate y)
            {
                int result = y.FullPhrase.CompareTo(x.FullPhrase);
                if (result != 0) return result;
                result = y.TermHits.CompareTo(x.TermHits);
                if (result != 0) return result;
                result = x.SourcePriority.CompareTo(y.SourcePriority);
                if (result != 0) return result;
                result = y.Timestamp.CompareTo(x.Timestamp);
                return result != 0 ? result : x.Sequence.CompareTo(y.Sequence);
            }
        }

        /// <summary>事实槽是 write-through：模型调一次工具落一次盘，不像历史那样等轮末。</summary>
        private static void SaveFacts(AgentCore agent)
        {
            if (!agent.IsPersistent) return;

            AgentStateStores.Facts
                .SaveFactsAsync(agent.SessionId, agent.Memory.ExportSnapshot(), default)
                .Forget();
        }
    }
}
