/*
 * LLM — LLM 供应商抽象接口 * 支持流式和非流式两种调用模式
 */

using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace LLM.Runtime
{
    public interface ILLMProvider
    {
        string ProviderName { get; }
        bool IsAvailable { get; }
        bool SupportsStreaming { get; }
        bool SupportsToolCalling { get; }

        UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct);
        UniTask CompleteStreamAsync(LLMRequest request,
            Action<LLMStreamChunk> onChunk,
            CancellationToken ct);

        int EstimateTokens(string text);
    }
}