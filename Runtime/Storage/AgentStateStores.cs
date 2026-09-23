/*
┌────────────────────────────┐
│　Description: 内核取状态存储的唯一入口
│　Remark: 每槽一个私有哨兵，守卫只认实例不认类型；
│　　　　　 setter 保持 public（宿主自装是规格的一部分），
│　　　　　 按槽判定取代原先只看 Facts 的 IsDefault（ADR-030）
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
        private static readonly NullAgentStateStore factsNull = new();
        private static readonly NullAgentStateStore historyNull = new();

        public static IFactStore Facts { get; set; } = factsNull;

        public static IConversationStore History { get; set; } = historyNull;

        /// <summary>装配守卫用：仍是本槽哨兵才允许装配根写入，与 Provider 侧"已注册即跳过"同口径</summary>
        public static bool IsFactsDefault => ReferenceEquals(Facts, factsNull);

        public static bool IsHistoryDefault => ReferenceEquals(History, historyNull);

        /// <summary>测试 seam，与 AgentActionRegistry.Reset() 同一套路</summary>
        public static void Reset()
        {
            Facts = factsNull;
            History = historyNull;
            LLMRuntimeSettings.ResetInstallDiagnostics();
        }
    }
}
