/*
 * ┌────────────────────────────────────────────┐
 * │ Description : AgentHost 场景宿主测试         │
 * │ Remark      : 生命周期、输入转发与核心快照    │
 * │ ClassName   : AgentHostTests                 │
 * └────────────────────────────────────────────┘
 */

using LLM.Runtime;
using LLM.Runtime.Agent;
using NUnit.Framework;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.TestTools;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentHostTests
    {
        private const string k_provider = "test_agent_host";

        private FakeKernelProvider provider;
        private AgentProfile_SO profile;
        private GameObject gameObject;
        private AgentHost host;
        private string receivedToken;
        private string finishedAnswer;
        private EAgentOutcome finishedOutcome;
        private IAgentOutput currentOutput;
        private bool lastActiveState;
        private int activeStateChangeCount;

        [SetUp]
        public void SetUp()
        {
            provider = new FakeKernelProvider(k_provider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(provider);
            dispatcher.SetDefaultProvider(k_provider);

            profile = FakeProfiles.Create("host");
            gameObject = new GameObject("AgentHostTest");
            gameObject.SetActive(false);
            host = gameObject.AddComponent<AgentHost>();
            host.Activate(profile);
            host.TokenReceived += OnTokenReceived;
            host.TurnFinished += OnTurnFinished;
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null)
            {
                host.TokenReceived -= OnTokenReceived;
                host.TurnFinished -= OnTurnFinished;
                host.Deactivate();
            }

            if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
            FakeProfiles.Destroy(profile);

            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.UnregisterProvider(k_provider);
            dispatcher.SetDefaultProvider(null);
            dispatcher.SetFallbackProvider(null);
        }

        [Test]
        public void Activate_CreatesSingleCoreAndExposesState()
        {
            Assert.IsTrue(host.IsActive);
            Assert.AreSame(profile, host.Profile);
            Assert.IsNotNull(host.Core);
        }

        [Test]
        public void Trigger_ForwardsCoreOutputToHostEvents()
        {
            provider.EnqueueAnswer("你好");

            host.Trigger("hello");

            Assert.AreEqual("你好", receivedToken);
            Assert.AreEqual("你好", finishedAnswer);
            Assert.AreEqual(EAgentOutcome.Completed, finishedOutcome);
        }

        [Test]
        public void Notify_AddsEventPrefixAndSetSnapshotInjectsContext()
        {
            provider.EnqueueAnswer("收到");
            host.SetCoreSnapshot("晴天");

            host.Notify("门开了");

            Assert.AreEqual("【事件】门开了", provider.Requests[0].Messages[0].Content);
            Assert.AreEqual("晴天", provider.Requests[0].ContextBlocks[0]);
        }

        [Test]
        public void Deactivate_ReleasesCoreAndIgnoresFurtherInput()
        {
            host.Deactivate();
            provider.EnqueueAnswer("不应发送");

            host.Trigger("hello");

            Assert.IsFalse(host.IsActive);
            Assert.IsNull(host.Core);
            Assert.AreEqual(0, provider.RequestCount);
        }

        [Test]
        public void ActiveChanged_ReportsActivationAndDeactivation()
        {
            host.ActiveChanged += OnActiveChanged;

            host.Deactivate();
            host.Activate(profile);

            Assert.AreEqual(2, activeStateChangeCount);
            Assert.IsTrue(lastActiveState);
            host.ActiveChanged -= OnActiveChanged;
        }

        [Test]
        public void Reactivate_IgnoresOutputFromPreviousCore()
        {
            var previousOutput = GetOutput(host.Core);
            var nextProfile = FakeProfiles.Create("next-host");
            host.Activate(nextProfile);
            receivedToken = null;

            previousOutput.OnToken("迟到输出");
            GetOutput(host.Core).OnToken("当前输出");

            Assert.AreEqual("当前输出", receivedToken);
            FakeProfiles.Destroy(nextProfile);
        }

        [Test]
        public void OutputSubscriberException_DoesNotEscapeToCore()
        {
            host.TokenReceived += ThrowOnToken;
            currentOutput = GetOutput(host.Core);
            LogAssert.Expect(LogType.Error,
                "<b>[AgentHost]</b> TokenReceived 订阅者异常: test subscriber");

            Assert.DoesNotThrow(InvokeCurrentOutput);

            host.TokenReceived -= ThrowOnToken;
        }

        private void OnTokenReceived(string token)
        {
            receivedToken = token;
        }

        private void OnTurnFinished(string answer, EAgentOutcome outcome)
        {
            finishedAnswer = answer;
            finishedOutcome = outcome;
        }

        private void OnActiveChanged(bool active)
        {
            lastActiveState = active;
            activeStateChangeCount++;
        }

        private static void ThrowOnToken(string token)
        {
            throw new InvalidOperationException("test subscriber");
        }

        private void InvokeCurrentOutput()
        {
            currentOutput.OnToken("token");
        }

        private static IAgentOutput GetOutput(AgentCore core)
        {
            var field = typeof(AgentCore).GetField("output", BindingFlags.Instance | BindingFlags.NonPublic);
            return (IAgentOutput)field.GetValue(core);
        }
    }
}
