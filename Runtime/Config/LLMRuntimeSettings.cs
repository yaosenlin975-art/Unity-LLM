/*
┌────────────────────────────┐
│　Description: LLM 运行期装配根
│　Remark: 把 LLMGlobalConfig_SO 的选择塞进
│　　　　　 Dispatcher 与 AgentStateStores；
│　　　　　 编辑器里缺该资产先补一个空的
│　ClassName: LLMRuntimeSettings
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
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

        // 跨 Activate 的失败去重记录：Install 每个宿主激活都跑，同一条错只报一次
        private static readonly HashSet<(string, string)> reported = new();

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
        /// 编辑器里缺配置就补一个空资产：路径与命名先钉死，下一步只差拖 ProviderConfig。
        /// 三个存储字段留空 = 不落盘，与手工新建的资产同一出厂值。
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
            if (UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) is not null) return null;

            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Resources"))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "Resources");
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Resources/LLM"))
                UnityEditor.AssetDatabase.CreateFolder("Assets/Resources", "LLM");

            var config = ScriptableObject.CreateInstance<LLMGlobalConfig_SO>();
            UnityEditor.AssetDatabase.CreateAsset(config, assetPath);
            UnityEditor.AssetDatabase.SaveAssets();

            Log.Warning(nameof(LLMRuntimeSettings), ZString.Format(
                "Resources 下缺 {0}.asset，已在 {1} 自动建好：把 ProviderConfig 拖进去再重进 Play；需要记忆与好感度落盘时，在同一份资产的三个存储字段里选实现（空 = 不落盘）",
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
            var created = new Dictionary<Type, object>();   // 本次装配内同类型共用实例（事实与轮次共档）
            var pending = new List<string>();

            InstallSlot<IFactStore>("Facts", config.AgentFactsStoreType,
                () => AgentStateStores.IsFactsDefault, s => AgentStateStores.Facts = s, created, pending);
            InstallSlot<IConversationStore>("History", config.AgentHistoryStoreType,
                () => AgentStateStores.IsHistoryDefault, s => AgentStateStores.History = s, created, pending);
            InstallSlot<INpcAffinityStore>("Affinity", config.NpcAffinityStoreType,
                () => NpcAffinityStore.IsDefault, s => NpcAffinityStore.Current = s, created, pending);

            // 三槽攒成一条：Install 每次 AgentHost.Activate 都跑，一槽一条会被淹
            if (pending.Count > 0)
                Log.Error(nameof(LLMRuntimeSettings), string.Join("; ", pending));
        }

        private static void InstallSlot<T>(string slot, string name, Func<bool> isDefault, Action<T> assign,
            Dictionary<Type, object> created, List<string> pending) where T : class
        {
            if (!isDefault()) return;                                    // 宿主/测试已自装
            if (string.IsNullOrWhiteSpace(name)) return;                 // 未配置：静默，不是错误

            var type = StoreTypeResolver.Resolve(name, typeof(T), out string error);
            if (type is null)
            {
                Report(pending, slot, name, error);
                return;
            }

            if (created.TryGetValue(type, out var reused) && reused is T hit)
            {
                assign(hit);
                return;
            }

            // 实例化一律走 Create<T>：ctor 异常的兜底只在那里
            var instance = StoreTypeResolver.Create<T>(name, out error);
            if (instance is null)
            {
                Report(pending, slot, name, error);
                return;
            }

            created[type] = instance;
            assign(instance);
        }

        private static void Report(List<string> pending, string slot, string name, string error)
        {
            // 同一 (槽, 类名) 只报一次：失败的槽仍留哨兵，下次 Activate 会再走到这里
            if (!reported.Add((slot, name))) return;

            pending.Add(ZString.Format("{0}={1} → {2}", slot, name, error ?? "未知原因"));
        }

        /// <summary>测试 seam：清掉失败去重记录，让"两次 Activate 只报一条"这类断言可重复验。</summary>
        internal static void ResetInstallDiagnostics() => reported.Clear();
    }
}
