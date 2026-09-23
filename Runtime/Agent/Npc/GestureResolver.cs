/*
 * ┌────────────────────────────────────────────┐
 * │ Description : NPC 本地说话动作检索器        │
 * │ Remark      : 硬过滤后按权重与最近使用选择   │
 * │ ClassName   : GestureResolver               │
 * └────────────────────────────────────────────┘
 */

using System.Collections.Generic;

namespace LLM.Runtime.Agent.Npc
{
    public sealed class GestureResolver
    {
        private readonly Dictionary<string, float> lastPlayedAt = new();

        public GestureDefinition Resolve(NpcGestureCatalog_SO catalog, GestureIntent intent,
            ENpcEmotion emotion, float nowSeconds)
        {
            if (catalog == null || intent.Type == EGestureIntent.None)
                return null;

            GestureDefinition[] gestures = catalog.Gestures;
            if (gestures == null) return null;
            GestureDefinition best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < gestures.Length; i++)
            {
                GestureDefinition candidate = gestures[i];
                if (!IsEligible(candidate, intent, emotion, nowSeconds)) continue;

                float recentPenalty = lastPlayedAt.ContainsKey(candidate.Id) ? 0.5f : 0f;
                float score = candidate.SelectionWeight - recentPenalty;
                if (score <= bestScore) continue;

                bestScore = score;
                best = candidate;
            }

            return best;
        }

        public void MarkPlayed(GestureDefinition gesture, float nowSeconds)
        {
            if (gesture == null || string.IsNullOrEmpty(gesture.Id)) return;
            lastPlayedAt[gesture.Id] = nowSeconds;
        }

        public void Reset()
        {
            lastPlayedAt.Clear();
        }

        private bool IsEligible(GestureDefinition gesture, GestureIntent intent,
            ENpcEmotion emotion, float nowSeconds)
        {
            if (gesture == null || gesture.Clip == null || string.IsNullOrEmpty(gesture.Id))
                return false;

            // 当前确认范围只支持上半身覆盖；全身与 additive 留在数据结构但明确拒绝执行。
            if (!gesture.UpperBodyOnly || gesture.Additive)
                return false;

            if (intent.Intensity < gesture.MinIntensity || intent.Intensity > gesture.MaxIntensity)
                return false;

            if (gesture.Intents == null || !Contains(gesture.Intents, intent.Type))
                return false;

            if (gesture.Emotions != null && gesture.Emotions.Length > 0 &&
                !Contains(gesture.Emotions, emotion))
                return false;

            return !lastPlayedAt.TryGetValue(gesture.Id, out float lastPlayed) ||
                   nowSeconds - lastPlayed >= gesture.CooldownSeconds;
        }

        private static bool Contains(EGestureIntent[] values, EGestureIntent value)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] == value) return true;
            return false;
        }

        private static bool Contains(ENpcEmotion[] values, ENpcEmotion value)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] == value) return true;
            return false;
        }
    }
}
