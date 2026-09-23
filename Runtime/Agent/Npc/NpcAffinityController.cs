/*
┌────────────────────────────┐
│　Description: NPC 好感状态与模型动作
│　Remark: 模型变化单轮限幅，游戏变化走
│　　　　　 同一事务但不受 ±20 限制
│　ClassName: NpcAffinityController
└────────────────────────────┘
*/

using System;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime;
using UnityEngine;

namespace LLM.Runtime.Agent.Npc
{
    public enum ENpcAffinityLevel
    {
        Hostile,
        Cold,
        Neutral,
        Friendly,
        Trusted
    }

    [DisallowMultipleComponent]
    public sealed class NpcAffinityController : MonoBehaviour
    {
        private const int k_minValue = -100;
        private const int k_maxValue = 100;
        private const int k_modelDeltaLimit = 20;

        [SerializeField]
        [InspectorName("初始好感度兜底")]
        [Tooltip("宿主初始化前使用的初始好感；运行期应由 NPC Profile 传入")]
        private int initialAffinity;

        private readonly NpcAffinityState state = new();
        private string sessionId;
        private int lastModelRound = int.MinValue;
        private bool initialized;

        public int CurrentValue => state.Value;

        public int CurrentRevision => state.Revision;

        public string LastReason => state.LastReason;

        public ENpcAffinityLevel CurrentLevel => GetLevel(state.Value);

        /// <summary>由宿主传入稳定 sessionId 与 Profile 初值；加载到存档时以存档为准。</summary>
        public void Initialize(string stableSessionId, int profileInitialAffinity)
        {
            sessionId = stableSessionId;
            state.Value = Clamp(profileInitialAffinity);
            state.Revision = 0;
            state.LastReason = null;
            lastModelRound = int.MinValue;
            initialized = !string.IsNullOrEmpty(stableSessionId);

            if (!initialized) return;

            NpcAffinityState saved;
            try
            {
                saved = NpcAffinityState.Deserialize(NpcAffinityStore.Current.Load(stableSessionId));
            }
            catch (Exception)
            {
                saved = null;
            }

            if (saved == null) return;

            state.Value = Clamp(saved.Value);
            state.Revision = Math.Max(0, saved.Revision);
            state.LastReason = saved.LastReason;
        }

        /// <summary>使用组件上的初值，供宿主暂未接入 Profile 时的最小落地路径。</summary>
        public void Initialize(string stableSessionId)
        {
            Initialize(stableSessionId, initialAffinity);
        }

        [AgentTool("get_affinity", "读取 NPC 当前对玩家的好感度、关系等级和态度。")]
        public string GetAffinity()
        {
            return RenderInjection();
        }

        [AgentAction("adjust_affinity", "根据本轮已经发生的互动调整对玩家的好感。没有充分理由时不要调用。",
            Idempotent = false)]
        public UniTask<AgentActionResult> AdjustAffinity(AgentActionContext context, int delta, string reason)
        {
            int roundSerial = context?.Agent != null ? context.Agent.RoundSerial : int.MinValue;
            return AdjustFromModel(delta, reason, roundSerial);
        }

        public UniTask<AgentActionResult> AdjustFromModel(int delta, string reason, int roundSerial)
        {
            if (!initialized)
                return FailureTask("NPC 好感尚未绑定有效的 sessionId。");

            if (delta < -k_modelDeltaLimit || delta > k_modelDeltaLimit)
                return FailureTask("模型单轮好感变化必须在 -20 到 20 之间。");

            if (string.IsNullOrWhiteSpace(reason))
                return FailureTask("reason 必须说明本轮玩家已经发生的具体言行。");

            if (roundSerial != int.MinValue && lastModelRound == roundSerial)
                return FailureTask("同一轮最多只能调整一次好感。");

            return ApplyAsync(delta, reason, roundSerial, default);
        }

        /// <summary>剧情或任务等确定性变化，不受模型单轮 ±20 限制。</summary>
        public UniTask<AgentActionResult> AdjustFromGame(int delta, string reason,
            CancellationToken ct = default)
        {
            if (!initialized)
                return FailureTask("NPC 好感尚未绑定有效的 sessionId。");

            if (string.IsNullOrWhiteSpace(reason))
                return FailureTask("reason 不能为空。");

            return ApplyAsync(delta, reason, int.MinValue, ct);
        }

        public string RenderInjection()
        {
            using var sb = ZString.CreateStringBuilder();
            sb.Append("[你与玩家当前的关系]\n好感度：");
            sb.Append(state.Value);
            sb.Append("/100\n关系：");
            sb.Append(GetLevelName(CurrentLevel));
            sb.Append("\n态度：");
            sb.Append(GetAttitude(CurrentLevel));
            return sb.ToString();
        }

        private async UniTask<AgentActionResult> ApplyAsync(int delta, string reason, int roundSerial,
            CancellationToken ct)
        {
            NpcAffinityState previous = state.Clone();
            int next = Clamp((long)state.Value + delta);

            if (next == state.Value)
                return AgentActionResult.Success(RenderInjection());

            state.Value = next;
            state.Revision++;
            state.LastReason = reason;

            try
            {
                await NpcAffinityStore.Current.SaveAsync(sessionId, NpcAffinityState.Serialize(state), ct);
            }
            catch (Exception ex)
            {
                Restore(previous);
                return Failure(ZString.Format("好感持久化失败：{0}", ex.Message));
            }

            if (roundSerial != int.MinValue)
                lastModelRound = roundSerial;

            return AgentActionResult.Success(RenderInjection());
        }

        private void Restore(NpcAffinityState previous)
        {
            state.Value = previous.Value;
            state.Revision = previous.Revision;
            state.LastReason = previous.LastReason;
        }

        private static AgentActionResult Failure(string message)
        {
            return AgentActionResult.Failure(message);
        }

        private static UniTask<AgentActionResult> FailureTask(string message)
        {
            return new UniTask<AgentActionResult>(Failure(message));
        }

        private static int Clamp(long value)
        {
            return (int)Math.Max(k_minValue, Math.Min(k_maxValue, value));
        }

        public static ENpcAffinityLevel GetLevel(int value)
        {
            if (value <= -61) return ENpcAffinityLevel.Hostile;
            if (value <= -21) return ENpcAffinityLevel.Cold;
            if (value <= 19) return ENpcAffinityLevel.Neutral;
            if (value <= 59) return ENpcAffinityLevel.Friendly;
            return ENpcAffinityLevel.Trusted;
        }

        private static string GetLevelName(ENpcAffinityLevel level)
        {
            return level switch
            {
                ENpcAffinityLevel.Hostile => "敌对",
                ENpcAffinityLevel.Cold => "冷淡",
                ENpcAffinityLevel.Neutral => "中立",
                ENpcAffinityLevel.Friendly => "友好",
                _ => "信任"
            };
        }

        private static string GetAttitude(ENpcAffinityLevel level)
        {
            return level switch
            {
                ENpcAffinityLevel.Hostile => "你对对方明显戒备，不主动帮助。",
                ENpcAffinityLevel.Cold => "你回答保留、态度疏远。",
                ENpcAffinityLevel.Neutral => "你按常理与对方交流。",
                ENpcAffinityLevel.Friendly => "你比较愿意帮助对方，但仍遵守自己的性格、知识边界和剧情权限。",
                _ => "你愿意透露符合角色与剧情权限的信息。"
            };
        }
    }
}
