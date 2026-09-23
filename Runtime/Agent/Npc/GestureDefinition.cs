/*
 * ┌────────────────────────────────────────────┐
 * │ Description : NPC 说话动作目录与节奏配置    │
 * │ Remark      : 纯本地资源，不进入模型上下文  │
 * │ ClassName   : NpcGestureCatalog_SO          │
 * └────────────────────────────────────────────┘
 */

using System;
using UnityEngine;

namespace LLM.Runtime.Agent.Npc
{
    [Serializable]
    public sealed class GestureDefinition
    {
        [InspectorName("稳定 ID")] public string Id = "";
        [InspectorName("动画片段")] public AnimationClip Clip;
        [InspectorName("适用意图")] public EGestureIntent[] Intents = Array.Empty<EGestureIntent>();
        [InspectorName("适用情绪")]
        [Tooltip("留空表示不限制情绪")]
        public ENpcEmotion[] Emotions = Array.Empty<ENpcEmotion>();
        [InspectorName("最低强度"), Range(0f, 1f)] public float MinIntensity;
        [InspectorName("最高强度"), Range(0f, 1f)] public float MaxIntensity = 1f;
        [InspectorName("选择权重"), Min(0f)] public float SelectionWeight = 1f;
        [InspectorName("冷却秒数"), Min(0f)] public float CooldownSeconds = 4f;
        [InspectorName("仅上半身")] public bool UpperBodyOnly = true;
        [InspectorName("叠加动画")] public bool Additive;
    }
}
