/*
 * Agent 测试脚手架 — 成员工具/动作组件
 * 实例方法挂在物体上，验证层级扫描、per-agent 收集与「打在实例上」的执行语义
 */

using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime;
using LLM.Runtime.Agent;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    public sealed class ScaffoldTestMemberTools : MonoBehaviour
    {
        public int EchoCount;
        public string LastText;

        [AgentTool("m_echo", "回显（成员工具脚手架）")]
        public string Echo(string text)
        {
            EchoCount++;
            LastText = text;
            return ZString.Concat("echo:", text);
        }

        [AgentAction("m_act", "成员动作（脚手架）", Idempotent = true)]
        public UniTask<AgentActionResult> Act(AgentActionContext ctx, string text)
        {
            LastText = text;
            return new UniTask<AgentActionResult>(AgentActionResult.Success(ZString.Concat("acted:", text)));
        }
    }
}
