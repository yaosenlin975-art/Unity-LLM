/*
 * LLM — LLM 原始响应结构
 * content + tool_calls 双通道
 */

using System;
using System.Collections.Generic;

namespace LLM.Runtime
{
    public class LLMResponse
    {
        public string Content;
        public List<LLMToolCall> ToolCalls = new();
        public string FinishReason;
        public int PromptTokens;
        public int CompletionTokens;
        public float LatencyMs;

        /// <summary>provider 侧缓存命中 token 数（DeepSeek / OpenAI cached_tokens）</summary>
        public int CacheHitTokens;
        /// <summary>provider 侧缓存未命中 token 数</summary>
        public int CacheMissTokens;
    }
}