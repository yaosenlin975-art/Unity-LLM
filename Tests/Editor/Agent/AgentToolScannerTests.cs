/*
┌────────────────────────────┐
│　Description: 成员工具候选枚举单元测试
│　Remark: 覆盖 Inspector「快速添加」的过
│　　　　　 滤与摊平规则，只验规则不验 UI
│　ClassName: AgentToolScannerTests
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Text;
using LLM.Editor;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Tools;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentToolScannerTests
    {
        private List<AgentMemberProvider> providers;

        [OneTimeSetUp]
        public void CollectOnce()
        {
            providers = AgentToolScanner.CollectMemberProviders();
        }

        [Test]
        public void FrameworkMemberToolIds_AreListed()
        {
            Assert.IsTrue(ContainsId("get_agent_transform"), "LLM.Runtime 自身要放行：程序集不在自己的 GetReferencedAssemblies 里");
            Assert.IsTrue(ContainsId("get_navigation_status"), "导航成员工具要进候选");
            Assert.IsTrue(ContainsId("move_to"), "成员动作要进候选");
            Assert.IsTrue(ContainsId("stop_navigation"), "成员动作要进候选");
        }

        [Test]
        public void EveryProvider_IsAnIdDeclaredByAnInstantiableMonoBehaviour()
        {
            for (int i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                var owner = provider.Owner;

                Assert.IsFalse(string.IsNullOrEmpty(provider.Id), "候选条目没有工具/动作名");
                Assert.IsNotNull(owner);
                Assert.IsTrue(typeof(MonoBehaviour).IsAssignableFrom(owner),
                    ZString.Concat(owner.FullName, " 不是 MonoBehaviour，挂不上物体"));
                Assert.IsFalse(owner.IsAbstract, ZString.Concat(owner.FullName, " 是抽象/静态类"));
                Assert.IsFalse(owner.IsNested, ZString.Concat(owner.FullName, " 是嵌套类"));
                Assert.IsFalse(owner.IsGenericTypeDefinition, ZString.Concat(owner.FullName, " 是泛型定义"));
                Assert.IsTrue(DeclaresId(owner, provider.Id),
                    ZString.Format("{0} 并未声明 {1}", owner.FullName, provider.Id));
            }
        }

        [Test]
        public void StaticGlobalAndTestAssemblyIds_StayOutOfCandidates()
        {
            Assert.IsFalse(ContainsId("get_current_time"), "static 全局工具不需要挂组件，不该进候选");
            Assert.IsFalse(ContainsId("m_echo"), "测试程序集的脚手架不该进候选");
            Assert.IsFalse(ContainsId("m_act"), "测试程序集的脚手架不该进候选");
            Assert.IsFalse(ContainsOwner(typeof(AgentHost)), "AgentHost 自身没有成员工具/动作");
        }

        [Test]
        public void Candidates_AreSortedById()
        {
            for (int i = 1; i < providers.Count; i++)
            {
                Assert.LessOrEqual(string.CompareOrdinal(providers[i - 1].Id, providers[i].Id), 0,
                    "菜单顺序每次打开都在跳");
            }
        }

        private bool ContainsId(string id)
        {
            for (int i = 0; i < providers.Count; i++)
                if (providers[i].Id == id) return true;
            return false;
        }

        private bool ContainsOwner(Type type)
        {
            for (int i = 0; i < providers.Count; i++)
                if (providers[i].Owner == type) return true;
            return false;
        }

        private static bool DeclaresId(Type owner, string id)
        {
            var methods = owner.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                var tool = methods[i].GetCustomAttribute<AgentToolAttribute>();
                if (tool != null && (tool.Name ?? methods[i].Name) == id) return true;

                var action = methods[i].GetCustomAttribute<AgentActionAttribute>();
                if (action != null && (action.Name ?? methods[i].Name) == id) return true;
            }

            return false;
        }
    }
}
