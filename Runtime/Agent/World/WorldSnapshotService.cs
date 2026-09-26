/*
┌────────────────────────────┐
│　Description: 世界侧快照聚合单例
│　Remark: static 即单例——注册早于任何场景组件的
│　　　　　 Awake，做成 MonoSingleton 会丢注册
│　ClassName: WorldSnapshotService
└────────────────────────────┘
*/

using System.Collections.Generic;
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
        }
        #endregion

        #region - 公开接口 -
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
    }
}
