/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 轮次轨迹                │
 * │ Remark      : 只读诊断出口，沙盒窗口与日志窗 │
 * │               口订阅，内核不持有订阅者        │
 * │ ClassName   : AgentTrace                    │
 * └────────────────────────────────────────────┘
 */

using System;

namespace LLM.Runtime.Agent
{
    public enum EAgentTraceKind
    {
        TurnStarted,
        TurnFinished,
        /// <summary>循环检测的 WARN / BLOCK / 叫停，detail 里带档位</summary>
        ActionIntercepted,
        Notice
    }

    public readonly struct AgentTraceEvent
    {
        public readonly string SessionId;
        public readonly EAgentTraceKind Kind;
        public readonly string Detail;

        /// <summary>该 agent 的轮次序号线，用来把同一轮的多条事件串起来</summary>
        public readonly int Round;

        /// <summary>内核时钟（Time.unscaledTime 或单测假时钟），单位秒</summary>
        public readonly float AtSeconds;

        public AgentTraceEvent(string sessionId, EAgentTraceKind kind, string detail, int round)
        {
            SessionId = sessionId;
            Kind = kind;
            Detail = detail;
            Round = round;
            AtSeconds = AgentCore.NowSeconds();
        }
    }

    /// <summary>
    /// 全局静态轨迹事件：agent 数量不多，且沙盒窗口本来就要看所有 agent 混排的时间线。
    /// 结构体带 SessionId 供窗口分列，内核不缓存历史事件（窗口自己限长）。
    /// </summary>
    public static class AgentTrace
    {
        public static event Action<AgentTraceEvent> OnEvent;

        public static void Emit(string sessionId, EAgentTraceKind kind, string detail, int round)
        {
            OnEvent?.Invoke(new AgentTraceEvent(sessionId, kind, detail, round));
        }
    }
}
