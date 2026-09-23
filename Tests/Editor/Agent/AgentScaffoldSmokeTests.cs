/*
 * Agent 测试脚手架 — 烟测
 * 用全套脚手架驱动一轮同步收敛的假轮次：FakeKernelProvider 出 tool_calls →
 * FakeRunner 执行 → 第二次请求收尾。Trigger 返回时整轮必须已完成
 * （EditMode 没有帧循环，这是后续 T4 所有矩阵行的成立前提，故单独成测）
 */

using System;
using LLM.Runtime;
using LLM.Runtime.Agent;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentScaffoldSmokeTests
    {
        private const string k_provider = "test_agent_scaffold";

        private FakeKernelProvider provider;
        private AgentProfile_SO profile;
        private FakeOutput output;
        private FakeRunner runner;
        private AgentCore agent;

        [SetUp]
        public void SetUp()
        {
            provider = new FakeKernelProvider(k_provider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(provider);
            dispatcher.SetDefaultProvider(k_provider);

            FakeClock.ResetToStart();
            FakeClock.Install();

            profile = FakeProfiles.Create("scaffold");
            output = new FakeOutput();
            runner = new FakeRunner();
            AgentActionRegistry.Register(typeof(AgentScaffoldSmokeTests).Assembly);
            agent = new AgentCore(profile, "s1", null, output, runner);
        }

        [TearDown]
        public void TearDown()
        {
            agent.Dispose();
            FakeProfiles.Destroy(profile);
            FakeClock.Restore();

            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.UnregisterProvider(k_provider);
            dispatcher.SetDefaultProvider(null);
            dispatcher.SetFallbackProvider(null);
        }

        [Test]
        public void Smoke_FullRoundSyncConverge()
        {
            runner.Script("t_look", "桌上有一把钥匙");
            provider.EnqueueToolCalls(new FakeCall("t_look", "{\"x\":1}"));
            provider.EnqueueAnswer("东西在桌上");

            agent.Trigger("查一下");

            Assert.AreEqual(2, provider.RequestCount, "tool_calls 之后应续发第二次请求收尾");
            Assert.AreEqual(1, runner.ExecutedIds.Count, "动作执行一次");
            Assert.AreEqual("t_look", runner.ExecutedIds[0]);
            Assert.AreEqual("{\"x\":1}", runner.ExecutedArgs[0]);
            Assert.AreEqual(1, output.FinishCount);
            Assert.AreEqual(EAgentOutcome.Completed, output.LastOutcome);
            StringAssert.Contains("东西在桌上", output.LastAnswer);
            Assert.IsTrue(output.Events[0].StartsWith("TOKEN:"), "流式 token 先于 FINISH 上屏");
            Assert.AreEqual(1, agent.Session.Context.RoundCount, "完成轮要写入历史");
        }

        [Test]
        public void Smoke_ProviderFailure_FallsBackAndConverges()
        {
            provider.EnqueueFailure("模拟网络炸了");

            agent.Trigger("你好");

            Assert.AreEqual(1, provider.RequestCount);
            Assert.AreEqual(1, output.FinishCount);
            Assert.AreEqual(EAgentOutcome.Failed, output.LastOutcome);
            Assert.AreEqual("（测试兜底）这一轮没有拿到回复。", output.LastAnswer,
                "Provider 全挂时取 FallbackLines 播出");
            Assert.AreEqual(1, agent.Session.Context.RoundCount, "失败轮也要入历史");
        }
    }
}
