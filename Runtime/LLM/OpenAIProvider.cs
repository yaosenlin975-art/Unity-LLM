/*
 * ┌────────────────────────────────────────────┐
 * │ Description : OpenAI 兼容供应商实现         │
 * │ Remark      : content + tool_calls 双通道   │
 * │               SSE 流式解析；支持任意兼容网关 │
 * │ ClassName   : OpenAIProvider                │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace LLM.Runtime
{
    public class OpenAIProvider : ILLMProvider
    {
        private const string k_defaultBaseUrl = "https://api.openai.com/v1";
        private const int k_timeoutSeconds = 30;
        private const int k_errorBodyPreview = 500;

        public string ProviderName { get; }
        public bool IsAvailable => !string.IsNullOrEmpty(apiKey) || baseUrl != k_defaultBaseUrl;
        public bool SupportsStreaming => true;
        public bool SupportsToolCalling => true;

        private readonly string apiKey;
        private readonly string model;
        private readonly string baseUrl;

        public OpenAIProvider(string apiKey, string model = "gpt-4o",
            string baseUrl = k_defaultBaseUrl, string providerName = "openai")
        {
            this.apiKey = apiKey;
            this.model = model;
            this.baseUrl = baseUrl;
            ProviderName = providerName;
        }

        public async UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct)
        {
            string body = BuildRequestBody(request, stream: false);

            using var webRequest = BuildRequest(body, streaming: false);
            webRequest.downloadHandler = new DownloadHandlerBuffer();

            await webRequest.SendWebRequest().WithCancellation(ct);
            ThrowOnError(webRequest, body);

            return ParseResponse(webRequest.downloadHandler.text);
        }

        public async UniTask CompleteStreamAsync(LLMRequest request,
            Action<LLMStreamChunk> onChunk, CancellationToken ct)
        {
            string body = BuildRequestBody(request, stream: true);

            using var webRequest = BuildRequest(body, streaming: true);
            var sseHandler = new SSEDownloadHandler(onChunk, ParseStreamChunk);
            webRequest.downloadHandler = sseHandler;

            // 流式不设整体 timeout：本地模型（LM Studio 等）一次生成就可能超过 30s，
            // 设了会被 UnityWebRequest 掐断，60s 的 idle 档反而永远轮不到。失控只由 idle 超时与墙钟管
            using var linkedCts = LinkCancellationToken(ct, sseHandler.IdleToken);
            await webRequest.SendWebRequest().WithCancellation(linkedCts.Token);
            ThrowOnError(webRequest, body);
        }

        public int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int cjkCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (IsCjk(text[i])) cjkCount++;
            }

            // CJK 约 1 字/Token，拉丁语系约 4 字符/Token
            int nonCjk = text.Length - cjkCount;
            return cjkCount + (int)(nonCjk * 0.25f) + 1;
        }

        private UnityWebRequest BuildRequest(string body, bool streaming)
        {
            var webRequest = new UnityWebRequest(ZString.Concat(baseUrl, "/chat/completions"), "POST");
            byte[] jsonBytes = Encoding.UTF8.GetBytes(body);
            webRequest.uploadHandler = new UploadHandlerRaw(jsonBytes);
            webRequest.uploadHandler.contentType = "application/json";
            if (!streaming)
                webRequest.timeout = k_timeoutSeconds;

            if (!string.IsNullOrEmpty(apiKey))
                webRequest.SetRequestHeader("Authorization", ZString.Concat("Bearer ", apiKey));

            return webRequest;
        }

        private static void ThrowOnError(UnityWebRequest webRequest, string body)
        {
            if (webRequest.result == UnityWebRequest.Result.Success) return;

            string errorBody = webRequest.downloadHandler?.text ?? "";
            string preview = body.Length > k_errorBodyPreview ? body.Substring(0, k_errorBodyPreview) : body;
            string errorMsg = string.IsNullOrEmpty(errorBody)
                ? ZString.Format("HTTP {0}: {1}", webRequest.responseCode, webRequest.error)
                : errorBody;

            Log.Error(nameof(OpenAIProvider),
                ZString.Format("{0}\n--- Request Body (preview) ---\n{1}", errorMsg, preview));
            throw new Exception(errorMsg);
        }

        private string BuildRequestBody(LLMRequest request, bool stream)
        {
            var body = new JObject
            {
                ["model"] = model,
                ["stream"] = stream,
                ["temperature"] = request.Temperature,
                ["max_tokens"] = request.MaxTokens
            };

            if (request.Tools != null && request.Tools.Count > 0)
            {
                body["tool_choice"] = request.ToolChoice.ToString().ToLower();
                body["tools"] = BuildToolsArray(request.Tools);
            }

            if (stream)
            {
                // 不声明 include_usage 的话流式响应不会回填 token 用量
                body["stream_options"] = new JObject { ["include_usage"] = true };
            }

            body["messages"] = BuildMessagesArray(request);
            return body.ToString(Formatting.None);
        }

        private static JArray BuildMessagesArray(LLMRequest request)
        {
            var messages = new JArray();

            string systemContent = request.BuildSystemContent();
            if (!string.IsNullOrEmpty(systemContent))
                messages.Add(new JObject { ["role"] = "system", ["content"] = systemContent });

            for (int i = 0; i < request.Messages.Count; i++)
            {
                var msg = request.Messages[i];
                if (msg == null) continue;

                var msgObj = new JObject
                {
                    ["role"] = msg.Role,
                    ["content"] = msg.Content
                };

                if (msg.Role == "tool" && !string.IsNullOrEmpty(msg.ToolCallId))
                    msgObj["tool_call_id"] = msg.ToolCallId;

                if (msg.ToolCalls != null && msg.ToolCalls.Length > 0)
                    msgObj["tool_calls"] = BuildToolCallsArray(msg.ToolCalls);

                messages.Add(msgObj);
            }

            return messages;
        }

        private static JArray BuildToolCallsArray(LLMToolCall[] toolCalls)
        {
            var array = new JArray();
            for (int i = 0; i < toolCalls.Length; i++)
            {
                var tc = toolCalls[i];
                if (tc == null) continue;
                array.Add(new JObject
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments
                    }
                });
            }
            return array;
        }

        private static JArray BuildToolsArray(List<LLMTool> tools)
        {
            var array = new JArray();
            for (int i = 0; i < tools.Count; i++)
            {
                var tool = tools[i];
                array.Add(new JObject
                {
                    ["type"] = tool.Type,
                    ["function"] = new JObject
                    {
                        ["name"] = tool.Function.Name,
                        ["description"] = tool.Function.Description,
                        ["parameters"] = ParseParametersSchema(tool.Function.Parameters)
                    }
                });
            }
            return array;
        }

        private static JToken ParseParametersSchema(string parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return new JObject { ["type"] = "object", ["properties"] = new JObject() };
            return JObject.Parse(parameters);
        }

        private LLMResponse ParseResponse(string responseBody)
        {
            var json = JObject.Parse(responseBody);
            var choices = json["choices"] as JArray;
            var choice = choices != null && choices.Count > 0 ? choices[0] : null;
            var message = Child(choice, "message");

            var response = new LLMResponse
            {
                Content = Child(message, "content")?.Value<string>() ?? "",
                FinishReason = Child(choice, "finish_reason")?.Value<string>() ?? ""
            };

            FillUsage(Child(json, "usage"), response);

            var toolCalls = Child(message, "tool_calls") as JArray;
            if (toolCalls != null)
            {
                foreach (var tc in toolCalls)
                {
                    var fn = Child(tc, "function");
                    response.ToolCalls.Add(new LLMToolCall
                    {
                        Id = Child(tc, "id")?.Value<string>() ?? "",
                        Name = Child(fn, "name")?.Value<string>() ?? "",
                        Arguments = Child(fn, "arguments")?.Value<string>() ?? ""
                    });
                }
            }

            return response;
        }

        /// <summary>
        /// 网关常把不适用的字段显式写成 JSON null，此时 token["k"] 返回 JValue(null) 而不是 C# null，
        /// 对它再取子节点就抛 "Cannot access child value on JValue"。所有链式取值一律走这里。
        /// </summary>
        private static JToken Child(JToken token, string key)
        {
            var value = (token as JObject)?[key];
            return value is null or { Type: JTokenType.Null } ? null : value;
        }

        private static void FillUsage(JToken usage, LLMResponse response)
        {
            if (usage == null) return;

            response.PromptTokens = Child(usage, "prompt_tokens")?.Value<int>() ?? 0;
            response.CompletionTokens = Child(usage, "completion_tokens")?.Value<int>() ?? 0;
            response.CacheHitTokens = ReadCacheHitTokens(usage);
            response.CacheMissTokens = Child(usage, "cache_miss_tokens")?.Value<int>() ?? 0;
        }

        /// <summary>缓存命中口径各家不一：OpenAI/DeepSeek 走 prompt_tokens_details.cached_tokens，其余兜底读 cache_hit_tokens</summary>
        private static int ReadCacheHitTokens(JToken usage)
        {
            int cached = Child(Child(usage, "prompt_tokens_details"), "cached_tokens")?.Value<int>() ?? 0;
            if (cached == 0)
                cached = Child(usage, "cache_hit_tokens")?.Value<int>() ?? 0;
            return cached;
        }

        // 复用 List，ParseStreamChunk 只在 ProcessLine 中同步调用
        private readonly List<LLMStreamChunk> _reusableChunkList = new(4);

        /// <summary>缓存 serializer：每条 chunk 都新建一个的话，省的垃圾又还回去了</summary>
        private static readonly JsonSerializer k_streamSerializer = JsonSerializer.CreateDefault();

        private List<LLMStreamChunk> ParseStreamChunk(string data)
        {
            _reusableChunkList.Clear();

            ChunkPayload payload;
            using (var reader = new JsonTextReader(new StringReader(data)))
                payload = k_streamSerializer.Deserialize<ChunkPayload>(reader);

            var choices = payload?.Choices;
            if (choices == null || choices.Length == 0)
            {
                // include_usage 的用量在收尾的独立事件里到达（choices 为空）
                if (payload?.Usage != null)
                {
                    var usage = payload.Usage;
                    _reusableChunkList.Add(new LLMStreamChunk("", default, false,
                        usage.PromptTokens, usage.CompletionTokens, ReadCacheHitTokens(usage)));
                }
                return _reusableChunkList;
            }

            var choice = choices[0];
            var delta = choice.Delta;
            if (delta == null) return _reusableChunkList;

            string contentDelta = delta.Content ?? "";
            bool hasToolCall = false;

            var toolCalls = delta.ToolCalls;
            if (toolCalls != null)
            {
                for (int i = 0; i < toolCalls.Length; i++)
                {
                    var tc = toolCalls[i];
                    var tcDelta = new ToolCallDelta(
                        tc.Index, tc.Id, tc.Function?.Name, tc.Function?.Arguments ?? "");

                    _reusableChunkList.Add(new LLMStreamChunk(contentDelta, tcDelta));
                    hasToolCall = true;
                    contentDelta = "";
                }
            }

            bool isDone = choice.FinishReason is "stop" or "tool_calls";

            if (!string.IsNullOrEmpty(contentDelta) || (!hasToolCall && isDone))
                _reusableChunkList.Add(new LLMStreamChunk(contentDelta, default, isDone));
            else if (isDone && _reusableChunkList.Count > 0)
            {
                var last = _reusableChunkList[^1];
                _reusableChunkList[^1] = new LLMStreamChunk(last.ContentDelta, last.ToolCallDelta, true);
            }

            return _reusableChunkList;
        }

        /// <summary>
        /// 流式 chunk 的线格式。用 DTO 而不是 JObject DOM：一条 chunk 建 DOM 要造 20 多个
        /// JProperty/JValue，这里只要 5~7 个对象，而 chunk 是逐 token 到达的。
        /// 附带把「网关把字段写成 JSON null」变成 null 字段，不再有对 JValue 取下级那类崩溃。
        /// </summary>
        private class ChunkPayload
        {
            [JsonProperty("choices")] public ChoicePayload[] Choices;
            [JsonProperty("usage")] public UsagePayload Usage;
        }

        private class ChoicePayload
        {
            [JsonProperty("delta")] public DeltaPayload Delta;
            [JsonProperty("finish_reason")] public string FinishReason;
        }

        private class DeltaPayload
        {
            [JsonProperty("content")] public string Content;
            [JsonProperty("tool_calls")] public ToolCallPayload[] ToolCalls;
        }

        private class ToolCallPayload
        {
            [JsonProperty("index")] public int Index;
            [JsonProperty("id")] public string Id;
            [JsonProperty("function")] public FunctionPayload Function;
        }

        private class FunctionPayload
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("arguments")] public string Arguments;
        }

        private class UsagePayload
        {
            [JsonProperty("prompt_tokens")] public int PromptTokens;
            [JsonProperty("completion_tokens")] public int CompletionTokens;
            [JsonProperty("cache_hit_tokens")] public int FlatCacheHitTokens;
            [JsonProperty("prompt_tokens_details")] public PromptDetailsPayload PromptTokensDetails;
        }

        private class PromptDetailsPayload
        {
            [JsonProperty("cached_tokens")] public int CachedTokens;
        }

        /// <summary>缓存命中口径两家一致：优先 prompt_tokens_details.cached_tokens，为 0 再读扁平的 cache_hit_tokens</summary>
        private static int ReadCacheHitTokens(UsagePayload usage)
        {
            int cached = usage.PromptTokensDetails?.CachedTokens ?? 0;
            return cached != 0 ? cached : usage.FlatCacheHitTokens;
        }

        private class SSEDownloadHandler : DownloadHandlerScript
        {
            private readonly System.Text.StringBuilder _buffer = new(4096);
            // 解码必须跨包：一个中文字 3 字节，包边界落在字节中间时 Encoding.UTF8.GetString
            // 会把半截序列变成 U+FFFD、续字节再变一次 ⇒ 台词与 tool 参数丢字。
            // Decoder 自己留不完整尾巴到下一包，只有 flush 才补替换符
            private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
            private readonly char[] _charBuf = new char[1024];
            private readonly Action<LLMStreamChunk> _onChunk;
            private readonly Func<string, List<LLMStreamChunk>> _parseChunk;
            private CancellationTokenSource _idleCts;
            private readonly int _idleTimeoutSeconds;

            public SSEDownloadHandler(
                Action<LLMStreamChunk> onChunk,
                Func<string, List<LLMStreamChunk>> parseChunk,
                int idleTimeoutSeconds = 60)
            {
                _onChunk = onChunk;
                _parseChunk = parseChunk;
                _idleTimeoutSeconds = idleTimeoutSeconds;
                _idleCts = new CancellationTokenSource();
                _idleCts.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSeconds));
            }

            public CancellationToken IdleToken
            {
                get
                {
                    // 局部捕获，避免 TOCTOU：检查与使用之间被 CompleteContent 置空
                    var cts = _idleCts;
                    return cts != null ? cts.Token : default;
                }
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0) return true;

                var idleCts = _idleCts;
                idleCts?.CancelAfter(TimeSpan.FromSeconds(_idleTimeoutSeconds));

                AppendDecoded(data, 0, dataLength, flush: false);

                while (TryExtractLine(out string line))
                    ProcessLine(line);

                return true;
            }

            private void AppendDecoded(byte[] data, int offset, int count, bool flush)
            {
                while (count > 0 || flush)
                {
                    _decoder.Convert(data, offset, count, _charBuf, 0, _charBuf.Length, flush,
                        out int bytesUsed, out int charsUsed, out _);
                    if (charsUsed > 0)
                        _buffer.Append(_charBuf, 0, charsUsed);

                    offset += bytesUsed;
                    count -= bytesUsed;

                    // 没有推进就是 Decoder 在等下一包的续字节，别再转一圈
                    if (bytesUsed == 0 && charsUsed == 0) break;
                }
            }

            /// <summary>从 buffer 头部截取一行，避免 ToString 整个缓冲区</summary>
            private bool TryExtractLine(out string line)
            {
                line = null;
                for (int i = 0; i < _buffer.Length; i++)
                {
                    if (_buffer[i] != '\n') continue;
                    line = _buffer.ToString(0, i).TrimEnd('\r');
                    _buffer.Remove(0, i + 1);
                    return true;
                }
                return false;
            }

            protected override void CompleteContent()
            {
                var cts = _idleCts;
                if (cts != null)
                {
                    cts.Dispose();
                    _idleCts = null;
                }

                // 收尾时把 Decoder 里剩的半截序列也倒出来（正常应为空；不 flush 就会静默吞掉最后一个字）
                AppendDecoded(Array.Empty<byte>(), 0, 0, flush: true);

                if (_buffer.Length > 0)
                {
                    ProcessLine(_buffer.ToString());
                    _buffer.Clear();
                }
            }

            private void ProcessLine(string line)
            {
                if (string.IsNullOrEmpty(line)) return;

                // SSE 允许 "data:{...}" 不带空格，只认带空格的那一种会整轮零 token 且不留一句错
                if (!line.StartsWith("data:")) return;

                string data = line.Substring(5);
                if (data.Length > 0 && data[0] == ' ')
                    data = data.Substring(1);

                if (data == "[DONE]")
                {
                    _onChunk(new LLMStreamChunk("", default, true));
                    return;
                }

                List<LLMStreamChunk> chunks;
                try
                {
                    chunks = _parseChunk(data);
                }
                catch (Exception ex)
                {
                    // try 只包"解析"：_onChunk 是消费端（内核攒字、宿主刷 UI），
                    // 把它一起包进来会把消费端异常记成"chunk 解析失败"并静默吞掉一个 token，
                    // 排障方向整个被带偏。一行坏数据也不该让整轮作废，所以解析失败只跳过这一行。
                    Log.Warning(nameof(OpenAIProvider),
                        ZString.Format("流式 chunk 解析失败: {0}", ex.Message));
                    return;
                }

                if (chunks == null) return;

                for (int i = 0; i < chunks.Count; i++)
                    _onChunk(chunks[i]);
            }
        }

        private static CancellationTokenSource LinkCancellationToken(
            CancellationToken caller, CancellationToken idle)
        {
            if (caller.CanBeCanceled && idle.CanBeCanceled)
                return CancellationTokenSource.CreateLinkedTokenSource(caller, idle);
            if (caller.CanBeCanceled)
                return CancellationTokenSource.CreateLinkedTokenSource(caller);
            if (idle.CanBeCanceled)
                return CancellationTokenSource.CreateLinkedTokenSource(idle);
            return new CancellationTokenSource();
        }

        private static bool IsCjk(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF) ||
                   (c >= 0x3400 && c <= 0x4DBF) ||
                   (c >= 0x3000 && c <= 0x303F) ||
                   (c >= 0xF900 && c <= 0xFAFF);
        }
    }
}
