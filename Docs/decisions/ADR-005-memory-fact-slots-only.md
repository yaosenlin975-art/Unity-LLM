# ADR-005: 记忆边界 = 会话历史 + 结构化事实槽，不引入向量检索

## Status
Accepted

## Date
2026-09-19

## Context
上一代模块有 `MemoryBridge` / `EntityMemoryStore` / `ImportanceScorer` / `MemoryTools` 四层记忆设施，因过度设计随 `LLM.Npc` 一起删除。RAG 在同一次重构中被明确舍弃（无 embedding 基建）。现在要定内核的记忆上限。

## Decision
两层：
1. **会话历史**：完全交给现有 `ContextManager`（轮数/token 双模式阈值 + 分区折叠 + LLM 摘要压缩 + 归档），不新增代码。
2. **事实槽**：`AgentMemory` 维护一组 `key/value/timestamp` 的结构化条目，由模型通过 `write_fact` / `forget_fact` / `list_facts` 显式读写（三者是 `[AgentAction]` 且 `Idempotent = true`，经 `AgentActionContext.Agent` 定位实例，不能用 `[Tool]`——静态方法拿不到 per-agent 实例），随存档 JSON 序列化，**每轮全量注入**（因此容量上限就是它的检索机制）。

事实槽整块走 `AskAsync(ephemeralContext)`，顺序为「事实槽 → 核心快照 → QueryableHint」；**不放进 `LLMSession.AddContext()`**，因为它只有追加和整体清空，`write_fact` 之后无法原位更新。三段最终仍拼在同一条 system 消息尾部，靠「前缀稳定」而非「消息隔离」省缓存（见设计稿 §注入排版）。

## Alternatives Considered

### 只要会话历史
- Pros：零新代码
- Cons：一重启就失忆，NPC 无法积累"玩家叫啥、上次谈成什么价"
- Rejected：对产品目标不够用，但它是事实槽之外的唯一可选项

### 记忆流 + recency×importance×relevance 检索打分（Generative Agents 式）
- Pros：容量无上限，语义召回强
- Cons：`relevance` 需要 embedding 与向量存储，正是刚被砍掉的 RAG 基建；`importance` 打分需要多一次 LLM 调用
- Rejected：基建没有就位，先不做。**将来的落点是 observe 类工具的返回值**——检索结果每次不同，属于每轮变的内容，不得进 `GetCoreSnapshot()`（那里只允许低频值，见 ADR-006 排版规则）

## Consequences
- 事实槽容量是功能边界而不是性能参数：`FactSlotMaxCount` 默认 60、区间 `[10,120]`，超出按时间戳淘汰；不靠 `forget_fact` 自觉
- 写入者是模型自己 → 会写错，因此 `list_facts` 存在让模型能自我校正，兜底靠宿主在存档里直接改 JSON
- 不做反思（reflection）、不做遗忘打分、不做去重合并——这些都要等检索基建
- 上一代记忆层被明确拒绝过，不要复活 `MemoryBridge` 这类命名与职责

See also: [ADR-001](./ADR-001-kernel-in-aot-llm-runtime.md)、[agent-kernel-design.md](../agent-kernel-design.md)
