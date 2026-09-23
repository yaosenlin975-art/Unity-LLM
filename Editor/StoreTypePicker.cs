/*
┌────────────────────────────┐
│　Description: 收集可配置的存储实现候选
│　Remark: 编辑器/测试程序集判定复用 AgentToolScanner 那两条，
│　　　　　 别另起一套口径；内核的 Null 对象不作候选（ADR-030）
│　ClassName: StoreTypePicker
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime;
using LLM.Runtime.Agent.Npc;
using LLM.Runtime.Storage;
using UnityEditor;

namespace LLM.Editor
{
    public readonly struct StoreTypeCandidate
    {
        /// <summary>序列化值；空 = 不持久化。</summary>
        public readonly string FullName;

        /// <summary>下拉文案。</summary>
        public readonly string DisplayName;

        /// <summary>Type.Name：嵌套类的 FullName 尾段是 "Outer+Inner"，只能按这个键判同名。</summary>
        public readonly string Name;

        public StoreTypeCandidate(string fullName, string displayName, string name)
        {
            FullName = fullName;
            DisplayName = displayName;
            Name = name;
        }
    }

    public static class StoreTypePicker
    {
        // 静态表随域重载归零，而新增类型必然经过一次编译（= 一次域重载），所以不必做失效
        private static readonly Dictionary<Type, List<StoreTypeCandidate>> cache = new();

        public static List<StoreTypeCandidate> Collect(Type requiredInterface)
        {
            if (cache.TryGetValue(requiredInterface, out var cached)) return cached;

            var result = new List<StoreTypeCandidate>();
            var nameHits = new Dictionary<string, int>();

            foreach (var type in TypeCache.GetTypesDerivedFrom(requiredInterface))
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;
                if (typeof(UnityEngine.Object).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null) continue;

                var asm = type.Assembly;
                if (AgentToolScanner.IsEditorAssembly(asm) || AgentToolScanner.IsTestAssembly(asm)) continue;

                // "不落盘"由下拉首项的空值唯一表达，不给第二条同义路径
                if (type == typeof(NullAgentStateStore) || type == typeof(NullNpcAffinityStore)) continue;

                nameHits.TryGetValue(type.Name, out int n);
                nameHits[type.Name] = n + 1;
                result.Add(new StoreTypeCandidate(type.FullName, ShortName(type), type.Name));
            }

            result.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));

            // 同短名时整条改用完整 FullName 消歧；序列化始终存 FullName，显示碰撞不影响取值
            for (int i = 0; i < result.Count; i++)
            {
                var candidate = result[i];
                if (nameHits[candidate.Name] > 1)
                    result[i] = new StoreTypeCandidate(candidate.FullName, candidate.FullName, candidate.Name);
            }

            cache[requiredInterface] = result;
            return result;
        }

        private static string ShortName(Type type)
        {
            return string.IsNullOrEmpty(type.Namespace)
                ? type.Name
                : ZString.Concat(type.Name, " (", type.Namespace, ")");
        }
    }
}
