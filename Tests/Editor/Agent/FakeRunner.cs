/*
 * Agent 测试脚手架 — 动作执行器
 * 记录执行序列 + 脚本化结果；再入模式（动作内部 Trigger/推进假时钟后 Tick）
 * 让 Stale/超时类矩阵行整体同步收敛（设计稿「动作内部再入」模式）
 */

using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Agent;

namespace LLM.Tests.Editor.Agent
{
    public sealed class FakeRunner : IAgentActionRunner
    {
        /// <summary>动作内部 Trigger 新输入：当前轮按 Stale 收尾（不依赖时钟）</summary>
        public const string ReentryTriggerStale = "triggerStale";

        /// <summary>动作内部推进假时钟越过动作截止再 Tick：走 [Action Failed] timeout</summary>
        public const string ReentryTimeoutAction = "timeoutAction";

        /// <summary>动作内部推进假时钟越过墙钟再 Tick：CancelTurn 整轮超时（OCE + FallbackLines）</summary>
        public const string ReentryTimeoutWall = "timeoutWall";

        private readonly Dictionary<string, string> results = new();
        private UniTaskCompletionSource<AgentActionResult> holdSource;
        private CancellationTokenSource holdCts;
        private AgentActionResult holdResult;
        private bool holdOutstanding;

        public string ReentryMode = "none";
        public string DefaultResult = "ok";
        public bool FailAll;

        public string HoldActionId = "";

        /// <summary>EXEC 事件写入的共享事件流（通常指向被测 FakeOutput.Events），供先说后做时序断言</summary>
        public FakeOutput Sink;

        /// <summary>再入动作内部 Trigger 之后 ct 的快照：cancelled = 叫停真的摸到了动作令牌</summary>
        public string LastReentryCtState = "";

        public readonly List<string> ExecutedIds = new();
        public readonly List<string> ExecutedArgs = new();

        /// <summary>挂起中的动作还没返回（hold 被领取后到 ReleaseHold 前为 true）</summary>
        public bool IsHolding => holdOutstanding;

        /// <summary>按 actionId 定制成功结果；未登记的动作返回 DefaultResult</summary>
        public void Script(string actionId, string result)
        {
            results[actionId] = result;
        }

        /// <summary>
        /// 武装挂起：下一个命中的 actionId 停在 runner 里不返回（锁被占用、轮被挂起），
        /// 测试主体推进外部事件（新输入 / 推时钟 Tick）后 ReleaseHold 收敛。
        /// 恢复必须走 ct.Register 回调 + Cancel()——与 AgentResourceLocks 的等待者同款，
        /// 实测这条路是内联恢复；直接对 source TrySetResult 反而不同步。
        /// </summary>
        public bool ArmHold(string actionId)
        {
            if (holdSource != null) return false;

            HoldActionId = actionId;
            holdSource = new UniTaskCompletionSource<AgentActionResult>();
            holdCts = new CancellationTokenSource();
            return true;
        }

        public bool ReleaseHold(AgentActionResult result)
        {
            if (holdSource == null) return false;

            holdResult = result;
            holdCts.Cancel();
            holdCts.Dispose();
            holdCts = null;
            return true;
        }

        public void Clear()
        {
            results.Clear();
            ExecutedIds.Clear();
            ExecutedArgs.Clear();
            ReentryMode = "none";
            FailAll = false;
            HoldActionId = "";
            holdSource = null;
            holdOutstanding = false;
            LastReentryCtState = "";
        }

        /// <summary>hold 的完成回调：经 cts.Cancel() 内联触发（见 ArmHold 注释）</summary>
        private void CompleteHold()
        {
            var pending = holdSource;
            holdSource = null;
            holdOutstanding = false;
            pending?.TrySetResult(holdResult);
        }

        public UniTask<AgentActionResult> RunAsync(string actionId, string argsJson,
            AgentActionContext ctx, CancellationToken ct)
        {
            ExecutedIds.Add(actionId);
            ExecutedArgs.Add(argsJson);
            if (Sink != null) Sink.Events.Add("EXEC:" + actionId);

            // 再入必须发生在记账之后：动作确实「执行过」才顶掉/超时，语义才成立
            if (ReentryMode == ReentryTriggerStale)
            {
                ctx.Agent.Trigger("【测试再入】新输入顶掉当前轮");
                LastReentryCtState = ct.IsCancellationRequested ? "cancelled" : "live";
            }
            else if (ReentryMode == ReentryTimeoutAction)
            {
                // 16s：越过默认 15s 动作截止，但不越 90s 墙钟——不然先炸的是墙钟不是动作
                FakeClock.Advance(16f);
                ctx.Agent.Tick();
            }
            else if (ReentryMode == ReentryTimeoutWall)
            {
                // 200s：直接越过 90s 墙钟，Tick 走 CancelTurn（整轮超时）而非动作超时
                FakeClock.Advance(200f);
                ctx.Agent.Tick();
            }

            if (holdSource != null && actionId == HoldActionId)
            {
                holdCts.Token.Register(CompleteHold);
                holdOutstanding = true;
                return holdSource.Task;
            }

            string result;
            if (!results.TryGetValue(actionId, out var scripted))
            {
                result = DefaultResult;
            }
            else
            {
                result = scripted;
            }

            var outcome = FailAll ? AgentActionResult.Failure(result) : AgentActionResult.Success(result);
            return new UniTask<AgentActionResult>(outcome);
        }
    }
}
