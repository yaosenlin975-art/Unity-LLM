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

        // 在场者收集复用表：动态段每轮每个 NPC 都要跑，是热路径，不每轮 new。
        // 只在 RenderFacts 内部进出（CollectPeers 进、AppendPeers 出），不跨帧持有引用；
        // AppendPeers 不会再调 RenderFacts，重入安全。动态段本身绝不缓存——在场者依赖
        // 请求者的 origin 与 except，整段缓存会让所有 NPC 拿到同一份"身边有谁"
        private static readonly List<Peer> peerScratch = new();
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

        /// <summary>
        /// 世界动态段 = 全局事实（各贡献者每轮自查）+ 在场者（按请求者现算）。
        /// 整段绝不缓存：origin 是"我"的位置、except 是"我"自己，A 与 B 看到的天然不同，
        /// 缓存会让所有 NPC 拿到同一份"身边有谁"。
        /// </summary>
        /// <param name="origin">请求者原点，在场者按到它的距离筛选与排序</param>
        /// <param name="except">要排除的观测物（通常是请求者自己），按引用相等排除</param>
        /// <param name="radius">半径（米），小于等于 0 时不输出在场者段</param>
        /// <param name="maxPeers">最多列出几个在场者，超出部分折叠为"另有 K 个未列出"；小于等于 0 表示不限</param>
        /// <returns>事实段（按 Order → 类型名 → 渲染文本排序、空文本跳过）与在场者段之间以换行连接；无内容时为空串</returns>
        public static string RenderFacts(Vector3 origin, IWorldObservable except,
            float radius, int maxPeers)
        {
            // sb 不能用 using 声明——using 变量禁止 ref 传参（CS1657），而 AppendPeers 必须
            // 以 ref 拿到同一个 builder 追加（Utf16ValueStringBuilder 是 struct，按值传全丢）。
            // 改手动 try/finally，Dispose 语义不丢
            var sb = ZString.CreateStringBuilder();
            try
            {
                if (facts.Count > 0)
                {
                    // 先渲染后排序（同 RenderLore 的三档键）：末档键就是渲染出来的文本，
                    // 只能先把文本取出来。这里不整段缓存，但顺序仍要确定——
                    // 排序退化成注册顺序的话，动态段会在没有任何事实变化时白白抖动
                    var segments = new List<FactSegment>(facts.Count);
                    for (int i = 0; i < facts.Count; i++)
                    {
                        var contributor = facts[i];
                        var text = contributor.RenderFact();
                        if (string.IsNullOrEmpty(text)) continue;

                        segments.Add(new FactSegment(contributor.Order, contributor.GetType().Name, text));
                    }

                    // 排的是片段副本，facts 本身保持注册顺序
                    segments.Sort(CompareFactSegment);

                    for (int i = 0; i < segments.Count; i++)
                    {
                        if (sb.Length > 0) sb.Append("\n");
                        sb.Append(segments[i].Text);
                    }
                }

                if (radius > 0f && observed.Count > 0)
                {
                    var peers = CollectPeers(origin, except, radius, maxPeers, out int truncated);
                    if (peers.Count > 0)
                    {
                        if (sb.Length > 0) sb.Append("\n");
                        AppendPeers(ref sb, peers, truncated);
                    }
                }

                return sb.ToString();
            }
            finally
            {
                sb.Dispose();
            }
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

        /// <summary>
        /// 全局事实段的排序比较：Order → 类型名 → 渲染文本的 ordinal，与 CompareLoreSegment 同构。
        /// 前两档只到"类型"粒度，同类型多实例且 Order 相同时两键全等，
        /// 排序会退化成注册顺序（= 注册先后），动态段文本在没有事实变化时也抖动
        /// </summary>
        private static int CompareFactSegment(FactSegment a, FactSegment b)
        {
            int byOrder = a.Order.CompareTo(b.Order);
            if (byOrder != 0) return byOrder;

            int byType = string.CompareOrdinal(a.TypeKey, b.TypeKey);
            if (byType != 0) return byType;

            return string.CompareOrdinal(a.Text, b.Text);
        }

        /// <summary>
        /// 收拢半径内的在场者：判活 → 排除请求者 → 半径过滤 → 三档排序 → 截断。
        /// 结果写进复用表 peerScratch 返回，调用方只读不回存
        /// </summary>
        private static List<Peer> CollectPeers(Vector3 origin, IWorldObservable except,
            float radius, int maxPeers, out int truncated)
        {
            peerScratch.Clear();

            float radiusSq = radius * radius;
            for (int i = 0; i < observed.Count; i++)
            {
                var item = observed[i];

                // 判活必须走 Unity 语义（R8）：observed 存的是接口引用，item == null 编译期
                // 选不到 UnityEngine.Object 的 == 重载，是纯引用判空，拦不住已销毁的组件；
                // 对残骸取 ObservedPosition 会抛 MissingReferenceException。先把引用转成
                // UnityEngine.Object 类型的局部变量再比 null，Unity 语义的重载才会被选中。
                // 注意方向是"仅拦已销毁组件"而不是"只留 Unity 对象"：纯 C# 实现没有
                // 原生侧、不存在残骸态，照常参与在场者，否则动态段离线不可测、
                // 未来的虚拟观测物（无 GameObject 的实现）也会被静默吞掉
                if (item is UnityEngine.Object unityObj && unityObj == null) continue;
                if (ReferenceEquals(item, except)) continue;

                var offset = item.ObservedPosition - origin;
                float distanceSq = offset.sqrMagnitude;
                if (distanceSq > radiusSq) continue;

                peerScratch.Add(new Peer
                {
                    Label = item.ObservedLabel,
                    Distance = Mathf.Sqrt(distanceSq),
                    State = item.ObservableState
                });
            }

            peerScratch.Sort(ComparePeer);

            if (maxPeers <= 0 || peerScratch.Count <= maxPeers)
            {
                truncated = 0;
                return peerScratch;
            }

            truncated = peerScratch.Count - maxPeers;
            peerScratch.RemoveRange(maxPeers, truncated);
            return peerScratch;
        }

        /// <summary>
        /// 在场者排序：距离 → 名字 → 状态文本 的 ordinal（裁决 2，与静态段三档键同构）。
        /// 只排距离的话，同距离的在场者先后随注册顺序抖；名字档打平（同名两人）
        /// 再落到状态文本。末档用文本兜死：三键全等意味着渲染行本身相同，怎么排输出都一致
        /// </summary>
        private static int ComparePeer(Peer a, Peer b)
        {
            int byDistance = a.Distance.CompareTo(b.Distance);
            if (byDistance != 0) return byDistance;

            int byLabel = string.CompareOrdinal(a.Label, b.Label);
            if (byLabel != 0) return byLabel;

            return string.CompareOrdinal(a.State, b.State);
        }

        // Utf16ValueStringBuilder 是 struct，不加 ref 的话追加全落在副本上，文本静默丢失
        private static void AppendPeers(ref Utf16ValueStringBuilder sb, List<Peer> peers, int truncated)
        {
            for (int i = 0; i < peers.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(peers[i].Label);
                sb.Append(ZString.Format("({0:0.0}米", peers[i].Distance));
                if (!string.IsNullOrEmpty(peers[i].State))
                    sb.Append(ZString.Format("|{0}", peers[i].State));
                sb.Append(")");
            }

            // 截断必须报数：不报，模型会以为场上只剩列出来的这几个
            if (truncated > 0)
                sb.Append(ZString.Format("，另有 {0} 个未列出", truncated));
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

        /// <summary>
        /// 在场者的一条渲染素材：名字、到请求者的距离（米）、自我状态。
        /// 三档排序键全在这里，渲染文本本身不作字段——行文本由 AppendPeers 现拼。
        /// 只在 CollectPeers→AppendPeers 之间存活，随复用表跨轮清空重用
        /// </summary>
        private struct Peer
        {
            public string Label;
            public float Distance;
            public string State;
        }

        /// <summary>
        /// 全局事实的一个片段：排序两把键 + 渲染好的文本（形状同 LoreSegment，
        /// 区别只在动态段每轮都重建、不进任何缓存）
        /// </summary>
        private struct FactSegment
        {
            public readonly int Order;
            public readonly string TypeKey;
            public readonly string Text;

            public FactSegment(int order, string typeKey, string text)
            {
                Order = order;
                TypeKey = typeKey;
                Text = text;
            }
        }
        #endregion
    }
}
