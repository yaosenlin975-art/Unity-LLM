/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 运行内核                │
 * │ Remark      : 触发与串行排队、代际作废、墙钟 │
 * │               额度、轮次编排与轨迹          │
 * │ ClassName   : AgentCore                     │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Storage;
using UnityEngine;

namespace LLM.Runtime.Agent
{
    /// <summary>
    /// 每个 agent 一份，由宿主（NPC MonoBehaviour / 助手 UI / 沙盒窗口）持有，不是单例。
    /// 事件驱动：Trigger/Notify 起轮；在飞期间的新输入不抢占 HTTP，只作废旧轮产出并排队。
    /// </summary>
    public sealed class AgentCore
    {
        /// <summary>
        /// 内核唯一的取时刻入口。三类超时一律「拿 NowSeconds 与截止时刻比较后主动 Cancel」，
        /// 不用 CancellationTokenSource.CancelAfter（线程池真实计时器，假时钟推不动）。
        /// EditMode 单测用反射整体替换这个字段。
        /// </summary>
        internal static Func<float> NowSeconds = ReadUnscaledTime;

        private static float ReadUnscaledTime() => Time.unscaledTime;

        private readonly AgentProfile_SO profile;
        private readonly IWorldContextProvider world;
        private readonly IAgentOutput output;
        private readonly LLMSession session;
        private readonly AgentToolSet toolSet;
        private readonly LoopGuard loopGuard = new();
        private readonly AgentActionExecutor executor;
        private readonly System.Text.StringBuilder turnText = new();
        private readonly List<PromiseEntry> promises = new();

        private int turnGeneration;
        private int activeGeneration;
        private int roundSerial;
        private bool turnInFlight;

        private string pendingInput;

        private CancellationTokenSource turnCts;
        private float turnDeadline;
        private float longRunningPausedAt;

        public string SessionId { get; }

        public string InstanceId { get; }

        /// <summary>宿主给了稳定 instanceId 才为真。临时实例既不读也不写存储（ADR-014）。</summary>
        public bool IsPersistent { get; }

        public AgentProfile_SO Profile => profile;

        public AgentMemory Memory { get; } = new();

        public LoopGuard LoopGuard => loopGuard;

        public LLMSession Session => session;

        /// <summary>该 agent 的成员工具/动作集合；宿主未提供时为 null（纯全局行为）</summary>
        public AgentToolSet ToolSet => toolSet;

        public int TurnGeneration => turnGeneration;

        public bool IsBusy => turnInFlight;

        public AgentCore(AgentProfile_SO profile, string instanceId, IWorldContextProvider ctx,
            IAgentOutput output, IAgentActionRunner runner = null, IAgentToolGate toolGate = null,
            AgentToolSet toolSet = null)
        {
            this.profile = profile;
            this.world = ctx;
            this.output = output;
            this.toolSet = toolSet;

            // key 留空时按人设派生并查重（ADR-012），Validate 的必填检查退化为最后防线
            profile?.EnsureKey();

            if (profile != null && !profile.Validate(out string profileError))
                Log.Error(nameof(AgentCore),
                    ZString.Format("AgentProfile_SO「{0}」配置不合法: {1}", profile.name, profileError));

            IsPersistent = !string.IsNullOrEmpty(instanceId);
            InstanceId = IsPersistent ? instanceId : Guid.NewGuid().ToString("N");
            SessionId = ZString.Format("{0}#{1}", profile != null ? profile.ProfileKey : "agent", InstanceId);

            session = new LLMSession(SessionId, profile?.BuildSystemPrompt(), profile?.CompressionConfig)
            {
                Temperature = profile?.Temperature ?? 0.8f,
                MaxTokens = profile?.MaxTokens ?? 1024
            };
            // 历史只在轮末写一次：压缩落后于落盘是 ADR-014 §4 明确接受的口径，
            // 不订阅 Context.Changed 再补一次写——那会让压缩轮一次触发两回全量重写

            // 宿主门控：声明与执行两层都读它，null 表示全部放行
            session.ToolGate = toolGate;

            Memory.Configure(profile?.FactSlotMaxCount ?? 60);
            loopGuard.Configure(profile?.GlobalRepeatLimit ?? 2, profile?.PerNameToolCallLimit ?? 4);

            if (profile != null && profile.TurnDeadlineSeconds <
                AgentProfile_SO.k_assumedToolStepsForDeadlineHint *
                (profile.ActionTimeoutSeconds + profile.LockWaitSeconds + profile.ExpectedLlmRttSeconds))
            {
                Log.Warning(nameof(AgentCore),
                    ZString.Format("{0} {1}", SessionId, profile.DescribeDeadlineHint()));
            }

            executor = new AgentActionExecutor(this, runner ?? new NullActionRunner());
            session.ToolExecutor = executor;

            AgentActionRegistry.RegisterSelfActions();
            BuildExtraTools();

            if (IsPersistent)
                PrefetchState();
            else
                Log.Warning(nameof(AgentCore),
                    $"宿主未提供 instanceId，{SessionId} 按临时实例运行：记忆与对话不落盘");
        }

