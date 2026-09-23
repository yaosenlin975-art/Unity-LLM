/*
┌────────────────────────────┐
│　Description: NPC 好感状态与动作测试
│　Remark: 覆盖模型单轮上限、事务回滚与
│　　　　　 sessionId 隔离
│　ClassName: NpcAffinityTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Agent.Npc;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class NpcAffinityTests
    {
        private readonly MemoryAffinityStore store = new();
        private GameObject gameObject;
        private NpcAffinityController controller;

        [SetUp]
        public void SetUp()
        {
            NpcAffinityStore.Current = store;
            gameObject = new GameObject("NpcAffinityTest");
            controller = gameObject.AddComponent<NpcAffinityController>();
            controller.Initialize("npc#one", 10);
        }

        [TearDown]
        public void TearDown()
        {
            NpcAffinityStore.Reset();
            if (gameObject != null) Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void Initialize_UsesSessionIdAsIndependentStateKey()
        {
            var otherObject = new GameObject("NpcAffinityOther");
            var other = otherObject.AddComponent<NpcAffinityController>();
            try
            {
                other.Initialize("npc#two", -30);
                controller.AdjustFromGame(15, "完成任务").GetAwaiter().GetResult();

                Assert.AreEqual(25, controller.CurrentValue);
                Assert.AreEqual(-30, other.CurrentValue);
                Assert.AreEqual(25, store.LoadValue("npc#one"));
            }
            finally
            {
                Object.DestroyImmediate(otherObject);
            }
        }

        [Test]
        public void AdjustFromModel_EnforcesTwentyAndAllowsOnlyOneSuccessPerRound()
        {
            var first = controller.AdjustFromModel(20, "玩家完成了承诺", 7).GetAwaiter().GetResult();
            var second = controller.AdjustFromModel(-20, "玩家再次要求修改", 7).GetAwaiter().GetResult();
            var invalid = controller.AdjustFromModel(21, "超出模型单轮上限", 8).GetAwaiter().GetResult();

            Assert.IsTrue(first.Ok, first.Content);
            Assert.IsFalse(second.Ok);
            Assert.IsFalse(invalid.Ok);
            Assert.AreEqual(30, controller.CurrentValue);
        }

        [Test]
        public void AdjustFromGame_DoesNotUseModelTwentyLimit()
        {
            var result = controller.AdjustFromGame(80, "剧情奖励").GetAwaiter().GetResult();

            Assert.IsTrue(result.Ok, result.Content);
            Assert.AreEqual(90, controller.CurrentValue);
        }

        [Test]
        public void Initialize_RestoresPersistedValueForTheSameSession()
        {
            controller.AdjustFromGame(12, "完成任务").GetAwaiter().GetResult();

            var otherObject = new GameObject("NpcAffinityReload");
            var reloaded = otherObject.AddComponent<NpcAffinityController>();
            try
            {
                reloaded.Initialize("npc#one", -80);

                Assert.AreEqual(22, reloaded.CurrentValue);
                Assert.AreEqual(1, reloaded.CurrentRevision);
                Assert.AreEqual("完成任务", reloaded.LastReason);
            }
            finally
            {
                Object.DestroyImmediate(otherObject);
            }
        }

        [Test]
        public void AdjustFromModel_RequiresReasonAndRollsBackWhenSaveFails()
        {
            store.FailWrites = true;

            var result = controller.AdjustFromModel(10, "玩家救了我", 1).GetAwaiter().GetResult();

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(10, controller.CurrentValue);
            Assert.AreEqual(0, controller.CurrentRevision);
            Assert.IsFalse(controller.AdjustFromModel(10, "", 2).GetAwaiter().GetResult().Ok);
        }

        [Test]
        public void RenderInjection_ContainsValueLevelAndAttitude()
        {
            var text = controller.RenderInjection();

            StringAssert.Contains("好感度：10/100", text);
            StringAssert.Contains("关系：中立", text);
            StringAssert.Contains("按常理交流", text);
        }

        [Test]
        public void AffinityLevel_UsesConfiguredBoundaries()
        {
            Assert.AreEqual(ENpcAffinityLevel.Hostile, NpcAffinityController.GetLevel(-61));
            Assert.AreEqual(ENpcAffinityLevel.Cold, NpcAffinityController.GetLevel(-60));
            Assert.AreEqual(ENpcAffinityLevel.Neutral, NpcAffinityController.GetLevel(-20));
            Assert.AreEqual(ENpcAffinityLevel.Friendly, NpcAffinityController.GetLevel(20));
            Assert.AreEqual(ENpcAffinityLevel.Trusted, NpcAffinityController.GetLevel(60));
        }

        [Test]
        public void Controller_DeclaresAffinityToolAndAction()
        {
            var tools = AgentToolRegistry.ScanHierarchyTools(gameObject);
            var actions = AgentActionRegistry.ScanHierarchyTools(gameObject);

            Assert.IsTrue(ContainsTool(tools, "get_affinity"));
            Assert.IsTrue(ContainsAction(actions, "adjust_affinity"));
        }

        private static bool ContainsTool(List<AgentToolRegistry.RegisteredTool> tools, string id)
        {
            for (int i = 0; i < tools.Count; i++)
                if (tools[i].Name == id) return true;
            return false;
        }

        private static bool ContainsAction(List<AgentActionDef> actions, string id)
        {
            for (int i = 0; i < actions.Count; i++)
                if (actions[i].Id == id) return true;
            return false;
        }

        private sealed class MemoryAffinityStore : INpcAffinityStore
        {
            private readonly Dictionary<string, string> values = new();

            public bool FailWrites;

            public string Load(string sessionId)
            {
                return values.TryGetValue(sessionId, out string json) ? json : null;
            }

            public UniTask SaveAsync(string sessionId, string json, CancellationToken ct)
            {
                if (FailWrites) throw new System.InvalidOperationException("test write failure");
                values[sessionId] = json;
                return UniTask.CompletedTask;
            }

            public int LoadValue(string sessionId)
            {
                var state = NpcAffinityState.Deserialize(Load(sessionId));
                return state?.Value ?? 0;
            }
        }
    }
}
