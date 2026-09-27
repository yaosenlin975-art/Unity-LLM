# ADR-019: AgentHost 不设「纳入全局静态工具」开关与 runner 覆盖位，工具面由逐条开关表达

## Status
Accepted

## Date
2026-09-20

## Context
上一轮连续加了两处 AgentHost 口子：

- ADR-017 加了 `actionRunner` 覆盖位（默认 `ReflectionActionRunner`，挂组件可替换）。
- ADR-018 加了 `AgentToolSet.IncludeGlobalStaticTools` / `LLMSession.IncludeRegistryTools`，以及 AgentHost 上的 `includeGlobalStaticTools` 布尔，用来决定是否把全局静态 `[AgentTool]` 纳入某个 agent。

用户指出两者都是多余的维度：**所有全局 `[AgentTool]` / `[AgentAction]` 与成员实例都应一视同仁地列在 Inspector**，「要什么工具」直接由逐条开关表达即可；动作落地也不需要宿主覆盖位——成员动作已经打在实例上，全局动作（内置记忆三件套）反射执行就够。

## Decision
1. 移除 `includeGlobalStaticTools` / `AgentToolSet.IncludeGlobalStaticTools` / `LLMSession.IncludeRegistryTools`。全局静态 `[AgentTool]` 与 `ExtraTools`（成员工具/动作 + 全局动作）一视同仁进声明，统一由 `AgentToolToggle` 的逐条开关（覆盖式：未收录 = 可用）决定是否进 `request.Tools`。
2. 移除 `AgentHost.actionRunner` 覆盖位。`AgentHost` 固定传 `new ReflectionActionRunner()` 处理全局动作；成员动作直接反射打实例。`IAgentActionRunner` 接口与 `AgentCore.runner` 构造参数保留，供沙盒/测试/自定义宿主程序化注入。
3. Inspector 恢复三组：动作（全局 `[AgentAction]` + 本体动作）/ 公共工具（全局静态 `[AgentTool]`）/ 本体工具（层级实例 `[AgentTool]`）。

## Alternatives Considered

### 保留 `includeGlobalStaticTools` 布尔（ADR-018 原案）
- 优点：能整体关掉全局静态工具面。
- 缺点：与逐条开关重复；多一个没人会填的维度，且「全关再逐条开」不如直接勾条目直观。
- 拒绝理由：用户要的是「全部列出、自己配」，布尔开关是多余口子。

### 保留 `actionRunner` 覆盖位（ADR-017 原案）
- 优点：宿主可整体替换全局动作的落地方式。
- 缺点：成员动作已提供宿主定制的正路；全局动作目前只有内置记忆，反射足够。
- 拒绝理由：推测性抽象；程序化注入仍可走 `AgentCore.runner`。

### 把全局静态工具从 agent 工具面移除
- 优点：agent 只用宿主层级上配的工具。
- 缺点：与用户「全局工具也要列出来、我自己勾」的诉求相反。
- 拒绝理由：误读了需求，已回退。

## Consequences

- 全局静态工具默认全部列在 Inspector 且默认可用；不需要的逐条关闭即可（覆盖式语义）。
- 不再存在 `IncludeRegistryTools` / `IncludeGlobalStaticTools` / `actionRunner` 三个口子；`AgentHost` 序列化面回到 `profile / instanceId / coreSnapshot / toolToggles`。
- 本 ADR 取代 ADR-017 的覆盖位部分与 ADR-018 的开关部分；ADR-018 的「成员实例工具/动作」主体结论仍然有效。
