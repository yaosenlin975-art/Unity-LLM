/*
┌────────────────────────────┐
│　Description: 确定性规则意图分类器
│　Remark: 只吐抽象意图，不碰动作目录
│　ClassName: RuleGestureIntentClassifier
└────────────────────────────┘
*/

namespace LLM.Runtime.Agent.Npc
{
    /// <summary>
    /// 设计稿 §4.3 的首期实现：短句关键词 + 问号/感叹号 + 当前情绪 → 抽象意图。
    /// 读不到完整历史，也不向主 LLM 追问；顺序即优先级，先判问句再判关键词。
    /// </summary>
    public static class RuleGestureIntentClassifier
    {
        private static readonly string[] k_questionWords =
            { "为什么", "怎么", "怎样", "是否", "什么", "吗", "呢" };
        private static readonly string[] k_rejectWords =
            { "不行", "不对", "不要", "不同意", "不可能", "拒绝", "才不", "没门", "别这样" };
        private static readonly string[] k_agreeWords =
            { "当然", "好的", "好呀", "可以", "没问题", "同意", "是啊", "行吧" };
        private static readonly string[] k_indicateWords =
            { "那边", "这儿", "这里", "那儿", "跟我来", "请看", "看向", "指向" };
        private static readonly string[] k_explainWords =
            { "因为", "原因", "说明", "解释", "其实", "也就是说", "之所以" };
        private static readonly string[] k_emphasizeWords =
            { "一定", "必须", "特别", "非常", "千万", "记住" };
        private static readonly string[] k_warnWords =
            { "小心", "注意", "危险", "别靠近", "慢点" };
        private static readonly string[] k_thinkWords =
            { "让我想", "想想", "考虑", "这个嘛" };
        private static readonly string[] k_welcomeWords =
            { "欢迎", "你好", "您好", "见到你" };

        public static GestureIntent Classify(string clause, ENpcEmotion emotion)
        {
            if (string.IsNullOrEmpty(clause))
                return GestureIntent.None;

            if (ContainsAny(clause, k_questionWords) ||
                clause.IndexOf('？') >= 0 || clause.IndexOf('?') >= 0)
                return new GestureIntent(EGestureIntent.Question, 0.7f);

            // 关键词按"代价"排序：拒/先是会改变对话走向的态度，错判比漏判贵
            if (ContainsAny(clause, k_rejectWords)) return new GestureIntent(EGestureIntent.Reject, 0.85f);
            if (ContainsAny(clause, k_agreeWords)) return new GestureIntent(EGestureIntent.Agree, 0.6f);
            if (ContainsAny(clause, k_warnWords)) return new GestureIntent(EGestureIntent.Warn, 0.85f);
            if (ContainsAny(clause, k_indicateWords)) return new GestureIntent(EGestureIntent.Indicate, 0.7f);
            if (ContainsAny(clause, k_explainWords)) return new GestureIntent(EGestureIntent.Explain, 0.7f);
            if (ContainsAny(clause, k_emphasizeWords)) return new GestureIntent(EGestureIntent.Emphasize, 0.85f);
            if (ContainsAny(clause, k_thinkWords)) return new GestureIntent(EGestureIntent.Think, 0.35f);
            if (ContainsAny(clause, k_welcomeWords)) return new GestureIntent(EGestureIntent.Welcome, 0.6f);

            if (emotion == ENpcEmotion.Angry) return new GestureIntent(EGestureIntent.AngryTalk, 0.8f);

            if (clause.IndexOf('！') >= 0 || clause.IndexOf('!') >= 0)
                return new GestureIntent(EGestureIntent.Emphasize, 0.8f);

            return new GestureIntent(EGestureIntent.NeutralTalk, 0.4f);
        }

        private static bool ContainsAny(string clause, string[] words)
        {
            for (int i = 0; i < words.Length; i++)
                if (clause.IndexOf(words[i], System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
