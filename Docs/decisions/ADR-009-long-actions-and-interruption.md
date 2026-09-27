# ADR-009: 长动作时长分档 + `Interruptible`，叫停时取消动作但不 cancel HTTP

## Status
Accepted

## Date
2026-09-19

## Context
带路、演出、移动这类动作设计上就要几十秒，而 `ActionTimeoutSeconds` 默认 15s 会把它误杀。更严重的是 ADR-003 定的"新输入不抢占、串行排队"：玩家中途说"算了不用带了"，NPC 会充耳不闻地把人带到目的地再说下一句，因为内核没有停掉一个正在 `await` 的动作的路径。

ADR-003 当初的"不抢占"是为了不白烧已付费的 token，那是针对 **HTTP 请求**的；动作是玩家能看见的外部行为，停不掉才是 bug。这两件事必须分开表述，否则会被读成"什么都不许取消"。

## Decision
1. 动作属性加两项：
   - `LongRunning`（默认 false）：走 `LongActionTimeoutSeconds`（默认 60，`[Range(15,180)]`），带路/演出/移动类标注。
   - `Interruptible`（默认 true）：新输入进来时，`AgentCore` 立即 cancel 所有在飞且可打断的动作，并回填 `[Action Failed] cancelled by new input`。**叫停只做三件事**：cancel 动作的 `ct`、停止上屏（代际不再匹配）、轮末按 `Stale` 收尾并播一次 `FallbackLines` 兜底（ADR-008 分支 2）。
   - **叫停不借用 `AbortTurn`，也不中断在飞往返**：`AbortTurn` 是 LoopGuard HARD 的专用出口（借来会把 outcome 误判成 `LoopAborted`，而测试断言的是 `Stale`）；往返继续跑完、结果照旧回填以维持 `tool_call` 配对，只是整轮产出最终被丢弃。让模型"圆"没有意义（产出要丢），但也没有额外成本——本来就不会为它多花一次请求。给物品、扣款这类「做一半更糟」的标 `false`，跑到完成为止。
   - 被叫停/超时/锁等待失败的调用**不计入非幂等配额**（只有 `Ok = true` 才计），否则一次排队失败就把该签名永久 `[Blocked]`。
2. **`TurnDeadlineSeconds` 覆盖「LLM 往返 + 锁等待 + 非 `LongRunning` 动作执行」**，只有 `LongRunning` 动作的执行期不扣额度。C# 的 CTS 不能暂停，所以实现是**额度累计**：进长动作前记下已耗额度 → 给动作独立 CTS → 动作返回后按剩余额度重建轮级 linked CTS（见设计稿 §一轮时序）。“不计入”必须写成这套机制，不能只写“挂独立 CTS”。

3. `IAgentActionRunner` 的契约写死：**所有动作必须响应 `ct`**；不响应 `ct` 的动作不得标 `Interruptible = true`。
4. **叫停与释放锁是两件事**：cancel `ct` 不等于释放已持有的资源锁。`AgentActionExecutor` 在 `finally` 里 `Dispose` 自己的 `LockLease`（幂等、带持有者身份，见 ADR-010），否则被叫停的持有者会让该键的队列永久卡死。同理，被叫停 / 超时 / 锁等待失败的调用**不计入非幂等配额**（只有 `Ok = true` 才计）。

## Alternatives Considered

### 只加长动作超时档，不支持叫停
- Pros：实现最省
- Cons：玩家叫停仍然无效，只是最终会自己走完
- Rejected：叫停才是体验上真正的问题

### 全局超时统一抬到 60s
- Pros：一行改动
- Cons：普通动作卡住也要等 60s，叫停问题依旧
- Rejected：把两类问题的时长混成一档

### 时长与打断全交给宿主
- Pros：内核更薄
- Cons：叫停无效会在每个宿主里重现一次
- Rejected：这是内核职责

## Consequences
- ADR-003 的"不抢占"语义被本条局部修订：**仅指不 cancel 在飞 HTTP 请求**；动作可被叫停。ADR-003 已补注
- 墙钟对长动作失效，意味着一轮 agent 的总时长可能远超 `TurnDeadlineSeconds`（带路 60s 是设计意图，不是失控）；宿主若要表现"NPC 正在赶路"，看 `OnToken` 播出去的 `say` 即可
- 不可打断动作与新输入的组合会产生"话已说出、动作继续"的观感，属预期
- `Interruptible` 默认 true，意味着忘记处理 `ct` 的动作会在被叫停时留下半截状态——契约要求动作自己做补偿

See also: [ADR-003](./ADR-003-event-driven-with-generation-invalidation.md)、[ADR-008](./ADR-008-say-before-you-do.md)、[设计稿](../agent-kernel-design.md)
