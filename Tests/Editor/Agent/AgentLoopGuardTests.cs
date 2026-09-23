/*
 * Agent Tests — 循环检测与资源锁（纯单元）
 * 覆盖设计稿测试矩阵里不依赖轮次编排的行：签名规范化、阶梯裁决、跨轮配额、锁排队与退队
 */

using System;
using System.Reflection;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime;
using LLM.Runtime.Agent;
using NUnit.Framework;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentLoopGuardTests
    {
        private static float fakeNow;

        private static float ReadFakeNow() => fakeNow;

        private Func<float> realClock;

        [SetUp]
        public void SetUp()
        {
            realClock = (Func<float>)Field().GetValue(null);
            fakeNow = 1000f;
            Field().SetValue(null, (Func<float>)ReadFakeNow);
            AgentResourceLocks.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            Field().SetValue(null, realClock);
            AgentResourceLocks.Reset();
        }

        private static FieldInfo Field()
        {
            return typeof(AgentCore).GetField("NowSeconds",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        }

        private static LoopGuard NewGuard(int globalLimit = 2, int perNameLimit = 4)
        {
            var guard = new LoopGuard();
            guard.Configure(globalLimit, perNameLimit);
            return guard;
        }

        private static ELoopVerdict Decide(LoopGuard guard, string name, string args,
            bool idempotent = true, int repeatLimit = 0, int nameRepeatLimit = 0)
        {
            return guard.Decide(LoopGuard.BuildSignature(name, args), name,
                idempotent, repeatLimit, nameRepeatLimit).Verdict;
        }

        [Test]
        public void BuildSignature_NormalizesKeysAndDropsSay()
        {
            var left = LoopGuard.BuildSignature("trade", "{\"b\":2,\"a\":1,\"say\":\"给你\"}");
            var right = LoopGuard.BuildSignature("trade", "{\"a\": 1, \"say\": \"换个措辞\", \"b\": 2}");

            Assert.AreEqual(left, right, "键序与空白不该影响签名");
            // 只比"两边相等"会被空规范化蒙过去：签名一旦塌成 trade() 这条照样绿，
            // 而不同参数已被判成同一个调用的事故就再也测不出来（B 步曾为此红一条）
            Assert.AreEqual("trade({a:1,b:2})", left, "规范化结果必须真的带上排序后的参数");
            Assert.IsFalse(left.Contains("say"), "say 不该出现在签名里");
        }

        [Test]
        public void BuildSignature_DifferentArgs_DifferentSignature()
        {
            Assert.AreNotEqual(
                LoopGuard.BuildSignature("observe", "{\"x\":1}"),
                LoopGuard.BuildSignature("observe", "{\"x\":2}"));

            // 只剥顶层 say：嵌套层里的 say 是模型自己填的参数内容，剥了会把
            // {"q":{"say":"x"}} 与 {"q":{"say":"y"}} 判成同一个调用
            Assert.AreNotEqual(
                LoopGuard.BuildSignature("ask", "{\"q\":{\"say\":\"x\",\"k\":1}}"),
                LoopGuard.BuildSignature("ask", "{\"q\":{\"say\":\"y\",\"k\":1}}"));
        }

        [Test]
        public void IdempotentTool_WarnThenBlock_NotAbort()
        {
            // ADR-023：幂等同参重复封顶 Block，不再 Abort；模型仍在本轮内可收口
            var guard = NewGuard(2);
            const string args = "{\"x\":1}";

            Assert.AreEqual(ELoopVerdict.Allow, Decide(guard, "observe", args));
            Assert.AreEqual(ELoopVerdict.Allow, Decide(guard, "observe", args));
            Assert.AreEqual(ELoopVerdict.Warn, Decide(guard, "observe", args));
            Assert.AreEqual(ELoopVerdict.Block, Decide(guard, "observe", args));
            Assert.AreEqual(ELoopVerdict.Block, Decide(guard, "observe", args));
        }

        [Test]
        public void NameCap_DifferentArgs_SameName()
        {
            var guard = NewGuard(2, 3);

            Assert.AreEqual(ELoopVerdict.Allow, Decide(guard, "search_past_conversation", "{\"q\":\"吃\"}"));
            Assert.AreEqual(ELoopVerdict.Allow, Decide(guard, "search_past_conversation", "{\"q\":\"食物\"}"));
            Assert.AreEqual(ELoopVerdict.Allow, Decide(guard, "search_past_conversation", "{\"q\":\"美食\"}"));
            var warn = guard.Decide(
                LoopGuard.BuildSignature("search_past_conversation", "{\"q\":\"饮食\"}"),
                "search_past_conversation", true, 0, 3);
            Assert.AreEqual(ELoopVerdict.Warn, warn.Verdict);
            Assert.IsTrue(warn.Message.Contains("简短回复"), "强制收口句必须在");

            var block = guard.Decide(
                LoopGuard.BuildSignature("search_past_conversation", "{\"q\":\"口味\"}"),
                "search_past_conversation", true, 0, 3);
            Assert.AreEqual(ELoopVerdict.Block, block.Verdict);
            Assert.IsTrue(block.Message.Contains("简短回复"), "强制收口句必须在");
        }

        [Test]
        public void NameCap_ToolOverrideMinusOne_Allows()
        {
            var guard = NewGuard(2, 2);
            for (int i = 0; i < 6; i++)
            {
                string args = ZString.Format("{{\"q\":\"w{0}\"}}", i);
                Assert.AreEqual(ELoopVerdict.Allow,
                    Decide(guard, "clock", args, true, 0, -1),
                    "NameRepeatLimit=-1 关闭该工具 NameCap");
            }
        }

        [Test]
        public void RepeatLimitMinusOne_ClosesL2_ButNameCapStillApplies()
        {
            var guard = NewGuard(2, 2);

            // L2 关闭：同参也不按 GlobalRepeatLimit 拦
            Assert.AreEqual(ELoopVerdict.Allow, Decide(guard, "clock", "{}", true, -1));
            Assert.AreEqual(ELoopVerdict.Allow, Decide(guard, "clock", "{}", true, -1));
            // NameCap 仍生效（profile 默认 2）
            Assert.AreEqual(ELoopVerdict.Warn, Decide(guard, "clock", "{}", true, -1));
            Assert.AreEqual(ELoopVerdict.Block, Decide(guard, "clock", "{}", true, -1));
        }

        [Test]
        public void RepeatAndNameClosed_Allows()
        {
            var guard = NewGuard(2);
            for (int i = 0; i < 6; i++)
                Assert.AreEqual(ELoopVerdict.Allow,
                    Decide(guard, "clock", "{}", true, -1, -1));
        }

        [Test]
        public void NonIdempotent_SecondCallBlocks_IgnoresRepeatLimitAndSays()
        {
            var guard = NewGuard(2);
            string sig1 = LoopGuard.BuildSignature("trade", "{\"item\":\"sword\",\"say\":\"给你剑\"}");
            string sig2 = LoopGuard.BuildSignature("trade", "{\"item\":\"sword\",\"say\":\"拿去吧\"}");

            Assert.AreEqual(sig1, sig2, "say 换了措辞不该绕开非幂等拦截");
            Assert.AreEqual(ELoopVerdict.Allow, guard.Decide(sig1, "trade", false, -1).Verdict);

            guard.NoteResult(sig1, "扣了 10 金", true, true);

            Assert.AreEqual(ELoopVerdict.Block, guard.Decide(sig1, "trade", false, -1).Verdict,
                "非幂等阈值恒为 1，RepeatLimit=-1 也照样拦");
        }

        [Test]
        public void NonIdempotent_FailedExecutionKeepsQuota()
        {
            var guard = NewGuard(2);
            var sig = LoopGuard.BuildSignature("trade", "{\"item\":\"sword\"}");

            Assert.AreEqual(ELoopVerdict.Allow, guard.Decide(sig, "trade", false, 0).Verdict);
            guard.NoteResult(sig, "[Action Failed] timeout", true, false);

            Assert.AreEqual(ELoopVerdict.Allow, guard.Decide(sig, "trade", false, 0).Verdict,
                "一次超时不该废掉该动作的终身配额");
        }

        [Test]
        public void NonIdempotent_CountersSurviveTurnBoundary()
        {
            var guard = NewGuard(2);
            var sig = LoopGuard.BuildSignature("trade", "{\"item\":\"sword\"}");

            guard.Decide(sig, "trade", false, 0);
            guard.NoteResult(sig, "成交", true, true);

            guard.BeginTurn();

            Assert.AreEqual(ELoopVerdict.Block, guard.Decide(sig, "trade", false, 0).Verdict,
                "轮内清零会让扣钱动作在两轮里各扣一次");
        }

        [Test]
        public void RepeatableSideEffect_AllowsSameSignatureInLaterTurn()
        {
            var guard = NewGuard(2);
            var sig = LoopGuard.BuildSignature("move_to_player", "{}");

            Assert.AreEqual(ELoopVerdict.Allow,
                guard.Decide(sig, "move_to_player", false, 0, 0, true).Verdict);
            guard.NoteResult(sig, "已开始移动", true, true);
            guard.BeginTurn();

            Assert.AreEqual(ELoopVerdict.Allow,
                guard.Decide(sig, "move_to_player", false, 0, 0, true).Verdict,
                "可重复副作用不能被 session 一次性配额永久拦截");
        }

        [Test]
        public void SameResultHash_BlocksFromNextCall()
        {
            var guard = NewGuard(2);
            const string args = "{\"x\":1}";
            var sig = LoopGuard.BuildSignature("observe", args);

            Assert.AreEqual(ELoopVerdict.Allow, guard.Decide(sig, "observe", true, 0).Verdict);
            guard.NoteResult(sig, "桌上有一把钥匙", true, true);

            Assert.AreEqual(ELoopVerdict.Allow, guard.Decide(sig, "observe", true, 0).Verdict);
            guard.NoteResult(sig, "桌上有一把钥匙", true, true);

            Assert.AreEqual(ELoopVerdict.Block, Decide(guard, "observe", args),
                "同名同参且结果相同 = 零新信息，跳过 WARN 直接 BLOCK");
        }

        [Test]
        public void SameResultHash_NotClearedByTurnBoundary()
        {
            var guard = NewGuard(2);
            var sig = LoopGuard.BuildSignature("observe", "{\"x\":1}");

            guard.Decide(sig, "observe", true, 0);
            guard.NoteResult(sig, "同一句", true, true);
            guard.Decide(sig, "observe", true, 0);
            guard.NoteResult(sig, "同一句", true, true);

            guard.BeginTurn();
            Assert.AreEqual(ELoopVerdict.Allow, guard.Decide(sig, "observe", true, 0).Verdict,
                "L2 黑名单仅本轮有效：下一轮同名同参可能是合理重试");
        }

        [Test]
        public void Locks_SameKeyQueuesInOrderAndDrainsOnDispose()
        {
            var first = AgentResourceLocks.TryAcquireAsync("shop:1", 5, "npc#a", 1, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.IsNotNull(first, "无人占用时应当直接拿到锁");

            // 下面两条不 await：EditMode 没有帧循环可推，只断言排队与移交的簿记
            AgentResourceLocks.TryAcquireAsync("shop:1", 5, "npc#b", 1, CancellationToken.None);
            AgentResourceLocks.TryAcquireAsync("shop:1", 5, "npc#c", 1, CancellationToken.None);
            Assert.AreEqual(2, AgentResourceLocks.WaitingCount("shop:1"), "后到的排队在队尾");

            first.Dispose();
            first.Dispose();
            Assert.AreEqual(1, AgentResourceLocks.WaitingCount("shop:1"),
                "一次释放只移交一个人（Dispose 幂等，第二次不重复移交），剩下那个仍在队尾");
        }

        [Test]
        public void Locks_ZeroWaitFailsImmediatelyWhenBusy()
        {
            var held = AgentResourceLocks.TryAcquireAsync("shop:2", 5, "npc#a", 1, CancellationToken.None)
                .GetAwaiter().GetResult();

            var busy = AgentResourceLocks.TryAcquireAsync("shop:2", 0, "npc#b", 1, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsNull(busy, "LockWaitSeconds=0 表示不排队，占用中直接失败");
            Assert.AreEqual(0, AgentResourceLocks.WaitingCount("shop:2"));

            held.Dispose();
        }

        [Test]
        public void Locks_WaiterAbandonsWhenOwnerTokenIsCancelled()
        {
            var held = AgentResourceLocks.TryAcquireAsync("shop:3", 5, "npc#a", 1, CancellationToken.None)
                .GetAwaiter().GetResult();

            using var cts = new CancellationTokenSource();
            var waiting = AgentResourceLocks.TryAcquireAsync("shop:3", 5, "npc#b", 1, cts.Token);
            Assert.AreEqual(1, AgentResourceLocks.WaitingCount("shop:3"));

            cts.Cancel();
            Assert.AreEqual(0, AgentResourceLocks.WaitingCount("shop:3"), "叫停即退队，不留悬挂等待者");

            held.Dispose();
        }

        [Test]
        public void Locks_WaiterExpiresOnKernelClockNotWallClock()
        {
            var held = AgentResourceLocks.TryAcquireAsync("shop:4", 5, "npc#a", 1, CancellationToken.None)
                .GetAwaiter().GetResult();

            var waiting = AgentResourceLocks.TryAcquireAsync("shop:4", 5, "npc#b", 1, CancellationToken.None);
            Assert.AreEqual(1, AgentResourceLocks.WaitingCount("shop:4"));

            fakeNow += 6f;
            AgentResourceLocks.PumpKey("shop:4");

            Assert.AreEqual(0, AgentResourceLocks.WaitingCount("shop:4"), "推进内核时钟即可到期，不用真等");
            held.Dispose();
        }
    }
}
