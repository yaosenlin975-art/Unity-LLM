/*
 * LLM — OpenAI 兼容 Provider 配置
 * 所有使用 LLM 的模块共享的 Provider 配置
 * 会话 / 编辑器工具等通过引用此 SO 获取连接参数
 */

using UnityEngine;

namespace LLM.Runtime
{
    [CreateAssetMenu(fileName = "LLM Provider Config", menuName = "LLM/Provider Config")]
    public class LLMProviderConfig_SO : LLMProviderConfigBase_SO
    {
        [Header("连接配置")]
        [Tooltip("LLM API Key，留空时尝试读取环境变量 LLM_API_KEY")]
        [SerializeField] private string apiKey = "";

        [Tooltip("模型名称")]
        public string Model = "gpt-4o";

        [Tooltip("API 地址（兼容 OpenAI 格式的第三方服务可直接填写）")]
        public string BaseUrl = "https://api.openai.com/v1";

        // 这里刻意不放 Temperature / MaxTokens / ToolChoice：它们曾经摆了四个没人读的旋钮，
        // 真值一律来自 AgentProfile_SO（AgentCore 建 session 时写进请求）。生成参数只有一处能配。

        protected override string DefaultProviderName => "openai";

        /// <summary>
        /// 获取 API Key：优先使用 Inspector 设置值，否则读取环境变量 LLM_API_KEY
        /// </summary>
        public string ApiKey
        {
            get => string.IsNullOrEmpty(apiKey)
                ? System.Environment.GetEnvironmentVariable("LLM_API_KEY") ?? ""
                : apiKey;
            set => apiKey = value;
        }

        public override ILLMProvider CreateProvider(string name)
        {
            return new OpenAIProvider(ApiKey, Model, BaseUrl, name);
        }
    }
}
