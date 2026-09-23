/*
 * ┌────────────────────────────────────────────┐
 * │ Description : 工具/动作入参格式闸           │
 * │ Remark      : 坏输入回灌指名到参数的原因，   │
 * │               不再静默按默认值执行          │
 * │ ClassName   : AgentArgumentGate             │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Reflection;
using Cysharp.Text;
using Newtonsoft.Json.Linq;

namespace LLM.Runtime.Agent
{
    /// <summary>
    /// tool_call 入参的格式闸：一次解析，产出 JObject 或一条指名到参数的原因。
    /// 必填依据与 schema 同源（ParameterInfo.HasDefaultValue），不另立一份声明。
    /// 只做校验不做绑定：动作路径还要 ctx 注入与 say 剥离，绑定留在各自的注册表里。
    /// </summary>
    public static class AgentArgumentGate
    {
        public const string k_errorPrefix = "[格式错误]";

        /// <summary>
        /// extraRequired：内核注入的伪参数（非幂等动作必填的 say），它必填但不在方法签名里。
        /// 返回 false 时 error 就是要回灌给模型的内容，调用方不得执行动作。
        /// </summary>
        public static bool TryValidate(string argsJson, ParameterInfo[] parameters,
            string[] extraRequired, out JObject parsed, out string error)
        {
            parsed = null;
            error = null;

            if (string.IsNullOrWhiteSpace(argsJson) || argsJson == "{}")
            {
                // 空参数本身合法，只要没有必填项
                return CheckRequired(parameters, extraRequired, null, out error);
            }

            try
            {
                parsed = JObject.Parse(argsJson);
            }
            catch (Exception ex)
            {
                error = ZString.Format("{0} 参数不是合法 JSON（{1}）。请按 schema 重新给出参数。",
                    k_errorPrefix, FirstLine(ex.Message));
                return false;
            }

            return CheckRequired(parameters, extraRequired, parsed, out error)
                   && CheckTypes(parameters, parsed, out error);
        }

        /// <summary>
        /// 取参数值。网关/模型常把字段显式写成 JSON null，此时 token[ key ] 返回 JValue(Null)
        /// 而不是 C# null，不判 Type 就会把"没给"当成"给了"放行，绑定阶段再静默降级成 0/""。
        /// </summary>
        public static JToken Value(JObject args, string key)
        {
            var token = args?[key];
            return token is null or { Type: JTokenType.Null } ? null : token;
        }

        /// <summary>
        /// 唯一的 token→CLR 参数转换口。闸的校验与两处绑定（动作 BindArguments、工具 ParseArguments）
        /// 必须共用它，否则会出现"闸放行但绑定抛异常"或"闸拦掉但本来能绑"。
        /// </summary>
        public static bool TryCoerce(JToken token, Type targetType, out object value)
        {
            try
            {
                if (targetType == typeof(int) || targetType == typeof(int?)) value = token.Value<int>();
                else if (targetType == typeof(float) || targetType == typeof(float?)) value = token.Value<float>();
                else if (targetType == typeof(bool) || targetType == typeof(bool?)) value = token.Value<bool>();
                else value = token.ToString();
                return true;
            }
            catch (Exception)
            {
                value = null;
                return false;
            }
        }

        private static bool CheckRequired(ParameterInfo[] parameters, string[] extraRequired,
            JObject args, out string error)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                if (param.HasDefaultValue) continue;
                if (Value(args, param.Name) != null) continue;

                error = ZString.Format("{0} 缺少必填参数 {1}。", k_errorPrefix, param.Name);
                return false;
            }

            if (extraRequired != null)
            {
                for (int i = 0; i < extraRequired.Length; i++)
                {
                    if (Value(args, extraRequired[i]) != null) continue;

                    error = ZString.Format("{0} 缺少必填参数 {1}。", k_errorPrefix, extraRequired[i]);
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static bool CheckTypes(ParameterInfo[] parameters, JObject args, out string error)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var token = Value(args, param.Name);
                if (token == null) continue;

                if (TryCoerce(token, param.ParameterType, out _)) continue;

                error = ZString.Format("{0} 参数 {1} 类型不对，收到 {2}。",
                    k_errorPrefix, param.Name, Snip(token.ToString()));
                return false;
            }

            error = null;
            return true;
        }

        private static string FirstLine(string message)
        {
            int cut = message.IndexOf('\n');
            return cut < 0 ? message : message.Substring(0, cut);
        }

        private static string Snip(string raw)
            => raw.Length <= 40 ? raw : ZString.Concat(raw.Substring(0, 40), "…");
    }
}
