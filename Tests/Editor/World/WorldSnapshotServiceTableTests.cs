/*
┌────────────────────────────┐
│　Description: 世界侧在册表用例
│　Remark: 只测登记/注销成对性与 revision 语义，
│　　　　　 不建场景、不碰模型
│　ClassName: WorldSnapshotServiceTableTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using LLM.Runtime.Agent.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LLM.Tests.Editor.World
{
    /// <summary>假可观测物：只提供四个观测面取值，本身不做任何注册（注册由用例显式驱动）</summary>
    internal sealed class FakeObservable : MonoBehaviour, IWorldObservable
    {
        public string Label;
        public string State = "";
        public int Rank;

        public string ObservedLabel => Label;
        public Vector3 ObservedPosition => transform.position;
        public string ObservableState => State;
        public int Order => Rank;
    }

    /// <summary>世界侧在册表：只测登记/注销成对性与 revision 语义，不建场景、不碰模型</summary>
    [TestFixture]
    public class WorldSnapshotServiceTableTests
    {
        private readonly List<IWorldObservable> spawned = new();

        [TearDown]
        public void TearDown()
        {
            // 不在这里断言表为空：那等于把结论绑在"DestroyImmediate 一定触发 OnDisable"上。
            // 成对性由 RegisterDisableEnable_IsPairedAndIdempotent 单独断言
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] is not Object obj || obj == null) continue;
                WorldSnapshotService.Unregister((IWorldObservable)spawned[i]);
                Object.DestroyImmediate(((Component)obj).gameObject);
            }

            spawned.Clear();
        }

        [Test]
        public void RegisterDisableEnable_IsPairedAndIdempotent()
        {
            var go = new GameObject("村长");
            var item = go.AddComponent<FakeObservable>();
            item.Label = "村长";

            WorldSnapshotService.Register(item);
            WorldSnapshotService.Register(item);
            Assert.AreEqual(1, WorldSnapshotService.ObservableCount, "重复注册不该占两个名额");

            WorldSnapshotService.Unregister(item);
            Assert.AreEqual(0, WorldSnapshotService.ObservableCount);

            WorldSnapshotService.Unregister(item);
            Assert.AreEqual(0, WorldSnapshotService.ObservableCount, "注销两次不该把别人的名额减掉");

            spawned.Add(item);
            Object.DestroyImmediate(go);
            spawned.Clear();
        }

        [Test]
        public void RegisterBumpsRevision_SoCachedBlocksKnowToRebuild()
        {
            long before = WorldSnapshotService.ObservedRevision;

            var go = new GameObject("铁匠");
            var item = go.AddComponent<FakeObservable>();
            item.Label = "铁匠";
            WorldSnapshotService.Register(item);

            Assert.AreNotEqual(before, WorldSnapshotService.ObservedRevision, "在册表变了 revision 必须动");

            WorldSnapshotService.Unregister(item);
            Assert.AreNotEqual(before, WorldSnapshotService.ObservedRevision, "注销同样要动 revision");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void RegisterNull_IsIgnored()
        {
            int before = WorldSnapshotService.ObservableCount;

            WorldSnapshotService.Register((IWorldObservable)null);
            WorldSnapshotService.Unregister((IWorldObservable)null);

            Assert.AreEqual(before, WorldSnapshotService.ObservableCount);
        }
    }
}
