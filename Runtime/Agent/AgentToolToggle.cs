/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 工具开关条目             │
 * │ Remark      : 挂在 AgentHost 上，按实例控制  │
 * │               该 agent 可见与可执行的工具    │
 * │ ClassName   : AgentToolToggle               │
 * └────────────────────────────────────────────┘
 */

using System;

namespace LLM.Runtime.Agent
{
    /// <summary>工具来源，仅用于 Inspector 分组显示，不参与过滤判定</summary>
    public enum EAgentToolSource
    {
        /// <summary>全局 static [AgentAction]</summary>
        Action = 0,

        /// <summary>全局 static [AgentTool]</summary>
        PublicTool = 1,

        /// <summary>本体实例 [AgentTool]（宿主层级）</summary>
        SelfTool = 2,

        /// <summary>本体实例 [AgentAction]（宿主层级）</summary>
        SelfAction = 3
    }

    [Serializable]
    public sealed class AgentToolToggle
    {
        public string ToolId = "";
        public EAgentToolSource Source = EAgentToolSource.Action;
        public bool Enabled = true;
    }
}
