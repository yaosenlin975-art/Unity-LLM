/*
┌────────────────────────────┐
│　Description: 两段注入通路用例
│　Remark: 钉死 system 块序 persona → contextBlocks →
│　　　　　 静态段 → 动态段，以及内核把世界/个体内容
│　　　　　 分派到正确那一段（含 QueryableHint 归位）
│　ClassName: AgentCoreInjectionTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Text;
using LLM.Runtime;
using LLM.Runtime.Agent;
using NUnit.Framework;

namespace LLM.Tests.Editor.Agent
{
    /// <summary>
    /// 两段注入：静态段必须排在动态段之前，否则稳定前缀被动态内容打断。
    /// 前两条打在 LLMSession 的形参位次上（新参插错位置就会串成一句话），
    /// 第三条走 AgentCore 真路径——只有它测得到"哪部分内容进了哪一段"。
    /// </summary>
    [TestFixture]
    public class AgentCoreInjectionTests
    {
        /// <summary>宿主世界的最小假实现：两段各给一句可辨认的文本</summary>
        private sealed class FakeWorld : IWorldContextProvider
        {
            public string Stable;
            public string Dynamic;

            public FakeWorld(string stable, string dynamicText)
            {
                Stable = stable;
                Dynamic = dynamicText;
            }

            public string GetStableContext() => Stable;

            public string GetCoreSnapshot() => Dynamic;
        }

        private const string k_provider = "test_injection";

        private FakeKernelProvider provider;
        private readonly List<AgentCore> agents = new();
        private readonly List<AgentProfile_SO> profiles = new();

        [SetUp]
        public void SetUp()
        {
            provider = new FakeKernelProvider(k_provider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(provider);
            dispatcher.SetDefaultProvider(k_provider);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < agents.Count; i++) agents[i].Dispose();
            for (int i = 0; i < profiles.Count; i++) FakeProfiles.Destroy(profiles[i]);
            agents.Clear();
            profiles.Clear();

            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.UnregisterProvider(k_provider);
            dispatcher.SetDefaultProvider(null);
            dispatcher.SetFallbackProvider(null);
        }

        [Test]
        public async Task StableBlockComesBeforeEphemeral()
        {
            provider.EnqueueAnswer("好");
            var session = new LLMSession(ZString.Concat(k_provider, "_session"), "人设")
            {
                EnableTools = false
            };

            await session.AskAsync("在吗", null, "静态段", "动态段");

            string system = provider.Requests[0].BuildSystemContent();
            int persona = system.IndexOf("人设", System.StringComparison.Ordinal);
            int stable = system.IndexOf("静态段", System.StringComparison.Ordinal);
            int dynamic = system.IndexOf("动态段", System.StringComparison.Ordinal);

            Assert.Greater(persona, -1);
            Assert.Greater(stable, -1, "静态段没进 system 消息");
            Assert.Greater(dynamic, -1, "动态段没进 system 消息");
            Assert.Less(persona, stable, "人设必须仍在最前，否则连原有前缀都保不住");
            Assert.Less(stable, dynamic, "静态段排在动态段之后就吃不到前缀缓存");
        }

        [Test]
        public async Task OmittedStable_KeepsOldShape()
        {
            provider.EnqueueAnswer("好");
            var session = new LLMSession(ZString.Concat(k_provider, "_session2"), "人设")
            {
                EnableTools = false
            };

            await session.AskAsync("在吗", null, null, "只有动态");

            string system = provider.Requests[0].BuildSystemContent();
            StringAssert.Contains("只有动态", system);
            Assert.AreEqual(1, system.Split(new[] { "\n\n" }, System.StringSplitOptions.None).Length - 1,
                "少一段就该少一个块分隔符，不能塞进空块");
        }

        [Test]
        public void AgentCore_RoutesEachPartToItsOwnBlock()
        {
            // 前两条只证明 LLMSession 认两段，内核送对没送对要在这里钉：
            // QueryableHint 是 profile 上的固定文本，混进动态段等于每轮重写稳定前缀
            provider.EnqueueAnswer("好");

            var profile = FakeProfiles.Create("injection", persona: "人设");
            profile.QueryableHint = "你可以查询：t_look";
            profiles.Add(profile);

            var agent = new AgentCore(profile, null, new FakeWorld("世界静态段", "世界动态段"), new FakeOutput());
            agents.Add(agent);

            agent.Trigger("在吗");

            var blocks = provider.Requests[0].ContextBlocks;
            Assert.AreEqual(2, blocks.Count, "两段各成一块：既不并成一块，也不散成三块");
            StringAssert.Contains("世界静态段", blocks[0]);
            StringAssert.Contains("你可以查询", blocks[0], "QueryableHint 归静态段");
            StringAssert.Contains("世界动态段", blocks[1]);
            Assert.IsFalse(blocks[1].Contains("你可以查询"), "固定文本不得留在动态段");
            Assert.IsFalse(blocks[0].Contains("世界动态段"));

            string system = provider.Requests[0].BuildSystemContent();
            Assert.AreEqual(0, system.IndexOf("人设", System.StringComparison.Ordinal),
                "人设仍是 system 开头，两段都往后挂");
        }
    }
}
