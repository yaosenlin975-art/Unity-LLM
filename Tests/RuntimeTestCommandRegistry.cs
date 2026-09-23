/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Runtime 测试命令白名单注册表   │
 * │ Remark      : 只按命令 ID 调用显式标记方法    │
 * │ ClassName   : RuntimeTestCommandRegistry     │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LLM.Runtime.RuntimeTesting
{
    public sealed class RuntimeTestCommandRegistry
    {
        private readonly Dictionary<string, RegisteredCommand> commands = new();

        public void Register(MonoBehaviour provider)
        {
            if (provider == null) return;

            var methods = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                var attribute = method.GetCustomAttribute<RuntimeTestCommandAttribute>();
                if (attribute == null || !TryValidate(method, attribute, out var command)) continue;
                if (!commands.ContainsKey(command.Id))
                    commands.Add(command.Id, new RegisteredCommand(command, provider));
            }
        }

        public void Clear()
        {
            commands.Clear();
        }

        public string DescribeCommands()
        {
            using var sb = ZString.CreateStringBuilder();
            sb.Append("[");

            bool first = true;
            foreach (var pair in commands)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"id\":");
                sb.Append(JsonConvert.ToString(pair.Value.Command.Id));
                sb.Append(",\"description\":");
                sb.Append(JsonConvert.ToString(pair.Value.Command.Description));
                sb.Append("}");
            }

            sb.Append("]");
            return sb.ToString();
        }

        public bool TryInvoke(string commandId, string argsJson, out string result)
        {
            result = null;
            if (string.IsNullOrEmpty(commandId) || !commands.TryGetValue(commandId, out var registered))
                return false;

            if (!TryBindArguments(registered.Command.Method, argsJson, out var arguments, out result))
                return false;

            try
            {
                result = registered.Command.Method.Invoke(registered.Target, arguments) as string ?? "";
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                result = ZString.Format("命令执行失败：{0}", inner.Message);
                return false;
            }
        }

        private static bool TryValidate(MethodInfo method, RuntimeTestCommandAttribute attribute,
            out RuntimeTestCommand command)
        {
            command = default;
            if (string.IsNullOrEmpty(attribute.Id) || method.IsGenericMethod || method.ReturnType != typeof(string))
                return false;

            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter.IsOut || parameter.ParameterType.IsByRef || !IsSimpleType(parameter.ParameterType))
                    return false;
            }

            command = new RuntimeTestCommand(attribute.Id, attribute.Description ?? "", method);
            return true;
        }

        private static bool TryBindArguments(MethodInfo method, string argsJson, out object[] arguments,
            out string error)
        {
            arguments = null;
            error = null;

            JObject json;
            try
            {
                json = string.IsNullOrWhiteSpace(argsJson) || argsJson == "{}"
                    ? new JObject()
                    : JObject.Parse(argsJson);
            }
            catch (Exception ex)
            {
                error = ZString.Format("参数 JSON 无效：{0}", ex.Message);
                return false;
            }

            var parameters = method.GetParameters();
            arguments = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var token = json[parameter.Name];
                if (token == null)
                {
                    if (!parameter.HasDefaultValue)
                    {
                        error = ZString.Format("缺少参数：{0}", parameter.Name);
                        return false;
                    }

                    arguments[i] = parameter.DefaultValue;
                    continue;
                }

                if (!TryConvertToken(token, parameter.ParameterType, out arguments[i]))
                {
                    error = ZString.Format("参数 {0} 类型不匹配", parameter.Name);
                    return false;
                }
            }

            foreach (var property in json.Properties())
            {
                bool known = false;
                for (int i = 0; i < parameters.Length; i++)
                    if (parameters[i].Name == property.Name) known = true;

                if (!known)
                {
                    error = ZString.Format("未知参数：{0}", property.Name);
                    return false;
                }
            }

            return true;
        }

        private static bool TryConvertToken(JToken token, Type type, out object value)
        {
            try
            {
                if (type == typeof(int)) value = token.Value<int>();
                else if (type == typeof(float)) value = token.Value<float>();
                else if (type == typeof(bool)) value = token.Value<bool>();
                else value = token.Value<string>();
                return value != null || type == typeof(string);
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static bool IsSimpleType(Type type)
        {
            return type == typeof(int) || type == typeof(float) || type == typeof(bool) || type == typeof(string);
        }

        private readonly struct RuntimeTestCommand
        {
            public readonly string Id;
            public readonly string Description;
            public readonly MethodInfo Method;

            public RuntimeTestCommand(string id, string description, MethodInfo method)
            {
                Id = id;
                Description = description;
                Method = method;
            }
        }

        private readonly struct RegisteredCommand
        {
            public readonly RuntimeTestCommand Command;
            public readonly MonoBehaviour Target;

            public RegisteredCommand(RuntimeTestCommand command, MonoBehaviour target)
            {
                Command = command;
                Target = target;
            }
        }
    }
}
