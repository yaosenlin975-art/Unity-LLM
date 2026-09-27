# ADR-003: 事件驱动 + 可关心跳，新输入按代际作废而非抢占

## Status
Accepted

## Date
2026-09-19

## Context
动作全部异步 `await`（见 ADR-007）后，一轮 agent 的耗时不可预测：最坏情况 `MaxToolRounds` 次往返，每次一轮 LLM 请求加若干个十几秒的动作。玩家在这期间连续输入时必须有一个明确的语义，否则会出现并发写历史、旧回答覆盖新回答这类难以复现的故障。参考项目 hippy-agent 用的是常驻循环 + 抢占取消；本项目的调研结论（`research/unity-npc-agents/REPORT.md` §6.1）主张分频混合，LLM 只做低频决策。

## Decision
- **触发**：只有两个入口 `Trigger(input)`（玩家/用户输入）与 `Notify(worldEvent)`（世界事件）。心跳是 profile 上的可选开关，默认关闭，最小间隔 `[Range(10,300)]` 秒，由 `AgentHeartbeat` 借 `MonoRunner` 统一计时。（**本条的心跳部分已被 [ADR-015](./ADR-015-drop-heartbeat-auto-turn.md) 删除**：起轮入口只剩这两个，`AgentHeartbeat` 类取消，时钟泵并入 `AgentCore`。）
- **并发**：一个 `AgentCore` 同一时刻只跑一轮，永不并发（`LLMSession.busy` 是最后一道保险）。
- **新输入语义**：**放弃产出、串行排队**。在飞的那轮 HTTP 不取消，但其**可打断动作会被立即 cancel**（ADR-009）；新输入使 `turnGeneration++` 并覆盖单槽 pending。该轮收尾时代际已落后于当前 → 判 `Stale`，仍发一次 `OnTurnFinished(已攒文本, Stale)` 让宿主收尾半截气泡，但不写历史，随即起 pending 那一轮。不后台保留旧产出，也**不 cancel 在飞的 HTTP**（「断流」仅指停止上屏转发，不指掐断请求）。注意本条只约束 HTTP 请求：**在飞的动作可以被叫停**，见 ADR-009。

## Alternatives Considered

### 纯事件驱动（无心跳）
- Pros：零意外成本，代码最少
- Cons：NPC 不会自己找事做，旧模块的 `AutonomousTrigger` 能力没有落点
- Rejected：产品需要 NPC"活着"，但心跳默认关闭即可兼得

### 常驻 tick 每 N 秒自主决策
- Pros：世界最"活"
- Cons：成本随 agent 数线性爆炸（调研 §6.2 记录的 25 agent × 2 游戏日达数千美元量级）
- Rejected：默认关闭，需要时按 profile 开

### 立即断流抢占（cancel 旧请求）
- Pros：响应最快
- Cons：已付费 token 白烧；半途执行完的动作没有回滚语义；取消遗留状态要处理
- Rejected：与"不并发"合起来看，收益只有几秒，代价是状态复杂度

## Consequences
- `OnTurnFinished` 的 `outcome` 是宿主表现层的分支依据：`Stale` 轮不再上屏该轮全文、NPC 不二次开口，但仍收到一次带已攒文本的 `Stale` 回调用于收尾半截气泡；`Notify(worldEvent)` 与 `Trigger(input)` 共用同一 pending 槽与同一作废规则
- 「`Stale` 不写历史」做不到只靠内核：`LLMSession.AskAsync` 目前无条件 `AddRound`，必须加 `bool writeHistory = true` 参数（A 步范围）
- pending 只留最后一条输入，多次连点会被合并——策划需要知道"玩家连发第三条会吃掉前两条"
- 心跳跳过次数（因单飞而丢弃的 tick）进 `AgentTrace`，用于判断某个 NPC 是否长期过载

See also: [ADR-006](./ADR-006-cost-guardrails-and-cache.md)、[ADR-007](./ADR-007-async-actions-with-turn-deadline.md)、[设计稿](../agent-kernel-design.md)
