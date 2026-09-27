# ADR-015: 删掉心跳自动起轮，时钟泵留在 AgentCore

## Status

Accepted — 本决策**取代** [ADR-003](./ADR-003-event-driven-with-generation-invalidation.md) 中"可关心跳"的那部分（Decision 第 1 条的第三个起轮入口，以及 Alternatives 里"纯事件驱动（无心跳）"的拒绝）。ADR-003 的主体——事件驱动、单飞、新输入按代际作废而非抢占——继续有效。

## Date

2026-09-20

## Context

心跳长这样：`HeartbeatEnabled` 打开后，每 `HeartbeatSeconds` 秒向模型发一句固定文案 `"【心跳】一段时间没有发生对话。"`，模型爱回什么回什么。

对码核实它的实际处境：

- 类字段默认 `false`，但 `Assets/Learn/LLM/AgentProfile_SO.asset` 里实际写着 `HeartbeatEnabled: 1` + `HeartbeatSeconds: 30`——Learn 场景真的每 30 秒起一轮。用户观察到的"定时向 LLM 发内容、LLM 自己回复"就是它，不是假想。删除时一并清掉该资产这两行。
- 打开之后的收益是零信息量的自说自话：没有对象、没有事件、没有目的，只换来一条模型自由发挥的回复，以及一次计费往返。ADR-006 能给的护栏只有"最小间隔 `[Range(10,300)]`"，等于只能控制烧钱速度。
- 它还给内核留下一条只有它用得上的路径：`TryAutoTurn` 的"上一轮在飞就丢弃并计数"（`DroppedHeartbeats` + `EAgentTraceKind.HeartbeatSkipped` + 测试矩阵一行）。

但 `AgentHeartbeat` 这个类同时兼着**第二职责**，而且是要命的：`Tick()` 里第一行就是 `agent.Tick()`，那是 `TurnDeadlineSeconds` 墙钟、动作硬超时、锁排队到期的**唯一**驱动源——内核刻意不用 `CancellationTokenSource.CancelAfter`（线程池真实计时器推不动假时钟），所有超时都靠"每帧拿 `NowSeconds()` 与截止时刻比较后主动 Cancel"。所以整个类一起删会把超时机制删没，卡死的轮次永远不结束。

## Decision

1. **删掉自动起轮**：`AgentHeartbeat` 类、`AgentCore.TryAutoTurn`、`AgentCore.DroppedHeartbeats`、`EAgentTraceKind.HeartbeatSkipped`、`AgentProfile_SO.HeartbeatEnabled` 与 `HeartbeatSeconds` 全部移除。起轮入口回到两个：`Trigger(input)` 与 `Notify(worldEvent)`。
2. **时钟泵留在 `AgentCore`**：`Tick()` 语义一字不改；Play 模式构造末尾 `AttachClockPump()` 挂 `MonoRunner.Update`，`Dispose()` 里 `DetachClockPump()` 摘掉（`MonoRunner` 的监听表是 `HashSet<Action>`，强引用委托，不摘就是死 agent 上继续泵）。泵此时只剩挂/摘一个委托，不值得再占一个文件，所以不改名换姓，直接并进 `AgentCore`。
3. **NPC 的"活着"改由世界事件承担**：想让某个 NPC 主动开口，是世界层/宿主调它的 `Notify(...)`，带上真正的原因（玩家靠近、任务状态变化、时间到点）。定时器的活儿交给宿主的世界调度，不在每个 agent 实例里各挂一个。

## Alternatives Considered

### 保留（Learn 场景本来就开着）
- Pros：不删代码，将来要用一勾就行
- Cons：一个打开就烧钱换空话的开关留在配置面上；内核为它保留一条只服务自己的起轮路径与一行测试矩阵；"未被论证的能力"会被后来者当成"该用的能力"
- Rejected：未被使用出价值的能力不该占内核代码面（内核代码量是 ADR-001 明确要压的东西）

### 心跳改成"只在有可说的事时才起轮"
- Pros：保留主动性的想象空间
- Cons：那已经是 `Notify` 的语义，只是多套了一层定时轮询去问"有没有事"
- Rejected：多一层轮询换不到任何新信息

### 保留 `AgentHeartbeat` 文件，只当纯时钟泵
- Pros：`AgentCore` 不新增成员
- Cons：一个类只剩 `Attach`/`Detach`/一个转发行；泵本来就是"驱动这个实例的时间"，归属就是这个实例
- Rejected：为删掉的语义留一个名字会骗人的文件

## Consequences

- ADR-003 Alternatives 里"纯事件驱动（无心跳）"从被拒变成采纳。当时那句"产品需要 NPC 活着"仍然成立，但承担它的是世界事件层，不是每个 agent 自带的定时器。
- ADR-006 §2 列出的运行时护栏少一条（心跳最小间隔）；`MaxToolRounds`、每 agent 单飞、墙钟额度不变。ADR-006 §1 关于"心跳主要走流式所以必须先补 `CacheHitTokens`"的那条 ⚠ 也随之失去一半理由，但补齐流式用量上报本身仍要做。
- `AgentProfile_SO` 少两个序列化字段，既有 Profile 资产无需迁移（值本来全是默认）。
- 沙盒与单测不受影响：`AgentCore.Tick()` 仍是 public 手工驱动点（`FakeRunner` 就在调它），EditMode 下依旧不注册 `MonoRunner`。
- 测试矩阵里"心跳反注册"一行改名为"时钟泵反注册"，断言不变（反射读 `MonoRunner` 的 `actions[Update]` 计数回退）。
- 明确排除：任何形式的定时起轮，包括"空闲 N 秒主动搭话"。将来的唯一落点是宿主的世界调度器调 `Notify()`——那属于世界/事件层，按 ADR-002 Consequences 不下沉进内核。

See also: [ADR-003](./ADR-003-event-driven-with-generation-invalidation.md)（心跳部分被本篇取代）、[ADR-006](./ADR-006-cost-guardrails-and-cache.md)、[ADR-002](./ADR-002-three-narrow-host-interfaces.md)、[agent-kernel-design.md](../agent-kernel-design.md) §时钟泵
