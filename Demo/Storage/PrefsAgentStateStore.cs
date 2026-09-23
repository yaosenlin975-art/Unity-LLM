/*
┌────────────────────────────┐
│　Description: 本地持久化参考实现：复用 PrefsHelper 归档
│　Remark: 载荷类型 AgentFactsBlob / AgentRoundsBlob 是接口签名的一部分，
│　　　　　 留在内核契约里；本类只负责交给 PrefsHelper（ADR-030）
│　ClassName: PrefsAgentStateStore
└────────────────────────────┘
*/

using System.Threading;
using Cysharp.Threading.Tasks;
using Lin.Runtime.Helper;
using LLM.Runtime.Storage;

namespace LLM.Demo.Storage
{
    /// <summary>
    /// 路径、编码、锁、WebGL 分支全交给 PrefsHelper，本模块不写文件 IO。
    /// Set 在返回前已同步落盘，所以调用方 .Forget() 不存在"退出时写没落地"的窗口。
    /// </summary>
    [UnityEngine.Scripting.Preserve]
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
