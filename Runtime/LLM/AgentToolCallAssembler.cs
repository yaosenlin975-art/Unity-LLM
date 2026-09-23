/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Tool Call 流式拼装器          │
 * │ Remark      : 工具调用的 JSON 参数按 chunk   │
 * │               分片到达，按 index 分槽累加    │
 * │ ClassName   : ToolCallAssembler             │
 * └────────────────────────────────────────────┘
 */

using System.Collections.Generic;
using Cysharp.Text;

namespace LLM.Runtime
{
    public class AgentToolCallAssembler
    {
        private readonly Dictionary<int, AgentToolCallBuilder> builders = new();

        public void Append(ToolCallDelta delta)
        {
            Append(delta.Index, delta.Id, delta.Name, delta.ArgumentsDelta);
        }

        public void Append(int index, string id, string name, string argumentsDelta)
        {
            if (!builders.TryGetValue(index, out var builder))
            {
                builder = new AgentToolCallBuilder();
                builders[index] = builder;
            }

            if (!string.IsNullOrEmpty(id)) builder.Id = id;
            if (!string.IsNullOrEmpty(name)) builder.Name = name;
            if (!string.IsNullOrEmpty(argumentsDelta))
                builder.ArgumentsBuilder.Append(argumentsDelta);
        }

        /// <summary>取出所有已拼接完整的调用（有 id、有函数名、参数 JSON 闭合）</summary>
        public List<LLMToolCall> GetCompletedCalls()
        {
            var result = new List<LLMToolCall>(builders.Count);

            foreach (var builder in builders.Values)
            {
                if (string.IsNullOrEmpty(builder.Id) || string.IsNullOrEmpty(builder.Name)) continue;

                string argsJson = builder.ArgumentsBuilder.ToString();
                if (string.IsNullOrEmpty(argsJson)) continue;

                if (!IsJsonComplete(argsJson))
                {
                    Log.Warning(nameof(AgentToolCallAssembler),
                        ZString.Format("工具 {0} 参数 JSON 不完整: {1}",
                            builder.Name, Truncate(argsJson, 100)));
                    continue;
                }

                result.Add(new LLMToolCall
                {
                    Id = builder.Id,
                    Name = builder.Name,
                    Arguments = argsJson
                });
            }

            return result;
        }

        public void Reset()
        {
            builders.Clear();
        }

        /// <summary>括号/字符串状态扫描，判断分片累加的参数 JSON 是否已经闭合</summary>
        public static bool IsJsonComplete(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;

            json = json.Trim();
            if (json.Length < 2) return false;

            bool startsWithBrace = json[0] == '{';
            if (!startsWithBrace && json[0] != '[') return false;
            if (json[json.Length - 1] != (startsWithBrace ? '}' : ']')) return false;

            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\' && inString)
                {
                    escape = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (inString) continue;

                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;

                if (depth < 0) return false;
            }

            return depth == 0 && !inString;
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length <= maxLength
                ? text
                : ZString.Concat(text.Substring(0, maxLength), "...");
        }

        private class AgentToolCallBuilder
        {
            public string Id;
            public string Name;
            public readonly System.Text.StringBuilder ArgumentsBuilder = new();
        }
    }
}
