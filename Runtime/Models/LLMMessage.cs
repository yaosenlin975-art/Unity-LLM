/*
 * LLM — LLM 消息结构
 */

using System;

namespace LLM.Runtime
{
    [Serializable]
    public class LLMMessage
    {
        public string Role;
        public string Content;
        public string ToolCallId;
        public LLMToolCall[] ToolCalls;
        /// <summary> 消息时间戳（Unix 秒），用于历史消息加载时还原真实时间 </summary>
        public long Timestamp;

        public LLMMessage() { }

        public LLMMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }

        public LLMMessage(string role, string content, string toolCallId)
        {
            Role = role;
            Content = content;
            ToolCallId = toolCallId;
        }

        public LLMMessage(string role, string content, LLMToolCall[] toolCalls)
        {
            Role = role;
            Content = content;
            ToolCalls = toolCalls;
        }
    }

    [Serializable]
    public class LLMToolCall
    {
        public string Id;
        public string Name;
        public string Arguments;
    }

    [Serializable]
    public class LLMTool
    {
        public string Type = "function";
        public LLMFunction Function;

        public LLMTool(string name, string description, string parameters)
        {
            Function = new LLMFunction
            {
                Name = name,
                Description = description,
                Parameters = parameters
            };
        }
    }

    [Serializable]
    public class LLMFunction
    {
        public string Name;
        public string Description;
        public string Parameters;
    }
}
