/*
┌────────────────────────────┐
│　Description: NPC 说话动作目录与节奏配置
│　Remark: 纯本地资源，不进入模型上下文
│　ClassName: NpcGestureCatalog_SO
└────────────────────────────┘
*/

using System;
using UnityEngine;

namespace LLM.Runtime.Agent.Npc
{
    [CreateAssetMenu(fileName = "NpcPerformanceProfile_SO", menuName = "LLM/NPC 说话表现配置")]
    public sealed class NpcPerformanceProfile_SO : ScriptableObject
    {
        [Header("动作资源")]
        [InspectorName("动作目录")] public NpcGestureCatalog_SO GestureCatalog;
        [InspectorName("基础说话循环")]
        [Tooltip("可留空；填写时应使用已启用循环且不含 Root Motion 的上半身 Clip")]
        public AnimationClip SpeakingLoop;

        [Header("流式切句")]
        [InspectorName("短句最大字符数"), Range(4, 80)] public int MaxClauseCharacters = 20;
        [InspectorName("停顿切句秒数"), Range(0.1f, 2f)] public float ClausePauseSeconds = 0.35f;

        [Header("动作节奏")]
        [InspectorName("手势触发概率"), Range(0f, 1f)] public float GestureFrequency = 0.75f;
        [InspectorName("最小手势间隔"), Min(0f)] public float MinimumGestureInterval = 1.2f;
        [InspectorName("单次回复最大手势数"), Range(0, 20)] public int MaxGesturesPerTurn = 4;
        [InspectorName("淡入秒数"), Min(0f)] public float BlendInSeconds = 0.15f;
        [InspectorName("淡出秒数"), Min(0f)] public float BlendOutSeconds = 0.2f;
        [InspectorName("播放速度"), Range(0.1f, 3f)] public float PlaybackSpeed = 1f;
    }
}
