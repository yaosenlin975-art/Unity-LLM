/*
┌────────────────────────────┐
│　Description: 编辑器工具发现
│　Remark: LLM.Editor 编译期引用不到游戏
│　　　　　 程序集，只能在编辑期反射发现并
│　　　　　 登记引用 LLM.Runtime 的程序集；
│　　　　　 另枚举可挂载的成员工具组件
│　ClassName: AgentToolScanner
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Text;
using LLM.Runtime;
using LLM.Runtime.Agent;
using UnityEngine;

namespace LLM.Editor
{
    /// <summary>
    /// Inspector 的“扫描并同步”用它把工具扫全：LLM.Editor 不能引用 Game.Hotfix 等程序集，
    /// 但运行时可反射枚举它们。只处理引用了 LLM.Runtime 的程序集，避免全 AppDomain 扫描。
    /// </summary>
    public static class AgentToolScanner
    {
        private static readonly Assembly llmRuntimeAssembly = typeof(AgentToolRegistry).Assembly;
        private static readonly string llmRuntimeName = llmRuntimeAssembly.GetName().Name;

        /// <summary>登记 LLM.Runtime 及所有引用它的程序集；注册表内部幂等，重复调用无副作用</summary>
        public static void RegisterProjectAssemblies()
        {
            RegisterIntoBoth(llmRuntimeAssembly);

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var asm = assemblies[i];
                if (asm == llmRuntimeAssembly) continue;
                if (asm.IsDynamic) continue;
                if (IsTestAssembly(asm)) continue;
                if (!References(asm, llmRuntimeName)) continue;

                RegisterIntoBoth(asm);
            }
        }

        /// <summary>
        /// 枚举「快速添加」的候选：LLM.Runtime 自身与所有引用它的程序集里，MonoBehaviour 上的
        /// 每个 public 实例 [AgentTool]/[AgentAction] 方法算一条。只认 MonoBehaviour——层级扫描走
        /// GetComponentsInChildren，普通类的实例方法挂不上物体，列出来也是骗人的。
        /// </summary>
        public static List<AgentMemberProvider> CollectMemberProviders()
        {
            var result = new List<AgentMemberProvider>();

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var asm = assemblies[i];
                if (asm.IsDynamic) continue;
                if (IsTestAssembly(asm)) continue;
                if (IsEditorAssembly(asm)) continue;
                // 程序集不会出现在自己的 GetReferencedAssemblies 里，框架自带的成员工具组件要单独放行
                if (asm != llmRuntimeAssembly && !References(asm, llmRuntimeName)) continue;

                if (!TryGetTypes(asm, out var types)) continue;

                for (int t = 0; t < types.Length; t++)
                    CollectProviders(types[t], result);
            }

            result.Sort(CompareById);
            return result;
        }

        private static void RegisterIntoBoth(Assembly asm)
        {
            AgentToolRegistry.Register(asm);
            AgentActionRegistry.Register(asm);
        }

        /// <summary>测试程序集也会引用 LLM.Runtime，跳过以免脚手架动作混进正式候选列表</summary>
        private static bool IsTestAssembly(Assembly asm)
        {
            var name = asm.GetName().Name;
            return name != null && name.IndexOf("Tests", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 编辑器专用程序集按 Unity 的命名约定识别（Foo.Editor / Assembly-CSharp-Editor）。
        /// 不能用「引用了 UnityEditor」判定：编辑器里构建的运行时程序集同样引用 UnityEditor.CoreModule，
        /// 那样会把 LLM.Runtime 自己误杀成编辑器侧。
        /// </summary>
        private static bool IsEditorAssembly(Assembly asm)
        {
            var name = asm.GetName().Name;
            if (name == null) return false;

            var segments = name.Split('.');
            for (int i = 0; i < segments.Length; i++)
                if (segments[i] == "Editor") return true;

            return name.EndsWith("-Editor", StringComparison.Ordinal);
        }

        private static bool References(Assembly asm, params string[] names)
        {
            try
            {
                var refs = asm.GetReferencedAssemblies();
                for (int i = 0; i < refs.Length; i++)
                {
                    for (int n = 0; n < names.Length; n++)
                    {
                        if (refs[i].Name == names[n]) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(AgentToolScanner),
                    ZString.Format("读取 {0} 的程序集引用失败: {1}", asm.GetName().Name, ex.Message));
            }

            return false;
        }

        private static bool TryGetTypes(Assembly asm, out Type[] types)
        {
            try
            {
                types = asm.GetTypes();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(AgentToolScanner),
                    ZString.Format("枚举 {0} 的类型失败: {1}", asm.GetName().Name, ex.Message));
                types = null;
                return false;
            }
        }

        /// <summary>把一个类型上的实例工具/动作声明摊平成候选条目；取名规则与注册表一致（attr.Name ?? method.Name）</summary>
        private static void CollectProviders(Type type, List<AgentMemberProvider> into)
        {
            // 静态类在 IL 里就是 abstract+sealed，和抽象基类一起被 IsAbstract 挡掉；
            // 嵌套类的 MonoScript 归属不明确，存进场景容易变 Missing Script
            if (type.IsAbstract || type.IsGenericTypeDefinition || type.IsNested) return;
            if (!typeof(MonoBehaviour).IsAssignableFrom(type)) return;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                var tool = method.GetCustomAttribute<AgentToolAttribute>();
                if (tool != null)
                {
                    into.Add(new AgentMemberProvider { Id = tool.Name ?? method.Name, Owner = type });
                    continue;
                }

                var action = method.GetCustomAttribute<AgentActionAttribute>();
                if (action != null)
                    into.Add(new AgentMemberProvider { Id = action.Name ?? method.Name, Owner = type });
            }
        }

        private static int CompareById(AgentMemberProvider a, AgentMemberProvider b)
        {
            return string.CompareOrdinal(a.Id, b.Id);
        }
    }

    /// <summary>「快速添加」的一条候选声明：工具/动作名 + 声明它的组件类型</summary>
    public sealed class AgentMemberProvider
    {
        public string Id;
        public Type Owner;
    }
}