        /// <summary>
        /// 声明合并：成员工具/动作优先入列，随后全局动作，按名去重。
        /// 全局静态 [AgentTool] 由 LLMSession 从注册表声明，不在此列。
        /// </summary>
        private void BuildExtraTools()
        {
            var seen = new HashSet<string>();
            toolSet?.BuildDeclarations(session.ExtraTools, seen);

            var globalActions = AgentActionRegistry.BuildDeclarations();
            for (int i = 0; i < globalActions.Count; i++)
            {
                var tool = globalActions[i];
                if (seen.Add(tool.Function.Name))
                    session.ExtraTools.Add(tool);
            }
        }

        /// <summary>玩家/用户输入。在飞时覆盖 pending 单槽，等当前轮自然收尾</summary>
        public void Trigger(string input) => Enqueue(input, false);

        /// <summary>世界事件。与 Trigger 共用同一槽与同一作废规则，起轮时前缀「【事件】」</summary>
        public void Notify(string worldEvent) => Enqueue(worldEvent, true);

        /// <summary>历史为空时写入一次 assistant 开场白，不发起 LLM 请求。</summary>
        public bool TrySeedAssistantMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || turnInFlight || session.Context.RoundCount != 0)
                return false;

            session.Context.AddRound(string.Empty, text);
            return true;
        }

        private void Enqueue(string text, bool isEvent)
        {
            if (string.IsNullOrEmpty(text)) return;

            var input = isEvent ? ZString.Concat("【事件】", text) : text;

            if (turnInFlight)
            {
                // 不抢占在飞 HTTP：只把代际抬上去，让旧产出作废、可打断动作先停
                turnGeneration++;
                executor.InterruptCurrent();
                pendingInput = input;
                return;
            }

            StartTurn(input);
        }

        /// <summary>
        /// 时钟泵。Play 模式由宿主 AgentHost.Update 每帧调一次；单测替换 NowSeconds 之后手工调。
        /// 墙钟与动作超时都靠这里到期，因为内核不用真实计时器。
        /// </summary>
        public void Tick()
        {
            if (!turnInFlight) return;

            float now = NowSeconds();

            if (now >= turnDeadline)
            {
                CancelTurn("墙钟超时");
                return;
            }

            executor.PumpActionDeadline(now);
        }

        public void Dispose()
        {
            CancelTurn("agent 销毁");
            turnCts?.Dispose();
            turnCts = null;
        }

        #region 轮次编排

        private void StartTurn(string input)
        {
            turnGeneration++;
            activeGeneration = turnGeneration;
            roundSerial++;

            turnInFlight = true;
            turnText.Clear();
            promises.Clear();
            loopGuard.BeginTurn();
            executor.ResetTurnState();

            turnDeadline = NowSeconds() + (profile?.TurnDeadlineSeconds ?? 90);
            turnCts?.Dispose();
            turnCts = new CancellationTokenSource();

            AgentTrace.Emit(SessionId, EAgentTraceKind.TurnStarted, input, roundSerial);
            RunTurnAsync(input, turnCts.Token).Forget();
        }

        private async UniTaskVoid RunTurnAsync(string input, CancellationToken ct)
        {
            var outcome = EAgentOutcome.Completed;
            string answer;

            try
            {
                answer = await session.AskAsync(input, HandleChunk, BuildInjection(), false, ct);

                // session 对轮间取消是优雅返回而非抛 OCE：墙钟到点落在工具轮之间时，
                // 不在这里补映射，超时会被记成 Completed，FallbackLines 也永远轮不到
                if (ct.IsCancellationRequested)
                    outcome = EAgentOutcome.Timeout;
            }
            catch (OperationCanceledException)
            {
                outcome = turnGeneration == activeGeneration ? EAgentOutcome.Timeout : EAgentOutcome.Stale;
                answer = null;
            }
            catch (Exception ex)
            {
                outcome = EAgentOutcome.Failed;
                answer = null;
                Log.Warning(nameof(AgentCore),
                    ZString.Format("{0} 本轮请求失败: {1}", SessionId, ex.Message));
            }

            FinishTurn(input, answer, outcome);
        }

        private void FinishTurn(string input, string answer, EAgentOutcome outcome)
        {
            turnInFlight = false;
            executor.ClearActionSlot();

            // 被新输入顶掉：产出丢弃、不写历史，只把半截气泡收住
            if (activeGeneration != turnGeneration)
            {
                UnfulfilledPromiseFallback();
                var staleText = turnText.Length > 0 ? turnText.ToString() : answer;
                output?.OnTurnFinished(staleText, EAgentOutcome.Stale);
                AgentTrace.Emit(SessionId, EAgentTraceKind.TurnFinished,
                    ZString.Format("Stale | {0}", staleText), roundSerial);
                DrainPending();
                return;
            }

            if (outcome == EAgentOutcome.Completed)
            {
                if (executor.AbortRequested) outcome = EAgentOutcome.LoopAborted;
            }

            var finalText = ResolveFinalText(answer, outcome);

            // 每一轮都入历史，含 LoopAborted：玩家那句话不能凭空消失，否则下一轮像没听见。
            // 被硬拦的轮产出不可信，但那句话是可信的——它决定下一轮的上下文对不对
            session.Context.AddRound(input, finalText);
            SaveHistory();

            AgentTrace.Emit(SessionId, EAgentTraceKind.TurnFinished,
                ZString.Format("{0} | {1}", outcome, finalText), roundSerial);
            output?.OnTurnFinished(finalText, outcome);

            if (outcome == EAgentOutcome.Timeout || outcome == EAgentOutcome.LoopAborted)
                UnfulfilledPromiseFallback();

            DrainPending();
        }

        private void DrainPending()
        {
            if (pendingInput == null) return;

            var next = pendingInput;
            pendingInput = null;
            StartTurn(next);
        }

        /// <summary>AskAsync 抛异常时只给异常不给文本，所以优先用内核自己攒的半截</summary>
        private string ResolveFinalText(string answer, EAgentOutcome outcome)
        {
            if (!string.IsNullOrEmpty(answer)) return answer;
            if (turnText.Length > 0) return turnText.ToString();
            // Completed 不编话；Timeout/Failed/LoopAborted 等空文本走 FallbackLines（ADR-023）
            if (outcome == EAgentOutcome.Completed) return "";

            return PickFallback();
        }

        private void HandleChunk(LLMStreamChunk chunk)
        {
            if (string.IsNullOrEmpty(chunk.ContentDelta)) return;

            turnText.Append(chunk.ContentDelta);
            if (activeGeneration == turnGeneration)
                output?.OnToken(chunk.ContentDelta);
        }

        private void CancelTurn(string reason)
        {
            if (turnCts == null || turnCts.IsCancellationRequested) return;

            AgentTrace.Emit(SessionId, EAgentTraceKind.Notice, reason, roundSerial);
            turnCts.Cancel();
        }

        #endregion

        #region 状态存储

        /// <summary>读只在构造期发生一次，必须赶在第一轮之前；宿主之后自行 Import 会覆盖这里的预加载。</summary>
        private void PrefetchState()
        {
            Memory.ImportSnapshot(AgentStateStores.Facts.LoadFacts(SessionId));
            session.Context.ImportSnapshot(AgentStateStores.History.LoadHistory(SessionId));
        }

        /// <summary>清空会话历史并落盘。事实槽不动——清的是对话记录，不是 NPC 的记忆。</summary>
        public void ClearHistory()
        {
            session.Context.Clear();
            session.Context.ClearArchive();
            SaveHistory();
        }

        private void SaveHistory()
        {
            if (!IsPersistent) return;

            // ct 传 default 而不是 TurnToken：轮已经收尾，超时或作废都不该把这一笔写带走
            AgentStateStores.History
                .SaveHistoryAsync(SessionId, session.Context.ExportSnapshot(), default)
                .Forget();
        }

        #endregion

        #region 注入与承诺

        /// <summary>
        /// 每轮重拼的三段：事实槽 → 核心快照 → QueryableHint。
        /// 顺序固定，且最终拼在同一条 system 消息尾部，靠"前缀逐字节稳定"省缓存。
        /// </summary>
        private string BuildInjection()
        {
            using var sb = ZString.CreateStringBuilder();

            var facts = Memory.RenderBlock();
            if (!string.IsNullOrEmpty(facts))
            {
                sb.Append(facts);
                sb.Append("\n");
            }

            var snapshot = world?.GetCoreSnapshot();
            if (!string.IsNullOrEmpty(snapshot))
            {
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(snapshot);
            }

            var hint = profile?.QueryableHint;
            if (!string.IsNullOrEmpty(hint))
            {
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(hint);
            }

            return sb.ToString();
        }

        internal int TurnTextLength => turnText.Length;

        internal bool IsCurrentGeneration(int generation) => generation == turnGeneration;

        internal int CurrentGeneration => activeGeneration;

        internal int RoundSerial => roundSerial;

        /// <summary>播出独立 say 段并登记承诺；turnText 仍保留它，供历史与模型上下文记住已说过的话</summary>
        internal void PlaySay(string actionId, string say)
        {
            if (string.IsNullOrEmpty(say)) return;

            // 有些模型会把 say 同时作为 tool_call 前的正文流出来。历史只记一次，
            // 但边界事件仍必须发出，UI 才能在动作开始处封口气泡。
            if (!turnText.ToString().EndsWith(say, StringComparison.Ordinal))
                turnText.Append(say);
            output?.OnSay(actionId, say);
            promises.Add(new PromiseEntry(actionId, say));
        }

        internal void FulfillPromise(string actionId)
        {
            for (int i = 0; i < promises.Count; i++)
            {
                if (promises[i].ActionId != actionId) continue;
                promises.RemoveAt(i);
                return;
            }
        }

        /// <summary>取该动作已播出的 say 原文；没播过返回 null。失败回填时用它拼承诺收口指令</summary>
        internal string PeekPromise(string actionId)
        {
            foreach (var entry in promises)
                if (entry.ActionId == actionId) return entry.Say;
            return null;
        }

        /// <summary>说了却没做成，且本轮产出已无意义：内核立即播一次兜底句</summary>
        private void UnfulfilledPromiseFallback()
        {
            if (promises.Count == 0) return;

            promises.Clear();
            var line = PickFallback();
            if (string.IsNullOrEmpty(line)) return;

            turnText.Append(line);
            output?.OnToken(line);
        }

        private string PickFallback()
        {
            var lines = profile?.FallbackLines;
            if (lines == null || lines.Length == 0) return "";

            // 固定取第一条而不是随机：兜底句是配置出来的口径，随机会让单测没法断言
            return lines[0];
        }

        #endregion

        #region 额度与动作槽

        /// <summary>普通动作与锁等待都扣本轮额度</summary>
        internal int ActionTimeoutSeconds => profile?.ActionTimeoutSeconds ?? 15;

        internal int LongActionTimeoutSeconds => profile?.LongActionTimeoutSeconds ?? 60;

        internal int LockWaitSeconds => profile?.LockWaitSeconds ?? 5;

        internal CancellationToken TurnToken => turnCts != null ? turnCts.Token : CancellationToken.None;

        /// <summary>LongRunning 动作的执行期不扣额度：记下起点，结束时把墙钟整体后移</summary>
        internal void BeginLongRunningBudget()
        {
            longRunningPausedAt = NowSeconds();
        }

        internal void EndLongRunningBudget()
        {
            turnDeadline += NowSeconds() - longRunningPausedAt;
        }

        #endregion

        private readonly struct PromiseEntry
        {
            public readonly string ActionId;
            public readonly string Say;

            public PromiseEntry(string actionId, string say)
            {
                ActionId = actionId;
                Say = say;
            }
        }
    }
}
