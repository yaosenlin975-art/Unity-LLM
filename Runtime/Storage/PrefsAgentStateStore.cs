/*
 * ┌────────────────────────────────────────────┐
 * │ Description : 本地持久化：复用 Lin 的 PrefsHelper │
 * │ Remark      : 两个载荷类型 = 两份归档，事实与 │
 * │               轮次两条写路径互不覆盖（ADR-014）│
 * │ ClassName   : PrefsAgentStateStore          │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Lin.Runtime.Helper;
using LLM.Runtime;
using LLM.Runtime.Agent;

namespace LLM.Runtime.Storage
{
    /// <summary>事实槽载荷。独立类型即独立归档，路径由 PrefsHelper 按类型名定死。</summary>
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

    /// <summary>
    /// 路径、编码、锁、WebGL 分支全交给 PrefsHelper，本模块不写文件 IO。
    /// Set 在返回前已同步落盘，所以调用方 .Forget() 不存在"退出时写没落地"的窗口。
    /// </summary>
    public sealed class PrefsAgentStateStore : IFactStore, IConversationStore
    {
        // ponytail: PrefsHelper.Set 每次整档重写（含全部 agent 的数据），一轮最多 1 次历史写 + N 次事实写。
        // 上限：合计到几百 KB 级会出现可感知卡顿；升级路径是脏标记 + 定时合并写。
        public AgentFactsBlob LoadFacts(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;

            return PrefsHelper.Get<AgentFactsBlob>(sessionId);
        }

        public UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionId)) return UniTask.CompletedTask;

            PrefsHelper.Set(sessionId, blob);
            return UniTask.CompletedTask;
        }

        public AgentRoundsBlob LoadHistory(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;

            return PrefsHelper.Get<AgentRoundsBlob>(sessionId);
        }

        public UniTask SaveHistoryAsync(string sessionId, AgentRoundsBlob blob, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionId)) return UniTask.CompletedTask;

            PrefsHelper.Set(sessionId, blob);
            return UniTask.CompletedTask;
        }
    }
}
