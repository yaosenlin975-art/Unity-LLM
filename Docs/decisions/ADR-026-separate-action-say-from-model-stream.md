# ADR-026: 动作 `say` 与模型正文使用独立输出段

## Status

Accepted

## Date

2026-09-22

## Context

ADR-008 把动作前置台词经 `IAgentOutput.OnToken` 播出。随后模型正文继续进入同一 token 流，运行时聊天面板只能把两段拼进同一气泡；请求发出时预建空泡还会产生空气泡。

## Decision

- `IAgentOutput` 增加 `OnSay(actionId, say)`，`AgentHost` 转发为 `SayPresented`；`OnToken` 只表示模型正文增量。
- `AgentCore.turnText` 仍记录 say，保证聊天历史和后续上下文知道 NPC 已说过什么。
- UI 独立显示 say；定稿时只从答案前缀剥掉已显示 say，模型正文另起气泡。
- 请求发出先显示“思考中…”，首个 say 或正文到达时移除；不提前创建空正文泡。

## Alternatives Considered

### say 不进入 `turnText`

拒绝：只说 say、模型无后续正文时，历史会丢掉玩家实际看见的台词。

### UI 从 token 内容猜 say 边界

拒绝：输出通道没有可靠边界，文本相同也不能证明来源。

## Consequences

- 宿主实现者需要处理一个明确的 `OnSay` 回调。
- 当前历史数据仍是一条合并的 assistant 文本；重新载入历史时不恢复双气泡布局，只保证语义完整。
- 本决策部分取代 ADR-008 中“say 经 `OnToken` 播出”的输出约定。
