/*
┌────────────────────────────┐
│　Description: NpcAgentHost 个体两表与身份用例
│　Remark: 钉死"个体注册不污染全局"“整段缓存返回
│　　　　　 同一引用”“双身份共存时以 NpcAgentHost
│　　　　　 为准”“残骸贡献者被跳过”四条缓存与身份红线
│　ClassName: NpcAgentHostSnapshotTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using System.Text.RegularExpressions;
using LLM.Runtime.Agent;
using LLM.Runtime.Agent.Npc;
using LLM.Runtime.Agent.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace LLM.Tests.Editor.Agent
{
    /// <summary>
    /// NPC 宿主的三职一体的两条前提：静态段必须整段缓存（否则 provider 的前缀命中落空），
    /// 个体侧登记必须只影响自己（否则全场 NPC 互相打作废）。另加双身份共存那条守卫。
    /// 生命周期靠 SetActive 驱动，纯逻辑部分离线探针已覆盖（见 task-6-report §4）。
    /// </summary>
    [TestFixture]
    public class NpcAgentHostSnapshotTests
    {
        private sealed class FakeProfile : ISelfProfileContributor
        {
            public int Rank;
            public string Text;
            public int Calls;
            public int Order => Rank;
            public string RenderProfile() { Calls++; return Text; }
        }

        /// <summary>模拟"自己记 last-state"的贡献者：状态没变就返回同一个字符串引用</summary>
        private sealed class FakeState : ISelfStateContributor
        {
            public int Rank;
            public int Hp;
            public int Calls;
            private int lastHp = int.MinValue;
            private string lastText;

            public int Order => Rank;

            public string RenderState()
            {
                Calls++;
                if (lastHp != Hp)
                {
                    lastHp = Hp;
                    lastText = $"生命值 {Hp}";
                }
                return lastText;
            }
        }

        // 叫 hostProfile 而不是 profile：brief 的用例体里有个 `var profile = new FakeProfile(...)`
        // 的局部量，同名会把字段遮掉，读代码的人得翻半天才知道注册的是哪个
        private AgentProfile_SO hostProfile;
        private readonly List<GameObject> spawned = new();
        private GameObject go;
        private NpcAgentHost host;

        [SetUp]
        public void SetUp()
        {
            hostProfile = FakeProfiles.Create("npc_host");
            host = CreateHost("YBot");
            go = host.gameObject;
        }

        [TearDown]
        public void TearDown()
        {
            // 注销写成显式 Unregister，而不是指望 DestroyImmediate 一定触发 OnDisable：
            // 漏一个在册残留，下一条按 ObservableCount 取基线的用例就会被带红。
            // 已销毁的走 Unity 语义判空跳过（重复注销本身幂等，不担心和 OnDisable 撞车）
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] is not Object obj || obj == null) continue;

                var created = spawned[i].GetComponent<NpcAgentHost>();
                if (created is not null) WorldSnapshotService.Unregister((IWorldObservable)created);

                Object.DestroyImmediate(spawned[i]);
            }

            spawned.Clear();
            FakeProfiles.Destroy(hostProfile);
        }

        /// <summary>
        /// 建一个"生命周期真的跑过"的宿主。先禁用再挂：激活态下 AddComponent 会当场跑
        /// OnEnable → Activate(null profile) → LogError，而 UTF 默认把未预期的 LogError
        /// 判成本条用例失败（形状同 AgentHostTests.SetUp）。再把 profile 填进基类并启用，
        /// 启用这一下才是 OnEnable 的真实路径——注册可观测身份全靠它
        /// </summary>
        private NpcAgentHost CreateHost(string name)
        {
            var createdGo = new GameObject(name);
            spawned.Add(createdGo);
            createdGo.SetActive(false);

            var created = createdGo.AddComponent<NpcAgentHost>();
            created.Activate(hostProfile);
            createdGo.SetActive(true);
            return created;
        }

        [Test]
        public void IsFoundByBaseTypeLookups_SoExistingCallSitesKeepWorking()
        {
            Assert.AreSame(host, go.GetComponent<AgentHost>(), "六处按 AgentHost 找的调用点都靠这条");
        }

        [Test]
        public void StableBlock_CachedUntilProfileRegistrationChanges()
        {
            var profile = new FakeProfile { Text = "可用动画：挥手、坐下", Rank = 1 };
            host.RegisterProfile(profile);

            var provider = (IWorldContextProvider)host;
            string first = provider.GetStableContext();
            string second = provider.GetStableContext();

            Assert.AreSame(first, second, "注册列表没变就该复用整段");
            Assert.AreEqual(1, profile.Calls);

            host.RegisterProfile(new FakeProfile { Text = "可用动画：奔跑", Rank = 2 });
            StringAssert.Contains("奔跑", provider.GetStableContext());
            Assert.AreEqual(2, profile.Calls, "注册变化必须触发重算");
        }

        [Test]
        public void OtherNpcRegistration_DoesNotInvalidateMine()
        {
            var mine = new FakeProfile { Text = "我的清单", Rank = 1 };
            host.RegisterProfile(mine);
            var provider = (IWorldContextProvider)host;
            provider.GetStableContext();
            Assert.AreEqual(1, mine.Calls);

            var other = CreateHost("别的NPC");
            other.RegisterProfile(new FakeProfile { Text = "他的清单", Rank = 1 });

            provider.GetStableContext();
            Assert.AreEqual(1, mine.Calls, "A 的注册不该影响 B 的静态段");
        }

        [Test]
        public void DynamicBlock_RebuiltEachRound_AndContributorDecidesWhatToReuse()
        {
            var state = new FakeState { Hp = 70, Rank = 1 };
            host.RegisterState(state);
            var provider = (IWorldContextProvider)host;

            StringAssert.Contains("生命值 70", provider.GetCoreSnapshot());
            StringAssert.Contains("生命值 70", provider.GetCoreSnapshot());
            Assert.AreEqual(2, state.Calls, "动态段每轮都要问");

            state.Hp = 40;
            StringAssert.Contains("生命值 40", provider.GetCoreSnapshot());
        }

        [Test]
        public void ObservableIdentity_ReportsSelfStateAndLabel()
        {
            go.name = "村长";
            var state = new FakeState { Hp = 55, Rank = 1 };
            host.RegisterState(state);

            var observable = (IWorldObservable)host;
            Assert.AreEqual("村长", observable.ObservedLabel);
            StringAssert.Contains("生命值 55", observable.ObservableState,
                "别人看我和我看自己，状态文案同源");
        }

        [Test]
        public void RegistersItselfAsObservable_WhenEnabled()
        {
            // SetUp 的 CreateHost 最后启用了一次，OnEnable 已经跑过，所以基线本身就含"我自己"
            int at = WorldSnapshotService.ObservableCount;

            go.SetActive(false);
            Assert.AreEqual(at - 1, WorldSnapshotService.ObservableCount, "禁用即出册");

            go.SetActive(true);
            Assert.AreEqual(at, WorldSnapshotService.ObservableCount, "重新启用不该重复登记");
        }

        /// <summary>
        /// 双身份共存（策划手挂过轻量 WorldObservable）：轻量那个必须出册。
        /// 它继续在册的话，NPC 会把自己念进在场者——RenderFacts 的 except 只按引用排掉
        /// NpcAgentHost 那个身份，同名同位置的第二个身份照样被列出来
        /// </summary>
        [Test]
        public void CoexistingLightweightIdentity_IsSuperseded()
        {
            var dualGo = new GameObject("双身份NPC");
            spawned.Add(dualGo);
            dualGo.SetActive(false);

            var dual = dualGo.AddComponent<NpcAgentHost>();
            var lightweight = dualGo.AddComponent<WorldObservable>();
            dual.Activate(hostProfile);

            int before = WorldSnapshotService.ObservableCount;
            LogAssert.Expect(LogType.Warning, new Regex("WorldObservable"));
            dualGo.SetActive(true);

            Assert.IsFalse(lightweight.enabled, "以 NpcAgentHost 为准：同物体的轻量身份被让位关闭");
            Assert.AreEqual(before + 1, WorldSnapshotService.ObservableCount, "只该在册一个身份");

            // 端到端那一条：在场者文本里不得出现自己的名字（只查在册数看不出这条，
            // 两个身份都在册时计数正好是 +2，而"念进自己"是文本层面的错）
            var facts = WorldSnapshotService.RenderFacts(Vector3.zero, dual, 100f, 0);
            StringAssert.DoesNotContain("双身份NPC", facts);

            // 不写 NoUnexpectedReceived：AgentCore 构造期本身可能打配置类 Log.Warning
            // （deadline 提示），那条期望与本题无关，收在这里只会平白带红
        }

        /// <summary>
        /// 判活（R10）只有这条路能验：纯 .NET 宿主里构造不出"Unity 语义已销毁"的对象
        /// （派生 UnityEngine.Object 的替身一走 == 就抛 SecurityException），而残骸在册恰恰是
        /// 子层级贡献者漏配对时的真实形态。形状同 WorldSnapshotServiceFactsTests 的残骸用例
        /// </summary>
        [Test]
        public void DestroyedProfileContributor_IsSkippedWhenTheBlockRebuilds()
        {
            var corpse = go.AddComponent<CorpseProfileContributor>();
            host.RegisterProfile(corpse);
            var provider = (IWorldContextProvider)host;
            StringAssert.Contains("残骸清单", provider.GetStableContext());

            // 故意不注销：模拟"漏了 OnDisable 配对"的贡献者，制造"在册但已是残骸"的窗口
            Object.DestroyImmediate(corpse);

            // 再注册一个把两层缓存都顶掉，渲染循环才会真的走到残骸身上。
            // 实现若按普通判空放行（接口引用上的 contributor == null 选不到 UnityEngine.Object 的
            // == 重载），它 RenderProfile 里那句 transform 就是 MissingReferenceException，
            // 整段个体静态渲染不出来——红会红在异常上，而不是断言上
            host.RegisterProfile(new FakeProfile { Text = "活着的清单", Rank = 2 });

            var text = provider.GetStableContext();
            StringAssert.Contains("活着的清单", text);
            StringAssert.DoesNotContain("残骸清单", text, "已销毁的贡献者不该再出文本");
        }
    }

    /// <summary>
    /// 残骸探针：故意不自注销的子层级贡献者。顶级类而非嵌套类（同 SnapshotProbeHost 的理由），
    /// RenderProfile 读 transform 是这类组件的常态写法——正是它让"判活写错"变成一次崩掉的渲染
    /// </summary>
    internal sealed class CorpseProfileContributor : MonoBehaviour, ISelfProfileContributor
    {
        public int Order => 0;

        public string RenderProfile()
        {
            var _ = transform.position;
            return "残骸清单";
        }
    }
}
