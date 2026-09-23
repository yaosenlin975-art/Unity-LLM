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
    [CreateAssetMenu(fileName = "NpcGestureCatalog_SO", menuName = "LLM/NPC 动作目录")]
    public sealed class NpcGestureCatalog_SO : ScriptableObject
    {
        [SerializeField]
        [InspectorName("动作列表")]
        private GestureDefinition[] gestures = Array.Empty<GestureDefinition>();

        public GestureDefinition[] Gestures => gestures;
    }
}
