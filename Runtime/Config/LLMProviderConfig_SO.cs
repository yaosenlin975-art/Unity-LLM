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
        /// <summary>编辑器本地覆盖的 EditorPrefs 键。本地覆盖不进版本库，避免密钥写进 .asset</summary>
        public const string k_editorPrefsApiKey = "Unity-LLM.ApiKey";

        [Header("连接配置")]
        [Tooltip("【不安全】直接写入会明文保存进 .asset 并可能进版本库。推荐顺序：编辑器本地覆盖（SetLocalApiKey）→ 环境变量 LLM_API_KEY → 这里。留空则回退后两者")]
        [SerializeField] private string apiKey = "";

        [Tooltip("模型名称")]
        public string Model = "gpt-4o";

        [Tooltip("API 地址（兼容 OpenAI 格式的第三方服务可直接填写）")]
        public string BaseUrl = "https://api.openai.com/v1";

        // 这里刻意不放 Temperature / MaxTokens / ToolChoice：它们曾经摆了四个没人读的旋钮，
        // 真值一律来自 AgentProfile_SO（AgentCore 建 session 时写进请求）。生成参数只有一处能配。

        protected override string DefaultProviderName => "openai";

        /// <summary>
        /// 获取 API Key。优先级：编辑器本地覆盖（不进版本库）→ 环境变量 LLM_API_KEY → 资产字段。
        /// 资产字段是兼容旧资产的兜底，不是推荐路径。全空时返回 ""，不影响无 key 的本地/测试回退。
        /// </summary>
        public string ApiKey
        {
            get
            {
#if UNITY_EDITOR
                string local = UnityEditor.EditorPrefs.GetString(k_editorPrefsApiKey, "");
                if (!string.IsNullOrEmpty(local)) return local;
#endif
                string env = System.Environment.GetEnvironmentVariable("LLM_API_KEY");
                if (!string.IsNullOrEmpty(env)) return env;

                return apiKey;
            }
            set => apiKey = value;
        }

#if UNITY_EDITOR
        /// <summary>写编辑器本地覆盖（不进版本库）。传 null/空串即清除</summary>
        public static void SetLocalApiKey(string value)
        {
            if (string.IsNullOrEmpty(value))
                UnityEditor.EditorPrefs.DeleteKey(k_editorPrefsApiKey);
            else
                UnityEditor.EditorPrefs.SetString(k_editorPrefsApiKey, value);
        }
#endif

        public override ILLMProvider CreateProvider(string name)
        {
            return new OpenAIProvider(ApiKey, Model, BaseUrl, name);
        }
    }
}
