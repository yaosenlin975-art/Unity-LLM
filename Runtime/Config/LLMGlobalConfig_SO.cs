/*
┌────────────────────────────┐
│　Description: LLM 全局配置：选实现与装配
│　Remark: 只做选择与引用，不搬既有 SO 字段
│　　　　　 （ADR-014）
│　ClassName: LLMGlobalConfig_SO
└────────────────────────────┘
*/

using LLM.Runtime.Agent.Npc;
using LLM.Runtime.Storage;
using UnityEngine;

namespace LLM.Runtime
{
    /// <summary>
    /// 放在 Resources/LLM/LLMGlobalConfig（= LLMRuntimeSettings.CONFIG_PATH），
    /// 由 LLMRuntimeSettings.Install() 读取。
    /// 存储字段存实现的 Type.FullName，装配时反射实例化（ADR-030）：
    /// 新增实现只要实现接口并出现在下拉里，不必改本资产的结构。
    /// 归档位置与命名不可配：由所选实现自己定死，配置项存在只会配了不生效。
    /// </summary>
    [CreateAssetMenu(fileName = "LLM Global Config", menuName = "LLM/Global Config")]
    public class LLMGlobalConfig_SO : ScriptableObject
    {
        [Header("Provider 配置（可留空）")]
        [Tooltip("只持有连接参数（Key / 模型 / 地址）；生成参数在人设资产上。这里接受任何 LLMProviderConfigBase_SO 派生资产")]
        public LLMProviderConfigBase_SO ProviderConfig;

        [Header("状态存储（实现类 FullName，空 = 不持久化）")]
        [StoreTypeSelect(typeof(IFactStore))]
        [Tooltip("事实槽落盘实现的 Type.FullName；下拉自动列出可选类，留空 = 不落盘")]
        public string AgentFactsStoreType;

        [StoreTypeSelect(typeof(IConversationStore))]
        [Tooltip("会话轮次落盘实现的 Type.FullName")]
        public string AgentHistoryStoreType;

        [StoreTypeSelect(typeof(INpcAffinityStore))]
        [Tooltip("NPC 好感落盘实现的 Type.FullName；与记忆/轮次分开选")]
        public string NpcAffinityStoreType;
    }
}
