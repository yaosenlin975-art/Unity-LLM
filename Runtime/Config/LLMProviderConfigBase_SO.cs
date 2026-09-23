/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Provider 配置的抽象基类        │
 * │ Remark      : 全局配置只认这个类型，新增      │
 * │               Provider 不必改装配根（ADR-014）│
 * │ ClassName   : LLMProviderConfigBase_SO      │
 * └────────────────────────────────────────────┘
 */

using Cysharp.Text;
using UnityEngine;

namespace LLM.Runtime
{
    /// <summary>
    /// 一类 Provider 的资产配置。实现方只需要回答"按这份配置怎么造 Provider"，
    /// 主/备注册与命名规则由基类统一，免得每种 Provider 各写一遍降级口径。
    /// </summary>
    public abstract class LLMProviderConfigBase_SO : ScriptableObject
    {
        [Tooltip("保底 Provider 配置：主 Provider 请求失败时自动降级到此配置")]
        public LLMProviderConfigBase_SO FallbackProviderConfig;

        /// <summary>按配置造 Provider。name 是 LLMDispatcher 的注册名。</summary>
        public abstract ILLMProvider CreateProvider(string name);

        /// <summary>注册名，由实现给出（如 "openai"）。保底自动加 _fallback 后缀。</summary>
        protected abstract string DefaultProviderName { get; }

        public void RegisterToDispatcher()
        {
            var dispatcher = LLMDispatcher.GetInstance();

            dispatcher.RegisterProvider(CreateProvider(DefaultProviderName));
            dispatcher.SetDefaultProvider(DefaultProviderName);

            if (FallbackProviderConfig is null) return;

            string fallbackName = ZString.Concat(DefaultProviderName, "_fallback");
            dispatcher.RegisterProvider(FallbackProviderConfig.CreateProvider(fallbackName));
            dispatcher.SetFallbackProvider(fallbackName);
        }
    }
}
