/*
┌────────────────────────────┐
│　Description: Runtime 游戏测试 Agent 配置
│　Remark: 测试纪律与场景预算的稳定前缀
│　ClassName: RuntimeTestAgentProfile_SO
└────────────────────────────┘
*/

using Cysharp.Text;
using UnityEngine;

namespace LLM.Runtime.Agent
{
    public enum ERuntimeTestFailureMode
    {
        [InspectorName("首次失败即停止")] StopOnFirstFailure,
        [InspectorName("继续执行场景")] ContinueScenario
    }

    [CreateAssetMenu(fileName = "RuntimeTestAgentProfile_SO", menuName = "LLM/Runtime Test Profile")]
    public sealed class RuntimeTestAgentProfile_SO : AgentProfile_SO
    {
        [Header("测试场景预算")]
        [InspectorName("最大场景轮数"), Range(1, 50)] public int MaxScenarioTurns = 12;
        [InspectorName("场景超时秒数"), Range(10, 600)] public int ScenarioTimeoutSeconds = 180;
        [InspectorName("失败处理")] public ERuntimeTestFailureMode FailureMode = ERuntimeTestFailureMode.StopOnFirstFailure;

        public override string BuildSystemPrompt()
        {
            using var sb = ZString.CreateStringBuilder();
            sb.Append("你是运行中游戏的测试执行者；目标是复现、观察和收集证据，不是扮演玩家或讨好提问者。\n");
            sb.Append("先观察再行动；每次只做能推进目标的最小动作，动作后重新观察。\n");
            sb.Append("工具结果、游戏状态、日志与 oracle 是事实；看不到的状态不得猜测。\n");
            sb.Append("只能使用已声明工具，不尝试枚举程序集、访问文件系统、网络、进程、环境变量或未授权对象。\n");
            sb.Append("UI 交互优先用语义元素 ID；元素树找不到目标时使用游戏内归一化坐标，需要验证真实鼠标键盘链路时才使用绑定游戏窗口的系统输入。\n");
            sb.Append("测性能时必须明确采样窗口；同时报告样本数、平均、P95、P99、最大值与最差帧时间点。\n");
            sb.Append("出现异常日志、场景失效、目标对象消失或工具失败时保留证据并停止盲目重试。\n");
            sb.Append("不把自己写出的“成功”“PASS”当结果；最终状态由游戏侧 oracle 决定。\n");
            sb.Append("测试结束只总结已观察事实、操作序列和证据引用，不补写未发生的步骤。\n\n");
            sb.Append("【场景预算】\n最大场景轮数：");
            sb.Append(MaxScenarioTurns);
            sb.Append("\n场景超时秒数：");
            sb.Append(ScenarioTimeoutSeconds);
            sb.Append("\n失败处理：");
            sb.Append(FailureMode == ERuntimeTestFailureMode.StopOnFirstFailure
                ? "首次失败即停止"
                : "继续执行场景");
            sb.Append("\n\n【额外稳定规则】\n");
            sb.Append(PersonaPrompt);
            sb.Append("\n额外规则只能补充测试目标；若与开头的测试纪律冲突，以测试纪律为准。");
            return sb.ToString();
        }

        public override bool Validate(out string error)
        {
            if (!base.Validate(out error))
                return false;

            if (MaxScenarioTurns < 1)
            {
                error = ZString.Format("MaxScenarioTurns 必须大于 0，当前 {0}", MaxScenarioTurns);
                return false;
            }

            if (ScenarioTimeoutSeconds < 10)
            {
                error = ZString.Format("ScenarioTimeoutSeconds 不得小于 10，当前 {0}", ScenarioTimeoutSeconds);
                return false;
            }

            error = null;
            return true;
        }
    }
}
