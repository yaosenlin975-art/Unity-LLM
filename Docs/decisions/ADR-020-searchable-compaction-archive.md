# ADR-020: `search_memory` 复用压缩归档，不恢复 MemoryBridge

## Status
Accepted

## Date
2026-09-20

## Context

ADR-005 将记忆限定为“会话历史 + 结构化事实槽”，并删除了上一代 `MemoryBridge`、`MemoryEntry`、importance 与向量检索。现在需要提供 `search_memory`，同时调查确认：压缩后的旧原文会从运行态和持久化历史中消失，但在 `EnableArchive` 开启时已经以 `List<ConversationRound>` JSON 写入 `LLMArchive/{sessionId}`。

只搜索事实槽没有明显价值，因为事实槽每轮已经全量注入。只搜索活动历史又无法恢复被压缩的原文。

## Decision

1. `search_memory` 搜索当前 agent 的三种既有数据：事实槽、活动 `ConversationRound`、当前 session 的压缩归档。
2. 使用确定性文本匹配和稳定排序；不增加 embedding、LLM 重排、importance、索引或缓存。
3. 工具实现为带 `AgentActionContext` 的只读 `[AgentAction]`，复用现有 per-agent 上下文注入路径。
4. 归档搜索从新到旧扫描，单次读取预算为 2 MiB；达到预算时显式返回截断提示。
5. `ClearHistory()` 同时清理该 session 的压缩归档，并对最终目录做根路径约束校验。
6. 压缩分区、异步替换和压缩后持久化的正确性问题与本功能同批修复，否则归档与活动历史不能构成可靠搜索语料。

## Alternatives Considered

### 只搜索事实槽与活动历史

- 优点：不读文件，改动最少。
- 缺点：事实已全量注入；被压缩的旧原文不可恢复。
- 拒绝理由：无法满足“回忆旧对话”的核心价值。

### 恢复 Mu 的 MemoryBridge

- 优点：有关键词索引、缓存和多维评分。
- 缺点：恢复已删除的第二套记忆实体、索引、淘汰和缓存体系，且与现有事实槽/会话历史形成三份真值。
- 拒绝理由：违反 ADR-005，维护成本大于当前数据规模的收益。

### 新增向量检索

- 优点：语义召回更强。
- 缺点：需要 embedding 模型、向量存储、重建、版本和成本治理。
- 拒绝理由：当前基建与验收需求均不支持；若未来采用，应作为独立检索后端另写 ADR。

## Consequences

- 默认开启归档时，现有活跃历史与压缩归档合起来覆盖完整的 user/assistant 对话文本。
- 关闭归档后，`search_memory` 只能搜索事实、活动历史与摘要，并明确告知无法恢复旧原文。
- 搜索延迟随归档规模增长，但有 2 MiB 上限；先接受线性扫描，测到瓶颈再加缓存。
- tool call 与 tool result 仍不属于对话记忆，不会被搜索。
- 本决策补充 ADR-005，不改变“不引入向量检索和反思式记忆”的边界。
