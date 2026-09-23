/*
┌────────────────────────────┐
│　Description: LLM 全局配置：选实现与装配
│　Remark: 只做选择与引用，不搬既有 SO 字段
│　　　　　 （ADR-014）
│　ClassName: LLMGlobalConfig_SO
└────────────────────────────┘
*/

using UnityEngine;

namespace LLM.Runtime
{
    public enum EAgentStateStoreKind
    {
        /// <summary>不持久化，等同状态接口化之前的行为</summary>
        None,

        /// <summary>独立包 com.lin.runtime-prefs-helper 的 PrefsHelper 归档</summary>
        Prefs
    }

    /// <summary>
    /// 放在 Resources/LLM/LLMGlobalConfig（= LLMRuntimeSettings.CONFIG_PATH），
    /// 由 LLMRuntimeSettings.Install() 读取。
    /// 归档位置与命名不可配：PrefsHelper 自己定死，配置项存在只会配了不生效。
    /// </summary>
    [CreateAssetMenu(fileName = "LLM Global Config", menuName = "LLM/Global Config")]
    public class LLMGlobalConfig_SO : ScriptableObject
    {
        [Header("Provider 配置（可留空）")]
        [Tooltip("只持有连接参数（Key / 模型 / 地址）；生成参数在人设资产上。这里接受任何 LLMProviderConfigBase_SO 派生资产")]
        public LLMProviderConfigBase_SO ProviderConfig;

        [Header("状态存储")]
        [Tooltip("事实槽与会话轮次落到哪里")]
        public EAgentStateStoreKind AgentStateStore = EAgentStateStoreKind.Prefs;

        [Tooltip("NPC 好感度落到哪里。与记忆/轮次分开勾，关一个不会连带关另一个")]
        public EAgentStateStoreKind NpcAffinity = EAgentStateStoreKind.Prefs;
    }
}
