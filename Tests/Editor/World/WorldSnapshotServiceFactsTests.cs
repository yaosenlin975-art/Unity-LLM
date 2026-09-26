/*
┌────────────────────────────┐
│　Description: 世界动态段用例
│　Remark: 只测 RenderFacts 的拼接、排序与现算语义，
│　　　　　 不建场景、不碰模型
│　ClassName: WorldSnapshotServiceFactsTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime.Agent.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LLM.Tests.Editor.World
{
    /// <summary>
    /// 挂在物体上的假事实贡献者：只为判活用例存在——必须真是 UnityEngine.Object 派生，
    /// DestroyImmediate 才能把它打成残骸（纯 C# 的 FakeFact 没有残骸态，测不到那条分支）。
    /// RenderFact 刻意读 transform：真实贡献者渲染全局事实时访问自身部件是常态，
    /// 残骸若没被判活拦下就在那里抛 MissingReferenceException，用例不会恒真
    /// </summary>
    internal sealed class FakeSceneFact : MonoBehaviour, IWorldFactContributor
    {
        public string Text;

        public int Order => 0;

        public string RenderFact() => ZString.Format("{0}：{1}", transform.name, Text);
    }

    /// <summary>
    /// 世界动态段：全局事实 + 按请求者现算的在场者。
    /// 覆盖四件事——拼接与格式、三档排序键的确定性、每轮现算绝不缓存、已销毁在册项的判活
    /// </summary>
    [TestFixture]
    public class WorldSnapshotServiceFactsTests
    {
        /// <summary>假事实：Sequence 每次被问返回一格，用来证明"变没变是谁决定的"</summary>
        private sealed class FakeFact : IWorldFactContributor
        {
            public int Rank;
            public string[] Sequence;
            public int Calls;

            public int Order => Rank;
            public string RenderFact()
            {
                var text = Sequence[System.Math.Min(Calls, Sequence.Length - 1)];
                Calls++;
                return text;
            }
        }

        private readonly List<GameObject> gos = new();
        private readonly List<IWorldObservable> items = new();
        private readonly List<FakeFact> facts = new();
        private readonly List<FakeSceneFact> sceneFacts = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < facts.Count; i++) WorldSnapshotService.Unregister(facts[i]);
            for (int i = 0; i < sceneFacts.Count; i++) WorldSnapshotService.Unregister(sceneFacts[i]);
            for (int i = 0; i < items.Count; i++) WorldSnapshotService.Unregister(items[i]);
            for (int i = 0; i < gos.Count; i++)
                if (gos[i] != null) Object.DestroyImmediate(gos[i]);
            gos.Clear(); items.Clear(); facts.Clear(); sceneFacts.Clear();
        }

        private FakeObservable Add(string label, float x, string state = "")
        {
            var go = new GameObject(label);
            go.transform.position = new Vector3(x, 0f, 0f);
            var item = go.AddComponent<FakeObservable>();
            item.Label = label;
            item.State = state;
            gos.Add(go);
            items.Add(item);
            WorldSnapshotService.Register((IWorldObservable)item);
            return item;
        }

        private FakeFact AddFact(params string[] sequence)
        {
            var fact = new FakeFact { Sequence = sequence };
            facts.Add(fact);
            WorldSnapshotService.Register(fact);
            return fact;
        }

        /// <summary>注册一个挂在物体上的事实贡献者（GameObject 由 gos 表统一清理）</summary>
        private FakeSceneFact AddSceneFact(string label, string text)
        {
            var go = new GameObject(label);
            var fact = go.AddComponent<FakeSceneFact>();
            fact.Text = text;
            gos.Add(go);
            sceneFacts.Add(fact);
            WorldSnapshotService.Register(fact);
            return fact;
        }

        [Test]
        public void NothingRegistered_RendersEmpty()
        {
            Assert.AreEqual("", WorldSnapshotService.RenderFacts(Vector3.zero, null, 10f, 6));
        }

        [Test]
        public void Peers_SortedByDistanceAndStateAppended()
        {
            Add("铁匠", 6f);
            var self = Add("我自己", 0.5f);
            Add("矿工", 3f, "正在挖");

            string text = WorldSnapshotService.RenderFacts(Vector3.zero, self, 10f, 6);

            Assert.AreEqual("矿工(3.0米|正在挖)、铁匠(6.0米)", text);
        }

        [Test]
        public void TruncatedPeers_MustReportTheHiddenCount()
        {
            var self = Add("我", 0f);
            Add("甲", 1f);
            Add("乙", 2f);
            Add("丙", 3f);

            string text = WorldSnapshotService.RenderFacts(Vector3.zero, self, 10f, 2);

            // 不报数，模型会以为场上只剩列出来的这两个
            StringAssert.Contains("另有 1 个未列出", text);
        }

        [Test]
        public void SameTable_DifferentOrigin_GivesDifferentText()
        {
            Add("甲", 1f);
            Add("乙", 9f);

            string near = WorldSnapshotService.RenderFacts(Vector3.zero, null, 5f, 6);
            string far = WorldSnapshotService.RenderFacts(new Vector3(9f, 0f, 0f), null, 5f, 6);

            Assert.AreEqual("甲(1.0米)", near);
            Assert.AreEqual("乙(0.0米)", far);
        }

        [Test]
        public void FactsAndPeers_JoinWithNewline()
        {
            AddFact("晴，午后起了风");
            var self = Add("我", 0f);
            Add("路人", 2f);

            string text = WorldSnapshotService.RenderFacts(Vector3.zero, self, 10f, 6);

            Assert.AreEqual("晴，午后起了风\n路人(2.0米)", text);
        }

        [Test]
        public void RenderFacts_IsNeverCached()
        {
            var fact = AddFact("稳定天气");
            var self = Add("我", 0f);
            Add("路人", 2f);

            WorldSnapshotService.RenderFacts(Vector3.zero, self, 10f, 6);
            WorldSnapshotService.RenderFacts(Vector3.zero, self, 10f, 6);

            Assert.AreEqual(2, fact.Calls,
                "动态段每次都要问贡献者（变没变由它自己判），整段缓存会让所有 NPC 拿到同一份在场者");
        }

        [Test]
        public void SameDistance_PeersSortByLabelOrdinalNotByRegistration()
        {
            // 距离档打平时必须落到名字 ordinal：否则同距在场者的先后随注册顺序（= Awake 先后）抖
            Add("甲", 2f);
            Add("乙", -2f);

            string forward = WorldSnapshotService.RenderFacts(Vector3.zero, null, 5f, 6);

            for (int i = 0; i < items.Count; i++) WorldSnapshotService.Unregister(items[i]);
            for (int i = items.Count - 1; i >= 0; i--) WorldSnapshotService.Register(items[i]);

            string reversed = WorldSnapshotService.RenderFacts(Vector3.zero, null, 5f, 6);

            // ordinal 定序：乙 U+4E59 在 甲 U+7532 前——甲先注册也排不到前面
            Assert.AreEqual("乙(2.0米)、甲(2.0米)", forward);
            Assert.AreEqual(forward, reversed);
        }

        [Test]
        public void SameDistanceSameLabel_PeersSortByStateOrdinal()
        {
            // 名字档也打平时落到第三档状态文本，三键全等则渲染行相同，怎么排输出都一致
            Add("矿工", 2f, "正在挖");
            Add("矿工", -2f, "正在歇");

            string forward = WorldSnapshotService.RenderFacts(Vector3.zero, null, 5f, 6);

            for (int i = 0; i < items.Count; i++) WorldSnapshotService.Unregister(items[i]);
            for (int i = items.Count - 1; i >= 0; i--) WorldSnapshotService.Register(items[i]);

            string reversed = WorldSnapshotService.RenderFacts(Vector3.zero, null, 5f, 6);

            Assert.AreEqual("矿工(2.0米|正在挖)、矿工(2.0米|正在歇)", forward);
            Assert.AreEqual(forward, reversed);
        }

        [Test]
        public void DestroyedObservable_IsSkippedWithoutThrowing()
        {
            Add("活人", 1f);
            Add("残骸", 2f);
            var doomedGo = gos[gos.Count - 1];
            Object.DestroyImmediate(doomedGo);

            // 故意不注销：模拟 OnDisable 兜底漏网的已销毁注册项。判活若不走 Unity 语义
            // （接口引用上的 == 是纯引用判空），这里对残骸取 ObservedPosition
            // 会抛 MissingReferenceException，用例直接红
            string text = WorldSnapshotService.RenderFacts(Vector3.zero, null, 10f, 6);

            Assert.AreEqual("活人(1.0米)", text);
        }

        [Test]
        public void DestroyedFactContributor_IsSkippedWithoutThrowing()
        {
            // 与在场者那条同形：残骸在册且不注销，判活若不走 Unity 语义，
            // FakeSceneFact.RenderFact 里读 transform 就抛 MissingReferenceException。
            // 残骸的话术也不许混进输出——它说的是上一局的事
            AddSceneFact("残骸气象站", "午后起了风");
            Object.DestroyImmediate(gos[gos.Count - 1]);
            AddSceneFact("气象站", "晴");

            string text = WorldSnapshotService.RenderFacts(Vector3.zero, null, 10f, 6);

            Assert.AreEqual("气象站：晴", text);
        }
    }
}
