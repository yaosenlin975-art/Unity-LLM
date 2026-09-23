/*
 * LLM Tests — LLMDispatcher Provider 注册表并发测试
 * 覆盖 RegisterProvider/UnregisterProvider/GetProvider 在并发下的字典安全
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Cysharp.Threading.Tasks;
using LLM.Runtime;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class LLMDispatcherLockTests
    {
        [Test]
        public async Task ConcurrentRegisterAndGet_DoesNotThrow()
        {
            var dispatcher = LLMDispatcher.GetInstance();
            var errors = new ConcurrentQueue<Exception>();
            var tasks = new List<Task>();

            for (int i = 0; i < 8; i++)
            {
                int reader = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (int j = 0; j < 200; j++)
                            _ = dispatcher.GetProvider($"reader_{reader}");
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }));
            }

            for (int i = 0; i < 4; i++)
            {
                int writer = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (int j = 0; j < 50; j++)
                        {
                            string name = $"writer_{writer}_{j}";
                            dispatcher.RegisterProvider(new FakeProvider(name));
                            dispatcher.UnregisterProvider(name);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Assert.IsEmpty(errors, "并发读写 Provider 注册表不应抛异常");
        }

        [Test]
        public void GetProvider_FallsBackWhenDefaultMissing()
        {
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(new FakeProvider("only_fallback"));
            dispatcher.SetFallbackProvider("only_fallback");

            try
            {
                Assert.IsNotNull(dispatcher.GetProvider("not_registered"),
                    "默认 Provider 缺失时应回落到 fallback");
            }
            finally
            {
                dispatcher.SetFallbackProvider(null);
                dispatcher.UnregisterProvider("only_fallback");
            }
        }

        private sealed class FakeProvider : ILLMProvider
        {
            public FakeProvider(string name)
            {
                ProviderName = name;
            }

            public string ProviderName { get; }
            public bool IsAvailable => true;
            public bool SupportsStreaming => false;
            public bool SupportsToolCalling => false;

            public UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct)
                => new(new LLMResponse { Content = "" });

            public UniTask CompleteStreamAsync(LLMRequest request,
                Action<LLMStreamChunk> onChunk, CancellationToken ct) => default;

            public int EstimateTokens(string text) => 0;
        }
    }
}
