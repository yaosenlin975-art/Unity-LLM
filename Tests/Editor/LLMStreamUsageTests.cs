/*
 * LLM Tests — 流式用量日志（A 步）
 * 验证 usage 落在 choices 为空的独立 chunk 上时，dispatcher 仍能跨 chunk 缓存并只发一次日志
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using LLM.Runtime;
using NUnit.Framework;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class LLMStreamUsageTests
    {
        private const string k_provider = "test_stream_usage";
        private const int k_promptTokens = 1234;
        private const int k_completionTokens = 56;
        private const int k_cacheHitTokens = 1024;

        private readonly List<LLMRequestLog> logs = new();
        private readonly List<LLMStreamChunk> chunks = new();

        [SetUp]
        public void SetUp()
        {
            logs.Clear();
            chunks.Clear();
            LLMDispatcher.OnRequestCompleted += HandleLog;
        }

        [TearDown]
        public void TearDown()
        {
            LLMDispatcher.OnRequestCompleted -= HandleLog;

            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.UnregisterProvider(k_provider);
            dispatcher.SetDefaultProvider(null);
            dispatcher.SetFallbackProvider(null);
        }

        private void HandleLog(LLMRequestLog log) => logs.Add(log);

        private void HandleChunk(LLMStreamChunk chunk) => chunks.Add(chunk);

        [Test]
        public async Task EnqueueStreamAsync_UsageOnSeparateChunk_EmitsOneLogWithCachedTokens()
        {
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(new FakeStreamProvider(k_provider));
            dispatcher.SetDefaultProvider(k_provider);

            await dispatcher.EnqueueStreamAsync(
                new LLMRequest { SessionId = "s1" }, HandleChunk, CancellationToken.None);

            Assert.AreEqual(1, logs.Count, "一次流式请求只发一条日志");
            Assert.AreEqual(k_promptTokens, logs[0].PromptTokens);
            Assert.AreEqual(k_completionTokens, logs[0].CompletionTokens);
            Assert.AreEqual(k_cacheHitTokens, logs[0].CacheHitTokens,
                "usage 在独立 chunk 上，必须跨 chunk 缓存后才取得到");
            Assert.IsFalse(logs[0].IsError);
            Assert.AreEqual("你好", logs[0].ContentPreview);

            var last = chunks[chunks.Count - 1];
            Assert.IsTrue(last.IsDone);
            Assert.AreEqual(0, last.PromptTokens + last.CompletionTokens + last.CacheHitTokens,
                "收尾 isDone chunk 本身不带 token，日志不能只在它上面取值");
        }

        [Test]
        public async Task EnqueueStreamAsync_ToolCallDelta_LogsToolNameWithoutError()
        {
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(new FakeStreamProvider(k_provider, emitToolCall: true));
            dispatcher.SetDefaultProvider(k_provider);

            await dispatcher.EnqueueStreamAsync(
                new LLMRequest { SessionId = "s1" }, HandleChunk, CancellationToken.None);

            Assert.AreEqual(1, logs.Count);
            Assert.IsTrue(logs[0].HasToolCall);
            Assert.AreEqual("observe_room", logs[0].ToolNames);
        }

        [Test]
        public async Task EnqueueStreamAsync_ProviderFails_EmitsSingleErrorLog()
        {
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(new FakeStreamProvider(k_provider, shouldFail: true));
            dispatcher.SetDefaultProvider(k_provider);

            bool threw = false;
            try
            {
                await dispatcher.EnqueueStreamAsync(
                    new LLMRequest { SessionId = "s1" }, HandleChunk, CancellationToken.None);
            }
            catch (Exception)
            {
                threw = true;
            }

            Assert.IsTrue(threw, "无 fallback 时异常应继续抛出");
            Assert.AreEqual(1, logs.Count, "失败的流式请求也要发一条日志，否则统计里凭空少一次请求");
            Assert.IsTrue(logs[0].IsError);
            StringAssert.Contains("fake stream failure", logs[0].ErrorMessage);
        }

        [Test]
        public void ParseStreamChunk_UsageOnlyEvent_CarriesCacheHitTokens()
        {
            // 上面三条测的是 dispatcher 的缓存逻辑；真链路的取数点在 provider 的解析里，
            // cached_tokens 一旦没读出来，日志窗口里的命中率仍是恒 0
            var provider = new OpenAIProvider("test_key", "test_model");
            const string usageEvent =
                "{\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3," +
                "\"prompt_tokens_details\":{\"cached_tokens\":5}}}";

            var parse = typeof(OpenAIProvider).GetMethod("ParseStreamChunk",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var chunks = (List<LLMStreamChunk>)parse.Invoke(provider, new object[] { usageEvent });

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(7, chunks[0].PromptTokens);
            Assert.AreEqual(3, chunks[0].CompletionTokens);
            Assert.AreEqual(5, chunks[0].CacheHitTokens);
            Assert.IsFalse(chunks[0].IsDone);
        }

        private sealed class FakeStreamProvider : ILLMProvider
        {
            private readonly bool shouldFail;
            private readonly bool emitToolCall;

            public FakeStreamProvider(string name, bool shouldFail = false, bool emitToolCall = false)
            {
                ProviderName = name;
                this.shouldFail = shouldFail;
                this.emitToolCall = emitToolCall;
            }

            public string ProviderName { get; }
            public bool IsAvailable => true;
            public bool SupportsStreaming => true;
            public bool SupportsToolCalling => true;

            public UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct)
                => throw new NotSupportedException();

            public UniTask CompleteStreamAsync(LLMRequest request,
                Action<LLMStreamChunk> onChunk, CancellationToken ct)
            {
                if (shouldFail)
                    throw new Exception("fake stream failure");

                onChunk(new LLMStreamChunk("你"));
                onChunk(new LLMStreamChunk("好"));

                if (emitToolCall)
                {
                    var delta = new ToolCallDelta(0, "call_1", "observe_room", "{\"x\":1}");
                    onChunk(new LLMStreamChunk("", delta));
                }

                onChunk(new LLMStreamChunk("", default, false,
                    k_promptTokens, k_completionTokens, k_cacheHitTokens));
                onChunk(new LLMStreamChunk("", default, true));
                return default;
            }

            public int EstimateTokens(string text) => 0;
        }
    }
}
