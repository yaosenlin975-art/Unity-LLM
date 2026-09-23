/*
 * ┌────────────────────────────────────────────┐
 * │ Description : AgentHost 工具门控测试         │
 * │ Remark      : 覆盖式语义、声明层过滤与执行期  │
 * │               拒绝三层都验一遍               │
 * │ ClassName   : AgentHostToolGateTests        │
 * └────────────────────────────────────────────┘
 */

using System.Collections.Generic;
using LLM.Runtime;
using LLM.Runtime.Agent;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentHostToolGateTests
    {
        private const string k_provider = "test_agent_tool_gate";

        private FakeKernelProvider provider;
        private AgentProfile_SO profile;
        private GameObject gameObject;
        private AgentHost host;
        private EAgentOutcome finishedOutcome;

        [SetUp]
        public void SetUp()
        {
            provider = new FakeKernelProvider(k_provider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(provider);
            dispatcher.SetDefaultProvider(k_provider);

            profile = FakeProfiles.Create("gate");

            // 先失活再加组件：否则 OnEnable 会用空 profile 触发一次错误日志
            gameObject = new GameObject("AgentHostToolGateTest");
            gameObject.SetActive(false);
            host = gameObject.AddComponent<AgentHost>();
            host.Activate(profile);
            host.TurnFinished += OnTurnFinished;
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null)
            {
                host.TurnFinished -= OnTurnFinished;
                host.Deactivate();
            }

            if (gameObject != null) Object.DestroyImmediate(gameObject);
            FakeProfiles.Destroy(profile);

            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.UnregisterProvider(k_provider);
            dispatcher.SetDefaultProvider(null);
            dispatcher.SetFallbackProvider(null);
        }

        [Test]
        public void IsToolEnabled_UnlistedDefaultsTrue()
        {
            Assert.IsTrue(host.IsToolEnabled("write_fact"), "未收录的工具默认可用");
            Assert.IsTrue(host.IsToolEnabled("never_registered_tool"));
        }

        [Test]
        public void SetToolEnabled_DisablesListedTool_WithoutAffectingOthers()
        {
            host.SetToolEnabled("search_past_conversation", false);

            Assert.IsFalse(host.IsToolEnabled("search_past_conversation"));
            Assert.IsTrue(host.IsToolEnabled("write_fact"), "其它工具不受影响");

            host.SetToolEnabled("search_past_conversation", true);
            Assert.IsTrue(host.IsToolEnabled("search_past_conversation"));
        }

        [Test]
        public void MergeToolToggles_PreservesExistingEnabled_AndAddsNewAsEnabled()
        {
            host.SetToolEnabled("search_past_conversation", false);

            var candidates = new List<AgentToolToggle>
            {
                new AgentToolToggle { ToolId = "search_past_conversation", Source = EAgentToolSource.Action },
                new AgentToolToggle { ToolId = "write_fact", Source = EAgentToolSource.Action }
            };

            host.MergeToolToggles(candidates, removeMissing: true);

            Assert.AreEqual(2, host.ToolToggles.Count);
            Assert.IsFalse(host.IsToolEnabled("search_past_conversation"), "既有条目的 Enabled 必须保留");
            Assert.IsTrue(host.IsToolEnabled("write_fact"), "新增项默认可用");
        }

        [Test]
        public void Request_ExcludesDisabledTools_AndKeepsOthers()
        {
            host.SetToolEnabled("search_past_conversation", false);
            provider.EnqueueAnswer("好");

            host.Trigger("hi");

            Assert.AreEqual(1, provider.RequestCount);
            List<LLMTool> tools = provider.Requests[0].Tools;
            Assert.IsTrue(HasTool(tools, "write_fact"), "未关闭的工具应保留在 request.Tools");
            Assert.IsFalse(HasTool(tools, "search_past_conversation"), "被关闭的工具不得进 request.Tools");
        }

        [Test]
        public void DisabledTool_IsRejectedAtExecutionTime()
        {
            host.SetToolEnabled("search_past_conversation", false);
            provider.EnqueueToolCalls(new FakeCall("search_past_conversation", "{\"query\":\"旧事\"}"));
            provider.EnqueueAnswer("换个说法");

            host.Trigger("查记忆");

            Assert.AreEqual(2, provider.RequestCount, "拒绝后仍要回填并续发第二次请求收尾");
            Assert.IsTrue(HasToolErrorResult(provider.Requests[1]), "被关闭的工具硬调时应回 [Tool Error]");
            Assert.AreEqual(EAgentOutcome.Completed, finishedOutcome);
        }

        private void OnTurnFinished(string answer, EAgentOutcome outcome)
        {
            finishedOutcome = outcome;
        }

        private static bool HasTool(List<LLMTool> tools, string name)
        {
            for (int i = 0; i < tools.Count; i++)
                if (tools[i].Function.Name == name) return true;
            return false;
        }

        private static bool HasToolErrorResult(LLMRequest request)
        {
            for (int i = 0; i < request.Messages.Count; i++)
            {
                var message = request.Messages[i];
                if (message.Role == "tool" && message.Content != null
                    && message.Content.Contains("[Tool Error]"))
                    return true;
            }

            return false;
        }
    }
}
