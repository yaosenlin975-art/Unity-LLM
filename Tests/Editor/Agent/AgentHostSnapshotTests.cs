/*
┌────────────────────────────┐
│　Description: AgentHost 快照两段化用例
│　Remark: 一边钉基类行为逐字不变（好感 + 手填
│　　　　　 兜底），一边钉子类重写点可达
│　ClassName: AgentHostSnapshotTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using LLM.Runtime.Agent;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LLM.Tests.Editor.Agent
{
    /// <summary>
    /// 宿主侧两段化的两条红线：基类 <see cref="AgentHost"/> 的动态段行为一字不改（纯聊天宿主
    /// 还在用它），静态段与生命周期钩子又必须是真虚方法——Task 6 的 NpcAgentHost 全靠这两个口子，
    /// 少一个虚标记就是子类悄悄失效。
    /// </summary>
    [TestFixture]
    public class AgentHostSnapshotTests
    {
        private readonly List<GameObject> spawned = new();

        [TearDown]
        public void TearDown()
        {
            // 断言中途变红时别把宿主漏在场景里（形状同 WorldObservableTests）；
            // 已销毁的走 Unity 语义判空跳过，不重复销毁
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] is not Object obj || obj == null) continue;

                Object.DestroyImmediate(spawned[i]);
            }

            spawned.Clear();
        }

        [Test]
        public void BaseHost_KeepsOldSnapshotBehaviour_WhenNoNpcPartsPresent()
        {
            var go = new GameObject("纯聊天宿主");
            spawned.Add(go);
            // 先禁用再加组件：激活态下 AddComponent 会当场跑 OnEnable → Activate(null profile)
            // → Log.Error，而 UTF 默认把未预期的 LogError 判成本条用例失败——红的原因跟要钉的
            // 快照语义无关。禁用态下 affinityController 恒 null，正好是纯聊天宿主的样子
            go.SetActive(false);
            var host = go.AddComponent<AgentHost>();

            host.SetCoreSnapshot("手填兜底块");

            var provider = (IWorldContextProvider)host;
            Assert.AreEqual("手填兜底块", provider.GetCoreSnapshot(), "基类行为不得因重构改变");
            Assert.IsTrue(string.IsNullOrEmpty(provider.GetStableContext()), "基类没有静态段");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void AgentHost_IsNoLongerSealed_SoNpcCanSubclass()
        {
            Assert.IsFalse(typeof(AgentHost).IsSealed, "NpcAgentHost 要继承它");
        }

        /// <summary>
        /// 接口实现转发到虚方法这件事得真测一次：若写成 <c>GetCoreSnapshot() => coreSnapshot</c>
        /// 这种直取字段的实现，基类用例照样绿，而 Task 6 的 NpcAgentHost 重写永远进不来（静默失效）
        /// </summary>
        [Test]
        public void DerivedHost_RewritePoints_AreReachedThroughTheInterface()
        {
            var go = new GameObject("派生宿主");
            spawned.Add(go);
            go.SetActive(false);
            var host = go.AddComponent<SnapshotProbeHost>();

            var provider = (IWorldContextProvider)host;
            Assert.AreEqual("子类静态段", provider.GetStableContext(), "静态段重写点没被走到");
            Assert.AreEqual("子类动态段", provider.GetCoreSnapshot(), "动态段重写点没被走到");

            // 启用一次，验 Unity 的消息派发真落到派生实现上（不调 base，所以不会去 Activate(null)）
            go.SetActive(true);
            Assert.AreEqual(1, host.EnableCalls, "OnEnable 不是虚的，子类挂不上注册");

            go.SetActive(false);
            Assert.AreEqual(1, host.DisableCalls, "OnDisable 同理");

            Object.DestroyImmediate(go);
        }
    }

    /// <summary>
    /// 用例专用的"子类视角"探针，重写的正是 NpcAgentHost 要重写的那四个口子。
    /// 写成同文件的顶级类而不是嵌套类：Unity 对嵌套 MonoBehaviour 的额外限制没必要掺进来了
    /// </summary>
    internal sealed class SnapshotProbeHost : AgentHost
    {
        public int EnableCalls;
        public int DisableCalls;

        protected override void OnEnable()
        {
            EnableCalls++;
        }

        protected override void OnDisable()
        {
            DisableCalls++;
        }

        protected override string RenderStableContext() => "子类静态段";

        protected override string RenderCoreSnapshot() => "子类动态段";
    }
}
