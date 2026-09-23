/*
┌────────────────────────────┐
│　Description: LLM 请求结构
│　Remark: 系统提示词与上下文注入槽位
│　ClassName: LLMRequest
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Cysharp.Text;

namespace LLM.Runtime
{
    public class LLMRequest
    {
        /// <summary>会话标识，仅用于日志与用量统计归组</summary>
        public string SessionId;

        /// <summary>系统提示词，拼装时固定为第一条 system 消息</summary>
        public string SystemPrompt;

        public List<LLMMessage> Messages = new();
        public List<LLMTool> Tools = new();
        public ELLMToolChoice ToolChoice = ELLMToolChoice.Auto;
        public float Temperature = 0.8f;
        public int MaxTokens = 1024;
        public ELLMRequestPriority Priority = ELLMRequestPriority.Normal;

        private readonly List<string> contextBlocks = new();

        /// <summary>按注入顺序排列的上下文块（世界信息、角色设定、检索结果等）</summary>
        public IReadOnlyList<string> ContextBlocks => contextBlocks;

        /// <summary>追加一块上下文，空内容忽略</summary>
        public void AddContextBlock(string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            contextBlocks.Add(content);
        }

        public void ClearContextBlocks()
        {
            contextBlocks.Clear();
        }

        /// <summary>
        /// 拼装 system 消息内容：系统提示词在前，上下文块按注入顺序追加。
        /// 顺序稳定可最大化 provider 侧的前缀缓存命中。
        /// </summary>
        public string BuildSystemContent()
        {
            if (string.IsNullOrEmpty(SystemPrompt) && contextBlocks.Count == 0)
                return null;

            using var sb = ZString.CreateStringBuilder();
            if (!string.IsNullOrEmpty(SystemPrompt))
                sb.Append(SystemPrompt);

            for (int i = 0; i < contextBlocks.Count; i++)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(contextBlocks[i]);
            }

            return sb.ToString();
        }
    }

    public enum ELLMToolChoice
    {
        [Description("auto")]
        Auto,
        [Description("none")]
        None,
        [Description("required")]
        Required
    }

    public enum ELLMRequestPriority
    {
        [Description("低")]
        Low,
        [Description("普通")]
        Normal,
        [Description("高")]
        High
    }
}
