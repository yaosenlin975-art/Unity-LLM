/*
┌────────────────────────────┐
│　Description: 世界侧快照聚合单例
│　Remark: static 即单例——注册早于任何场景组件的
│　　　　　 Awake，做成 MonoSingleton 会丢注册
│　ClassName: WorldSnapshotService
└────────────────────────────┘
*/

using System.Collections.Generic;
using Cysharp.Text;
using UnityEngine;

namespace LLM.Runtime.Agent.World
{
    /// <summary>
    /// 世界侧在册表与渲染入口。只存"可观测物"和"世界事实贡献者"，
    /// 不存 Agent——内核当年明确拒过全局 Agent 注册表。
    /// </summary>
    public static class WorldSnapshotService
    {
        #region - 字段 -
        private static readonly List<IWorldObservable> observed = new();
        private static readonly List<IWorldLoreContributor> lore = new();
        private static readonly List<IWorldFactContributor> facts = new();

        private static long observedRevision;
        private static long loreRevision;
        private static long factRevision;

        // 静态段的整段缓存。-1 表示"从未渲染过"，与 revision 的取值域（0 起自增）不重叠，
        // 于是"引用非空 + revision 相等"两道判定任一不成立就必然走重算
        private static string loreText;
        private static long loreTextRevision = -1;
        #endregion

        #region - 属性 -
        public static int ObservableCount => observed.Count;
        public static int LoreCount => lore.Count;
        public static int FactCount => facts.Count;

        /// <summary>注册列表每变一次自增。缓存段靠比对它决定要不要重算</summary>
        public static long ObservedRevision => observedRevision;
        public static long LoreRevision => loreRevision;
        public static long FactRevision => factRevision;
        #endregion

        #region - 生命周期 -
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            // 关掉 Domain Reload 的项目里静态表会带上一局残留；这种模式下静态字段初始化器
            // 不会重跑，所以 revision 也必须归零，否则空表配上旧 revision 会让缓存端出过期快照
            observed.Clear();
            lore.Clear();
            facts.Clear();
            observedRevision = loreRevision = factRevision = 0;
            loreText = null;
            loreTextRevision = -1;
        }
        #endregion

        #region - 公开接口 -
        /// <summary>
        /// 世界静态段。整段缓存：注册列表没变就直接返回同一个 string 引用，
        /// 这样它留在 system 消息的稳定前缀里，provider 缓存才能命中。
        /// 重算的唯一触发是注册列表变化（loreRevision）——贡献者自己改了文本不算。
        /// </summary>
        /// <returns>各贡献者文本按 Order → 类型名 → 渲染文本排序后拼接，空文本跳过；无内容时为空串</returns>
        public static string RenderLore()
        {
            if (loreText is not null && loreTextRevision == loreRevision)
                return loreText;

            if (lore.Count == 0)
            {
                loreText = "";
                loreTextRevision = loreRevision;
                return loreText;
            }

            // 先渲染后排序：排序键的最后一档是渲染出来的文本本身，只能先把文本取出来。
            // 只在未命中路径上做，每个贡献者这一轮仍只被问一次（Calls 语义不变）
            var segments = new List<LoreSegment>(lore.Count);
            for (int i = 0; i < lore.Count; i++)
            {
                var contributor = lore[i];
                var text = contributor.RenderLore();
                if (string.IsNullOrEmpty(text)) continue;

                segments.Add(new LoreSegment(contributor.Order, contributor.GetType().Name, text));
            }

            // 排的是这份片段列表，不是 lore 本身：List.Sort 原地改顺序，
            // 直接排 lore 会把"注册顺序"这个事实破坏掉
            segments.Sort(CompareLoreSegment);

            using var sb = ZString.CreateStringBuilder();
            for (int i = 0; i < segments.Count; i++)
            {
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(segments[i].Text);
            }

            loreText = sb.ToString();
            loreTextRevision = loreRevision;
            return loreText;
        }

        public static void Register(IWorldObservable item)
        {
            if (item == null || Contains(observed, item)) return;
            observed.Add(item);
            observedRevision++;
        }

        public static void Unregister(IWorldObservable item)
        {
            if (item == null) return;
            if (Remove(observed, item)) observedRevision++;
        }

        public static void Register(IWorldLoreContributor contributor)
        {
            if (contributor == null || Contains(lore, contributor)) return;
            lore.Add(contributor);
            loreRevision++;
        }

        public static void Unregister(IWorldLoreContributor contributor)
        {
            if (contributor == null) return;
            if (Remove(lore, contributor)) loreRevision++;
        }

        public static void Register(IWorldFactContributor contributor)
        {
            if (contributor == null || Contains(facts, contributor)) return;
            facts.Add(contributor);
            factRevision++;
        }

        public static void Unregister(IWorldFactContributor contributor)
        {
            if (contributor == null) return;
            if (Remove(facts, contributor)) factRevision++;
        }
        #endregion

        #region - 私有方法 -
        /// <summary>
        /// 静态段的排序比较：Order → 类型名 → 渲染文本的 ordinal。
        /// 前两档只到"类型"粒度，同一类型注册多个实例且 Order 相同时两键全等，
        /// 排序会退化成注册顺序（= Awake 先后），静态段就不再逐字节稳定，
        /// 整段缓存与 provider 前缀命中的前提直接落空。最后一档用文本兜死：
        /// 三键全等意味着文本也相同，此时怎么排都输出同一串字节。
        /// </summary>
        private static int CompareLoreSegment(LoreSegment a, LoreSegment b)
        {
            int byOrder = a.Order.CompareTo(b.Order);
            if (byOrder != 0) return byOrder;

            int byType = string.CompareOrdinal(a.TypeKey, b.TypeKey);
            if (byType != 0) return byType;

            return string.CompareOrdinal(a.Text, b.Text);
        }

        private static bool Contains<T>(List<T> list, T item)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return true;
            return false;
        }

        private static bool Remove<T>(List<T> list, T item)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!ReferenceEquals(list[i], item)) continue;
                list.RemoveAt(i);
                return true;
            }

            return false;
        }
        #endregion

        #region - 嵌套类型 -
        /// <summary>
        /// 静态段的一个片段：排序要用的两把键 + 渲染好的文本。
        /// 只有缓存未命中时才构造，重算结束随局部列表一起丢掉；
        /// 做成 struct 是为了不在"注册表变化"这条路上多扔一堆堆上对象
        /// </summary>
        private struct LoreSegment
        {
            public readonly int Order;
            public readonly string TypeKey;
            public readonly string Text;

            public LoreSegment(int order, string typeKey, string text)
            {
                Order = order;
                TypeKey = typeKey;
                Text = text;
            }
        }
        #endregion
    }
}
