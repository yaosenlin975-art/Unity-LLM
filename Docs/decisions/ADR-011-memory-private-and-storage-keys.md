# ADR-011: 记忆一律私有，实例 id 与存储 key 定死格式

## Status
Accepted

## Date
2026-09-19

## Context
会话历史与事实槽都是 per-instance 状态（`ContextManager` 与 `AgentMemory` 各一份；上一代的全局静态 `LLMMessagePool` 已在重构时删除，它正是"跨 NPC 缓存串线"的根因）。但两处仍缺规格：`sessionId` 没有唯一性约束（归档路径是 `LLMArchive/<sessionId ?? "default">`，同 profile 的两个 NPC 会写进同一目录），事实槽/历史的持久化 key 完全没定。同时会出现"商人知道卫兵刚被玩家打过"这类跨 agent 知情需求，需要定边界。

## Decision
1. **记忆全部私有**：`AgentMemory` 只属于一个 agent，写入只能通过该 agent 自己的 `write_fact`。不提供 `scope = world` 之类的共享作用域。
2. **跨 agent 的共同事实走世界快照**：宿主在 `IWorldContextProvider.GetCoreSnapshot()` 里按需拼入（任务完成、物价、声望）。它们是只读世界真相，不是记忆，因此不需要并发写保护。
3. **sessionId 规则定死**：`{profileKey}#{instanceId}`。`instanceId` 由宿主提供（存档里的稳定 id），宿主不给时 `AgentCore` 用 `Guid.NewGuid()` 生成并通过 `AgentCore.SessionId` 暴露。禁止用 profileKey 单独当 id。
4. **内核不做文件 IO**（本条的"落盘时机归宿主"部分已被 [ADR-014](./ADR-014-storage-interfaces-held-by-kernel.md) 取代：时机改由内核在 `write_fact`/`FinishTurn` 触发，但写路径经 `IFactStore`/`IConversationStore` 且默认不启用，仍是单通道。第 3、5 条与本条的双通道顾虑继续有效）：事实槽只提供 `ExportJson()` / `ImportJson()`，历史已有现成的 `ContextManager.ExportRoundsJson()` / `ImportRoundsJson()`；落盘路径、格式、时机全归宿主存档。理由：内核再开一条 `LLMAgent/{sessionId}/history.json` 就会与既有 Export/Import 形成**双通道**，同一份数据两处写、谁赢不确定，且 `history.json` 根本没人认领。`sessionId` 规则单独就足以解决串档问题。
5. `profileKey` 是 `AgentProfile_SO` 上的必填 string，**禁止含 `#`、`/`、`\`**；`instanceId` 走 `AgentCore` 构造参数（不进三窄接口，见 ADR-002），宿主不给则由 `AgentCore` 生成 `Guid` 并暴露。

## Alternatives Considered

### 内核提供共享记忆作用域（private / world）
- Pros：NPC 之间可自行积累共识
- Cons：需要共享文件 + 跨实例写锁；一个 NPC 写错全世界都错，且极难排查
- Rejected：共享事实的正确来源是世界状态，不是某个 agent 的断言

### 不规定 sessionId 与存储 key，宿主自便
- Pros：零约束
- Cons：同 profile 多实例必然串档；每次接入都要重新讨论一遍
- Rejected：这是本次审查明确点出的缺口

### 记忆按 profileKey 存（同种 NPC 共享记忆）
- Pros：省事，天然"这个商人类型都记得"
- Cons：两个商人会互相记得彼此与不同玩家的对话，语义错误
- Rejected：与实例隔离冲突

## Consequences
- 同种 NPC 需要共享的知识属于**配置/人设**（`AgentProfile_SO`）或世界快照，不属于记忆
- `instanceId` 必须由存档提供稳定值，否则每次读档都会生成新的空记忆（宿主责任，构造参数即入口）
- 内核唯一保留的写盘是 `ContextManager` 既有的压缩归档 `persistentDataPath/LLMArchive/{sessionId}/`（开关 `EnableArchive` 已存在），它只是调试转储；运行期状态的落盘完全归宿主
- 明确排除：跨 agent 读写、记忆复制/同步、世界事实的写入接口

See also: [ADR-005](./ADR-005-memory-fact-slots-only.md)、[ADR-002](./ADR-002-three-narrow-host-interfaces.md)、[设计稿](../agent-kernel-design.md)
