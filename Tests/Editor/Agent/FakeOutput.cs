/*
 * Agent 测试脚手架 — 输出探针
 * 按 TOKEN / FINISH 记录事件序列，供「先说后做」时序与兜底台词类用例断言
 */

using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime.Agent;

namespace LLM.Tests.Editor.Agent
{
    public sealed class FakeOutput : IAgentOutput
    {
        public readonly List<string> Events = new();

        public int FinishCount { get; private set; }
        public EAgentOutcome LastOutcome { get; private set; }
        public string LastAnswer { get; private set; }

        public void OnToken(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            Events.Add("TOKEN:" + delta);
        }

        public void OnSay(string actionId, string say)
        {
            if (string.IsNullOrEmpty(say)) return;
            Events.Add("SAY:" + actionId + ":" + say);
        }

        public void OnTurnFinished(string answer, EAgentOutcome outcome)
        {
            FinishCount++;
            LastOutcome = outcome;
            LastAnswer = answer;
            Events.Add("FINISH:" + outcome + ":" + answer);
        }

        /// <summary>所有 TOKEN 事件按序拼接——「say 先于动作、正文流式」的完整性断言用</summary>
        public string ConcatTokens()
        {
            using var sb = ZString.CreateStringBuilder();
            for (int i = 0; i < Events.Count; i++)
            {
                if (Events[i].StartsWith("TOKEN:"))
                {
                    sb.Append(Events[i].Substring(6));
                }
            }
            return sb.ToString();
        }

        public void Clear()
        {
            Events.Clear();
            FinishCount = 0;
            LastOutcome = EAgentOutcome.Completed;
            LastAnswer = null;
        }
    }
}
