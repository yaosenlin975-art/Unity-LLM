/*
┌────────────────────────────┐
│　Description: NPC 好感持久化的 Prefs 参考实现
│　Remark: 与内核窄接口同命名空间放置只是迁移中途态，
│　　　　　 落地后随 LLM.Demo 程序集走（ADR-030）
│　ClassName: PrefsNpcAffinityStore
└────────────────────────────┘
*/

using System.Threading;
using Cysharp.Threading.Tasks;
using Lin.Runtime.Helper;
using LLM.Runtime.Agent.Npc;

namespace LLM.Demo.Agent.Npc
{
    /// <summary>本地实现复用 PrefsHelper（独立包 com.lin.runtime-prefs-helper）；载荷类型独立，避免与事实槽互相覆盖。</summary>
    [UnityEngine.Scripting.Preserve]
    public sealed class PrefsNpcAffinityStore : INpcAffinityStore
    {
        public string Load(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;

            return PrefsHelper.Get<NpcAffinityBlob>(sessionId)?.Json;
        }

        public UniTask SaveAsync(string sessionId, string json, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(sessionId))
                PrefsHelper.Set(sessionId, new NpcAffinityBlob { Json = json });

            return UniTask.CompletedTask;
        }
    }

    [System.Serializable]
    public sealed class NpcAffinityBlob
    {
        public string Json;
    }
}
