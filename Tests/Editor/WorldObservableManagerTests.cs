using System.Collections.Generic;
using LLM.Demo.Agent.Npc;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LLM.Tests.Editor
{
    /// <summary>观测层在册表：只测登记/注销的成对性与查询语义，不建场景、不碰模型。</summary>
    [TestFixture]
    public class WorldObservableManagerTests
    {
        private sealed class FakeObservable : WorldObservable
        {
            public string State = "";

            public override string ObservableState => State;
        }

        private readonly List<FakeObservable> spawned = new();
        private readonly List<ObservedInfo> results = new();

        [TearDown]
        public void TearDown()
        {
            // 不在这里断言在册表为空：那等于把用例结论绑在"DestroyImmediate 一定触发 OnDisable"上，
            // 红起来分不清是被测逻辑坏还是清理路径没跑。成对性由 EnableRegistersDisableUnregisters 单独断言。
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] == null) continue;
                WorldObservableManager.Unregister(spawned[i]);
                Object.DestroyImmediate(spawned[i].gameObject);
            }

            spawned.Clear();
            results.Clear();
        }

        private FakeObservable Spawn(string name, float distance, string state = "")
        {
            var go = new GameObject(name);
            go.transform.position = new Vector3(distance, 0f, 0f);
            var item = go.AddComponent<FakeObservable>();
            item.State = state;
            spawned.Add(item);
            return item;
        }

        [Test]
        public void EnableRegistersDisableUnregisters()
        {
            var item = Spawn("村长", 2f);
            Assert.AreEqual(1, WorldObservableManager.Count);

            item.gameObject.SetActive(false);
            Assert.AreEqual(0, WorldObservableManager.Count, "禁用即出册");

            item.gameObject.SetActive(true);
            Assert.AreEqual(1, WorldObservableManager.Count, "重新启用不该重复登记");
        }

        [Test]
        public void CollectOrdersByDistanceAndReportsTruncatedCount()
        {
            Spawn("铁匠", 6f);
            Spawn("村长", 1f);
            Spawn("矿工", 3f);
            Spawn("路人", 99f);

            int truncated = WorldObservableManager.CollectNearby(Vector3.zero, 10f, 2, null, results);

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(1, truncated, "10m 内三个只报两个，剩下的必须报成数量");
            Assert.AreEqual("村长", results[0].Label);
            Assert.AreEqual("矿工", results[1].Label);
            Assert.AreEqual(1f, results[0].Distance, 0.001f);
        }

        [Test]
        public void CollectSkipsSelfAndCarriesStateText()
        {
            var self = Spawn("我自己", 0.5f);
            Spawn("玩家", 2f, "正在跑动");

            int truncated = WorldObservableManager.CollectNearby(Vector3.zero, 5f, 6, self, results);

            Assert.AreEqual(0, truncated);
            Assert.AreEqual(1, results.Count, "发起查询的那个不该出现在自己的快照里");
            Assert.AreEqual("玩家", results[0].Label);
            Assert.AreEqual("正在跑动", results[0].State);
        }
    }
}
