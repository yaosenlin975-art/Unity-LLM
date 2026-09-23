/*
 * ┌────────────────────────────────────────────┐
 * │ Description : LLM 调度器                    │
 * │ Remark      : 全局单例，Provider 注册 +      │
 * │               主/备降级 + 请求日志事件       │
 * │ ClassName   : LLMDispatcher                 │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using UnityEngine.Pool;

namespace LLM.Runtime
{
    public class LLMDispatcher
    {
        private static LLMDispatcher instance;
        public static LLMDispatcher GetInstance()
        {
            if (instance == null)
                instance = new LLMDispatcher();

            return instance;
        }

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Dictionary<string, ILLMProvider> providers = new();

        private string defaultProvider;
        private string fallbackProvider;

        /// <summary>每次请求结束（含失败）后触发，供日志窗口与埋点订阅</summary>
        public static event Action<LLMRequestLog> OnRequestCompleted;

        public LLMDispatcher RegisterProvider(ILLMProvider provider)
        {
            if (provider == null) return this;

            _lock.Wait();
            try
            {
                providers[provider.ProviderName] = provider;
            }
            finally
            {
                _lock.Release();
            }
            return this;
        }

        public void UnregisterProvider(string providerName)
        {
            if (string.IsNullOrEmpty(providerName)) return;

            _lock.Wait();
            try
            {
                providers.Remove(providerName);
            }
            finally
            {
                _lock.Release();
            }
        }

        public LLMDispatcher SetDefaultProvider(string providerName)
        {
            _lock.Wait();
            try
            {
                defaultProvider = providerName;
            }
            finally
            {
                _lock.Release();
            }
            return this;
        }

        public void SetFallbackProvider(string providerName)
        {
            _lock.Wait();
            try
            {
                fallbackProvider = providerName;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>取可用 Provider：指定名 → 默认 → 备用 → null</summary>
        public ILLMProvider GetProvider(string providerName = null)
        {
            _lock.Wait();
            try
            {
                // 传空串必须回落到默认 Provider：调用方（如压缩器）从 SO 读名字，未配置就是空串
                string target = string.IsNullOrEmpty(providerName) ? defaultProvider : providerName;

                if (!string.IsNullOrEmpty(target) &&
                    providers.TryGetValue(target, out var provider) &&
                    provider.IsAvailable)
                    return provider;

                if (!string.IsNullOrEmpty(fallbackProvider) &&
                    providers.TryGetValue(fallbackProvider, out var fallback) &&
                    fallback.IsAvailable)
                    return fallback;

                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async UniTask<LLMResponse> EnqueueAsync(LLMRequest request, CancellationToken ct)
        {
            var provider = GetProvider()
                           ?? throw new Exception("[LLMDispatcher] 无可用的 LLM 供应商");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var response = await provider.CompleteAsync(request, ct);
                sw.Stop();
                EmitLog(request, provider.ProviderName, response, sw.ElapsedMilliseconds, null);
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                EmitLog(request, provider.ProviderName, null, sw.ElapsedMilliseconds, ex.Message);

                var fallback = GetProvider(fallbackProvider);
                if (fallback == null || fallback.ProviderName == provider.ProviderName)
                    throw;

                LogFallback(provider.ProviderName, fallback.ProviderName, ex.Message);
                return await fallback.CompleteAsync(request, ct);
            }
        }

        public async UniTask EnqueueStreamAsync(LLMRequest request,
            Action<LLMStreamChunk> onChunk, CancellationToken ct)
        {
            var provider = GetProvider()
                           ?? throw new Exception("[LLMDispatcher] 无可用的 LLM 供应商");

            var collector = new StreamLogCollector(request.SessionId, provider.ProviderName, onChunk);

            try
            {
                await provider.CompleteStreamAsync(request, collector.OnChunk, ct);
                collector.Flush(null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                collector.Flush(ex.Message);

                var fallback = GetProvider(fallbackProvider);
                if (fallback == null || fallback.ProviderName == provider.ProviderName)
                    throw;

                LogFallback(provider.ProviderName, fallback.ProviderName, ex.Message);

                // 降级这条也发日志：确实又发了一次请求，成功用量不记就丢掉了唯一的成本诊断来源
                var retry = new StreamLogCollector(request.SessionId, fallback.ProviderName, onChunk);
                try
                {
                    await fallback.CompleteStreamAsync(request, retry.OnChunk, ct);
                }
                catch (Exception retryEx) when (retryEx is not OperationCanceledException)
                {
                    retry.Flush(retryEx.Message);
                    throw;
                }

                retry.Flush(null);
            }
        }

        /// <summary>
        /// 流式请求的用量累加器。usage 落在 choices 为空的独立 chunk 上、[DONE] 那条不带 token，
        /// 所以必须跨 chunk 缓存、到流结束统一发一次日志，否则真链路恒 0。
        /// </summary>
        private sealed class StreamLogCollector
        {
            private const int k_previewLimit = 200;

            private readonly string sessionId;
            private readonly string providerName;
            private readonly Action<LLMStreamChunk> onNext;
            private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            private string contentPreview = "";
            private int promptTokens;
            private int completionTokens;
            private int cacheHitTokens;
            private bool hasToolCall;
            private bool flushed;
            private readonly HashSet<string> toolNames = new();

            public StreamLogCollector(string sessionId, string providerName, Action<LLMStreamChunk> onNext)
            {
                this.sessionId = sessionId;
                this.providerName = providerName;
                this.onNext = onNext;
            }

            public void OnChunk(LLMStreamChunk chunk)
            {
                if (chunk.PromptTokens > 0) promptTokens = chunk.PromptTokens;
                if (chunk.CompletionTokens > 0) completionTokens = chunk.CompletionTokens;
                if (chunk.CacheHitTokens > 0) cacheHitTokens = chunk.CacheHitTokens;

                if (!string.IsNullOrEmpty(chunk.ContentDelta) && contentPreview.Length < k_previewLimit)
                    contentPreview = ZString.Concat(contentPreview, chunk.ContentDelta);

                var delta = chunk.ToolCallDelta;
                if (delta.Id != null || delta.Name != null || delta.ArgumentsDelta != null)
                {
                    hasToolCall = true;
                    if (!string.IsNullOrWhiteSpace(delta.Name))
                        toolNames.Add(delta.Name);
                }

                onNext?.Invoke(chunk);
            }

            public void Flush(string error)
            {
                if (flushed) return;
                flushed = true;
                stopwatch.Stop();

                if (contentPreview.Length > k_previewLimit)
                    contentPreview = contentPreview.Substring(0, k_previewLimit);

                Emit(new LLMRequestLog
                {
                    SessionId = sessionId,
                    Provider = providerName,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    IsError = error != null,
                    ErrorMessage = error,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    CacheHitTokens = cacheHitTokens,
                    ContentPreview = contentPreview,
                    HasToolCall = hasToolCall,
                    ToolNames = JoinNames(toolNames),
                    // args 增量要到 LLMSession 的 assembler 才拼得完整，这里不重复实现一遍
                    ToolSummary = ""
                });
            }

            private static string JoinNames(HashSet<string> names)
            {
                using var sb = ZString.CreateStringBuilder();
                foreach (var name in names)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(name);
                }
                return sb.ToString();
            }
        }

        private static void LogFallback(string from, string to, string error)
        {
            Log.Warning(nameof(LLMDispatcher),
                ZString.Format("Provider '{0}' 失败，降级到 '{1}': {2}", from, to, error));
        }

        /// <summary>
        /// 发日志事件。订阅方（日志窗口、埋点）抛异常绝不能冒回请求路径——
        /// 那会被上面的 catch 当成 provider 失败，从而真真切切重发一次计费请求。
        /// </summary>
        private static void Emit(LLMRequestLog log)
        {
            try
            {
                OnRequestCompleted?.Invoke(log);
            }
            catch (Exception ex)
            {
                Log.Error(nameof(LLMDispatcher),
                    ZString.Format("OnRequestCompleted 订阅方异常: {0}", ex.Message));
            }
        }

        private static void EmitLog(LLMRequest request, string providerName,
            LLMResponse response, long latencyMs, string error)
        {
            Emit(new LLMRequestLog
            {
                SessionId = request.SessionId,
                Provider = providerName,
                LatencyMs = latencyMs,
                IsError = error != null,
                ErrorMessage = error,
                PromptTokens = response?.PromptTokens ?? 0,
                CompletionTokens = response?.CompletionTokens ?? 0,
                CacheHitTokens = response?.CacheHitTokens ?? 0,
                ContentPreview = response?.Content,
                HasToolCall = response?.ToolCalls.Count > 0,
                ToolNames = BuildToolNames(response?.ToolCalls),
                ToolSummary = BuildToolSummary(response?.ToolCalls)
            });
        }

        private static string BuildToolNames(List<LLMToolCall> toolCalls)
        {
            if (toolCalls == null || toolCalls.Count == 0) return "";

            var names = ListPool<string>.Get();
            var seen = HashSetPool<string>.Get();

            for (int i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                if (tc == null || string.IsNullOrWhiteSpace(tc.Name)) continue;
                if (seen.Add(tc.Name)) names.Add(tc.Name);
            }

            using var sb = ZString.CreateStringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
            }

            string result = sb.ToString();
            names.Clear();
            seen.Clear();
            ListPool<string>.Release(names);
            HashSetPool<string>.Release(seen);
            return result;
        }

        private static string BuildToolSummary(List<LLMToolCall> toolCalls, int maxArgsLength = 120)
        {
            if (toolCalls == null || toolCalls.Count == 0) return "";

            using var sb = ZString.CreateStringBuilder();
            for (int i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                if (tc == null) continue;

                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(string.IsNullOrWhiteSpace(tc.Name) ? "unknown" : tc.Name);
                sb.Append(": ");

                if (string.IsNullOrWhiteSpace(tc.Arguments))
                {
                    sb.Append("无参数");
                    continue;
                }

                string args = tc.Arguments.Replace("\r", "").Replace("\n", " ");
                if (args.Length <= maxArgsLength) sb.Append(args);
                else
                {
                    sb.Append(args.Substring(0, maxArgsLength));
                    sb.Append("...");
                }
            }

            return sb.ToString();
        }
    }

    public struct LLMRequestLog
    {
        public string SessionId;
        public string Provider;
        public long LatencyMs;
        public int PromptTokens;
        public int CompletionTokens;
        public int CacheHitTokens;
        public string ContentPreview;
        public bool HasToolCall;
        public string ToolNames;
        public string ToolSummary;
        public bool IsError;
        public string ErrorMessage;
    }
}
