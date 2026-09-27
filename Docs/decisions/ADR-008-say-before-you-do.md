# ADR-008: 副作用动作强制 `say` 参数，用承诺表收口"说了没做成"

> 输出通道部分已由 [ADR-026](./ADR-026-separate-action-say-from-model-stream.md) 修订：`say` 改走 `IAgentOutput.OnSay`，不再伪装成模型 token。

## Status
Accepted

## Date
2026-09-19

## Context
玩家要求 NPC 带路时，实际观察到：NPC 一言不发地自己寻路走到目的地，**然后**才说"好的，跟我来"。根因不是顺序错乱——文本 token 确实在工具执行之前就转发了——而是模型常常只回 `tool_calls`、`content` 为空，于是本轮没有任何话上屏，等动作 await 完成后下一轮才补台词。"先说后做"因此完全依赖模型自觉，而便宜模型经常不自觉。

连带问题：话一旦说出口就收不回。动作可能失败（目标被占、钱不够、超时、被新输入取消），此时玩家听到的是一句空头承诺。

## Decision
1. **`Idempotent = false` 的动作，schema 里强制一个必填字符串参数 `say`**（`Idempotent = true` 的只读工具不加，避免每次查询都逼模型说话）。
2. `AgentActionExecutor` 在调用 runner **之前**，把 `say` 经 `IAgentOutput.OnToken` 播出去；本轮模型已有正文则不重复播。
3. `say` 为空 → 直接静默执行，不阻塞。不加「actionId → 台词模板」表：ADR-007 已明确拒绝按 actionId 建覆盖表，且 Unity ScriptableObject 存不了 `Dictionary`，为它新写包装类型不值。要求模型必带 `say` 靠 `required` 与提示词。
4. **承诺表**：每次播报把 `{actionId, say}` 记进本轮的承诺登记。失败后分两条路收口：
   - 本轮**还会继续发请求**（普通失败回填）→ 内核不播模板，但在回填文本尾部注入强制指令：`（注意：你已对玩家说过「{say}」但未成功，必须先向玩家说明再给替代方案）`，交给模型自己圆。
   - 本轮产出已无意义（墙钟超时 / 往返耗尽 / HARD / Provider 全挂 / **被新输入叫停而判 `Stale`**）→ 内核立即播 `FallbackLines` 之一。注意「被叫停」不等于中断在飞请求，只是产出作废（ADR-009）。
   - 台词池只有一个（`FallbackLines`）。不另开 `UnmetPromiseLines`：否则墙钟路径下「空文本兜底」与「承诺兜底」会同时命中、播两句话，还得再定优先序。

## Alternatives Considered

### 靠流式天然顺序，不加约定
- Pros：零代码零 token
- Cons：正是出 bug 的现状，模型不说话就没有任何保证
- Rejected：这就是用户提出这条需求的原始病例

### 两段式 propose / commit（先宣告、可打断、再执行）
- Pros：保证最强，适合交易类需要玩家确认的演出
- Cons：每笔副作用动作多一次完整往返，token 翻倍，与 ADR-006"不做运行时闸门但控成本"取向冲突
- Rejected：先不做，真需要玩家确认时再开

### 纯宿主模板播报（不看模型说什么）
- Pros：最可控、零 token 成本
- Cons：千人一面，模型失去表达方式
- Rejected：只作为 `say` 为空时的兜底档保留

### 改成"做完再说"
- Pros：彻底没有空头承诺
- Cons：与需求相反，带路又会变成走到目的地才开口
- Rejected：直接违背本条要解决的问题

## Consequences
- 播报发生在执行前，所以**动作被打断或被取消时话说出去了**，必须靠承诺表兜底；`Interruptible` 被叫停（ADR-009）同样走分支 2。但**锁排队失败不走**：取锁在播报之前，失败时话还没说出口，无需补话
- 便宜模型可能把 `say` 填成与参数无关的废话，属提示词调优范畴，内核不做语义校验
- `say` 是**保留参数名**：动作方法自带 `say` 会在注册期报错并跳过注册。若只写「不重复注入」而 executor 仍一律剥离，该方法永远收不到值——看似安全实则死路
- **`say` 必须从 LoopGuard 签名里剔除**（executor 读出后即从 args 移除，runner 也收不到它）：否则模型每次换一个措辞，L2 签名就不同，「非幂等阈值恒 1」被一句话绕开，重复扣钱的洞重新打开
- 被叫停的轮次按 `Stale` 收尾但仍要播一次承诺兜底：`say` 一经 `OnToken` 播出即视为已上屏，宿主只能收尾半截气泡、不得清空
- `say` 是 schema 里的普通 `string` 参数，与"跳过非复杂类型参数"的规则不冲突
- 失败指令注入会让失败回填变长（约 60 字/次），可接受

See also: [ADR-009](./ADR-009-long-actions-and-interruption.md)、[ADR-010](./ADR-010-cross-agent-resource-locks.md)、[设计稿](../agent-kernel-design.md)
