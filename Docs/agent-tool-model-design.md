# Agent 工具与动作：全局 + 成员模型（对齐 Mu）

## 需求分析

现状（对码核实）：

- `AgentToolRegistry` 只扫 `Public | Static`，`Execute` 固定 `Invoke(null, args)`；`AgentActionRegistry` 同样只扫静态、`BuildDef` 不记实例目标。
- `LLMSession.BuildRequest` 无条件声明全部全局静态 `[AgentTool]`；`AgentCore` 把全部全局 `[AgentAction]` 塞进 `ExtraTools`。
- 没有成员（实例）工具/动作，没有 per-agent 收集。`agent-tool-gating-design.md` 曾把成员工具列为非目标。

参考 `Mu/Client/Assets`（对码核实）：

- 全局静态 `[Tool]`：`Register(Assembly)` → 全局表；Mu 用 `Profile.IncludeGlobalStaticTools` 决定是否纳入（`NpcToolPipeline.cs:300-316`）。
- 成员 `[Tool]`：实例方法挂在 NPC GameObject（含子物体）的 MonoBehaviour 上（`NpcWorldTools : MonoBehaviour`），由 `ScanTools(instance)` + `ScanHierarchyTools(root)` 按 NPC 收集进 `instanceTools`，**不进全局表**；执行 `Method.Invoke(tool.Target, args)` 打在实例上（`ToolRegistry.cs:193-207, 398-414`）。
- Inspector 按 `Builtin / Public / Self` 三组开关。

目标：框架对齐「全局 + 成员」两分。成员工具/动作挂在 `AgentHost` 自身 GameObject 层级上，per-agent 私有，执行打在实例；全局静态 `[AgentTool]`/`[AgentAction]` 与成员实例**全部列在 Inspector**，由逐条开关决定谁进请求；`[AgentAction]` 同样支持成员实例。

成功标准：把带实例 `[AgentTool]` 的组件挂到 `AgentHost` 下，同步后模型可调用且执行落在该组件；关掉后既不可见也不可执行；全局静态 `[AgentTool]` 默认列在「公共工具」组，可逐条关闭；`[AgentAction]` 实例方法同样可用；同 Profile 多实例成员工具互不串。

## 方案选型

### 采用

1. **注册表支持实例目标**：`RegisteredTool` / `AgentActionDef` 增 `Target`；新增 `ScanTools(object)` / `ScanHierarchyTools(GameObject)`；全局静态路径与 `Execute(string, args)` 保持不变。
2. **per-agent `AgentToolSet`**：`AgentHost` 在 `Activate` 时扫描自身层级，收集成员工具/动作（对齐 Mu 的 `NpcToolPipeline.instanceTools`），交给 `AgentCore`。
3. **声明合并**：成员工具/动作与全局动作一并进 `session.ExtraTools`（按名去重）；全局静态 `[AgentTool]` 仍由 `LLMSession` 从注册表声明。两者统一过 `ToolGate`，由 Inspector 的逐条开关决定去留。
4. **执行路由**：`AgentActionExecutor` 先查成员集合（成员动作 → 反射打 `Target` 并 await；成员工具 → 反射打 `Target`），再回落全局动作 runner / 全局工具回退执行器。
5. **Inspector 三组**：动作（全局 `[AgentAction]` + 本体动作）/ 公共工具（全局静态 `[AgentTool]`）/ 本体工具（层级实例 `[AgentTool]`）。全局与本体一视同仁地列出，按条目勾选。

### 未采用方案

| 方案 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 把成员工具塞进全局注册表 | 全局表跨 agent 共享，成员工具是 per-agent 的，会互相覆盖/串台 | 无 |
| 用 Mu 的 `[Tool]` 特性整体替换现有 `[AgentTool]`/`[AgentAction]` | 会丢掉异步动作与墙钟/锁/先说后做（ADR-007~010） | 无 |
| 成员动作也走 `IAgentActionRunner` | runner 接口只给 `actionId`，拿不到 `Target`；改接口会波及测试与既有实现 | 确需宿主接管成员动作执行时再扩展接口 |
| 加「是否纳入全局静态工具」的布尔开关（Mu 的 `IncludeGlobalStaticTools`） | 与逐条开关重复；「要什么工具」直接由条目勾选表达即可，布尔口子是多余维度 | 无；见 ADR-019 |
| AgentHost 暴露 `actionRunner` 覆盖位 | 成员动作已直接打实例；全局动作（内置记忆）反射即可，无需宿主覆盖 | 需要程序化替换时用 `AgentCore.runner` 构造参数 |

## 接口设计

