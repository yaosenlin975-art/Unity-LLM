/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 动画工具与动作            │
 * │ Remark      : 只转发给同层级的 IAgentAnimationDriver，│
 * │               本文件不认识 Animator 也不认识 AnimGraph │
 * │ ClassName   : AgentAnimationTools            │
 * └────────────────────────────────────────────┘
 */

using System.Collections.Generic;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Agent;
using UnityEngine;
using UnityEngine.Pool;

namespace LLM.Runtime.Tools
{
    /// <summary>
    /// 挂在 Agent 宿主层级上的动画成员工具/动作组件。
    /// 驱动（后端实现）与它分开：换 Animator 还是 AnimGraph 不影响本文件，也不影响内核。
    /// 接线方向要说清：驱动找的是**本物体及其子物体**（不往上找），所以驱动要么和这个工具挂在
    /// 同一个物体上，要么挂在它的子物体上；挂到父物体上会得到"未挂载动画驱动"这个看似接错线的结果。
    /// （白名单反过来，允许挂在父级，便于整个角色共用一份）
    /// </summary>
    public sealed class AgentAnimationTools : MonoBehaviour
    {
        #region - 公开接口 -

        [AgentTool("get_animation_state", "查询当前正在播放的动画状态。")]
        public string GetAnimationState()
        {
            IAgentAnimationDriver driver = ResolveDriver();
            if (driver == null)
                return "[Tool Error] 该 Agent 未挂载动画驱动（Animator 或 AnimGraph 驱动）。";

            return ZString.Format("backend={0} current={1}",
                driver.BackendName, driver.DescribeCurrentState());
        }

        [AgentTool("list_animation_states", "列出该 Agent 能播放的动画状态名，以及能写的动画参数名（名字必须逐字取自这里）。")]
        public string ListAnimationStates()
        {
            IAgentAnimationDriver driver = ResolveDriver();
            if (driver == null)
                return "[Tool Error] 该 Agent 未挂载动画驱动（Animator 或 AnimGraph 驱动）。";

            var states = ListPool<string>.Get();
            var parameters = ListPool<string>.Get();
            driver.CollectStateNames(states);
            driver.CollectParameterNames(parameters);

            string vocabulary = states.Count == 0
                // 空状态词表要当场说破：Animator 后端不填白名单、或白名单把状态全过滤掉就是这个状态。
                // 判据不能带上 parameters 的条件——参数词表非空而状态为空恰恰是最需要说破的组合
                ? ZString.Concat("(没有可播的状态——", driver.BackendName,
                    " 后端要在动画驱动同层级配好状态词表) ")
                : string.Empty;

            // 分隔符用 ", " 而不是空格：状态名本身可以带空格（AnimGraph 里拖动画资产建状态时，
            // 默认名就是动画文件名），用空格连接会让 "Idle Walk" 与两个名字分不开，
            // 而工具文案要求模型逐字回传
            string result = ZString.Format("{0}states=[{1}] parameters=[{2}]",
                vocabulary, Join(states, ", "), Join(parameters, ", "));
            ListPool<string>.Release(states);
            ListPool<string>.Release(parameters);
            return result;
        }

        [AgentAction("play_animation_state", "播放指定动画状态。stateName 必须逐字取自 list_animation_states；fadeInSeconds 填 0 表示硬切。", Idempotent = false)]
        public UniTask<AgentActionResult> PlayAnimationState(string stateName, float fadeInSeconds)
        {
            IAgentAnimationDriver driver = ResolveDriver();
            if (driver == null)
                return Failure("缺少动画驱动。");

            if (string.IsNullOrEmpty(stateName))
                return Failure("状态名不能为空。");

            string state = stateName.Trim();
            string error = driver.PlayState(state, fadeInSeconds < 0f ? 0f : fadeInSeconds);
            if (error != null)
                return Failure(error);

            // 回填的是真正播出去的那个名字，不是模型原样给的（带空白时会误导它以为名字长那样）
            return Success(ZString.Format("已切换到 {0}。", state));
        }

        [AgentAction("set_animation_parameter", "按名字写动画参数，value 一律给字符串：bool 填 true/false，trigger 填任意值即触发一次，数值填数字。名字必须取自 list_animation_states。", Idempotent = false)]
        public UniTask<AgentActionResult> SetAnimationParameter(string name, string value)
        {
            IAgentAnimationDriver driver = ResolveDriver();
            if (driver == null)
                return Failure("缺少动画驱动。");

            if (string.IsNullOrEmpty(name))
                return Failure("参数名不能为空。");

            string parameter = name.Trim();
            string error = driver.SetParameter(parameter, value);
            if (error != null)
                return Failure(error);

            return Success(ZString.Format("已设置 {0}={1}。", parameter, value));
        }

        [AgentAction("set_animation_speed", "设置整体动画播放速率，1 为正常，0 为定格。", Idempotent = false)]
        public UniTask<AgentActionResult> SetAnimationSpeed(float speed)
        {
            IAgentAnimationDriver driver = ResolveDriver();
            if (driver == null)
                return Failure("缺少动画驱动。");

            string error = driver.SetSpeed(speed);
            if (error != null)
                return Failure(error);

            return Success(ZString.Format("动画速率已设为 {0:F2}。", speed));
        }

        #endregion

        #region - 私有方法 -

        // 驱动是运行时挂/换的（Prefab 复用同一份工具），所以每次都现找而不在 Awake 缓存
        private IAgentAnimationDriver ResolveDriver()
        {
            return GetComponentInChildren<IAgentAnimationDriver>(true);
        }

        private static string Join(List<string> values, string separator)
        {
            using var sb = ZString.CreateStringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                    sb.Append(separator);
                sb.Append(values[i]);
            }

            return sb.ToString();
        }

        private static UniTask<AgentActionResult> Success(string content)
        {
            return new UniTask<AgentActionResult>(AgentActionResult.Success(content));
        }

        private static UniTask<AgentActionResult> Failure(string reason)
        {
            return new UniTask<AgentActionResult>(AgentActionResult.Failure(reason));
        }

        #endregion
    }
}
