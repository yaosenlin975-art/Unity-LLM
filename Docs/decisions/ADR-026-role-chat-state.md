# ADR-026: Role 的 Agent 聊天态，与"一个角色只有一个动画驱动"

- **Status**: Accepted
- **Date**: 2026-09-22
- **相关**: [ADR-021](./ADR-021-animation-driver-boundary.md)（动画驱动边界）、[ADR-018](./ADR-018-global-and-member-tools.md)、`Assets/Learn/ARPG/Docs/agent-chat-state-design.md`、`Assets/Learn/ARPG/Docs/role-controller-design.md`

## Context

`evo-code-review` 在 `Assets/Learn/ARPG/Scripts` 抓到两条互相咬在一起的问题：

1. `RoleController` 与 `AgentAnimGraphDriver` **同时**实现 `IAgentAnimationDriver` + `INpcGestureDriver`。取用方（`AgentAnimationTools.cs:128`、`NpcSpeechPerformanceController.cs:90-91`）用 `GetComponentInChildren<T>(true)` 拿驱动 ⇒ 谁生效取决于层级遍历顺序。ADR-021 决策 4 把"同一物体挂两个驱动"定为配置错误，但代码把它变成了默认形态；而且 `Role/` 下没有任何白名单引用，ADR-021 决策 5 的裁剪闸门对角色完全失效。
2. NPC 一边跟玩家说话，一边可能被模型的一个 `move_to` 拽走，或者被打断进 Hurt——观感就是"边说边跑"。

第 2 条的修法决定第 1 条往哪走，所以一起定。

## Decision

### 1. 新增 `ERoleState.Chat`：只放行动画播放的静默态

聊天态不引入新机制，只是把 `RoleStateBase` 已有的两组默认实现钉住：

| 能力 | 走哪条路 | 聊天态行为 |
| --- | --- | --- |
| 动画播放（`play_animation_state` / `set_animation_parameter` / 手势槽） | `IAgentAnimationDriver` / `INpcGestureDriver`，**不经状态机** | 有效 |
| 导航（`move_to` / `move_to_player` / `stop_navigation`） | `IRoleNavigation`，由状态自决（e36b800） | 基类默认 ⇒ 回"当前状态不接受导航" |
| 玩家输入（移动/转向/跳跃/攻击/蹲/交互） | `IPlayerActions` 回调 | 基类空实现 ⇒ 无响应 |
| 受击与复活 | `TakeDamage` / `Revive` 虚方法 | `ChatState` override 置空 |

进出由宿主接线：`RoleChatBridge`（`Learn/ARPG`）订阅 `AgentHost.TokenReceived` 进态、`TurnFinished` 出态。插件不引用 `Learn.*`，所以这座桥必须在宿主侧。

### 2. 一个角色物体上只允许一个动画驱动

`RoleController` 就是角色的驱动（它握着 `AnimGraphManager` 与状态机，播放必须经状态机才不被 locomotion 盖掉）。`AgentAnimGraphDriver` 不再与它同挂。不做"静默谁赢"：`RoleController.Awake` 检测到同层级另有 `IAgentAnimationDriver` 实现时 `Log.Warning` 指名冲突方。

## Alternatives Considered

| 方案 | 结论 |
| --- | --- |
| 用 `IAgentToolGate` 在聊天期间把导航工具摘掉 | 拒绝。门控是**能力开关**，会把工具从声明里抹掉，模型以为角色根本没有移动能力；状态自决回的是"当前状态不接受导航"，模型知道只是暂时不行 |
| 把 `RoleController` 的动画面整体搬进 `AgentAnimGraphDriver`（消除双实现） | 拒绝（当下）。驱动的播放不经状态机，会被 locomotion 覆盖，也拿不到"当前在播哪个 Role 状态"。真要统一，得先给驱动一个"经宿主状态机播放"的入口 |
| 聊天态允许受击/死亡迁移 | 拒绝。用户裁决是"其他操作置空"，照做；代价（玩家可拿对话当无敌帧）记在设计稿 §5，要放开只需删 `ChatState.TakeDamage` 一个 override |
| 在 `RoleChatBridge` 里直接判 `AgentHost` 的轮次事件、不给 Role 加状态 | 拒绝。位移与输入仍由状态机管，不在状态里拦就得在 Controller 里到处加 `if (chatting)`，那是更贵的一种耦合 |

## Consequences

- 正面：NPC 说话期间位移与输入确定性地静默，动画仍可播；双驱动的取舍从"层级顺序赌"变成"启动期报出来"；聊天态的进出只有一条路（`EnterChat` / `ExitChat`），`ExitChat` 在非聊天态是空操作，迟到回调不会把别的状态踢回 Idle。
- 代价与已知边界：
  - **聊天中不掉血**——按裁决字面执行，是玩法漏洞而非技术缺陷，落点在 `ChatState.TakeDamage`。
  - `ChatState` 依赖宿主接线。没有 `RoleChatBridge`（或等价接线）的角色，这个状态永远进不去。
  - 白名单对角色仍**未接**：本次只加了双驱动告警，把 `AgentAnimationWhitelist` 接到 `RoleAnimator` 的播放入口是下一笔（记在 `Docs/PENDING_TESTS.md`）。
  - 实机未验收：编辑器桥不可用，聊天态的进/出、导航被拒文案、动画仍可播都只有编译级证据。
