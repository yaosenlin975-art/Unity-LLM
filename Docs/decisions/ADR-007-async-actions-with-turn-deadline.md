# ADR-007: 动作全部异步 await，配 per-action 超时与 per-turn 墙钟

## Status
Accepted

## Date
2026-09-19

## Context
现有 `ToolRegistry.Execute` 是同步静态方法、只返回 `string`。NPC 动作（走到某处、播演出、完成一笔交易）本质是异步且可能十几秒不返回。可选做法有三种：内核 await 动作完成；动作 fire-and-forget 后立即返回 accepted；或者返回 pending 再由宿主回调收口。

## Decision
动作统一异步 `await`：`IAgentActionRunner.RunAsync(...) → UniTask<AgentActionResult>`，结果直接作为 tool 消息回填，模型能拿到真实成败。配套两条时间约束：

- **per-action 超时**：`ActionTimeoutSeconds [Range(3,60)]`，默认 15s。超时即 `cancel` 该动作并回填 `[Action Failed] timeout`，由模型自己改道或道歉。超时判定统一用 `AgentCore.NowSeconds` 与截止时刻比较后主动 `Cancel()`，**不用 `CancelAfter`**（线程池真实计时器无法被假时钟推进，EditMode 就测不了这条路径）。契约要求动作**要么幂等要么自己补偿**（已扣的钱不指望回滚）。
- **per-turn 墙钟 deadline**：`TurnDeadlineSeconds [Range(10,180)]`，默认 90s。由 `AgentCore` 建 linked CTS 实现（`LongRunning` 动作期间要按额度重建，见 ADR-009）；超时后**已有累积文本非空则用该文本，为空才取 `FallbackLines`**，`outcome = Timeout`。
- 参数必须满足自洽规则 `TurnDeadlineSeconds ≥ MaxToolRounds × (ActionTimeoutSeconds + LockWaitSeconds + 预期 LLM RTT)`，默认 `90 ≥ 3 × (15 + 5 + 5) = 75` ✓。早期版本给的是 25s 墙钟 / 5 轮 / 15s 动作，数学上不可能成立。

实现上需要给 `LLMSession` 加一个注入点（既有代码要动的地方不止一处，完整清单见设计稿 §实现顺序）：

```csharp
public interface IToolExecutor
{
    UniTask<ToolExecutionResult> ExecuteAsync(string name, string argsJson, CancellationToken ct);
}
public readonly struct ToolExecutionResult
{
    public readonly string Content;   // 回填给模型的 tool 消息
    public readonly bool AbortTurn;   // LoopGuard HARD 命中 → 结束往返循环
}
```

`LLMSession.ToolExecutor` 默认实现是 `SyncToolRegistryExecutor`（保持现有同步行为，助手 UI 不受影响），内核注入异步版 `AgentActionExecutor`。

## Alternatives Considered

### fire-and-forget + 事件回调（返回 pending，宿主完成后另起一轮）
- Pros：不挂请求、和事件驱动天然合轨、实现最小
- Cons：模型拿不到动作真实结果，失败也当成功；一次交互要跨两轮才能闭环
- Rejected：语义模糊的代价大于多写一个执行器

### 全部同步（沿用 ToolRegistry）
- Pros：零改动
- Cons：NPC 动作必须"立即完成"，动画与寻路无法表达
- Rejected：不满足产品形态

### 超时只放弃等待、不 cancel 动作
- Pros：演出类动作不会半途被掐
- Cons：幽灵副作用——模型以为失败又选一次，第一次还在跑
- Rejected：与"重复执行有副作用的动作"同类风险，交给 `LoopGuard` 的 `Idempotent=false` 拦截也不覆盖跨轮情况

## Consequences
- HTTP 请求本身不会长时间挂着：工具是在两次请求**之间**执行的，挂的是"一轮 agent 的墙钟"，因此 deadline 是必需的而不是可选项
- `AbortTurn` 是内核唯一能让 `LLMSession` 提前跳出往返循环的通道，不额外开放 cancel 语义
- 有副作用的动作保持 `[AgentAction]` 默认的 `Idempotent = false` 即可——非幂等的拦截阈值恒为 1，不吃 `RepeatLimit`，无需逐个调参
- 循环检测只有**一处配置源**：属性上的 `RepeatLimit`/`Idempotent`，其中 `RepeatLimit = 0` 表示继承 profile 的 `GlobalRepeatLimit`。**不做按 actionId 覆盖的表**——两套来源一定会写出优先序 bug，且当前没有需要覆盖的场景
- 动作 await 期间新输入进来 → HTTP 不抢占（ADR-003），但可打断动作会被 cancel（ADR-009），该轮按 `Stale` 收尾

See also: [ADR-002](./ADR-002-three-narrow-host-interfaces.md)、[ADR-003](./ADR-003-event-driven-with-generation-invalidation.md)、[设计稿](../agent-kernel-design.md)
