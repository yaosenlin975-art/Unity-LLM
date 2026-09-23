/*
┌────────────────────────────┐
│　Description: LLM 流式输出 chunk
│　Remark: content 与 tool_call 增量分开；
│　　　　　 usage 落在 choices 为空的独立 chunk 上（见 OpenAIProvider.ParseStreamChunk）
│　ClassName: LLMStreamChunk
└────────────────────────────┘
*/

using System;

namespace LLM.Runtime
{
    public readonly struct LLMStreamChunk
    {
        public readonly string ContentDelta;
        public readonly ToolCallDelta ToolCallDelta;
        public readonly bool IsDone;

        /// <summary>
        /// 以下三项仅在携带 usage 的那个 chunk 上有效（流式里 usage 是独立 chunk，
        /// 且带 usage 的 chunk 之后还会再来一条不带 token 的 isDone），其余 chunk 为 0。
        /// </summary>
        public readonly int PromptTokens;
        public readonly int CompletionTokens;
        public readonly int CacheHitTokens;

        public LLMStreamChunk(string contentDelta, ToolCallDelta toolCallDelta = default, bool isDone = false,
            int promptTokens = 0, int completionTokens = 0, int cacheHitTokens = 0)
        {
            ContentDelta = contentDelta;
            ToolCallDelta = toolCallDelta;
            IsDone = isDone;
            PromptTokens = promptTokens;
            CompletionTokens = completionTokens;
            CacheHitTokens = cacheHitTokens;
        }
    }

    public readonly struct ToolCallDelta
    {
        public readonly int Index;
        public readonly string Id;
        public readonly string Name;
        public readonly string ArgumentsDelta;

        public ToolCallDelta(int index, string id = null, string name = null, string argumentsDelta = null)
        {
            Index = index;
            Id = id;
            Name = name;
            ArgumentsDelta = argumentsDelta;
        }
    }
}
