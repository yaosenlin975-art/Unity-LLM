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
    /// <summary>
    /// 抽象表达意图。分类器只吐这一层，具体播哪个 Clip 由本地目录检索决定，
    /// 因此动作库增长不会改变模型看到的任何东西。
    /// </summary>
    public enum EGestureIntent
    {
        [InspectorName("无")]
        None,
        [InspectorName("普通交谈")]
        NeutralTalk,
        [InspectorName("解释")]
        Explain,
        [InspectorName("强调")]
        Emphasize,
        [InspectorName("赞同")]
        Agree,
        [InspectorName("拒绝")]
        Reject,
        [InspectorName("疑问")]
        Question,
        [InspectorName("思考")]
        Think,
        [InspectorName("欢迎")]
        Welcome,
        [InspectorName("指示")]
        Indicate,
        [InspectorName("警告")]
        Warn,
        [InspectorName("愤怒交谈")]
        AngryTalk
    }
}