```csharp
// AgentToolRegistry —— 全局静态不变，新增实例目标
public struct RegisteredTool { ...; public MethodInfo Method; public object Target; ... }
public static List<RegisteredTool> ScanTools(object target);              // 实例方法
public static List<RegisteredTool> ScanHierarchyTools(GameObject root);   // 层级所有 MonoBehaviour 实例方法
public static string Execute(RegisteredTool tool, string argumentsJson);  // 打在 tool.Target 上
public static List<RegisteredTool> Snapshot();                            // 供 Inspector 枚举全局静态工具

// AgentActionRegistry —— 同样新增 Target 与实例扫描
public sealed class AgentActionDef { ...; public object Target; }
public static List<AgentActionDef> ScanTools(object target);
public static List<AgentActionDef> ScanHierarchyTools(GameObject root);
public static List<AgentActionDef> Snapshot();                            // 供 Inspector 枚举全局动作
internal static UniTask<AgentActionResult> InvokeAsync(AgentActionDef def, string argsJson, AgentActionContext ctx);

// Runtime/Agent/AgentToolSet.cs（新）
public sealed class AgentToolSet
{
    public void Collect(GameObject root);                       // 扫层级成员工具/动作
    public bool TryGetTool(string name, out AgentToolRegistry.RegisteredTool tool);
    public bool TryGetAction(string name, out AgentActionDef def);
    public void BuildDeclarations(List<LLMTool> into, HashSet<string> seen);
}

// AgentCore —— 新增可选 toolSet
public AgentCore(..., IAgentActionRunner runner = null, IAgentToolGate toolGate = null,
    AgentToolSet toolSet = null);
public AgentToolSet ToolSet { get; }

// AgentHost
public AgentToolSet ToolSet { get; }   // Activate 时收集自身层级
```

- `AgentCore` 构造：`ExtraTools` = 成员工具/动作声明 + 全局动作声明（去重）；全局静态 `[AgentTool]` 不在其中，由 `LLMSession` 声明。
- `LLMSession.BuildRequest`：声明全局静态 `[AgentTool]` 与 `ExtraTools`，按名去重、统一过 `ToolGate`（无「是否纳入全局」开关）。
- `AgentHost` 固定用 `new ReflectionActionRunner()` 处理全局动作；成员动作不经过它。
- `AgentActionExecutor.ExecuteAsync`：成员动作优先于全局动作，成员工具优先于全局工具；`RunWithTimeout` 按 `def.Target` 决定走 runner 还是反射打实例。

## 数据结构

- `AgentToolSet`：成员工具 `Dictionary<string, RegisteredTool>` + 成员动作 `Dictionary<string, AgentActionDef>`，仅运行时、随宿主实例存续。
- `RegisteredTool.Target` / `AgentActionDef.Target`：实例方法非空，全局静态为 null（`Execute` 用它区分）。
- `AgentToolToggle.Source`：`Action / PublicTool / SelfTool / SelfAction`，仅用于 Inspector 分组。

## 关键流程

1. `AgentHost.Activate` → `new AgentToolSet()` → `Collect(gameObject)`（扫层级成员工具/动作）→ 传入 `AgentCore`。
2. `AgentCore` 构造 → 声明合并（成员工具/动作 + 全局动作，去重）。
3. `BuildRequest` → 全局静态 `[AgentTool]` 与 `ExtraTools` 去重、过门（逐条开关）→ `request.Tools`。
4. 模型 `tool_call` → 执行器：成员动作（反射打实例 await）→ 成员工具（反射打实例）→ 全局动作（runner）→ 全局工具（回退执行器）。
5. Inspector 扫描 → 全局 `Snapshot()`（动作/工具）+ 本体 `ScanHierarchyTools(host.gameObject)` → 三组开关。

## 文件范围

Runtime：

- `Runtime/Actions/AgentToolRegistry.cs`（`Target` + 实例扫描 + `Execute(RegisteredTool, args)` + `Snapshot`）
- `Runtime/Agent/AgentActions.cs`（`AgentActionDef.Target` + 实例扫描 + `InvokeAsync` + 执行器路由）
- `Runtime/Agent/AgentToolSet.cs`（新增）
- `Runtime/Agent/AgentCore.cs`（`toolSet` 参数 + 声明合并）
- `Runtime/LLM/LLMSession.cs`（去重 + 过门）
- `Runtime/Agent/AgentHost.cs`（收集层级 + 固定 ReflectionActionRunner）
- `Runtime/Agent/AgentToolToggle.cs`（Source 扩展）

Editor：

- `Editor/AgentHostInspector.cs`（三组开关，全局 + 本体）
- `Editor/AgentToolScanner.cs`（全局程序集发现）

Docs：本文件、`Docs/decisions/ADR-018-global-and-member-tools.md`、`ADR-019-*`、`agent-tool-gating-design.md` 非目标更正、README/DESIGNS/CHANGELOG/PENDING_TESTS。

Tests：`Tests/Editor/Agent/AgentToolSetTests.cs`（成员工具/动作扫描与执行、声明与执行端到端）。

## 非目标（明确不做）

| 不做项 | 原因 | 将来的唯一落点 |
| --- | --- | --- |
| 成员工具/动作的全局注册（`Register(instance)` 进全局表） | per-agent 私有即可满足；全局注册会跨 agent 串台 | 确需跨 agent 共享实例工具时再设计 |
| 成员动作走宿主 runner | runner 接口拿不到 Target | 需要时扩展 runner 接口 |
| 「是否纳入全局静态工具」布尔开关 | 与逐条开关重复，多余维度 | 无；要什么工具直接勾条目 |
| 工具参数级过滤/权限模型 | 当前只需按名开关 | 需要时加白名单开关 |
| 运行中动态增删层级成员工具 | 收集在 `Activate` 时一次 | 需要时加 `Refresh`（对齐 Mu 的 `RefreshHierarchyTools`） |
