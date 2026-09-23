/*
┌────────────────────────────┐
│　Description: 抽象表达意图与情绪枚举
│　Remark: 表演导演内部的中间语言，不进
│　　　　　 提示词也不进动作目录对外
│　ClassName: GestureIntent
└────────────────────────────┘
*/

using UnityEngine;

namespace LLM.Runtime.Agent.Npc
{
    /// <summary>说话期分类器用到的情绪。取值口径与 NpcAgentProfile 的情绪字段一致</summary>
    public enum ENpcEmotion
    {
        [InspectorName("平静")]
        Neutral,
        [InspectorName("高兴")]
        Happy,
        [InspectorName("悲伤")]
        Sad,
        [InspectorName("愤怒")]
        Angry,
        [InspectorName("害怕")]
        Fearful
    }
}
