# ADR-010: 跨 agent 资源锁用 FIFO 队列串行化同资源动作

## Status
Accepted

## Date
2026-09-19

## Context
LoopGuard 的重复拦截是**单 agent 实例内**的状态，两个 NPC 同时对同一目标执行 `trade_player` / `take_item` 互相拦不住；`ToolRegistry` 与 `LLMDispatcher` 都是进程级共享。方案讨论时先提的是"立即失败让模型改道"，用户明确选择了**按顺序排队等锁**。

排队比拒单贵得多：它会把一个 agent 的轮次挂在另一个 agent 的动作上（带路 60s 能把等着交易的 NPC 挂到墙钟超时），并引入死锁、退队、公平性三类新问题。因此本 ADR 一并钉死这些约束。

## Decision
新增进程级 `AgentResourceLocks`（挂在 `AgentActionExecutor` 上，执行前获取、执行后释放）：

```csharp
public static class AgentResourceLocks
{
    // 同一 key 一条 FIFO 队列；等待受 waitSeconds 与 ct 双重约束
    // 返回 null = 未获得锁；拿到即持有一个 LockLease
    public static UniTask<LockLease> TryAcquireAsync(string key, int waitSeconds, CancellationToken ct);

// LockLease：IDisposable，内部记录持有者身份（sessionId + turnGeneration + 序号）
// Dispose 幂等，且只会释放自己那一次持有 —— 第二次调用不会放掉别人刚拿到的锁
}
```

五条硬约束：

1. **一个动作只允许一把锁**：`LockKey` 是单个字符串（可为空 = 不加锁）。多资源原子操作（交易 = 玩家 + 物品）需要全局锁序，本期不做（见设计稿 §非目标一行），不做"backlog"这种无指向的承诺。
2. **等待上限** `LockWaitSeconds`（默认 5，`[Range(0,30)]`；0 = 不等待直接失败）。超时不执行，回填 `[Action Failed] 目标被占用（排队 {N}s 未获得锁）`。等待计入轮级墙钟额度。
3. **`LockKey` 是 args 模板而不是字面量**：写成 `{argName}` 形式（`item:{itemId}`、`player:{playerId}`），executor 用本次 `argsJson` 插值出真实键——否则文档里那些 `item:{itemId}` 没人知道 `itemId` 从哪来。模板引用了 args 里不存在的键 → 注册期 `Log.Error`；插值结果为空 → 视作不加锁并 `Log.Warning`。
4. **锁键粒度可判定**：键中**必须含 `:`**（即 `资源类型:实例 id`），否则注册期 `Log.Error`。口号式的"禁止 `world`/`shop`"没人能执行。
5. **取消必须退队**：`TryAcquireAsync` 接收 `waitSeconds` 与 `ct`；agent 销毁、轮次判 `Stale`、墙钟到点、被新输入取消时，等待者出队、持有者 `Dispose` 自己的 lease。`AgentCore` 在轮次结束时统一 cancel 本轮 cts，因此不存在悬挂的等待者。

**释放只能靠 lease，不能靠 `Release(key)`**：释放动作有两个触发点（executor 的 `finally`、轮次级统一清理），无身份的 `Release(key)` 会让第二个触发点放掉**下一个持有者刚拿到的锁**，同键两个 NPC 于是并发进入动作——正是这条机制要防的事。`LockLease.Dispose()` 幂等且带身份，两处调用都安全。

**锁等待超时不计入 LoopGuard 计数**——动作根本没执行、没有副作用；若计入非幂等的 session 配额，一次排队失败就会让该签名永久 `[Blocked]`（NPC 从此不肯再跟这个玩家交易）。防无限重试靠 `MaxToolRounds`（一轮内往返封顶），不靠计数惩罚。

## Alternatives Considered

### 立即失败，不排队（推荐过）
- Pros：实现约 25 行，绝不会挂死；模型拿到明确失败会自己改道或说"你稍等"
- Cons：同一帧两个 NPC 抢同一货架时，后到者直接失败，需要模型自己重来
- Rejected（用户选择排队）：体验上"排队 20s"比"立刻被拒"更合理，尤其交易与对话对象

### 短等待（固定 2s）后失败
- Pros：覆盖毫秒级竞争，实现比队列简单
- Cons：2s 覆盖不了带路这类长持有；仍是失败而非顺序执行
- Rejected：与排队目标不一致

### 优先级队列（玩家输入 > 心跳）
- Pros：重要轮次优先拿到锁
- Cons：需要优先级来源、饥饿保护、公平性论证
- Rejected：本期 FIFO，饿死由 `LockWaitSeconds` 上限兜住

## Consequences
- 这是**同资源串行化**，不是并发限流，与 ADR-006"不做运行时限流器"不冲突：它不限制并发 agent 数量、不限制 token，只保证同一资源不被并发操作
- `LongRunning` 动作应尽量**不持锁**（带路独占的是 NPC 自己，用 `npc:{id}` 而不是玩家的键），否则排队方会被挂满 60s
- 死锁风险由"单键"约束消除；将来支持多键时必须补全局锁序，那时另写 ADR
- 需要新增测试：两个 agent 同 key 的顺序执行、等待超时、销毁后退队、`Stale` 后退队；`AgentResourceLocks` 要暴露 `WaitingCount(key)` 与 `Reset()` 作为测试 seam，否则这些断言不可观测
- 队列只保证**同键串行**，不保证公平：某键持有者卡住时，同键后来者各等满 `LockWaitSeconds` 后失败

See also: [ADR-006](./ADR-006-cost-guardrails-and-cache.md)、[ADR-009](./ADR-009-long-actions-and-interruption.md)、[设计稿](../agent-kernel-design.md)
