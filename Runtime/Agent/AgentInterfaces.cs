/*
┌────────────────────────────┐
│　Description: Agent 内核与宿主的窄接口
│　Remark: 不携带任何宿主具体类型，NPC 与
│　　　　　 对话助手共用同一套内核
│　ClassName: IWorldContextProvider
└────────────────────────────┘
*/

using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace LLM.Runtime.Agent
{
    /// <summary>世界状态只读入口。只放低频值（身份、目标、关系等级），每轮都变的东西走 observe 工具</summary>
    public interface IWorldContextProvider
    {
        string GetCoreSnapshot();
    }

    /// <summary>内核唯一的输出通道。宿主自己决定上屏方式（气泡 / 打字机 / 日志）</summary>
    public interface IAgentOutput
    {
        /// <summary>模型正文流式增量。内核逐条校验代际，作废轮的 token 不再送上来</summary>
        void OnToken(string delta);

        /// <summary>动作执行前的角色台词；与模型正文是两个独立展示段</summary>
        void OnSay(string actionId, string say);

        void OnTurnFinished(string answer, EAgentOutcome outcome);
    }

    /// <summary>动作落地处。宿主可整体换掉反射执行器；没有动作时注入 NullActionRunner</summary>
    public interface IAgentActionRunner
    {
        UniTask<AgentActionResult> RunAsync(string actionId, string argsJson,
            AgentActionContext ctx, CancellationToken ct);
    }

    /// <summary>
    /// 逐工具可见性门。宿主按实例决定哪些工具可用，null 等价于全部放行。
    /// 声明层与执行层都读它，保证被关闭的工具既不可见也不可执行。
    /// </summary>
    public interface IAgentToolGate
    {
        bool IsToolEnabled(string toolId);
    }

    /// <summary>
    /// 动画后端窄接口。内核侧只认字符串与 List，不引用 Animator / AnimGraph 等任何动画类型，
    /// 实现者放在玩法层（Learn.AgentAnimation），由宿主挂进同一层级供 GetComponentInChildren 找到。
    /// 写操作约定统一：返回 null = 成功，返回非空字符串 = 回填给模型的中文失败原因（含"名字不在词表里"）。
    /// </summary>
    public interface IAgentAnimationDriver
    {
        /// <summary>只用于回填文案的后端标识，如 Animator / AnimGraph</summary>
        string BackendName { get; }

        /// <summary>模型可播的状态名词表，元素为可直接回传给 PlayState 的字符串</summary>
        void CollectStateNames(List<string> results);

        /// <summary>模型可写的参数词表，元素形如 "MoveSpeed:float"</summary>
        void CollectParameterNames(List<string> results);

        /// <summary>当前在播状态的可读描述，供 get_animation_state 回填</summary>
        string DescribeCurrentState();

        /// <summary>切换到指定状态，fadeInSeconds&lt;=0 表示硬切</summary>
        string PlayState(string stateName, float fadeInSeconds);

        /// <summary>按参数声明类型写入 value；trigger 类型视为触发一次</summary>
        string SetParameter(string name, string value);

        /// <summary>整体播放速率，0 = 定格</summary>
        string SetSpeed(float speed);
    }

    /// <summary>
    /// NPC 纯表现动作后端。接收本地已经选好的 Clip，不向模型暴露动作目录；
    /// 与 IAgentAnimationDriver 的状态机工具契约相互独立。
    /// </summary>
    public interface INpcGestureDriver
    {
        bool IsGesturePlaying { get; }

        bool TryPlayGesture(AnimationClip clip, float blendInSeconds, float blendOutSeconds,
            float speed, bool upperBodyOnly, bool additive, bool loop, out string error);

        void StopGesture(float blendOutSeconds);
    }

    public enum EAgentOutcome
    {
        Completed,
        /// <summary>被新输入顶掉，本轮产出一律丢弃且不写历史</summary>
        Stale,
        Timeout,
        Failed,
        LoopAborted
    }

    public readonly struct AgentActionResult
    {
        /// <summary>false → Content 作为失败原因回填，前缀 [Action Failed]</summary>
        public readonly bool Ok;

        /// <summary>成功 = 结果文本，失败 = 原因。不单设 Error 字段</summary>
        public readonly string Content;

        public AgentActionResult(bool ok, string content)
        {
            Ok = ok;
            Content = content;
        }

        public static AgentActionResult Success(string content) => new(true, content);

        public static AgentActionResult Failure(string reason) => new(false, reason);
    }

    /// <summary>动作执行上下文。每次调用新建，不携带任何宿主具体类型</summary>
    public sealed class AgentActionContext
    {
        public AgentCore Agent;
        public AgentProfile_SO Profile;
        public string SessionId;
        public CancellationToken CancellationToken;
    }
}
