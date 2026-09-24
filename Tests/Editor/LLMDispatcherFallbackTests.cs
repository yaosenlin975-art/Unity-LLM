/*
 * LLM Tests — LLMDispatcher Fallback 降级日志测试
 * 验证 B4 修复：默认 Provider 失败降级到 fallback provider 时记录日志
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using LLM.Runtime;
using Cysharp.Threading.Tasks;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class LLMDispatcherFallbackTests
    {
        private const string k_defaultProviderName = "test_failing_default";
        private const string k_fallbackProviderName = "test_fallback_ok";
        private const string k_expectedContent = "fallback response";
        private const string k_failMessage = "fake failure";

        [Test]
        public async Task EnqueueAsync_DefaultFails_LogsFallbackAndUsesFallbackProvider()
        {
            var dispatcher = LLMDispatcher.GetInstance();

            var failing = new FakeProvider(k_defaultProviderName, shouldFail: true);
            var fallback = new FakeProvider(k_fallbackProviderName, shouldFail: false,
                content: k_expectedContent);

            dispatcher.RegisterProvider(failing);
            dispatcher.RegisterProvider(fallback);
            dispatcher.SetDefaultProvider(k_defaultProviderName);
            dispatcher.SetFallbackProvider(k_fallbackProviderName);

            var logs = new List<string>();
            void LogHandler(string condition, string stackTrace, LogType type)
            {
                if (condition.Contains("降级到"))
                    logs.Add(condition);
            }

            Application.logMessageReceived += LogHandler;
            try
            {
                var request = new LLMRequest { SessionId = "test_session" };
                LLMResponse response = await dispatcher.EnqueueAsync(request, CancellationToken.None);

                Assert.AreEqual(k_expectedContent, response.Content,
                    "应该使用 fallback provider 的响应");
                Assert.IsNotEmpty(logs,
                    "默认 Provider 失败降级时应记录日志");
                StringAssert.Contains(k_defaultProviderName, logs[0],
                    "日志应包含失败的 provider 名");
                StringAssert.Contains(k_fallbackProviderName, logs[0],
                    "日志应包含 fallback provider 名");
                StringAssert.Contains(k_failMessage, logs[0],
                    "日志应包含异常消息");
            }
            finally
            {
                Application.logMessageReceived -= LogHandler;
                dispatcher.UnregisterProvider(k_defaultProviderName);
                dispatcher.UnregisterProvider(k_fallbackProviderName);
                dispatcher.SetDefaultProvider(null);
                dispatcher.SetFallbackProvider(null);
            }
        }

        [Test]
        public async Task EnqueueStreamAsync_DefaultFails_LogsFallbackAndDeliversFallbackChunks()
        {
            var dispatcher = LLMDispatcher.GetInstance();

            var failing = new FakeProvider(k_defaultProviderName, shouldFail: true);
            var fallback = new FakeProvider(k_fallbackProviderName, shouldFail: false,
                content: k_expectedContent);

            dispatcher.RegisterProvider(failing);
            dispatcher.RegisterProvider(fallback);
            dispatcher.SetDefaultProvider(k_defaultProviderName);
            dispatcher.SetFallbackProvider(k_fallbackProviderName);

            var logs = new List<string>();
            var receivedChunks = new List<LLMStreamChunk>();

            void LogHandler(string condition, string stackTrace, LogType type)
            {
                if (condition.Contains("降级到"))
                    logs.Add(condition);
            }

            Application.logMessageReceived += LogHandler;
            try
            {
                var request = new LLMRequest { SessionId = "test_session" };
                await dispatcher.EnqueueStreamAsync(request,
                    chunk => receivedChunks.Add(chunk),
                    CancellationToken.None);

                Assert.IsNotEmpty(logs,
                    "流式默认 Provider 失败降级时应记录日志");
                StringAssert.Contains(k_defaultProviderName, logs[0],
                    "日志应包含失败的 provider 名");
                StringAssert.Contains(k_fallbackProviderName, logs[0],
                    "日志应包含 fallback provider 名");

                Assert.IsNotEmpty(receivedChunks,
                    "应通过 fallback provider 收到流式 chunk");
                Assert.AreEqual(k_expectedContent, receivedChunks[0].ContentDelta,
                    "fallback chunk 内容应匹配");
            }
            finally
            {
                Application.logMessageReceived -= LogHandler;
                dispatcher.UnregisterProvider(k_defaultProviderName);
                dispatcher.UnregisterProvider(k_fallbackProviderName);
                dispatcher.SetDefaultProvider(null);
                dispatcher.SetFallbackProvider(null);
            }
        }

        [Test]
        public async Task EnqueueAsync_DefaultSucceeds_NoFallbackLog()
        {
            var dispatcher = LLMDispatcher.GetInstance();

            var ok = new FakeProvider(k_defaultProviderName, shouldFail: false,
                content: k_expectedContent);
            var fallback = new FakeProvider(k_fallbackProviderName, shouldFail: false,
                content: "should_not_be_used");

            dispatcher.RegisterProvider(ok);
            dispatcher.RegisterProvider(fallback);
            dispatcher.SetDefaultProvider(k_defaultProviderName);
            dispatcher.SetFallbackProvider(k_fallbackProviderName);

            var logs = new List<string>();
            void LogHandler(string condition, string stackTrace, LogType type)
            {
                if (condition.Contains("降级到"))
                    logs.Add(condition);
            }

            Application.logMessageReceived += LogHandler;
            try
            {
                var request = new LLMRequest { SessionId = "test_session" };
                LLMResponse response = await dispatcher.EnqueueAsync(request, CancellationToken.None);

                Assert.AreEqual(k_expectedContent, response.Content,
                    "默认 Provider 成功时应直接使用其响应");
                Assert.IsEmpty(logs,
                    "默认 Provider 成功时不应记录降级日志");
            }
            finally
            {
                Application.logMessageReceived -= LogHandler;
                dispatcher.UnregisterProvider(k_defaultProviderName);
                dispatcher.UnregisterProvider(k_fallbackProviderName);
                dispatcher.SetDefaultProvider(null);
                dispatcher.SetFallbackProvider(null);
            }
        }

        [Test]
        public async Task EnqueueStreamAsync_DefaultAlreadyEmitted_DoesNotReplayOnFallback()
        {
            var dispatcher = LLMDispatcher.GetInstance();

            // 默认 Provider 先吐了一段字再断：那段字已经进了本轮正文与宿主气泡，
            // 换供应商重放等于把台词说两遍，所以此时必须让失败可见地抛出去
            var half = new FakeProvider(k_defaultProviderName, shouldFail: true,
                content: "half ", emitBeforeFail: true);
            var fallback = new FakeProvider(k_fallbackProviderName, shouldFail: false,
                content: k_expectedContent);

            dispatcher.RegisterProvider(half);
            dispatcher.RegisterProvider(fallback);
            dispatcher.SetDefaultProvider(k_defaultProviderName);
            dispatcher.SetFallbackProvider(k_fallbackProviderName);

            var receivedChunks = new List<LLMStreamChunk>();
            Exception thrown = null;
            try
            {
                try
                {
                    await dispatcher.EnqueueStreamAsync(new LLMRequest { SessionId = "test_session" },
                        chunk => receivedChunks.Add(chunk), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                Assert.IsNotNull(thrown, "吐过字后失败应把原异常抛出，而不是悄悄换供应商");
                Assert.AreEqual(1, receivedChunks.Count, "只该收到失败那轮已吐的一段，fallback 不得重放");
                Assert.AreEqual("half ", receivedChunks[0].ContentDelta);
            }
            finally
            {
                dispatcher.UnregisterProvider(k_defaultProviderName);
                dispatcher.UnregisterProvider(k_fallbackProviderName);
                dispatcher.SetDefaultProvider(null);
                dispatcher.SetFallbackProvider(null);
            }
        }

        private sealed class FakeProvider : ILLMProvider
        {
            private readonly bool shouldFail;
            private readonly bool emitBeforeFail;
            private readonly string content;

            public FakeProvider(string name, bool shouldFail, string content = "", bool emitBeforeFail = false)
            {
                ProviderName = name;
                this.shouldFail = shouldFail;
                this.content = content;
                this.emitBeforeFail = emitBeforeFail;
            }

            public string ProviderName { get; }
            public bool IsAvailable => true;
            public bool SupportsStreaming => true;
            public bool SupportsToolCalling => false;

            public UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct)
            {
                if (shouldFail)
                    throw new Exception(k_failMessage);
                return new UniTask<LLMResponse>(new LLMResponse { Content = content });
            }

            public UniTask CompleteStreamAsync(LLMRequest request,
                Action<LLMStreamChunk> onChunk, CancellationToken ct)
            {
                if (shouldFail)
                {
                    if (emitBeforeFail)
                        onChunk(new LLMStreamChunk(content, default, false));
                    throw new Exception(k_failMessage);
                }

                onChunk(new LLMStreamChunk(content, default, true));
                return default;
            }

            public int EstimateTokens(string text) => 0;
        }
    }
}
