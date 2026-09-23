/*
┌────────────────────────────┐
│　Description: 内核取状态存储的唯一入口
│　Remark: 默认 Null，由 LLMRuntimeSettings
│　　　　　 .Install() 换上真实实现
│　ClassName: AgentStateStores
└────────────────────────────┘
*/

namespace LLM.Runtime.Storage
{
    /// <summary>
    /// 静态持有者而非构造参数：内核与宿主之间只认三个窄接口（ADR-002），
    /// 存储不属于宿主能力，走装配根注入，同 LLMDispatcher / AgentActionRegistry 的既有惯用法。
    /// </summary>
    public static class AgentStateStores
    {
        private static readonly NullAgentStateStore nullStore = new();

        public static IFactStore Facts = nullStore;

        public static IConversationStore History = nullStore;

        /// <summary>装配守卫用：已被换过（宿主或测试自装）就不再覆盖，与 Provider 侧"已注册即跳过"同口径</summary>
        public static bool IsDefault => ReferenceEquals(Facts, nullStore);

        /// <summary>测试 seam，与 AgentActionRegistry.Reset() 同一套路</summary>
        public static void Reset()
        {
            Facts = nullStore;
            History = nullStore;
        }
    }
}
