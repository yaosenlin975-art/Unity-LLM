/*
┌────────────────────────────┐
│　Description: 玩家进入打招呼范围时上报世界事件的参考实现
│　Remark: 开不开口由好感档与概率在本地判，
│　　　　　 说什么、配什么动作一律交回模型（ADR-015）
│　ClassName: NpcProximityGreeter
└────────────────────────────┘
*/

using System;
using Cysharp.Text;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Agent.Npc;
using LLM.Runtime.Agent.World;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LLM.Demo.Agent.Npc
{
    /// <summary>
    /// 挂在有 <see cref="NpcAgentHost"/> 的 NPC 上：玩家走进触发体，就按好感档抽一次概率，
    /// 抽中了只发一条世界事件（<see cref="AgentHost.Notify"/>）——说不说话、说什么、要不要配动作，由 NPC 自己决定。
    /// 本地不产台词，所以也不需要往会话历史里补一句 NPC 没说过的话。
    /// 接线要求：NPC 有 Collider 且 isTrigger，玩家带 tag=Player 且身上有 Rigidbody，否则收不到 Enter；
    /// 观测身份由宿主自己提供（<see cref="NpcAgentHost"/> 就是 <see cref="IWorldObservable"/> 的实现者），
    /// 拿不到身份就不搭话（见 <see cref="HasObservable"/>）。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NpcAgentHost))]
    public sealed class NpcProximityGreeter : MonoBehaviour
    {
        [Serializable]
        private struct GreetingGate
        {
            [Header("生效好感档下限")] public ENpcAffinityLevel minLevel;
            [Header("搭话概率")] [Range(0f, 1f)] public float chance;
        }

        [Header("视野查询")]
        [Tooltip("半径（米）与最多列几个在场者，用于写进事件文本")]
        [SerializeField, Min(0.01f)] private float observeRadius = 8f;
        [SerializeField, Min(1)] private int observeMaxCount = 6;

        [Header("最小上报间隔（秒）")]
        // ponytail: 一个 NPC 一个全局冷却，两个玩家先后走进来时后一个会被吞掉；
        // 真要多玩家并存再按 instanceId 存表——好感度现在也没有玩家维度（ADR-011），单玩家前提下够用
        [SerializeField, Min(0f)] private float cooldownSeconds = 60f;

        [Header("按好感档的闸门")]
        [Tooltip("从上往下匹配第一个达标的档位；chance 为 0 即这一档不主动搭话。一条不配等于永不搭话")]
        [SerializeField] private GreetingGate[] gates = Array.Empty<GreetingGate>();

        private const string k_playerTag = "Player";

        private AgentHost host;
        private NpcAffinityController affinity;

        // NPC 自己也得是可被感知物：不然它会在"你看到的在场者"里念出自己的名字。
        // 身份的来源就是宿主本身（NpcAgentHost 实现 IWorldObservable）。仍按接口取、拿不到就拒绝搭话，
        // 是因为这条链上还有一个真实的落空场景：宿主是基类 AgentHost 的旧资产
        // （本类的 RequireComponent 到这一版才从 AgentHost 收紧到 NpcAgentHost）。
        // 那时 self 为 null，RenderFacts 的 except 也跟着是 null，NPC 会把自己列进在场者——
        // 正好犯下本类一直在防的那件事，所以宁可不搭话
        private IWorldObservable self;

        private bool warnedMissingObservable;
        private float lastNotifiedAt = float.NegativeInfinity;

        private void Awake()
        {
            host = GetComponent<AgentHost>();
            affinity = GetComponentInChildren<NpcAffinityController>(true);
            self = GetComponent<IWorldObservable>();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other == null || !other.CompareTag(k_playerTag)) return;
            // 宿主销毁后 Unity 的判空为 true，再取属性才抛，两条要分开写
            if (host == null || !host.IsActive) return;
            // 在飞时 Notify 会抬代际并顶掉 pendingInput 单槽，等于用一句招呼打断玩家正在等的回答
            if (host.Core.IsBusy) return;

            // 没有观测身份就宁可不搭话：self 为 null 时 RenderFacts 的 except 也是 null，
            // NPC 会把自己列进"你看到的在场者"，正好犯下本类一直在防的那件事
            if (!HasObservable)
            {
                WarnMissingObservable();
                return;
            }

            float now = Time.time;
            if (now - lastNotifiedAt < cooldownSeconds) return;

            var level = affinity != null ? affinity.CurrentLevel : ENpcAffinityLevel.Neutral;
            if (!RollGate(level)) return;

            lastNotifiedAt = now;
            host.Notify(BuildEvent(other.gameObject));
        }

        /// <summary>
        /// 身上的观测身份还在不在。判活走 Unity 语义（与 <c>WorldSnapshotService</c> 的遍历同形）：
        /// 接口引用上的 == null 选不到 <c>UnityEngine.Object</c> 重载，组件被单独 Destroy 后引用非空，
        /// 取属性才抛。方向在这里不伤及无辜——GetComponent 只会返回 MonoBehaviour，
        /// 不存在无原生侧的纯托管实现
        /// </summary>
        private bool HasObservable => self is UnityEngine.Object observable && observable != null;

        private bool RollGate(ENpcAffinityLevel level)
        {
            for (int i = 0; i < gates.Length; i++)
            {
                if (level < gates[i].minLevel) continue;
                return Random.value <= gates[i].chance;
            }

            // 一条闸门都没配就当这个 NPC 不主动搭话，不替策划猜个默认概率
            return false;
        }

        /// <summary>接线缺一环就报一次，别每次进触发体刷一条：控制台会被埋掉，而配置错误看一次就够</summary>
        private void WarnMissingObservable()
        {
            if (warnedMissingObservable) return;
            warnedMissingObservable = true;

            Log.Warning(nameof(NpcProximityGreeter),
                "同一个物体上没有 IWorldObservable 实现，不搭话——否则会把自己列进「你看到的在场者」。宿主需是 NpcAgentHost（它自己就是观测身份），旧预制体上还是基类 AgentHost 时换掉即可",
                this);
        }

        /// <summary>玩家物体名常是 "Player (1)"、"Capsule" 这种，喂给模型会被原样复述；挂了可观测物就用它的观测名</summary>
        private static string ResolveLabel(GameObject player)
        {
            var observable = player.GetComponentInParent<IWorldObservable>();

            // 判活同 HasObservable：残骸上的 ObservedLabel 一取就抛，宁可退回物体名
            if (observable is UnityEngine.Object alive && alive != null) return observable.ObservedLabel;

            return player.name;
        }

        /// <summary>只报事实（谁、多远、还有谁在场），不写"快打个招呼"这种祈使句——那等于每轮怂恿模型开口</summary>
        private string BuildEvent(GameObject player)
        {
            float distance = (player.transform.position - transform.position).magnitude;

            // 在场者与全局事实整段交框架产出（半径/名额沿用 Inspector 的两个字段）。
            // ponytail: 玩家若也在册，会同时出现在首句和在场者列表里并占掉一个名额——
            // RenderFacts 的 except 只认一个目标，为这个边缘再加一组排除参数不划算
            var facts = WorldSnapshotService.RenderFacts(transform.position, self, observeRadius, observeMaxCount);

            // 框架没人可报时返回空串，直接插进 {2} 会让正文以换行收尾。空串必须补一句"没有别人"：
            // 缺席本身是信息（旧实现就有这句），只留个换行会被模型读成"这一项未知"而不是"场上只剩我"
            var body = string.IsNullOrEmpty(facts)
                ? "。你身边没有别人。"
                : ZString.Concat("。\n", facts);

            return ZString.Format("玩家「{0}」走进了你的打招呼范围，距离 {1:0.0} 米{2}",
                ResolveLabel(player), distance, body);
        }
    }
}
