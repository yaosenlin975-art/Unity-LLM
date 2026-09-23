/*
┌────────────────────────────┐
│　Description: Agent 状态存储抽象
│　Remark: 事实槽与会话轮次各一个接口，
│　　　　　 载荷使用强类型 Blob（ADR-029）
│　ClassName: AgentStorageInterfaces
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Agent;

namespace LLM.Runtime.Storage
{
    /// <summary>事实槽载荷。独立类型即独立归档，路径由存储实现按类型名定死。</summary>
    [Serializable]
    public class AgentFactsBlob
    {
        public List<AgentFact> Facts;
    }

    /// <summary>会话轮次载荷。</summary>
    [Serializable]
    public class AgentRoundsBlob
    {
        public List<ConversationRound> Rounds;
    }

    /// <summary>事实槽读写口。</summary>
    public interface IFactStore
    {
        /// <summary>没有存档时返回 null。同步签名是刻意的——读点唯一且在 AgentCore 构造期。</summary>
        AgentFactsBlob LoadFacts(string sessionId);

        UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob, CancellationToken ct);
    }

    /// <summary>会话轮次读写口。</summary>
    public interface IConversationStore
    {
        AgentRoundsBlob LoadHistory(string sessionId);

        UniTask SaveHistoryAsync(string sessionId, AgentRoundsBlob blob, CancellationToken ct);
    }

    /// <summary>
    /// 缺能力靠 Null 对象表达，内核里不出现判空（ADR-002 的既有约定）。
    /// 这也是 AgentStateStores 的默认值：不装配就等于接口化之前的纯内存行为。
    /// </summary>
    public sealed class NullAgentStateStore : IFactStore, IConversationStore
    {
        public AgentFactsBlob LoadFacts(string sessionId) => null;

        public AgentRoundsBlob LoadHistory(string sessionId) => null;

        public UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob, CancellationToken ct) => UniTask.CompletedTask;

        public UniTask SaveHistoryAsync(string sessionId, AgentRoundsBlob blob, CancellationToken ct) => UniTask.CompletedTask;
    }
}
