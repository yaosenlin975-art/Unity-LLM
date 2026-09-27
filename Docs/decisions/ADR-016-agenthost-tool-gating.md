# ADR-016: AgentHost 以覆盖式可见性表门控每个 agent 的可用工具

## Status
Accepted

## Date
2026-09-20

## Context
工具目前是进程级全局的：`[AgentTool]` 静态方法与 `[AgentAction]` 动作都由全局注册表反射扫描，`LLMSession.BuildRequest` 无条件把全部声明塞进 `request.Tools`；`AgentProfile_SO` 没有工具字段。这导致所有 agent 对模型暴露同一套工具，无法按角色裁剪能力（例如某个 NPC 不该有记忆工具或某类动作）。本仓库在运行时存在多实例同一 Profile 的场景，能力必须能按实例区分。

`Mu/Client/Assets` 里的同类问题由 `NPCBrain` 的序列化可见性表 + 自定义 Inspector 解决，本决策沿用其交互形态。

## Decision
在 `AgentHost` 上放一份 per-instance 的序列化工具开关表 `List<AgentToolToggle>`，采用**覆盖式**语义：未收录的工具默认可用，收录项按 `Enabled`。宿主实现窄接口 `IAgentToolGate`，经 `AgentCore` 的可选构造参数注入 `LLMSession.ToolGate`。

过滤在两处落地，二者缺一不可：
1. `LLMSession.BuildRequest` 声明阶段过滤注册表工具与 `ExtraTools`，被关闭的工具模型不可见；
2. `AgentActionExecutor.ExecuteAsync` 入口拦截，未启用工具名直接回 `[Tool Error]`，不进循环检测、不走 runner。

Inspector 由 `LLM.Editor` 的 `[CustomEditor(typeof(AgentHost))]` 暴露分组开关与"扫描并同步"按钮；同步时遍历 `AppDomain`，注册所有引用 `LLM.Runtime` 的程序集以发现游戏侧工具（`LLM.Editor` 编译期无法引用 `Game.Hotfix`）。注册表新增 `Snapshot()` 只读枚举供 Inspector 使用。

## Alternatives Considered

### 启用表放 `AgentProfile_SO`
- 优点：配置集中，换 Profile 即换能力。
- 缺点：同一 Profile 的多个实例共享一份能力；且会扩大 Profile 的职责。
- 拒绝理由：本仓库明确"每个 agent 一份 Core"，能力需 per-instance。

### 用委托做过滤 seam
- 优点：`Func<string,bool>` 最省代码。
- 缺点：与项目"匿名委托一律换成实际方法"规范冲突，替换与单测也不如具名接口清晰。
- 拒绝理由：新增窄接口与既有 `IAgentToolExecutor`、`IWorldContextProvider` 风格一致。

### 在 `AgentToolRegistry` 内做 per-agent 过滤
- 优点：调用方无感。
- 缺点：全局静态注册表会塞入实例状态，污染跨 agent 共享的缓存与扫描结果。
- 拒绝理由：注册表必须保持无实例状态。

### 白名单语义（未收录即不可用）
- 优点：默认最小权限，更安全。
- 缺点：每次新增工具都要回头同步，否则老 agent 静默失去新工具。
- 拒绝理由：当前无最小权限需求，覆盖式更符合参考实现的既定行为。

### 只过滤声明、不做执行期拦截
- 优点：改动最小。
- 缺点：模型硬调被关闭的工具仍会执行，门控不实。
- 拒绝理由：控制必须真实生效。

### 用 Odin/Sirenix 实现 Inspector 与序列化
- 优点：分组、折叠、内联编辑等 UI 更省事。
- 缺点：给 `LLM.Runtime`/`LLM.Editor` 增加对 Sirenix 的依赖，削弱插件可独立复用的定位。
- 拒绝理由：原生 `UnityEditor.Editor` + `[Serializable]` + `SerializedProperty` 已满足逐项开关与同步需求。

## Consequences

- 同一 Profile 的多个 `AgentHost` 实例可拥有不同工具集，且不影响各自的记忆与历史。
- 被关闭的工具在请求与执行两层都不出现/不执行；新增工具默认对所有 agent 可用，不会因未同步而误伤。
- 运行时通过 `SetToolEnabled` 改动开关在下一轮生效；在飞轮使用已声明的工具集。
- 沙盒窗口等直接构造 `AgentCore` 的入口不传门即保持原有"全部可用"行为。
- 编辑期扫描会把引用 `LLM.Runtime` 的程序集注册进全局注册表；该行为仅发生在编辑器会话内。
