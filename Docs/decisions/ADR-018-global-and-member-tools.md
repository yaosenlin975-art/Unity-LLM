# ADR-018: 工具/动作区分全局静态与成员实例，全局静态由宿主开关控制纳入

## Status
Accepted（开关部分已被 ADR-019 取代：`IncludeGlobalStaticTools` / `IncludeRegistryTools` 已移除，全局静态工具改为逐条开关；成员实例模型仍有效）

## Date
2026-09-20

## Context
框架原有工具/动作只有全局静态一种：`AgentToolRegistry` 只扫 `Public | Static` 并 `Invoke(null, args)`，`AgentActionRegistry` 同样全静态，`LLMSession.BuildRequest` 无条件把全部全局静态 `[AgentTool]` 声明进每个 agent 的请求。参考实现 `Mu/Client/Assets` 则区分两类：全局静态 `[Tool]` 进全局表但默认不纳入（`Profile.IncludeGlobalStaticTools`），成员 `[Tool]` 是挂在 NPC GameObject 层级上的实例方法，按 NPC 收集、`Invoke(Target, args)` 执行。此前 ADR-013 与本框架的门控设计都把成员工具列为非目标，导致挂在宿主上的实例工具/动作完全不可用，且宿主路径下的动作因没有 runner 全部报「该 agent 不支持动作」。

## Decision
对齐 Mu 的两分模型：

1. 注册表支持实例目标：`RegisteredTool.Target` / `AgentActionDef.Target`，新增 `ScanTools(object)` / `ScanHierarchyTools(GameObject)`；全局静态扫描与 `Execute(string, args)` 不变。
2. 新增 per-agent `AgentToolSet`，由 `AgentHost` 在 `Activate` 时扫描自身 GameObject 层级收集成员工具/动作，交给 `AgentCore`；成员集合不进全局注册表。
3. 成员工具/动作与全局动作声明合并进 `session.ExtraTools` 并按名去重；全局静态 `[AgentTool]` 由新增的 `LLMSession.IncludeRegistryTools` 控制，取值来自 `AgentToolSet.IncludeGlobalStaticTools`（宿主 Inspector 开关）。
4. 执行路由优先级：成员动作（反射打实例并 await）→ 成员工具（反射打实例）→ 全局动作（`IAgentActionRunner`）→ 全局工具（同步回退执行器）。
5. Inspector 分三组：动作（全局 `[AgentAction]` + 本体动作）、公共工具（全局静态 `[AgentTool]`）、本体工具（层级实例 `[AgentTool]`）。

## Alternatives Considered

### 把成员工具注册进全局注册表
- 优点：执行路径统一，不用 per-agent 集合。
- 缺点：全局表跨 agent 共享，同名成员工具互相覆盖；不同实例的能力会串台。
- 拒绝理由：成员工具的本质是 per-agent。

### 用 Mu 的 `[Tool]` 特性整体替换 `[AgentTool]`/`[AgentAction]`
- 优点：与参考实现完全一致。
- 缺点：丢掉异步动作、per-action 超时、资源锁、先说后做（ADR-007~010）。
- 拒绝理由：框架的产品形态需要异步动作。

### 成员动作也交给 `IAgentActionRunner`
- 优点：宿主统一接管动作执行。
- 缺点：runner 接口只接收 `actionId`，拿不到 `Target`；要改接口会波及既有实现与测试。
- 拒绝理由：成员动作是实例方法，直接反射执行即可；runner 继续服务全局动作。

### 全局静态工具维持无条件全量纳入
- 优点：零配置，新增全局工具对所有 agent 立即可用。
- 缺点：与 Mu 语义不符；无法按 agent 收敛全局工具面。
- 拒绝理由：成员工具已满足 per-agent 定制，全局面应可控。

## Consequences

- 挂在 `AgentHost` 层级上的实例 `[AgentTool]`/`[AgentAction]` 成为该 agent 私有能力，执行落在组件实例上。
- 全局静态 `[AgentTool]` 默认不纳入，需宿主显式开启；全局 `[AgentAction]` 仍默认纳入（内置记忆三件套依赖它）。
- 同 Profile 多实例的成员工具互不影响；成员集合在 `Activate` 时收集一次。
- `AgentCore` 直接构造（测试、沙盒）不传 `toolSet` 时保持原全局行为；沙盒窗口仍走全局 + runner。
- 本决策取代 ADR-013 非目标表中「动态动作 runner 配置」的后续状态，并更正 `agent-tool-gating-design.md` 的成员工具非目标。
