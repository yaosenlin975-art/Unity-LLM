/*
┌────────────────────────────┐
│　Description: 流式文本短句切分器
│　Remark: 标点/长度/停顿三条边界，不逐
│　　　　　 token 判定，避免姿态抖动
│　ClassName: GestureClauseSegmenter
└────────────────────────────┘
*/

using System.Collections.Generic;

namespace LLM.Runtime.Agent.Npc
{
    /// <summary>
    /// 把 ContentDelta 增量切成短句。设计稿 §4.2 的三条边界：明确标点、缓冲够长、流停顿。
    /// 纯逻辑、不碰 Unity 对象，也不改写正文——它只读文本，表现旁路无权截断输出。
    /// </summary>
    public sealed class GestureClauseSegmenter
    {
        private readonly char[] buffer;
        private readonly Queue<string> ready = new Queue<string>();
        private readonly int maxClauseChars;
        private readonly float pauseSeconds;

        private int length;
        private float lastAppendSeconds;

        public GestureClauseSegmenter(int maxClauseChars = 20, float pauseSeconds = 0.35f)
        {
            // 上代理对要多留一格：切点不许落在代理对中间
            this.maxClauseChars = maxClauseChars > 1 ? maxClauseChars : 2;
            this.pauseSeconds = pauseSeconds;
            buffer = new char[this.maxClauseChars + 2];
        }

        /// <summary>nowSeconds 用与 TryTakeTimedOut 同一时间基准（unscaled 墙钟即可）</summary>
        public void Append(string delta, float nowSeconds)
        {
            if (string.IsNullOrEmpty(delta)) return;
            lastAppendSeconds = nowSeconds;

            for (int i = 0; i < delta.Length; i++)
            {
                char c = delta[i];

                // 切点撞上代理对前半，先把已攒的结算掉，让代理对完整落进下一句
                if (char.IsHighSurrogate(c) && length + 1 >= maxClauseChars)
                    Flush();

                buffer[length++] = c;

                if (IsBoundary(c))
                    Flush();
                else if (length >= maxClauseChars)
                    Flush();
            }
        }

        /// <summary>取一个已结算的短句（含结尾标点）</summary>
        public bool TryTakeClause(out string clause)
        {
            if (ready.Count == 0)
            {
                clause = null;
                return false;
            }

            clause = ready.Dequeue();
            return true;
        }

        /// <summary>流停够了就把半截吐出去；没停够则不动，等下一个 delta 或收尾</summary>
        public bool TryTakeTimedOut(float nowSeconds, out string clause)
        {
            if (length > 0 && nowSeconds - lastAppendSeconds >= pauseSeconds)
                Flush();

            return TryTakeClause(out clause);
        }

        /// <summary>流结束：把残留半截当短句吐出，不丢字</summary>
        public bool TryTakeRemaining(out string clause)
        {
            if (length > 0) Flush();
            return TryTakeClause(out clause);
        }

        public void Reset()
        {
            ready.Clear();
            length = 0;
        }

        private static bool IsBoundary(char c)
        {
            switch (c)
            {
                case '，':
                case '。':
                case '！':
                case '？':
                case '；':
                case ',':
                case '.':
                case '!':
                case '?':
                case ';':
                case '\n':
                    return true;
                default:
                    return false;
            }
        }

        private void Flush()
        {
            if (length == 0) return;

            string clause = new string(buffer, 0, length);
            length = 0;

            // 只剩标点或空白的碎片没有语义，播一次手势反而更抖
            if (HasContent(clause))
                ready.Enqueue(clause);
        }

        private static bool HasContent(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (!IsBoundary(s[i]) && !char.IsWhiteSpace(s[i])) return true;
            return false;
        }
    }
}
