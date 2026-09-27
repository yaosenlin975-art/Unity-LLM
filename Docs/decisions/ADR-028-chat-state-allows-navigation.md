# ADR-028: 聊天态放开导航（取代 ADR-026 决策 1 的"导航被拒"）

- **Status**: Accepted
- **Date**: 2026-09-22
- **相关**: [ADR-026](./ADR-026-role-chat-state.md)（本篇取代其导航部分）、[ADR-021](./ADR-021-animation-driver-boundary.md)、`Assets/Learn/ARPG/Docs/agent-chat-state-design.md`

## Context

ADR-026 决策 1 让 `ChatState` 落回 `RoleStateBase` 的默认实现，导航一律回"当前状态不接受导航"，理由是"NPC 说话时被模型 `move_to` 拽走像走神"。

实机发现这条既不解决原问题、又制造新问题：

1. `move_to` / `move_to_player` 都是模型**自己**通过指令调的；拦掉它不等于"防止走神"，等于"模型决定要走却走不动"。
2. 现实回合流程会卡死：模型调 `move_to_player` → 进 `Nav` 开始走；模型正文的首个 token 到达 → `RoleChatBridge.OnToken` → `EnterChat` → `Nav→Chat`，`NavState.OnExit` 把 `NavMeshAgent` 关掉、寻路被打断；角色停在半路，之后再调 `move_to_player` 又被 Chat 拒。表现是"说得出'来了来了'，但永远走不到玩家身边"。

## Decision

1. **`ChatState` 接受导航**：override `MoveTo` / `MoveToPlayer`，成功就 `Trigger(Nav)`（与 `IdleState` 一致）；`StopNavigation` 回"当前未在导航"（Chat 本身不在导航，导航会迁 `Nav`）。
2. **聊天态只锁玩家输入与受击/复活**，不再锁导航。
3. **赶路时不切聊天态**：`RoleController.EnterChat()` 改返回 `bool`，`IsNavPathActive || ActiveStateName == Nav` 时返回 `false`、不 `Trigger(Chat)`，避免经 `NavState.OnExit` 打断本轮自己的寻路。`RoleChatBridge.OnToken` 只有在 `EnterChat()` 返回 `true` 时才落 `inChat` 标记，否则后续 token 继续重试；走完回 `Idle` 后再进 Chat。

## Alternatives Considered

| 方案 | 结论 |
| --- | --- |
| 保持 Chat 拒绝导航，只加"寻路中 `EnterChat` 不打断" | 拒绝。只解决被打断的一半，不解决"已进 Chat 后模型再发 `move_to` 被拒"。 |
| 让 `move_to_player` 阻塞到到站再继续本轮（`WaitForNavigation` 真正生效） | 拒绝（本次）。回合被拉长、台词延后；且 `WaitForNavigation` 的同帧 `IsNavPathActive` 门槛是独立遗留问题。 |
| 只允许 `move_to_player`、拒绝 `move_to` | 拒绝。用户裁决：`move_to` 同样是 NPC 自己通过指令调的，不该按"谁发起"区别对待。 |
| Chat 期间用 `IAgentToolGate` 把导航工具摘掉 | 拒绝，同 ADR-026：门控会从声明里抹掉能力，模型以为角色根本不能移动。 |

## Consequences

- 正面：模型自己的走位意图能落地（先走过去再说话、说完再走都行）；`Chat` 只剩"锁玩家输入 + 免伤"，语义更清晰。
- 代价与已知边界：
  - 模型若在聊天态反复发 `move_to`，NPC 会真的来回走——这是"放开"的代价，按 B 方案接受。
  - `RoleChatBridge` 现在依赖 `EnterChat()` 的返回值；宿主若自己接线，也要遵守"返回 false 就别记 inChat、后面重试"的约定，否则整轮都进不了 Chat。
  - 实机验收：本篇行为已在编辑器 Play 实测（Chat→`MoveToPlayer`→`Nav`；`Nav` 中 `EnterChat()` 返回 false 且不打断；到站后 `EnterChat()` 返回 true 进 Chat），但经真实 LLM 回合的观感仍需人工验收，见 `Docs/PENDING_TESTS.md`。
