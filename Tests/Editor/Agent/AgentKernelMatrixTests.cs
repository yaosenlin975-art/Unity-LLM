/*
 * Agent Tests — B 步轮次编排矩阵（设计稿测试矩阵未覆盖行）
 * 全部走 T3 脚手架：FakeKernelProvider 出脚本、FakeRunner 执行/挂起/再入、
 * FakeClock 推时钟，整轮同步收敛（EditMode 无帧循环）。
 * 「Provider 全挂」行由 AgentScaffoldSmokeTests.Smoke_ProviderFailure 覆盖，不在此重复。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Text;
using Lin.Runtime.Helper;
using LLM.Demo.Storage;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Storage;
using NUnit.Framework;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentKernelMatrixTests
    {
        private const string k_provider = "test_agent_matrix";
        private const string k_fallback = "（测试兜底）这一轮没有拿到回复。";

        private FakeKernelProvider provider;
        private FakeOutput output;
        private readonly PrefsAgentStateStore store = new();
        private readonly List<string> traceLines = new();
        private readonly List<AgentCore> agents = new();
        private readonly List<AgentProfile_SO> profiles = new();
        private readonly List<FakeRunner> runners = new();

        [SetUp]
        public void SetUp()
        {
            provider = new FakeKernelProvider(k_provider);
            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.RegisterProvider(provider);
            dispatcher.SetDefaultProvider(k_provider);

            FakeClock.ResetToStart();
            FakeClock.Install();
            AgentResourceLocks.Reset();

            output = new FakeOutput();
            AgentStateStores.Reset();
            traceLines.Clear();
            AgentTrace.OnEvent += HandleTraceEvent;
        }

        [TearDown]
        public void TearDown()
        {
            AgentTrace.OnEvent -= HandleTraceEvent;
            for (int i = 0; i < agents.Count; i++) agents[i].Dispose();
            for (int i = 0; i < profiles.Count; i++) FakeProfiles.Destroy(profiles[i]);
            FakeClock.Restore();
            AgentResourceLocks.Reset();
            for (int i = 0; i < agents.Count; i++) PurgeState(agents[i].SessionId);
            AgentStateStores.Reset();

            var dispatcher = LLMDispatcher.GetInstance();
            dispatcher.UnregisterProvider(k_provider);
            dispatcher.SetDefaultProvider(null);
            dispatcher.SetFallbackProvider(null);
        }

        private AgentCore NewAgent(string instanceId, FakeRunner runner,
            int perNameToolCallLimit = 4, string queryableHint = null)
        {
            var profile = FakeProfiles.Create("matrix", perNameToolCallLimit: perNameToolCallLimit);
            profile.QueryableHint = queryableHint ?? "";
            profiles.Add(profile);
            runner.Sink = output;
            runners.Add(runner);

            AgentActionRegistry.Register(typeof(AgentKernelMatrixTests).Assembly);
            var agent = new AgentCore(profile, instanceId, null, output, runner);
            agents.Add(agent);
            return agent;
        }

        /// <summary>注入真 ReflectionActionRunner：动作方法体真实执行（write_fact 等要写状态的用例用这个）</summary>
        private AgentCore NewAgentWithRealActions(string instanceId)
        {
            var profile = FakeProfiles.Create("matrix");
            profiles.Add(profile);

            AgentActionRegistry.Register(typeof(AgentKernelMatrixTests).Assembly);
            var agent = new AgentCore(profile, instanceId, null, output, new ReflectionActionRunner());
            agents.Add(agent);
            return agent;
        }

        private static LockLease HoldKey(string key)
        {
            return AgentResourceLocks.TryAcquireAsync(key, 5, "manual-holder", 1,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        private static string LastToolContent(LLMRequest request)
        {
            var messages = request.Messages;
            for (int i = messages.Count - 1; i >= 0; i--)
                if (messages[i].Role == "tool")
                    return messages[i].Content;
            return "";
        }

        private static int CountEventPrefix(List<string> events, string prefix)
        {
            int hit = 0;
            for (int i = 0; i < events.Count; i++)
                if (events[i].StartsWith(prefix, StringComparison.Ordinal))
                    hit++;
            return hit;
        }

        private static int IndexOfEvent(List<string> events, string item)
        {
            for (int i = 0; i < events.Count; i++)
                if (events[i] == item)
                    return i;
            return -1;
        }

        // ── 先说后做 ──────────────────────────────────────────────

        [Test]
        public void Say_PlaysBeforeRunnerEnters()
        {
            var runner = new FakeRunner();
            var agent = NewAgent("say", runner);
            provider.EnqueueToolCalls(new FakeCall("t_trade", "{\"item\":\"sword\",\"say\":\"我去拿剑\"}"));
            provider.EnqueueAnswer("拿到了");

            agent.Trigger("去");

            Assert.AreEqual("SAY:t_trade:我去拿剑", output.Events[0], "say 必须作为独立消息段先上屏");
            Assert.AreEqual("EXEC:t_trade", output.Events[1], "say 播出后动作才进 runner");
            Assert.AreEqual("拿到了", output.ConcatTokens(), "模型正文流不得混入动作 say");
            StringAssert.DoesNotContain("say", runner.ExecutedArgs[0], "say 是保留伪参数，交给 runner 前必须剥掉");
        }

        [Test]
        public void Say_StillCreatesBoundary_WhenModelAlreadyStreamedSameText()
        {
            var runner = new FakeRunner();
            var agent = NewAgent("say-boundary", runner);
            provider.EnqueueContentAndToolCalls("我这就过来。",
                new FakeCall("t_trade", "{\"item\":\"sword\",\"say\":\"我这就过来。\"}"));
            provider.EnqueueAnswer("我到了。");

            agent.Trigger("过来");

            CollectionAssert.Contains(output.Events, "SAY:t_trade:我这就过来。",
                "即使 say 已作为模型正文流出，动作边界也必须通知 UI 封口气泡");
            Assert.AreEqual("我这就过来。我到了。", output.ConcatTokens());
        }

        // ── 承诺收口 ──────────────────────────────────────────────

        [Test]
        public void Promise_ModelRound_NudgeInBackfill()
        {
            var runner = new FakeRunner { FailAll = true };
            var agent = NewAgent("promise-model", runner);
            provider.EnqueueToolCalls(new FakeCall("t_trade", "{\"item\":\"sword\",\"say\":\"我去拿剑\"}"));
            provider.EnqueueAnswer("剑没拿到，抱歉");

            agent.Trigger("去");

            Assert.IsTrue(LastToolContent(provider.Requests[1]).Contains("不要假装已完成"),
                "说了没做到且模型还有机会回答时，回填必须带承诺收口指令");
            Assert.AreEqual(0, CountEventPrefix(output.Events, "TOKEN:" + k_fallback),
                "模型圆路径内核不得抢播兜底句");
            Assert.AreEqual(EAgentOutcome.Completed, output.LastOutcome);
        }

        [Test]
        public void Promise_KernelCovers_OnInterrupt()
        {
            var runner = new FakeRunner { ReentryMode = FakeRunner.ReentryTriggerStale };
            var agent = NewAgent("promise-kernel", runner);
            provider.EnqueueToolCalls(new FakeCall("t_trade", "{\"item\":\"sword\",\"say\":\"我去拿剑\"}"));
            provider.EnqueueAnswer("旧轮的回答");
            provider.EnqueueAnswer("新轮的回答");

            agent.Trigger("去");

            Assert.AreEqual("cancelled", runner.LastReentryCtState, "叫停必须真的取消动作令牌");
            Assert.AreEqual(1, CountEventPrefix(output.Events, "FINISH:Stale:"), "旧轮按 Stale 收尾");
            Assert.AreEqual(1, CountEventPrefix(output.Events, "TOKEN:" + k_fallback),
                "被叫停的未兑现承诺由内核兜底，且只播一次");
            Assert.AreEqual(2, output.FinishCount, "旧轮 Stale 收尾 + 新轮正常收尾");
            Assert.AreEqual(EAgentOutcome.Completed, output.LastOutcome, "排队的新轮照常完成");
        }

        // ── 叫停与配额 ────────────────────────────────────────────

        [Test]
        public void Interrupt_QuotaPreserved_SameArgsRetriesNextRound()
        {
            var runner = new FakeRunner { ReentryMode = FakeRunner.ReentryTriggerStale };
            var agent = NewAgent("interrupt", runner);
            provider.EnqueueToolCalls(new FakeCall("t_trade", "{\"item\":\"sword\",\"say\":\"措辞一\"}"));
            provider.EnqueueAnswer("旧轮的回答");
            provider.EnqueueToolCalls(new FakeCall("t_trade", "{\"item\":\"sword\",\"say\":\"措辞二\"}"));
            provider.EnqueueAnswer("新轮完成");

            agent.Trigger("去");

            Assert.AreEqual(2, runner.ExecutedIds.Count,
                "被叫停的动作不吃配额：下一轮同参数必须还能执行（非幂等阈值恒 1）");
            Assert.AreEqual(runner.ExecutedArgs[0], runner.ExecutedArgs[1], "剥掉 say 后参数应完全一致");
            Assert.AreEqual(EAgentOutcome.Completed, output.LastOutcome);
        }

        [Test]
        public void ActionTimeout_QuotaPreserved_RetriesNextRound()
        {
            var runner = new FakeRunner { ReentryMode = FakeRunner.ReentryTimeoutAction };
            var agent = NewAgent("timeout", runner);
            provider.EnqueueToolCalls(new FakeCall("t_trade", "{\"item\":\"sword\",\"say\":\"我去\"}"));
            provider.EnqueueAnswer("这轮超时了");
            provider.EnqueueToolCalls(new FakeCall("t_trade", "{\"item\":\"sword\",\"say\":\"再去\"}"));
            provider.EnqueueAnswer("这轮成了");

            agent.Trigger("去");
            Assert.IsTrue(LastToolContent(provider.Requests[1]).Contains("[Action Failed] timeout"),
                "动作超时走 [Action Failed] timeout 回填");

            agent.Trigger("再试");
            Assert.AreEqual(2, runner.ExecutedIds.Count, "超时一次不废配额，同参数下一轮仍可执行");
        }

        // ── 墙钟与往返 ────────────────────────────────────────────

        [Test]
        public void WallClock_Timeout_FallsBack()
        {
            var runner = new FakeRunner { ReentryMode = FakeRunner.ReentryTimeoutWall };
            var agent = NewAgent("wallclock", runner);
            provider.EnqueueToolCalls(new FakeCall("t_look", "{\"x\":1}"));
            provider.EnqueueAnswer("不该被看到");

            agent.Trigger("看");

            Assert.AreEqual(EAgentOutcome.Timeout, output.LastOutcome,
                ZString.Format("墙钟到点整轮按 Timeout 收尾 [trace: {0}] [reqs={1}] [tokens={2}]",
                    TraceDump(), provider.RequestCount, output.ConcatTokens()));
            Assert.AreEqual(k_fallback, output.LastAnswer, "无累积文本时取 FallbackLines");
            Assert.AreEqual(1, provider.RequestCount, "第二次请求被取消路径截断（provider 抛 OCE 前不记账）");
        }

        [Test]
        public void MultiToolTurns_NotCutByMissingMaxToolRounds()
        {
            // ADR-023：删除工具次数总闸后，多步不同工具可跑完；不再存在 ToolRoundsExhausted
            var runner = new FakeRunner();
            var agent = NewAgent("multi", runner);
            provider.EnqueueToolCalls(new FakeCall("t_look", "{\"x\":1}"));
            provider.EnqueueToolCalls(new FakeCall("t_look", "{\"x\":2}"));
            provider.EnqueueAnswer("看完了");

            agent.Trigger("多看几眼");

            Assert.AreEqual(3, provider.RequestCount);
            Assert.AreEqual(EAgentOutcome.Completed, output.LastOutcome);
            Assert.AreEqual("看完了", output.LastAnswer);
        }

        [Test]
        public void FailedRound_WritesHistoryWithFallback()
        {
            var runner = new FakeRunner();
            var agent = NewAgent("failhist", runner);
            provider.EnqueueFailure("网络炸了");

            agent.Trigger("你好");

            Assert.AreEqual(EAgentOutcome.Failed, output.LastOutcome);
            Assert.AreEqual(1, agent.Session.Context.RoundCount, "失败轮也要入历史，玩家的话不能凭空消失");
            var history = agent.Session.Context.GetHistoryMessages();
            var last = history[history.Count - 1];
            Assert.AreEqual("assistant", last.Role);
            Assert.AreEqual(k_fallback, last.Content, "历史里该轮 assistant 是上屏的兜底句");
        }

        // ── 记忆与注入 ────────────────────────────────────────────

        [Test]
        public void Memory_InjectEvictRoundTrip()
        {
            var memory = new AgentMemory();
            memory.Configure(2);
            memory.Write("a", "1");
            memory.Write("b", "2");
            memory.Write("c", "3");
            Assert.AreEqual(2, memory.Count, "超容量按时间戳淘汰");
            Assert.AreEqual("b", memory.Facts[0].Key, "最旧的 a 被淘汰");

            memory.Write("b", "2v2");
            Assert.AreEqual("c", memory.Facts[0].Key, "命中更新移到末尾");

            var json = memory.ExportJson();
            var restored = new AgentMemory();
            restored.Configure(2);
            restored.ImportJson(json);
            Assert.AreEqual(memory.RenderBlock(), restored.RenderBlock(), "导出导入往返一致");

            var snapshot = memory.ExportSnapshot();
            snapshot.Facts[0].Value = "外部改动";
            Assert.AreNotEqual(snapshot.Facts[0].Value, memory.Facts[0].Value, "快照不能与内存共享可变对象");

            var restoredSnapshot = new AgentMemory();
            restoredSnapshot.ImportSnapshot(snapshot);
            snapshot.Facts[0].Value = "二次改动";
            Assert.AreNotEqual(snapshot.Facts[0].Value, restoredSnapshot.Facts[0].Value, "导入后不能持有 Prefs 缓存对象");
        }

        [Test]
        public void Memory_WriteFact_InjectedNextRound()
        {
            var agent = NewAgentWithRealActions("memory");
            provider.EnqueueToolCalls(new FakeCall("write_fact", "{\"key\":\"color\",\"value\":\"红色\"}"));
            provider.EnqueueAnswer("记住了");
            provider.EnqueueAnswer("第二条回答");

            agent.Trigger("记住颜色");

            Assert.AreEqual(1, agent.Memory.Count);

            // 注入每轮（turn）重拼：本 turn 写入的事实，从下一个 turn 的请求开始可见
            agent.Trigger("再聊一句");
            StringAssert.Contains("已知事实", provider.Requests[2].BuildSystemContent());
            StringAssert.Contains("红色", provider.Requests[2].BuildSystemContent(),
                "write_fact 后下一个 turn 的请求注入含该条");
        }

        // ── 状态落盘（ADR-014）────────────────────────────────────

        [Test]
        public void State_TemporaryInstanceWritesNothing()
        {
            AgentStateStores.Facts = store;
            AgentStateStores.History = store;
            var agent = NewAgentWithRealActions(null);
            provider.EnqueueAnswer("第一句");

            agent.Trigger("你好");

            Assert.IsFalse(agent.IsPersistent, "宿主没给 instanceId 就是临时实例");
            Assert.AreEqual(1, agent.Session.Context.RoundCount, "临时实例照常对话");
            Assert.IsNull(store.LoadHistory(agent.SessionId), "临时实例不落盘，否则每读一次档堆一个孤儿存档");
        }

        [Test]
        public void State_StableInstanceSavesAndPrefetches()
        {
            AgentStateStores.Facts = store;
            AgentStateStores.History = store;
            var agent = NewAgentWithRealActions("save");
            provider.EnqueueToolCalls(new FakeCall("write_fact", "{\"key\":\"color\",\"value\":\"红色\"}"));
            provider.EnqueueAnswer("记住了");

            agent.Trigger("记住颜色");

            Assert.AreEqual("红色", store.LoadFacts(agent.SessionId).Facts[0].Value, "write_fact 当场落盘");
            Assert.AreEqual("记住颜色", store.LoadHistory(agent.SessionId).Rounds[0].UserMessage, "轮末落历史");

            // 同 instanceId = 同 sessionId：构造期预加载要真把状态装回来，这才是读档那一跳
            var reloaded = NewAgentWithRealActions("save");
            Assert.AreEqual(1, reloaded.Memory.Count, "事实槽读档预加载");
            Assert.AreEqual(1, reloaded.Session.Context.RoundCount, "历史读档预加载");
        }

        private static void PurgeState(string sessionId)
        {
            PrefsHelper.DeleteKey<AgentFactsBlob>(sessionId);
            PrefsHelper.DeleteKey<AgentRoundsBlob>(sessionId);
        }

        [Test]
        public void Injection_PrefixStableAcrossRounds()
        {
            var runner = new FakeRunner();
            var agent = NewAgent("prefix", runner, queryableHint: "你可以查询：t_look");
            provider.EnqueueToolCalls(new FakeCall("t_look", "{\"x\":1}"));
            provider.EnqueueAnswer("第一轮");
            provider.EnqueueToolCalls(new FakeCall("t_look", "{\"x\":2}"));
            provider.EnqueueAnswer("第二轮");

            agent.Trigger("第一轮");
            agent.Trigger("第二轮");

            var first = provider.Requests[0].BuildSystemContent();
            Assert.IsNotEmpty(first, "system 内容不应为空（空对空比较是假绿）");
            Assert.AreEqual(first, provider.Requests[2].BuildSystemContent(),
                "事实槽与输入不变时，跨轮注入必须逐字节相等（没人往里塞时间/坐标）");
        }

        [Test]
        public void Declarations_StayOutOfToolRegistry()
        {
            var runner = new FakeRunner();
            var agent = NewAgent("decl", runner);
            provider.EnqueueAnswer("直接回答");

            agent.Trigger("hi");

            Assert.IsFalse(AgentToolRegistry.TryGet("t_look", out _), "[AgentAction] 绝不进 AgentToolRegistry");
            Assert.IsFalse(AgentToolRegistry.TryGet("write_fact", out _), "内置记忆动作也不进 AgentToolRegistry");

            var request = provider.Requests[0];
            int look = 0;
            int writeFact = 0;
            int forgetFact = 0;
            int searchPastConversation = 0;
            for (int i = 0; i < request.Tools.Count; i++)
            {
                var name = request.Tools[i].Function.Name;
                if (name == "t_look") look++;
                if (name == "write_fact") writeFact++;
                if (name == "forget_fact") forgetFact++;
                if (name == "search_past_conversation") searchPastConversation++;
                Assert.AreNotEqual("list_facts", name);
                Assert.AreNotEqual("search_memory", name);
            }
            Assert.AreEqual(1, look, "动作声明经 ExtraTools 恰好出现一次");
            Assert.AreEqual(1, writeFact);
            Assert.AreEqual(1, forgetFact);
            Assert.AreEqual(1, searchPastConversation);
        }

        // ── 资源锁 ────────────────────────────────────────────────

        [Test]
        public void LockQueue_TwoAgentsEnterRunnerSerially()
        {
            var runnerA = new FakeRunner();
            var runnerB = new FakeRunner();
            var agentA = NewAgent("lockA", runnerA);
            var agentB = NewAgent("lockB", runnerB);
            runnerA.ArmHold("t_lock");
            provider.EnqueueToolCalls(new FakeCall("t_lock", "{\"item\":\"gem\"}"));
            provider.EnqueueToolCalls(new FakeCall("t_lock", "{\"item\":\"gem\"}"));
            provider.EnqueueAnswer("A 完成");
            provider.EnqueueAnswer("B 完成");

            int executedBaseline = CountTotalExecuted();

            agentA.Trigger("a");
            Assert.IsTrue(runnerA.IsHolding, "A 挂在 runner 里占着锁");

            agentB.Trigger("b");
            Assert.AreEqual(1, AgentResourceLocks.WaitingCount("item:gem"), "B 在队尾排队");
            Assert.AreEqual(1, CountTotalExecuted() - executedBaseline, "B 未获锁前不得进 runner（只有 A 进过）");

            runnerA.ReleaseHold(AgentActionResult.Success("好了"));

            Assert.AreEqual(0, AgentResourceLocks.WaitingCount("item:gem"),
                ZString.Format("释放后队伍清空 [trace: {0}]", TraceDump()));
            Assert.AreEqual(2, CountTotalExecuted() - executedBaseline, "A 释放后 B 才进入 runner，顺序串行");
            Assert.AreEqual(4, provider.RequestCount, "两个 agent 各两轮请求");
            Assert.AreEqual(2, output.FinishCount);
            Assert.AreEqual(EAgentOutcome.Completed, output.LastOutcome);
        }

        [Test]
        public void LockWait_Timeout_FailsWithoutQuota()
        {
            var runner = new FakeRunner();
            var agent = NewAgent("lockwait", runner);
            var lease = HoldKey("item:gem");
            provider.EnqueueToolCalls(new FakeCall("t_lock", "{\"item\":\"gem\"}"));
            provider.EnqueueAnswer("这轮放弃了");
            provider.EnqueueToolCalls(new FakeCall("t_lock", "{\"item\":\"gem\"}"));
            provider.EnqueueAnswer("这轮成了");

            agent.Trigger("a");
            FakeClock.Advance(6f);
            AgentResourceLocks.PumpKey("item:gem");

            Assert.AreEqual(0, runner.ExecutedIds.Count, "排队失败的等待方从未进 runner");
            StringAssert.Contains("未获得锁", LastToolContent(provider.Requests[1]),
                "锁等待超时回填 [Action Failed]");
            Assert.AreEqual(0, AgentResourceLocks.WaitingCount("item:gem"));

            lease.Dispose();
            agent.Trigger("b");
            Assert.AreEqual(1, runner.ExecutedIds.Count, "锁释放后同参数可再执行");
        }

        [Test]
        public void LockWait_DequeueOnInterrupt()
        {
            var runner = new FakeRunner();
            var agent = NewAgent("deque", runner);
            var lease = HoldKey("item:gem");
            provider.EnqueueToolCalls(new FakeCall("t_lock", "{\"item\":\"gem\"}"));
            provider.EnqueueAnswer("旧轮回答");
            provider.EnqueueAnswer("新轮回答");

            agent.Trigger("a");
            Assert.AreEqual(1, AgentResourceLocks.WaitingCount("item:gem"), "等待者在队上");

            agent.Trigger("新输入");

            Assert.AreEqual(0, AgentResourceLocks.WaitingCount("item:gem"), "叫停即退队，无悬挂等待者");
            Assert.AreEqual(0, runner.ExecutedIds.Count, "被退队的动作从未执行");
            Assert.AreEqual(1, CountEventPrefix(output.Events, "FINISH:Stale:"), "旧轮按 Stale 收尾");

            lease.Dispose();
        }

        private void HandleTraceEvent(AgentTraceEvent e)
        {
            traceLines.Add(ZString.Format("{0} R{1} {2} {3}", e.SessionId, e.Round, e.Kind, e.Detail));
        }

        /// <summary>失败消息里附带轨迹：断言挂了能直接看出内核走了哪条路</summary>
        private string TraceDump()
        {
            return ZString.Join(" | ", traceLines);
        }

        private int CountTotalExecuted()
        {
            int total = 0;
            for (int i = 0; i < runners.Count; i++) total += runners[i].ExecutedIds.Count;
            return total;
        }
    }
}
