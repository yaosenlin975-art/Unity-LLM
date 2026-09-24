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
using LLM.Runtime.Agent;
using LLM.Runtime.Agent.Npc;
using UnityEngine;
using Random = UnityEngine.Random;

namespace LLM.Demo.Agent.Npc
{
    /// <summary>
    /// 挂在有 <see cref="AgentHost"/> 的 NPC 上：玩家走进触发体，就按好感档抽一次概率，
    /// 抽中了只发一条世界事件（<see cref="AgentHost.Notify"/>）——说不说话、说什么、要不要配动作，由 NPC 自己决定。
    /// 本地不产台词，所以也不需要往会话历史里补一句 NPC 没说过的话。
    /// 接线要求：NPC 有 Collider 且 isTrigger，玩家带 tag=Player 且身上有 Rigidbody，否则收不到 Enter。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AgentHost))]
    // NPC 自己也得是可被感知物：不然它会在"你看到的在场者"里念出自己的名字
    [RequireComponent(typeof(WorldObservable))]
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
        private WorldObservable self;
        private readonly System.Collections.Generic.List<ObservedInfo> nearby = new();
        private float lastNotifiedAt = float.NegativeInfinity;

        private void Awake()
        {
            host = GetComponent<AgentHost>();
            affinity = GetComponentInChildren<NpcAffinityController>(true);
            self = GetComponent<WorldObservable>();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other == null || !other.CompareTag(k_playerTag)) return;
            // 宿主销毁后 Unity 的判空为 true，再取属性才抛，两条要分开写
            if (host == null || !host.IsActive) return;
            // 在飞时 Notify 会抬代际并顶掉 pendingInput 单槽，等于用一句招呼打断玩家正在等的回答
            if (host.Core.IsBusy) return;

            float now = Time.time;
            if (now - lastNotifiedAt < cooldownSeconds) return;

            var level = affinity != null ? affinity.CurrentLevel : ENpcAffinityLevel.Neutral;
            if (!RollGate(level)) return;

            lastNotifiedAt = now;
            host.Notify(BuildEvent(other.gameObject));
        }

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

        /// <summary>玩家物体名常是 "Player (1)"、"Capsule" 这种，喂给模型会被原样复述；挂了可监测物就用它的观测名。</summary>
        private static string ResolveLabel(GameObject player)
        {
            var observable = player.GetComponentInParent<WorldObservable>();
            return observable != null ? observable.ObservedLabel : player.name;
        }

        /// <summary>只报事实（谁、多远、还有谁在场），不写"快打个招呼"这种祈使句——那等于每轮怂恿模型开口。</summary>
        private string BuildEvent(GameObject player)
        {
            float distance = (player.transform.position - transform.position).magnitude;

            using var sb = ZString.CreateStringBuilder();
            sb.Append(ZString.Format("玩家「{0}」走进了你的打招呼范围，距离 {1:0.0} 米。", ResolveLabel(player), distance));

            int truncated = WorldObservableManager.CollectNearby(
                transform.position, observeRadius, observeMaxCount, self, nearby);

            if (nearby.Count == 0)
            {
                sb.Append("你身边没有别人。");
                return sb.ToString();
            }

            // ponytail: 玩家若也挂了可监测物，会同时出现在首句和这份列表里并占掉一个名额。
            // 挤掉一个别人比再给查询接口加一组排除参数便宜；真要精确再改成多目标排除
            sb.Append("你看到的在场者：");
            for (int i = 0; i < nearby.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(nearby[i].Label);
                sb.Append(ZString.Format("({0:0.0}米)", nearby[i].Distance));
            }

            if (truncated > 0)
                sb.Append(ZString.Format("，另有 {0} 个未列出", truncated));

            return sb.ToString();
        }
    }
}
