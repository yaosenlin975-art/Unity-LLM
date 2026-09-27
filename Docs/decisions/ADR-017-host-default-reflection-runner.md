# ADR-017: AgentHost 默认注入 ReflectionActionRunner 并提供覆盖位

## Status
Accepted（覆盖位部分已被 ADR-019 取代：`AgentHost.actionRunner` 已移除；默认注入 `ReflectionActionRunner` 的结论保留）

## Date
2026-09-20

## Context
`AgentHost.Activate` 构造 `AgentCore` 时没有提供 `IAgentActionRunner`，`AgentCore` 按既定默认回退到 `NullActionRunner`（`AgentCore.cs:109`）。结果是宿主路径下所有 `[AgentAction]`（含内置记忆三件套 `write_fact/forget_fact/list_facts`）一律返回「该 agent 不支持动作」。ADR-013 当时把「业务动作 runner」列为非目标，默认 `NullActionRunner` 是有意留白；但工具门控在 Inspector 里把动作候选暴露出来后，勾选即报错，留白变成了可见缺陷。

框架内已有 `ReflectionActionRunner`，可反射执行 public static `[AgentAction]` 并经 `AgentActionContext.Agent` 定位实例，正是 `[AgentAction]` 的标准落地方式。

## Decision
`AgentHost` 在未挂覆盖组件时注入内置 `ReflectionActionRunner`；新增序列化字段 `MonoBehaviour actionRunner` 作为覆盖位，仅当它实现 `IAgentActionRunner` 时生效，否则告警并回退内置反射执行器。沙盒窗口同样改为注入 `ReflectionActionRunner`。`AgentCore` 直接构造时的默认仍是 `NullActionRunner`（不变），由调用方显式决定。

## Alternatives Considered

### 继续默认 NullActionRunner，要求业务自行实现 runner
- 优点：保持 ADR-013 原状，宿主不替业务做决定。
- 缺点：零配置下勾选的动作全部报「不支持动作」，与「工具全自主」（ADR-004）和 Inspector 的勾选语义直接冲突。
- 拒绝理由：默认行为必须可用，否则门控是假功能。

### 只加覆盖位、不默认反射
- 优点：宿主不引入任何默认落地策略。
- 缺点：用户仍须手工挂一个 runner 组件，否则勾选的动作继续报错；对纯 static 动作是无谓配置。
- 拒绝理由：绝大多数 `[AgentAction]` 是 static，反射执行器已足够。

### 在 AgentProfile_SO 上配置 runner
- 优点：随人设走。
- 缺点：runner 是宿主/场景的落地方式，不是人设；且 SO 无法引用场景里的组件。
- 拒绝理由：职责错位。

### 让 AgentCore 默认 ReflectionActionRunner
- 优点：连沙盒与测试都零配置。
- 缺点：把「动作是否落地」的决策从宿主挪进内核；纯会话（不需要动作）与测试场景的语义被动改变。
- 拒绝理由：保持内核默认中立，落地选择留给宿主。

## Consequences

- 宿主路径下 static `[AgentAction]`（含内置记忆动作）零配置可用。
- 需要实例态或玩法语义的动作，仍可在 `AgentHost` 上挂自定义 `IAgentActionRunner` 组件覆盖。
- 覆盖组件未实现接口时不再静默失败，而是告警并回退。
- `AgentCore` 直接构造（测试、纯会话）默认仍为 `NullActionRunner`，行为不变。
- 本决策取代 ADR-013 非目标表中「动态动作 runner 配置」一项。
