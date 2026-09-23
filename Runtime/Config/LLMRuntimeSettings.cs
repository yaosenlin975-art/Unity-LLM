/*
┌────────────────────────────┐
│　Description: LLM 运行期装配根
│　Remark: 把 LLMGlobalConfig_SO 的选择塞进
│　　　　　 Dispatcher 与 AgentStateStores；
│　　　　　 编辑器里缺该资产先补一个空的
│　ClassName: LLMRuntimeSettings
└────────────────────────────┘
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
#if UNITY_EDITOR
            config ??= CreateConfigAsset();
#endif
            if (config is null)
            {
                Log.Error(nameof(LLMRuntimeSettings), ZString.Format(
                    "Resources 下没有 {0}.asset：请 Assets → Create → LLM → Global Config 后放进 Resources/ 并拖入 ProviderConfig，否则既没有 Provider 也没有状态存储", CONFIG_PATH));
                return;
            }

            RegisterProviders(config);
            InstallStateStore(config);
        }

#if UNITY_EDITOR
        // 只靠 Install() 触发等于没触发：不进 Play、不开沙盒就永远不补，看着像"自动创建没生效"。
        // InitializeOnLoad 阶段 Application 还没就绪，建资产要延一帧做。
        [UnityEditor.InitializeOnLoadMethod]
        private static void EnsureConfigAssetOnEditorLoad()
            => UnityEditor.EditorApplication.delayCall += () => CreateConfigAsset();

        /// <summary>
        /// 编辑器里缺配置就补一个空资产：路径与命名先钉死、两项存储留 None，下一步只差拖 ProviderConfig。
        /// 构建体里造不出资产，仍走上面的报错。
        /// </summary>
        private static LLMGlobalConfig_SO CreateConfigAsset()
        {
            const string assetPath = "Assets/Resources/LLM/LLMGlobalConfig.asset";

            // Install() 每次 Activate 都会经过这里，而新建的资产要等一次导入才进得了 Resources.Load：
            // 先按资产路径认一次，否则第二次会把用户已经填好的配置整个覆盖掉
            var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<LLMGlobalConfig_SO>(assetPath);
            if (existing is not null) return existing;

            // 同一路径上已经躺着别的资产：不覆盖，退回报错让人自己处理
            if (UnityEditor.AssetDatabase.LoadAssetAtPath<Object>(assetPath) is not null) return null;

            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Resources"))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "Resources");
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Resources/LLM"))
                UnityEditor.AssetDatabase.CreateFolder("Assets/Resources", "LLM");

            var config = ScriptableObject.CreateInstance<LLMGlobalConfig_SO>();
            // 空壳资产不默认往 PlayerPrefs 里写数据：要落盘由开发者自己勾 Prefs（手工建的资产仍按类默认值）
            config.AgentStateStore = EAgentStateStoreKind.None;
            config.NpcAffinity = EAgentStateStoreKind.None;
            UnityEditor.AssetDatabase.CreateAsset(config, assetPath);
            UnityEditor.AssetDatabase.SaveAssets();

            Log.Warning(nameof(LLMRuntimeSettings), ZString.Format(
                "Resources 下缺 {0}.asset，已在 {1} 自动建好：把 ProviderConfig 拖进去再重进 Play；需要记忆与好感度落盘时把 AgentStateStore / NpcAffinity 勾成 Prefs",
                CONFIG_PATH, assetPath));

            return config;
        }
#endif

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
