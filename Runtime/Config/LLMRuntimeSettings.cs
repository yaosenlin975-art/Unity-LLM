/*
 * ┌────────────────────────────────────────────┐
 * │ Description : LLM 运行期装配根              │
 * │ Remark      : 把 LLMGlobalConfig_SO 的选择塞进 │
 * │               Dispatcher 与 AgentStateStores │
 * │ ClassName   : LLMRuntimeSettings            │
 * └────────────────────────────────────────────┘
 */

using Cysharp.Text;
using LLM.Runtime.Agent.Npc;
using LLM.Runtime.Storage;
using UnityEngine;

namespace LLM.Runtime
{
    /// <summary>
    /// 唯一的装配入口，取代原先散在宿主里的 Resources.Load("LLMProviderConfig")。
    /// 幂等靠既有的「Provider 已注册就跳过」判定，不另立状态位。
    /// </summary>
    public static class LLMRuntimeSettings
    {
        public const string CONFIG_PATH = "LLM/LLMGlobalConfig";

        public static void Install()
        {
            var config = Resources.Load<LLMGlobalConfig_SO>(CONFIG_PATH);
            if (config is null)
            {
                Log.Error(nameof(LLMRuntimeSettings), ZString.Format(
                    "Resources 下没有 {0}.asset：请 Assets → Create → LLM → Global Config 后放进 Resources/" +
                    " 并拖入 ProviderConfig，否则既没有 Provider 也没有状态存储", CONFIG_PATH));
                return;
            }

            RegisterProviders(config);
            InstallStateStore(config);
        }

        private static void RegisterProviders(LLMGlobalConfig_SO config)
        {
            if (LLMDispatcher.GetInstance().GetProvider() is not null) return;

            if (config.ProviderConfig is null)
            {
                Log.Error(nameof(LLMRuntimeSettings),
                    ZString.Format("{0} 没有引用 LLMProviderConfig，真链路会失败", CONFIG_PATH));
                return;
            }

            config.ProviderConfig.RegisterToDispatcher();
        }

        private static void InstallStateStore(LLMGlobalConfig_SO config)
        {
            // Install() 被每个 AgentHost.Activate 调一次：已经装好（或宿主/测试自装过）就别再覆盖
            if (!AgentStateStores.IsDefault) return;

            if (config.AgentStateStore != EAgentStateStoreKind.None)
            {
                var store = new PrefsAgentStateStore();
                AgentStateStores.Facts = store;
                AgentStateStores.History = store;
            }

            if (config.NpcAffinity != EAgentStateStoreKind.None)
                NpcAffinityStore.Current = new PrefsNpcAffinityStore();
        }
    }
}
