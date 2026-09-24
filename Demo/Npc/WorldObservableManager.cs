/*
┌────────────────────────────┐
│　Description: 场景中可监测物的在册表与查询
│　Remark: 静态表而非场景组件，避免注册早于管理器 Awake
│　　　　　 的时序坑；半径等配置留在调用方 Inspector
│　ClassName: WorldObservableManager
└────────────────────────────┘
*/

using System.Collections.Generic;
using UnityEngine;

namespace LLM.Demo.Agent.Npc
{
    /// <summary>一次查询的产物：只带模型用得上的四项，不外露活引用，避免拼接文本时碰到已被销毁的对象。</summary>
    public readonly struct ObservedInfo
    {
        public readonly string Label;
        public readonly Vector3 Position;
        public readonly float Distance;
        public readonly string State;

        public ObservedInfo(string label, Vector3 position, float distance, string state)
        {
            Label = label;
            Position = position;
            Distance = distance;
            State = state;
        }
    }

    /// <summary>
    /// 可监测物的在册表。这张表住在宿主/Demo 层：内核仍然只见到字符串，
    /// ADR-013 拒掉的"内核里的全局 Agent 注册表"没有被重建在这里。
    /// </summary>
    public static class WorldObservableManager
    {
        private static readonly List<WorldObservable> observed = new();

        /// <summary>在册数量，供诊断与测试断言注册/注销是否成对。</summary>
        public static int Count => observed.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            // 关掉 Domain Reload 的项目里静态表会带上局残留，这里统一清一次
            observed.Clear();
        }

        public static void Register(WorldObservable observable)
        {
            if (observable == null || observed.Contains(observable)) return;
            observed.Add(observable);
        }

        public static void Unregister(WorldObservable observable)
        {
            if (observable == null) return;
            observed.Remove(observable);
        }

        /// <summary>
        /// 以 origin 为球心、radius 为半径收集在册可监测物，按距离升序写入 results。
        /// 返回因超过 maxCount 而被丢掉的数量——调用方必须把它写成"另有 K 个未列出"，
        /// 否则模型会以为场上只剩列出来的这几个。
        /// </summary>
        public static int CollectNearby(Vector3 origin, float radius, int maxCount,
            WorldObservable except, List<ObservedInfo> results)
        {
            results.Clear();

            if (maxCount <= 0 || radius <= 0f) return 0;

            float radiusSq = radius * radius;
            for (int i = 0; i < observed.Count; i++)
            {
                var item = observed[i];
                if (item == null || ReferenceEquals(item, except)) continue;

                var position = item.ObservedPosition;
                var offset = position - origin;
                float distanceSq = offset.sqrMagnitude;
                if (distanceSq > radiusSq) continue;

                results.Add(new ObservedInfo(item.ObservedLabel, position,
                    Mathf.Sqrt(distanceSq), item.ObservableState));
            }

            if (results.Count == 0) return 0;

            results.Sort(CompareByDistance);

            if (results.Count <= maxCount) return 0;

            int truncated = results.Count - maxCount;
            results.RemoveRange(maxCount, truncated);
            return truncated;
        }

        private static int CompareByDistance(ObservedInfo a, ObservedInfo b) => a.Distance.CompareTo(b.Distance);
    }
}
