/*
┌────────────────────────────┐
│　Description: 抽象表达意图与情绪枚举
│　Remark: 表演导演内部的中间语言，不进
│　　　　　 提示词也不进动作目录对外
│　ClassName: GestureIntent
└────────────────────────────┘
*/

namespace LLM.Runtime.Agent.Npc
{

    /// <summary>一次意图判定的结果：类型 + 强度（0–1，供目录的 MinIntensity/MaxIntensity 过滤）</summary>
    public readonly struct GestureIntent
    {
        public EGestureIntent Type { get; }
        public float Intensity { get; }

        public GestureIntent(EGestureIntent type, float intensity)
        {
            Type = type;
            Intensity = intensity;
        }

        public static readonly GestureIntent None = new GestureIntent(EGestureIntent.None, 0f);
    }
}
