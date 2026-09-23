/*
┌────────────────────────────┐
│　Description: NPC 好感持久化窄接口
│　Remark: 好感独立于事实槽与会话历史，
│　　　　　 由装配根或玩法侧注入实现
│　ClassName: NpcAffinityStore
└────────────────────────────┘
*/

using System.Threading;
using Cysharp.Threading.Tasks;
using Lin.Runtime.Helper;
using Newtonsoft.Json;

namespace LLM.Runtime.Agent.Npc
{
    public interface INpcAffinityStore
    {
        string Load(string sessionId);

        UniTask SaveAsync(string sessionId, string json, CancellationToken ct);
    }

    /// <summary>好感存储的装配入口；未装配时保持内存行为。</summary>
    public static class NpcAffinityStore
    {
        private static readonly NullNpcAffinityStore nullStore = new();

        public static INpcAffinityStore Current = nullStore;

        public static void Reset()
        {
            Current = nullStore;
        }
    }

    public sealed class NullNpcAffinityStore : INpcAffinityStore
    {
        public string Load(string sessionId) => null;

        public UniTask SaveAsync(string sessionId, string json, CancellationToken ct)
        {
            return UniTask.CompletedTask;
        }
    }

    /// <summary>本地实现复用 PrefsHelper（独立包 com.lin.runtime-prefs-helper）；载荷类型独立，避免与事实槽互相覆盖。</summary>
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

    public sealed class NpcAffinityState
    {
        public int Value;
        public int Revision;
        public string LastReason;

        public NpcAffinityState Clone()
        {
            return new NpcAffinityState
            {
                Value = Value,
                Revision = Revision,
                LastReason = LastReason
            };
        }

        public static string Serialize(NpcAffinityState state)
        {
            return JsonConvert.SerializeObject(state);
        }

        public static NpcAffinityState Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                return JsonConvert.DeserializeObject<NpcAffinityState>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
