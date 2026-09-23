using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Text;
using LLM.Runtime.Agent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LLM.Runtime
{
    public static class AgentToolRegistry
    {
        private static readonly Dictionary<string, RegisteredTool> tools = new();
        private static int version;
        private static readonly object _lock = new();
        private static readonly List<Assembly> registeredAssemblies = new();
        private static readonly Lazy<bool> _initializer = new Lazy<bool>(InitSelfAssembly);

        private static bool InitSelfAssembly()
        {
            Register(typeof(AgentToolRegistry).Assembly);
            return true;
        }

        public struct RegisteredTool
        {
            public string Name;
            public string Description;
            public string ParametersJsonSchema;
            public MethodInfo Method;

            /// <summary>成员工具（实例方法）的落地对象；全局静态工具为 null</summary>
            public object Target;

            /// <summary>0 = 继承全局阈值，-1 = 关闭检测</summary>
            public int RepeatLimit;
            public int NameRepeatLimit;

            public bool Idempotent;
        }

        public static int Version => version;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void AutoRegister()
        {
            _ = _initializer.Value;
        }

        public static void Register(Assembly assembly)
        {
            if (assembly == null) return;
            lock (_lock)
            {
                if (registeredAssemblies.Contains(assembly)) return;
                registeredAssemblies.Add(assembly);
                try
                {
                    ScanAssembly(assembly);
                }
                catch (Exception ex)
                {
                    Log.Warning(nameof(AgentToolRegistry),
                        ZString.Format("扫描 {0} 失败: {1}", assembly.GetName().Name, ex.Message));
                }
            }
        }

        public static void Refresh()
        {
            lock (_lock)
            {
                tools.Clear();
                version++;
                foreach (var asm in registeredAssemblies)
                {
                    try
                    {
                        ScanAssembly(asm);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(nameof(AgentToolRegistry),
                            ZString.Format("扫描 {0} 失败: {1}", asm.GetName().Name, ex.Message));
                    }
                }
            }
        }

        private static void ScanAssembly(Assembly asm)
        {
            foreach (var type in asm.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<AgentToolAttribute>();
                    if (attr == null) continue;

                    if (!ValidateMethod(method, out string error))
                    {
                        Log.Warning(nameof(AgentToolRegistry),
                            ZString.Format("跳过 {0}.{1}: {2}", type.Name, method.Name, error));
                        continue;
                    }

                    string name = attr.Name ?? method.Name;
                    string description = attr.Description ?? "";
                    string paramSchema = BuildParameterSchema(method);

                    tools[name] = new RegisteredTool
                    {
                        Name = name,
                        Description = description,
                        ParametersJsonSchema = paramSchema,
                        Method = method,
                        RepeatLimit = attr.RepeatLimit,
                        NameRepeatLimit = attr.NameRepeatLimit,
                        Idempotent = attr.Idempotent
                    };
                    version++;
                }
            }
        }

        private static bool ValidateMethod(MethodInfo method, out string error)
        {
            error = null;

            if (method.ReturnType != typeof(string))
            {
                error = ZString.Format("返回值必须是 string，实际为 {0}", method.ReturnType.Name);
                return false;
            }

            foreach (var param in method.GetParameters())
            {
                if (!IsSimpleType(param.ParameterType))
                {
                    error = ZString.Format("参数 '{0}' 类型 {1} 不支持", param.Name, param.ParameterType.Name);
                    return false;
                }
            }

            return true;
        }

        private static bool IsSimpleType(Type type)
        {
            if (type == typeof(int) || type == typeof(float) || type == typeof(string) || type == typeof(bool))
                return true;
            if (type == typeof(int?) || type == typeof(float?) || type == typeof(bool?))
                return true;
            return false;
        }

        private static string BuildParameterSchema(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var properties = new JObject();
            var required = new JArray();

            foreach (var param in parameters)
            {
                var propDef = new JObject
                {
                    ["type"] = GetJsonType(param.ParameterType),
                    ["description"] = ""
                };
                properties[param.Name] = propDef;

                if (!param.HasDefaultValue)
                    required.Add(param.Name);
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count > 0)
                schema["required"] = required;

            return schema.ToString(Formatting.None);
        }

        private static string GetJsonType(Type type)
        {
            if (type == typeof(int) || type == typeof(int?)) return "integer";
            if (type == typeof(float) || type == typeof(float?)) return "number";
            if (type == typeof(bool) || type == typeof(bool?)) return "boolean";
            return "string";
        }

        public static void Register(string name, string description, string paramSchema, MethodInfo method,
            int repeatLimit = 0, bool idempotent = true, int nameRepeatLimit = 0)
        {
            lock (_lock)
            {
                tools[name] = new RegisteredTool
                {
                    Name = name,
                    Description = description,
                    ParametersJsonSchema = paramSchema,
                    Method = method,
                    RepeatLimit = repeatLimit,
                    NameRepeatLimit = nameRepeatLimit,
                    Idempotent = idempotent
                };
                version++;
            }
        }

        /// <summary>按名取注册项，供循环检测读该工具自己的 RepeatLimit / Idempotent</summary>
        public static bool TryGet(string name, out RegisteredTool tool)
        {
            // 与 ToLLMTools 同理：惰性初始化会走 Register 的锁，必须在取锁之前触发
            _ = _initializer.Value;

            lock (_lock)
            {
                if (tools.TryGetValue(name, out tool)) return true;
                tool = default;
                return false;
            }
        }

        public static void Unregister(string name)
        {
            lock (_lock)
            {
                if (tools.Remove(name))
                    version++;
            }
        }

        /// <summary>已注册工具的只读快照，供编辑器 Inspector 枚举候选；返回新 List，不暴露内部字典</summary>
        public static List<RegisteredTool> Snapshot()
        {
            // 与 ToLLMTools 同理：惰性初始化会走 Register 的锁，必须在取锁之前触发
            _ = _initializer.Value;

            lock (_lock)
            {
                var result = new List<RegisteredTool>(tools.Count);
                foreach (var kv in tools)
                    result.Add(kv.Value);
                return result;
            }
        }

        /// <summary>扫描单个实例上的 public 实例 [AgentTool] 方法，返回带 Target 的成员工具（不写全局表）</summary>
        public static List<RegisteredTool> ScanTools(object target)
        {
            var result = new List<RegisteredTool>();
            if (target == null) return result;

            CollectInstanceTools(target.GetType(), target, result);
            return result;
        }

        /// <summary>扫描 root（含自身）下所有 MonoBehaviour 的实例 [AgentTool]，成员工具 per-agent 私有</summary>
        public static List<RegisteredTool> ScanHierarchyTools(GameObject root)
        {
            var result = new List<RegisteredTool>();
            if (root == null) return result;

            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null) continue;
                CollectInstanceTools(behaviour.GetType(), behaviour, result);
            }

            return result;
        }

        private static void CollectInstanceTools(Type type, object target, List<RegisteredTool> result)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<AgentToolAttribute>();
                if (attr == null) continue;

                if (!ValidateMethod(method, out string error))
                {
                    Log.Warning(nameof(AgentToolRegistry),
                        ZString.Format("跳过 {0}.{1}: {2}", type.Name, method.Name, error));
                    continue;
                }

                result.Add(new RegisteredTool
                {
                    Name = attr.Name ?? method.Name,
                    Description = attr.Description ?? "",
                    ParametersJsonSchema = BuildParameterSchema(method),
                    Method = method,
                    Target = target,
                    RepeatLimit = attr.RepeatLimit,
                    NameRepeatLimit = attr.NameRepeatLimit,
                    Idempotent = attr.Idempotent
                });
            }
        }

        private static List<LLMTool> cachedLLMTools;
        private static int cachedVersion = -1;

        public static List<LLMTool> ToLLMTools()
        {
            // 必须在取锁之前触发：惰性初始化本身会走 Register 的锁
            _ = _initializer.Value;

            lock (_lock)
            {
                if (cachedVersion == version && cachedLLMTools != null)
                    return cachedLLMTools;

                var result = new List<LLMTool>(tools.Count);
                foreach (var kv in tools)
                {
                    var tool = kv.Value;
                    result.Add(new LLMTool(tool.Name, tool.Description, tool.ParametersJsonSchema));
                }
                cachedLLMTools = result;
                cachedVersion = version;
                return result;
            }
        }

        public static string Execute(string toolName, string argumentsJson)
        {
            RegisteredTool tool;
            lock (_lock)
            {
                if (!tools.TryGetValue(toolName, out tool))
                    return ZString.Concat("[Tool Error] Unknown tool: ", toolName);
            }

            return Execute(tool, argumentsJson);
        }

        /// <summary>执行指定工具：成员工具打在 Target 实例上，全局静态 Target 为 null</summary>
        public static string Execute(RegisteredTool tool, string argumentsJson)
        {
            try
            {
                // 格式闸：坏参数不执行，返回串本身就是回灌给模型的原因。
                // 必须在 try 内：拿到 default(RegisteredTool)（Method 为 null）时
                // 也要照旧退化成 "[Tool Error] …" 串，而不是把 NRE 抛给调用方。
                if (!AgentArgumentGate.TryValidate(argumentsJson, tool.Method.GetParameters(), null,
                        out var parsed, out string formatError))
                    return formatError;

                if (tool.Target is UnityEngine.Object unityObj && unityObj == null)
                    return ZString.Format("[Tool Error] {0}: 目标已销毁", tool.Name);

                var args = ParseArguments(tool, parsed);
                var result = tool.Method.Invoke(tool.Target, args);
                return result as string ?? "";
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return ZString.Format("[Tool Error] {0}: {1}: {2}",
                    tool.Name, inner.GetType().Name, inner.Message);
            }
        }

        private static object[] ParseArguments(RegisteredTool tool, JObject parsed)
        {
            var parameters = tool.Method.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                // 取值与转换都走闸那一套，与动作路径共用同一个映射，避免两处答案不一致
                var token = AgentArgumentGate.Value(parsed, param.Name);

                if (token != null)
                {
                    args[i] = AgentArgumentGate.TryCoerce(token, param.ParameterType, out object coerced)
                        ? coerced
                        : GetDefault(param.ParameterType);
                }
                else if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                }
                else
                {
                    args[i] = GetDefault(param.ParameterType);
                }
            }

            return args;
        }

        private static object GetDefault(Type type)
        {
            if (type == typeof(int) || type == typeof(int?)) return 0;
            if (type == typeof(float) || type == typeof(float?)) return 0f;
            if (type == typeof(bool) || type == typeof(bool?)) return false;
            return "";
        }
    }
}
