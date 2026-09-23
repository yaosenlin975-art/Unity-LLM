/*
┌────────────────────────────┐
│　Description: 上下文压缩器
│　Remark: 结构化摘要 + 单次 LLM 调用 +
│　　　　　 机械降级（无 LLM 也能收尾）
│　ClassName: ContextCompressor
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;

namespace LLM.Runtime
{
    public class ContextCompressor
    {
        private const string k_summaryTagOpen = "<compaction-summary>";
        private const string k_summaryTagClose = "</compaction-summary>";
        private const int k_maxFallbackLines = 5;
        private const string k_compressorSession = "context_compressor";

        private const string k_structuredSummaryPrompt = @"You are compacting an earlier conversation segment to save context.
The kept recent turns stay verbatim alongside your summary, so only the folded part needs to be captured.
Write under these exact headings, omitting a heading only if it has no content:

## Standing facts & constraints
Everything the user stated that still governs the conversation — names, locations, items, preferences, hard rules. Be exhaustive; prefer over- to under-including.

## Goal
The user's request and intent in this segment.

## Decisions & rationale
Key choices made and why — so they are not re-litigated.

## Key entities
Items, locations, people and IDs mentioned, with the specific facts that matter: prices, quantities, relationships.

## Pending & next step
What is still in progress or unstarted, and the single most concrete next action.

Rules: be terse — bullet points and fragments, not prose. Preserve names, numbers and IDs exactly. Do NOT invent anything not present in the messages.";

        private readonly CompressionConfig_SO config;
        private readonly bool enableDebug;

        public ContextCompressor(CompressionConfig_SO config = null)
        {
            this.config = config;
            this.enableDebug = config?.EnableCompressionDebug ?? false;
        }

        public static bool IsCompactionSummary(ConversationRound round)
        {
            return round.IsSummary;
        }

        /// <summary>摘要回灌历史时统一包一层标签，让模型知道这是压缩过的旧内容</summary>
        public static string WrapSummary(string summary)
        {
            using var sb = ZString.CreateStringBuilder();
            sb.Append(k_summaryTagOpen);
            sb.Append("\nSummary of earlier conversation (older messages were compacted to save context):\n");
            sb.Append(summary);
            sb.Append("\n");
            sb.Append(k_summaryTagClose);
            return sb.ToString();
        }

        /// <summary>短小的首轮用户提问不参与折叠，永久锚定在头部</summary>
        public static bool IsPinnableUserTurn(ConversationRound round, int contextWindow, float tokPerChar)
        {
            if (round.IsSummary) return false;
            if (string.IsNullOrEmpty(round.UserMessage) && string.IsNullOrEmpty(round.AssistantMessage))
                return false;

            int charCount = (round.UserMessage?.Length ?? 0) + (round.AssistantMessage?.Length ?? 0);
            int estimatedTokens = (int)(charCount * tokPerChar);

            int budget = 1500;
            if (contextWindow > 0)
            {
                int byWindow = (int)(contextWindow * 0.15f);
                if (byWindow < budget) budget = byWindow;
            }
            return estimatedTokens <= budget;
        }

        public async UniTask<CompressionResult> CompressAsync(
            List<ConversationRound> rounds,
            string sessionId,
            CancellationToken ct = default)
        {
            if (rounds == null || rounds.Count == 0)
                return CompressionResult.Empty;

            if (config != null && !config.EnableLLMCompression)
                return CreateMechanicalFallback(rounds);

            var messages = new List<LLMMessage>
            {
                new("system", k_structuredSummaryPrompt),
                new("user", BuildDialogueText(rounds))
            };

            var request = new LLMRequest
            {
                SessionId = ZString.Concat(k_compressorSession, ":", sessionId ?? ""),
                Messages = messages,
                Temperature = config?.CompressionTemperature ?? 0.3f,
                MaxTokens = config?.CompressionMaxTokens ?? 500,
                Priority = ELLMRequestPriority.Low
            };

            try
            {
                var result = await SummarizeOnce(request, ct);
                return result.Summary != null ? result : CreateMechanicalFallback(rounds);
            }
            finally
            {
                messages.Clear();
            }
        }

        /// <summary>
        /// 摘要只试一次：失败有机械降级兜底，重试等于把整段等待再叠一层，
        /// 而压缩是插在对话路径上的，用户只会觉得更卡。
        /// </summary>
        private async UniTask<CompressionResult> SummarizeOnce(LLMRequest request, CancellationToken ct)
        {
            float timeout = config?.CompressionTimeoutSeconds ?? 15f;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeout));

                var provider = LLMDispatcher.GetInstance().GetProvider(config?.CompressionProviderName);
                if (provider == null)
                {
                    Log.Warning(nameof(ContextCompressor), "无可用 Provider，跳过 LLM 摘要");
                    return CompressionResult.Empty;
                }

                var response = await provider.CompleteAsync(request, cts.Token);
                if (!string.IsNullOrEmpty(response.Content))
                    return new CompressionResult(response.Content.Trim(), true);

                Log.Warning(nameof(ContextCompressor), "摘要返回空内容");
            }
            catch (OperationCanceledException)
            {
                Log.Warning(nameof(ContextCompressor), "摘要超时");
                return CompressionResult.Empty;
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(ContextCompressor),
                    ZString.Format("摘要请求失败: {0}", ex.Message));
            }

            return CompressionResult.Empty;
        }

        private static string BuildDialogueText(List<ConversationRound> rounds)
        {
            using var sb = ZString.CreateStringBuilder();
            for (int i = 0; i < rounds.Count; i++)
            {
                var round = rounds[i];
                if (round.IsSummary)
                {
                    sb.Append("[previous summary]\n");
                    sb.Append(round.UserMessage ?? round.AssistantMessage ?? "");
                    sb.Append("\n\n");
                    continue;
                }
                if (!string.IsNullOrEmpty(round.UserMessage))
                    sb.Append(ZString.Format("[user]\n{0}\n\n", round.UserMessage));
                if (!string.IsNullOrEmpty(round.AssistantMessage))
                    sb.Append(ZString.Format("[assistant]\n{0}\n\n", round.AssistantMessage));
            }
            return sb.ToString();
        }

        private static CompressionResult CreateMechanicalFallback(List<ConversationRound> rounds)
        {
            using var sb = ZString.CreateStringBuilder();
            sb.Append(ZString.Format("[Mechanical fold] {0} earlier round(s) folded to free context.\n", rounds.Count));

            int max = Math.Min(rounds.Count, k_maxFallbackLines);
            for (int i = 0; i < max; i++)
            {
                var round = rounds[i];
                if (round.IsSummary)
                {
                    sb.Append(ZString.Format("- [prior summary]: {0}\n", Truncate(round.UserMessage ?? "", 80)));
                    continue;
                }
                if (!string.IsNullOrEmpty(round.UserMessage))
                    sb.Append(ZString.Format("- User: {0}\n", Truncate(round.UserMessage, 60)));
                if (!string.IsNullOrEmpty(round.AssistantMessage))
                    sb.Append(ZString.Format("- Assistant: {0}\n", Truncate(round.AssistantMessage, 60)));
            }

            if (rounds.Count > max)
                sb.Append(ZString.Format("- ... ({0} total rounds)\n", rounds.Count));

            return new CompressionResult(sb.ToString(), false);
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length <= maxLength
                ? text
                : ZString.Concat(text.Substring(0, maxLength), "...");
        }

        /// <summary>压缩开始事件（UI 可订阅显示进度）。</summary>
        public static event Action<CompressionEvent> OnCompactStarted;

        /// <summary>压缩完成事件（UI 可订阅显示结果）。</summary>
        public static event Action<CompressionEvent> OnCompactCompleted;

        internal static void EmitCompactStarted(CompressionEvent evt) => OnCompactStarted?.Invoke(evt);
        internal static void EmitCompactCompleted(CompressionEvent evt) => OnCompactCompleted?.Invoke(evt);
    }

    public struct CompressionResult
    {
        public readonly string Summary;
        public readonly bool IsLLMGenerated;

        public CompressionResult(string summary, bool isLLMGenerated)
        {
            Summary = summary;
            IsLLMGenerated = isLLMGenerated;
        }

        public static CompressionResult Empty => new(null, false);
    }

    /// <summary>压缩事件数据。</summary>
    public struct CompressionEvent
    {
        public string Trigger;          // "auto" / "force"
        public string SessionId;
        public int FoldedRounds;        // 被压缩的轮数
        public int RemainingRounds;     // 压缩后剩余轮数
        public int FoldedChars;         // 被压缩内容的字符总数
        public string Summary;          // 生成的摘要内容
        public int SummaryChars;        // 摘要字符数
        public bool IsLLMGenerated;     // 是否由 LLM 生成（false = 机械降级）
        public int EstimatedTokens;     // 压缩前的估算 token 总量
        public string ArchivePath;      // 归档文件路径（如有）
        public long ElapsedMs;          // 压缩耗时
        public string Error;            // 错误信息（如有）

        public readonly string SummaryShort
        {
            get
            {
                if (!string.IsNullOrEmpty(Error)) return ZString.Concat("error: ", Error);
                string src = IsLLMGenerated ? "LLM" : "mechanical";
                return ZString.Format("{0} rounds -> {1} chars ({2})", FoldedRounds, SummaryChars, src);
            }
        }
    }

    [Serializable]
    public class ConversationRound
    {
        public string UserMessage;
        public string AssistantMessage;
        public long Timestamp;
        public bool IsSummary;
        public bool IsPinned;
    }
}
