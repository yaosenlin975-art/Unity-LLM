/*
┌────────────────────────────┐
│　Description: 轻量可观测物组件用例
│　Remark: 只测启用/禁用的在册成对性、标签回落，
│　　　　　 以及框架侧 RenderFacts 能否直接看见它，
│　　　　　 不建场景、不碰模型
│　ClassName: WorldObservableTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using LLM.Runtime.Agent.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LLM.Tests.Editor.World
{
    /// <summary>
    /// 上提后的 <see cref="WorldObservable"/>：验证"启用即在册、禁用即出册"这条身份通路，
    /// 以及它被框架的在场者查询真实看见——这两条通了，Demo 侧那张在册表才可以删。
    /// </summary>
    [TestFixture]
    public class WorldObservableTests
    {
        private readonly List<GameObject> spawned = new();

        [TearDown]
        public void TearDown()
        {
            // 注销写成显式 Unregister，而不是指望 DestroyImmediate 一定触发 OnDisable：
            // 漏一个在册残留，下一条断言整段文本的用例就会被多出来的在场者带红。
            // 已销毁的物体走 Unity 语义判空跳过（重复注销本身是幂等的，不担心和 OnDisable 撞车）
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] is not Object obj || obj == null) continue;

                var item = spawned[i].GetComponent<WorldObservable>();
                if (item is not null) WorldSnapshotService.Unregister((IWorldObservable)item);

                Object.DestroyImmediate(spawned[i]);
            }

            spawned.Clear();
        }

        [Test]
        public void EnableRegistersDisableUnregisters()
        {
            int before = WorldSnapshotService.ObservableCount;

            var go = new GameObject("村长");
            spawned.Add(go);
            var item = go.AddComponent<WorldObservable>();

            Assert.AreEqual(before + 1, WorldSnapshotService.ObservableCount);

            go.SetActive(false);
            Assert.AreEqual(before, WorldSnapshotService.ObservableCount, "禁用即出册");

            go.SetActive(true);
            Assert.AreEqual(before + 1, WorldSnapshotService.ObservableCount, "重新启用不该重复登记");

            Object.DestroyImmediate(go);
            Assert.AreEqual(before, WorldSnapshotService.ObservableCount);
        }

        [Test]
        public void LabelFallsBackToGameObjectName()
        {
            var go = new GameObject("雪原路标");
            spawned.Add(go);
            var item = go.AddComponent<WorldObservable>();

            Assert.AreEqual("雪原路标", item.ObservedLabel);

            Object.DestroyImmediate(go);
        }

        /// <summary>
        /// 在册表归框架之后，在场者段由 <see cref="WorldSnapshotService.RenderFacts"/> 产出：
        /// 这条用例钉的是"上提"这件事本身——组件注册进的那张表就是查询读的那张表，
        /// 且请求者排除自己的语义对真组件成立（greeter 靠它不念出自己的名字）
        /// </summary>
        [Test]
        public void RenderFacts_SeesEnabledObservable_ExcludesItAsRequester()
        {
            var go = new GameObject("铁匠");
            go.transform.position = new Vector3(1f, 0f, 0f);
            spawned.Add(go);
            var item = go.AddComponent<WorldObservable>();

            Assert.AreEqual("铁匠(1.0米)", WorldSnapshotService.RenderFacts(Vector3.zero, null, 5f, 6),
                "默认无自我状态，所以只出名字与距离，不该带竖线");

            Assert.AreEqual("", WorldSnapshotService.RenderFacts(Vector3.zero, item, 5f, 6),
                "请求者自己不许出现在自己的在场者列表里");

            go.SetActive(false);
            Assert.AreEqual("", WorldSnapshotService.RenderFacts(Vector3.zero, null, 5f, 6),
                "禁用后不该再被看见");

            Object.DestroyImmediate(go);
        }
    }
}
