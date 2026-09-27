# ADR-027: 可重复副作用动作不占 session 一次性配额

## Status

Accepted

## Date

2026-09-22

## Context

`Idempotent=false` 同时承担“需要前置 say”和“同签名整 session 只能成功一次”两种语义。导航、停止与表演需要前置台词，但同一局跨情景重复执行是正常行为；空参 `move_to_player()` 尤其会在成功一次后永久被拦。

## Decision

- `AgentActionAttribute` 增加 `Repeatable`，默认 `false`，保持既有一次性动作安全语义。
- `Idempotent=false, Repeatable=true` 仍强制 `say`，但循环检测只使用本轮 NameCap/L2/L3，不读取 session 一次性配额。
- `move_to` 与 `move_to_player` 标为 Repeatable；`stop_navigation` 按其空操作可安全重试的实际语义标为 Idempotent。

## Alternatives Considered

### 把导航直接标成 Idempotent

拒绝：会同时移除 schema 中必填的前置 `say`，破坏先说后做。

### 新建三值动作类型枚举

拒绝：现有两个布尔量已能表达只读幂等、一次性副作用、可重复副作用三种组合；迁移所有声明没有额外收益。

## Consequences

- 一次性交易、扣款和好感调整保持原行为。
- 可重复副作用在同一轮仍受 NameCap/L2/L3 限制，跨轮可以再次执行。
- 本决策扩展 ADR-004/023 的 LoopGuard 分类，不恢复整轮工具次数总闸。
