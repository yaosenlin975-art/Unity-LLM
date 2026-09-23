/*
 * ┌────────────────────────────────────────────┐
 * │ Description : 成员工具/动作集合测试          │
 * │ Remark      : 层级扫描、per-agent 收集、打  │
 * │               在实例上执行、全局静态可选纳入 │
 * │ ClassName   : AgentToolSetTests             │
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
    public class AgentToolSetTests
    {
        private const string k_provider = "test_agent_tool_set";

        private GameObject gameObject;
        private ScaffoldTestMemberTools member;

        [SetUp]
        public void SetUp()
        {
            gameObject = new GameObject("AgentToolSetTest");
            member = gameObject.AddComponent<ScaffoldTestMemberTools>();
        }

        [TearDown]
        public void TearDown()
        {
            if (gameObject != null) Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void ScanHierarchyTools_FindsMemberToolAndAction_WithoutTouchingGlobal()
        {
            var tools = AgentToolRegistry.ScanHierarchyTools(gameObject);
            var actions = AgentActionRegistry.ScanHierarchyTools(gameObject);

            Assert.IsTrue(HasTool(tools, "m_echo"), "层级扫描应发现成员工具");
            Assert.IsTrue(HasAction(actions, "m_act"), "层级扫描应发现成员动作");

            for (int i = 0; i < tools.Count; i++)
                if (tools[i].Name == "m_echo") Assert.AreSame(member, tools[i].Target, "成员工具要带 Target");

            Assert.IsFalse(AgentToolRegistry.TryGet("m_echo", out _), "成员工具不得进全局表");
        }

        [Test]
        public void Execute_MemberTool_RunsOnInstance()
        {
            var tools = AgentToolRegistry.ScanHierarchyTools(gameObject);
            AgentToolRegistry.RegisteredTool tool = default;
            for (int i = 0; i < tools.Count; i++)
                if (tools[i].Name == "m_echo") tool = tools[i];

            string result = AgentToolRegistry.Execute(tool, "{\"text\":\"hi\"}");

            Assert.AreEqual("echo:hi", result);
            Assert.AreEqual(1, member.EchoCount, "执行要落在该实例上");
            Assert.AreEqual("hi", member.LastText);
        }

        [Test]
        public void AgentToolSet_CollectsAndDeclares()
        {
            var set = new AgentToolSet();
            set.Collect(gameObject);

            var into = new List<LLMTool>();
            set.BuildDeclarations(into, new HashSet<string>());

            Assert.AreEqual(1, set.MemberToolCount);
            Assert.AreEqual(1, set.MemberActionCount);
            Assert.IsTrue(HasDeclaration(into, "m_echo"));
            Assert.IsTrue(HasDeclaration(into, "m_act"));
        }

        [Test]
        public void AgentCore_WithToolSet_DeclaresAndExecutesMemberTool()
        {
            var provider = new FakeKernelProvider(k_provider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(provider);
            dispatcher.SetDefaultProvider(k_provider);

            var profile = FakeProfiles.Create("toolset");
            var output = new FakeOutput();
            var set = new AgentToolSet();
            set.Collect(gameObject);
            var agent = new AgentCore(profile, "s1", null, output, new NullActionRunner(), null, set);

            try
            {
                provider.EnqueueToolCalls(new FakeCall("m_echo", "{\"text\":\"yo\"}"));
                provider.EnqueueAnswer("完成");

                agent.Trigger("走一个成员工具");

                Assert.IsTrue(HasToolDeclaration(provider.Requests[0].Tools, "m_echo"),
                    "成员工具要进 request.Tools");
                Assert.AreEqual(1, member.EchoCount, "成员工具执行要打在实例上");
                Assert.AreEqual("yo", member.LastText);
                Assert.AreEqual(2, provider.RequestCount, "回填后应续发第二次请求收尾");
            }
            finally
            {
                agent.Dispose();
                FakeProfiles.Destroy(profile);
                dispatcher.UnregisterProvider(k_provider);
                dispatcher.SetDefaultProvider(null);
                dispatcher.SetFallbackProvider(null);
            }
        }

        [Test]
        public void AgentCore_WithToolSet_ExecutesMemberAction()
        {
            var provider = new FakeKernelProvider(k_provider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(provider);
            dispatcher.SetDefaultProvider(k_provider);

            var profile = FakeProfiles.Create("toolset-act");
            var output = new FakeOutput();
            var set = new AgentToolSet();
            set.Collect(gameObject);
            var agent = new AgentCore(profile, "s2", null, output, new NullActionRunner(), null, set);

            try
            {
                provider.EnqueueToolCalls(new FakeCall("m_act", "{\"text\":\"go\"}"));
                provider.EnqueueAnswer("完成");

                agent.Trigger("走一个成员动作");

                Assert.AreEqual("go", member.LastText, "成员动作执行要打在实例上");
                Assert.IsTrue(HasToolDeclaration(provider.Requests[0].Tools, "m_act"),
                    "成员动作要进 request.Tools");
            }
            finally
            {
                agent.Dispose();
                FakeProfiles.Destroy(profile);
                dispatcher.UnregisterProvider(k_provider);
                dispatcher.SetDefaultProvider(null);
                dispatcher.SetFallbackProvider(null);
            }
        }

        private static bool HasTool(List<AgentToolRegistry.RegisteredTool> tools, string name)
        {
            for (int i = 0; i < tools.Count; i++)
                if (tools[i].Name == name) return true;
            return false;
        }

        private static bool HasAction(List<AgentActionDef> actions, string id)
        {
            for (int i = 0; i < actions.Count; i++)
                if (actions[i].Id == id) return true;
            return false;
        }

        private static bool HasDeclaration(List<LLMTool> tools, string name)
        {
            for (int i = 0; i < tools.Count; i++)
                if (tools[i].Function.Name == name) return true;
            return false;
        }

        private static bool HasToolDeclaration(List<LLMTool> tools, string name)
        {
            return HasDeclaration(tools, name);
        }
    }
}
