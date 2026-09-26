/*
┌────────────────────────────┐
│　Description: 世界静态段整段缓存用例
│　Remark: 只验"同引用返回 + revision 驱动重算"两件事，
│　　　　　 不建场景、不碰模型
│　ClassName: WorldSnapshotServiceLoreTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using LLM.Runtime.Agent.World;
using NUnit.Framework;

namespace LLM.Tests.Editor.World
{
    /// <summary>世界静态段：整段缓存命中返回同一 string 引用，注册列表变化才重算</summary>
    [TestFixture]
    public class WorldSnapshotServiceLoreTests
    {
        private sealed class FakeLore : IWorldLoreContributor
        {
            public string Text;
            public int Rank;
            public int Calls;

            public int Order => Rank;
            public string RenderLore()
            {
                Calls++;
                return Text;
            }
        }

        private readonly List<FakeLore> registered = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < registered.Count; i++)
                WorldSnapshotService.Unregister(registered[i]);
            registered.Clear();
        }

        private FakeLore Add(string text, int rank)
        {
            var lore = new FakeLore { Text = text, Rank = rank };
            registered.Add(lore);
            WorldSnapshotService.Register(lore);
            return lore;
        }

        [Test]
        public void EmptyTable_RendersEmptyString()
        {
            Assert.AreEqual("", WorldSnapshotService.RenderLore());
        }

        [Test]
        public void RenderOrdersByOrderThenJoins()
        {
            Add("后半段", 2);
            Add("前半段", 1);

            Assert.AreEqual("前半段\n后半段", WorldSnapshotService.RenderLore());
        }

        [Test]
        public void SecondRender_ReturnsSameStringInstance_WhenNothingChanged()
        {
            Add("矿镇常年被雪盖着", 1);

            string first = WorldSnapshotService.RenderLore();
            string second = WorldSnapshotService.RenderLore();

            // AreSame 而不是 AreEqual：只断言"两次文本相等"是废重量，
            // 算了两遍也相等。命中缓存的证据是同一个引用
            Assert.AreSame(first, second);
        }

        [Test]
        public void RegisterInvalidatesCache_RebuildsOnce()
        {
            var a = Add("第一段", 1);
            WorldSnapshotService.RenderLore();
            Assert.AreEqual(1, a.Calls);

            WorldSnapshotService.RenderLore();
            Assert.AreEqual(1, a.Calls, "没变化不该再问一次贡献者");

            Add("第二段", 2);
            string text = WorldSnapshotService.RenderLore();

            Assert.AreEqual(2, a.Calls, "注册列表变了必须重算");
            StringAssert.Contains("第二段", text);
        }

        [Test]
        public void BlankContribution_IsSkipped()
        {
            Add("", 1);
            Add("有效段", 2);

            Assert.AreEqual("有效段", WorldSnapshotService.RenderLore());
        }
    }
}
