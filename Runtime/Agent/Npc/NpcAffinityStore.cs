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

        public static INpcAffinityStore Current { get; set; } = nullStore;

        /// <summary>装配守卫用：仍是本槽哨兵才允许装配根写入</summary>
        public static bool IsDefault => ReferenceEquals(Current, nullStore);

        public static void Reset()
        {
            Current = nullStore;
            LLMRuntimeSettings.ResetInstallDiagnostics();
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
