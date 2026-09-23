/*
 * ┌────────────────────────────────────────────┐
 * │ Description : 工具执行注入点                │
 * │ Remark      : LLMSession 把 tool_call 交给   │
 * │               外部执行的口子 + 默认同步实现   │
 * │ ClassName   : SyncToolRegistryExecutor      │
 * └────────────────────────────────────────────┘
 */

using System.Threading;
using Cysharp.Threading.Tasks;

namespace LLM.Runtime
{
    /// <summary>
    /// 工具/动作执行注入点。刻意不带 tool_call id —— 回填 tool_call_id 是 LLMSession 的职责。
    /// </summary>
    public interface IAgentToolExecutor
    {
        UniTask<AgentToolExecutionResult> ExecuteAsync(string name, string argsJson, CancellationToken ct);
    }

    public readonly struct AgentToolExecutionResult
    {
        /// <summary>回填给模型的内容（成功=结果文本，失败=[...] 前缀的原因）</summary>
        public readonly string Content;

        /// <summary>
        /// 为真则本轮立即收尾：不再发下一次请求，且同批剩余的 tool_call 一律不执行、不回填。
        /// 该轮产出整体丢弃，所以不需要给 dangling tool_call 补帧。
        /// </summary>
        public readonly bool AbortTurn;

        public AgentToolExecutionResult(string content, bool abortTurn = false)
        {
            Content = content;
            AbortTurn = abortTurn;
        }
    }

    /// <summary>默认执行器：同步跑 ToolRegistry，包成 UniTask 以对齐注入点签名</summary>
    public sealed class SyncAgentToolRegistryExecutor : IAgentToolExecutor
    {
        public UniTask<AgentToolExecutionResult> ExecuteAsync(string name, string argsJson, CancellationToken ct)
        {
            return new UniTask<AgentToolExecutionResult>(new AgentToolExecutionResult(AgentToolRegistry.Execute(name, argsJson)));
        }
    }
}
