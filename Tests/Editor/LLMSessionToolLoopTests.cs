/*
 * LLM Tests — LLMSession 工具往返注入点
 * 覆盖 AbortTurn 收尾、writeHistory、ExtraTools 声明隔离、无次数总闸
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime;
using NUnit.Framework;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class LLMSessionToolLoopTests
    {
        private const string k_provider = "test_session_tool_loop";
        private const string k_streamProvider = "test_session_tool_loop_stream";

        private readonly List<LLMStreamChunk> forwarded = new();
        private FakeToolProvider provider;

        [SetUp]
        public void SetUp()
        {
            forwarded.Clear();
            provider = new FakeToolProvider(k_provider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(provider);
            dispatcher.SetDefaultProvider(k_provider);
        }

        [TearDown]
        public void TearDown()
        {
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.UnregisterProvider(k_provider);
            dispatcher.SetDefaultProvider(null);
            dispatcher.SetFallbackProvider(null);
        }

        [Test]
        public async Task AskAsync_ToolAborts_SkipsSameBatchAndStopsRequesting()
        {
            provider.EnqueueToolCalls("drop_item", "pick_item");
            provider.EnqueueAnswer("不该被请求到");

            var executor = new FakeExecutor { AbortOn = "drop_item" };
            var session = NewSession(executor);

            await session.AskAsync("做点什么", writeHistory: false);

            Assert.AreEqual(1, provider.RequestCount, "AbortTurn 后不得再发下一次请求");
            Assert.AreEqual(1, executor.Executed.Count, "同批剩余 tool_call 不执行");
            Assert.AreEqual("drop_item", executor.Executed[0]);
            Assert.AreEqual(0, session.Context.RoundCount, "writeHistory=false 时内核自己决定落不落历史");
        }

        [Test]
        public async Task AskAsync_ToolBatch_BackfillsOneToolMessagePerCall()
        {
            provider.EnqueueToolCalls("a", "b");
            provider.EnqueueAnswer("完成");

            var session = NewSession(new FakeExecutor());
            await session.AskAsync("两件都查");

            var messages = provider.Requests[provider.RequestCount - 1].Messages;
            int toolMessages = 0;
            for (int i = 0; i < messages.Count; i++)
                if (messages[i].Role == "tool") toolMessages++;

            Assert.AreEqual(2, toolMessages, "缺一条 tool 回填就会 dangling");
            Assert.AreEqual(1, session.Context.RoundCount, "不传 writeHistory 时仍按老行为落历史");
        }

        [Test]
        public async Task AskAsync_Streaming_AssemblesSplitToolCallAndKeepsBothRoundsText()
        {
            // 内核走的是流式：文本跨往返累积、tool_call 分片拼装、usage 落在独立 chunk 上
            var streamProvider = new FakeStreamingToolProvider(k_streamProvider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(streamProvider);
            dispatcher.SetDefaultProvider(k_streamProvider);

            try
            {
                var executor = new FakeExecutor();
                var session = NewSession(executor);

                string answer = await session.AskAsync("查一下", CollectChunk);

                Assert.AreEqual(2, streamProvider.RequestCount);
                Assert.AreEqual(1, executor.Executed.Count);
                Assert.AreEqual("look", executor.Executed[0]);
                Assert.AreEqual("好的，我看一下。东西在桌上", answer);

                int tokens = 0;
                for (int i = 0; i < forwarded.Count; i++)
                    if (!string.IsNullOrEmpty(forwarded[i].ContentDelta)) tokens++;
                Assert.AreEqual(3, tokens, "只把 content 增量转给宿主");
            }
            finally
            {
                dispatcher.UnregisterProvider(k_streamProvider);
                dispatcher.SetDefaultProvider(k_provider);
            }
        }

        [Test]
        public async Task AskAsync_ManyToolRounds_NotCutBySessionBudget()
        {
            // ADR-023：LLMSession 不再有 MaxToolRounds；模型一直回 tool_calls 时会继续请求直到给出正文
            provider.EnqueueToolCalls("a");
            provider.EnqueueToolCalls("b");
            provider.EnqueueAnswer("做完");

            var executor = new FakeExecutor();
            var session = NewSession(executor);
            string answer = await session.AskAsync("连续查");

            Assert.AreEqual(3, provider.RequestCount);
            Assert.AreEqual(2, executor.Executed.Count);
            Assert.AreEqual("做完", answer);
        }

        [Test]
        public async Task ExtraTools_AreDeclaredOnceAndNeverEnterToolRegistry()
        {
            var session = NewSession(new FakeExecutor());
            session.ExtraTools.Add(new LLMTool("act_walk", "走过去", "{}"));

            provider.EnqueueAnswer("一");
            await session.AskAsync("a");
            provider.EnqueueAnswer("二");
            await session.AskAsync("b");

            Assert.AreEqual(1, CountTool(provider.Requests[0], "act_walk"));
            Assert.AreEqual(1, CountTool(provider.Requests[1], "act_walk"),
                "ToLLMTools 返回的是注册表缓存 List，追加前必须复制否则逐轮累积");
            Assert.IsFalse(AgentToolRegistry.TryGet("act_walk", out _), "动作声明不得进 AgentToolRegistry");
        }

        [Test]
        public async Task EnableToolsFalse_HidesRegistryToolsAndExtraTools()
        {
            var session = NewSession(new FakeExecutor());
            session.ExtraTools.Add(new LLMTool("act_walk", "走过去", "{}"));
            session.EnableTools = false;

            provider.EnqueueAnswer("一");
            await session.AskAsync("a");

            Assert.AreEqual(0, provider.Requests[0].Tools.Count);
        }

        private void CollectChunk(LLMStreamChunk chunk) => forwarded.Add(chunk);

        private static int CountTool(LLMRequest request, string toolName)
        {
            int hit = 0;
            for (int i = 0; i < request.Tools.Count; i++)
                if (request.Tools[i].Function.Name == toolName) hit++;
            return hit;
        }

        private LLMSession NewSession(IAgentToolExecutor executor)
        {
            return new LLMSession(ZString.Concat(k_provider, "_session"))
            {
                ToolExecutor = executor
            };
        }

        private sealed class FakeExecutor : IAgentToolExecutor
        {
            public string AbortOn;
            public readonly List<string> Executed = new();

            public UniTask<AgentToolExecutionResult> ExecuteAsync(string name, string argsJson, CancellationToken ct)
            {
                Executed.Add(name);
                bool abort = name == AbortOn;
                return new UniTask<AgentToolExecutionResult>(
                    new AgentToolExecutionResult(abort ? "[Blocked]" : "ok", abort));
            }
        }

        private sealed class FakeToolProvider : ILLMProvider
        {
            private readonly List<LLMResponse> script = new();

            public FakeToolProvider(string name)
            {
                ProviderName = name;
            }

            public string ProviderName { get; }
            public bool IsAvailable => true;
            public bool SupportsStreaming => false;
            public bool SupportsToolCalling => true;

            public int RequestCount { get; private set; }
            public readonly List<LLMRequest> Requests = new();

            public void EnqueueAnswer(string content)
            {
                script.Add(new LLMResponse { Content = content });
            }

            public void EnqueueToolCalls(params string[] names)
            {
                var response = new LLMResponse();
                for (int i = 0; i < names.Length; i++)
                {
                    response.ToolCalls.Add(new LLMToolCall
                    {
                        Id = ZString.Concat("call_", i),
                        Name = names[i],
                        Arguments = "{}"
                    });
                }
                script.Add(response);
            }

            public void Clear()
            {
                script.Clear();
                Requests.Clear();
                RequestCount = 0;
            }

            public UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct)
            {
                Requests.Add(request);
                RequestCount++;

                // 脚本用尽后重复最后一条，用来模拟模型一直不肯收口
                int index = Math.Min(RequestCount - 1, script.Count - 1);
                return new UniTask<LLMResponse>(script[index]);
            }

            public UniTask CompleteStreamAsync(LLMRequest request,
                Action<LLMStreamChunk> onChunk, CancellationToken ct)
                => throw new NotSupportedException();

            public int EstimateTokens(string text) => 0;
        }

        private sealed class FakeStreamingToolProvider : ILLMProvider
        {
            public FakeStreamingToolProvider(string name)
            {
                ProviderName = name;
            }

            public string ProviderName { get; }
            public bool IsAvailable => true;
            public bool SupportsStreaming => true;
            public bool SupportsToolCalling => true;

            public int RequestCount { get; private set; }

            public UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct)
                => throw new NotSupportedException();

            public UniTask CompleteStreamAsync(LLMRequest request,
                Action<LLMStreamChunk> onChunk, CancellationToken ct)
            {
                RequestCount++;

                if (RequestCount == 1)
                {
                    onChunk(new LLMStreamChunk("好的，"));
                    onChunk(new LLMStreamChunk("我看一下。"));
                    onChunk(new LLMStreamChunk("", new ToolCallDelta(0, "call_1", "look", "{\"key\":")));
                    onChunk(new LLMStreamChunk("", new ToolCallDelta(0, null, null, "\"desk\"}")));
                    onChunk(new LLMStreamChunk("", default, false, 20, 5, 12));
                }
                else
                {
                    onChunk(new LLMStreamChunk("东西在桌上"));
                }

                onChunk(new LLMStreamChunk("", default, true));
                return default;
            }

            public int EstimateTokens(string text) => 0;
        }
    }
}
