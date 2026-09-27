# ADR-029: Agent 状态存储改用强类型载荷

## Status

Accepted

## Date

2026-09-22

## Context

ADR-014 把 `IFactStore` 和 `IConversationStore` 的载荷定为 JSON 字符串。默认实现因此先把 `List<AgentFact>` / `List<ConversationRound>` 序列化成字符串，再由 `PrefsHelper` 序列化包含该字符串的 Blob。当前不存在只接收 JSON 原文的 Store 实现，两个内存结构也已是稳定的可序列化数据类。

## Decision

1. `AgentFactsBlob` 直接持有 `List<AgentFact>`，`AgentRoundsBlob` 直接持有 `List<ConversationRound>`。
2. `IFactStore` 和 `IConversationStore` 直接传递对应 Blob，不再传递 JSON 字符串。
3. 内存对象与 Blob 之间使用独立快照，不与 `PrefsHelper` 的类型缓存共享可变实例。
4. 保留 `ExportJson/ImportJson` 作为显式导入导出 API，但 Prefs 持久化不再调用它们。
5. 旧 `Json` 字段直接删除，现有记忆不做兼容迁移；这是用户明确接受的数据格式切换。

ADR-014 中“载荷一律是 JSON 字符串”的决定被本 ADR 取代；其余关于两个存储接口、两份归档和内核持有 Store 的决定继续有效。

## Alternatives Considered

### 保留 JSON blob

拒绝：它只有利于尚不存在的字符串后端，却让当前 Prefs 实现每次读写都做两层 JSON 转换。

### 直接以 `List<T>` 作为归档类型

拒绝：`PrefsHelper` 按 `T` 生成归档路径，保留现有两个 Blob 名称的改动更小，且归档用途更直接。

### 新增独立持久化 DTO

拒绝：`AgentFact` 与 `ConversationRound` 本身只包含可序列化字段，再造一组同形 DTO 只会增加映射代码。

## Consequences

- 新存档只由 `PrefsHelper` 序列化一次，Inspector 也直接获得可用对象。
- Store 接口与 Agent 状态类型发生显式耦合；这是具体状态存储接口应有的契约，不再为假设后端隐藏。
- 旧存档中的 `Json` 字段被忽略，事实和历史按空状态开始；不保留迁移代码。
- 未来真实服务器 Store 若需 JSON，由该 Store 在自己的传输边界序列化。
