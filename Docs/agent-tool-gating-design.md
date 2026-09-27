# AgentHost 工具门控设计

## 需求分析

现状（已对码核实）：

- `AgentToolRegistry` 与 `AgentActionRegistry` 都是进程级全局注册表，反射扫描各自程序集里的 `[AgentTool]` / `[AgentAction]`。
- `LLMSession.BuildRequest`（`Runtime/LLM/LLMSession.cs:206-214`）在 `EnableTools` 时无条件声明 `AgentToolRegistry.ToLLMTools()` 的全部工具，再追加 `session.ExtraTools` 的全部动作。
- `AgentCore` 构造时（`Runtime/Agent/AgentCore.cs:109-110`）把 `AgentActionRegistry.BuildDeclarations()` 全部塞进 `ExtraTools`，其中含内置记忆动作 `write_fact / forget_fact / search_past_conversation`。
- `AgentProfile_SO` 没有任何工具字段。

结论：当前无法按 agent 区分可用工具，所有 agent 对模型暴露同一套工具。

目标：`AgentHost` 持有它这一个实例的工具启用表，在 Inspector 里暴露并逐项开关；被关闭的工具既不出现在该 agent 的 `request.Tools` 里，即使模型硬调也会在执行期被拒绝。交互形态参考 `Mu/Client/Assets` 的 `NPCBrain` + `NPCBrainInspector`。

成功标准：同一 `AgentProfile_SO` 挂到两个 `AgentHost`。宿主 A 关闭 `write_fact`，宿主 B 不改动。进入 Play Mode 后 A 的请求 tools 不含 `write_fact`，模型若调用返回 `[Tool Error]`；B 的请求仍含 `write_fact` 且可正常执行。A/B 的记忆与历史互不影响。

## 方案选型

### 采用

1. **控制面放在 `AgentHost`（per-instance）**，与 Mu 把可见性表放 `NPCBrain` 一致：同一 Profile 的多个实例可以有不同能力。
2. **启用语义用覆盖式**：列表未收录的工具默认可用，收录项按 `Enabled` 生效。与 Mu `IsToolVisible`（`NPCBrain.cs:105-119`）一致，新增工具不回头同步也不会误伤。
3. **过滤 seam 放在 `LLMSession`**：它是声明层的唯一汇总点（注册表工具与 `ExtraTools` 都从这里进请求），一处过滤覆盖两类工具。
4. **门对象用窄接口 `IAgentToolGate`**，经 `AgentCore` 可选构造参数注入，不新建全局事件总线，也不把宿主类型带进内核；扩展 ADR-002 的窄接口风格。
5. **执行期兜底放在 `AgentActionExecutor`**：它是动作与普通工具落地的唯一入口（`Runtime/Agent/AgentActions.cs:499`），在循环检测之前拦截未启用工具名。
6. **Inspector 扫描策略**：编辑期遍历 `AppDomain`，对引用 `LLM.Runtime` 的程序集调用注册表 `Register`，解决 `LLM.Editor` 不能引用 `Game.Hotfix` 的问题（编译期不可达，但运行时程序集可反射发现）。
7. **Inspector 用原生 `UnityEditor.Editor`**：不引入 Odin/Sirenix，也不在 `AgentHost`/`AgentToolToggle` 上挂任何 Odin 特性；`LLM.Editor.asmdef` 维持只引用 `LLM.Runtime` / `UniTask` / `Lin.Runtime`。序列化数据用原生 `[Serializable]` + `SerializedProperty`，Prefab 覆盖用 `PrefabUtility.RecordPrefabInstancePropertyModifications` 兜底。

### 未采用方案

| 方案 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 启用表放 `AgentProfile_SO` | 同 Profile 多实例会共享同一份能力，与"每实例一 Core"的既有设计冲突；Profile 已承担通用运行参数 | 若确需"模板级默认能力"，在 Profile 加一份默认表，运行时与宿主表做合并 |
| 用 `Func<string,bool>` / `Predicate<string>` 做 seam | 与项目"匿名委托一律换成实际方法"规范冲突，也不如接口便于单测与替换 | 无 |
| 在 `AgentToolRegistry` 内部做 per-agent 过滤 | 注册表是全局静态的，塞入实例状态会污染跨 agent 共享的缓存与扫描结果 | 无 |
| 白名单语义（未收录即不可用） | 新增工具默认对既有 agent 不可见，必须逐个回填同步，误伤面大 | 明确需要最小权限模型时，再加一个"仅白名单"开关，不改变本设计 |
| 只过滤声明、不做执行期拦截 | 模型仍可能硬调被关闭的工具，门控不实 | 无 |
| `AgentHost` 直接操作 `session.ExtraTools` | 覆盖不了 `AgentToolRegistry` 的普通工具，且与 `BuildRequest` 的复制语义打架 | 无 |
| 用 Odin/Sirenix 实现 Inspector 或序列化数据 | 会给 `LLM.Runtime`/`LLM.Editor` 引入额外依赖，与插件保持可独立复用的定位相悖；原生编辑器已足够 | 无 |

## 接口设计

```csharp
// Runtime/Agent/AgentInterfaces.cs —— 新增窄接口
public interface IAgentToolGate
{
    /// <summary>返回 false 则该工具既不出现在 request.Tools，执行期也被拒绝</summary>
    bool IsToolEnabled(string toolId);
}

// Runtime/Agent/AgentToolToggle.cs —— 新增数据
public enum EAgentToolSource { Action = 0, Tool = 1 }

[Serializable]
public sealed class AgentToolToggle
{
    public string ToolId = "";
    public EAgentToolSource Source = EAgentToolSource.Action;
    public bool Enabled = true;
}

// Runtime/Agent/AgentHost.cs
public sealed class AgentHost : MonoBehaviour, IWorldContextProvider, IAgentToolGate
{
    public IReadOnlyList<AgentToolToggle> ToolToggles { get; }

    // 未收录 → true；收录 → 该条 Enabled。Mu 同语义
    public bool IsToolEnabled(string toolId);

    // 运行时改开关：未收录时按需追加条目
    public void SetToolEnabled(string toolId, bool enabled);

    // Inspector 同步入口：保留已有条目的 Enabled，新增项默认 true，removeMissing 时移除失效项
    public void MergeToolToggles(List<AgentToolToggle> candidates, bool removeMissing);
}

// Runtime/Agent/AgentCore.cs —— 构造尾部新增可选参数，既有调用点不变
public AgentCore(AgentProfile_SO profile, string instanceId, IWorldContextProvider ctx,
    IAgentOutput output, IAgentActionRunner runner = null, IAgentToolGate toolGate = null);

// Runtime/LLM/LLMSession.cs
public IAgentToolGate ToolGate { get; set; }   // null = 全部放行

// 注册表快照（供 Inspector 枚举候选，不暴露内部字典）
public static List<AgentToolRegistry.RegisteredTool> AgentToolRegistry.Snapshot();
public static List<AgentActionDef> AgentActionRegistry.Snapshot();
```

- `AgentHost.Activate` 创建 Core 时传 `toolGate: this`；`AgentCore` 构造里赋值 `session.ToolGate = toolGate`。
- `LLMSession.BuildRequest` 声明阶段对注册表工具与 `ExtraTools` 逐条按 `ToolGate` 过滤。
- `AgentActionExecutor.ExecuteAsync` 开头查 `agent.Session.ToolGate`，未启用直接回 `[Tool Error] 该 agent 未启用工具 {name}`，不进 LoopGuard、不走 runner。
- 注册表快照新增方法仅返回副本，不影响既有 `ToLLMTools()` 缓存与 `BuildDeclarations()`。

## 数据结构

- `AgentHost`：`[SerializeField] List<AgentToolToggle> toolToggles`，序列化在组件上，Prefab 实例按 Unity 覆盖语义保存。
- `LLMSession.ToolGate`：仅运行时引用，随 Core 生命周期；随 `AgentCore.Dispose` 一并丢弃，不落盘。
- `EAgentToolSource`：仅用于 Inspector 分组显示（动作 / 工具），不参与过滤判定。

## 关键流程

1. Inspector 点"扫描并同步工具列表"→ 编辑期扫描程序集并 `Register` → 从 `AgentToolRegistry.Snapshot()`（源=Tool）与 `AgentActionRegistry.Snapshot()`（源=Action）收集候选 → `MergeToolToggles(candidates, removeMissing: true)` → 写回序列化字段并记 Prefab 覆盖。
2. Inspector 点「快速添加成员工具/动作组件」→ `AgentToolScanner.CollectMemberProviders()` 把 `LLM.Runtime` 自身与所有引用它的程序集里，MonoBehaviour 上的每个 public 实例 `[AgentTool]`/`[AgentAction]` 方法摊平成一条候选，菜单文案 = `名字（组件类名）`，取名规则与注册表一致（`attr.Name ?? method.Name`）。程序集不在自己的 `GetReferencedAssemblies()` 里，故 `LLM.Runtime` 要单独放行；编辑器程序集按名字里的 `Editor` 段判定，**不能**用「引用了 UnityEditor」——编辑器构建的运行时程序集同样引用 `UnityEditor.CoreModule`；再跳过测试程序集与抽象/泛型/嵌套类型。已提供的名字用 `ScanHierarchyTools`（含子物体，与运行期 `AgentToolSet.Collect` 同口径）过滤掉 → 选中即 `Undo.AddComponent` 挂上声明它的那个组件 → 复用步骤 1 把新条目并进 `toolToggles`（同一组件的多个方法会一起进来，它们的候选项也就同时消失）。只在编辑模式画这个按钮：`AgentCore.BuildExtraTools` 在构造时就冻结了成员声明，Play 中挂组件既不落盘也不会生效。
3. `OnEnable` → `Activate(profile)` → `new AgentCore(..., toolGate: this)` → `session.ToolGate = this`。
4. 每轮 `BuildRequest` → 按 `ToolGate` 过滤 `AgentToolRegistry.ToLLMTools()` 与 `extraTools` → 只声明启用项。
5. 模型 `tool_call` → `AgentActionExecutor.ExecuteAsync` 先查 `ToolGate` → 未启用回 `[Tool Error]`，启用则照原路径执行。
6. `IsToolEnabled` 线性遍历条目（数量很小），未命中返回 true。

## 文件范围

Runtime：

- `Runtime/Agent/AgentInterfaces.cs`（新增 `IAgentToolGate`）
- `Runtime/Agent/AgentToolToggle.cs`（新增枚举与序列化条目）
- `Runtime/Agent/AgentHost.cs`（序列化表 + 门实现 + 同步/运行时接口）
- `Runtime/Agent/AgentCore.cs`（可选 `toolGate` 参数）
- `Runtime/LLM/LLMSession.cs`（`ToolGate` + 声明过滤）
- `Runtime/Agent/AgentActions.cs`（执行期拦截 + `AgentActionRegistry.Snapshot`）
- `Runtime/Actions/AgentToolRegistry.cs`（`Snapshot`）

Editor：

- `Editor/AgentHostInspector.cs`（新增，自定义 Inspector；含成员工具/动作组件的快速添加下拉）
- `Editor/AgentToolScanner.cs`（新增，编辑期程序集发现；`CollectMemberProviders` 枚举可挂载的成员工具/动作组件）

Docs：本文件、`Docs/decisions/ADR-016-agenthost-tool-gating.md`、`README.md` 工具章节补充。

Tests：`Tests/Editor/Agent/AgentHostToolGateTests.cs`。

## 非目标（明确不做）

| 不做项 | 原因 | 将来的唯一落点 |
| --- | --- | --- |
| 实例/层级工具（Mu 的 Self 组） | 本文件成稿时框架工具全是静态方法，无实例扫描对象 | 已由 [agent-tool-model-design.md](./agent-tool-model-design.md) 落地（ADR-018）：全局 + 成员实例两分 |
| 角色/分组/最小权限白名单 | 当前只需逐项开关 | 需要权限模型时加白名单开关 |
| `AgentProfile_SO` 级默认能力 | 需求是宿主控制 | 模板默认能力时在 Profile 加默认表并合并 |
| 运行时开关热改立即对在飞轮生效 | 门在 `BuildRequest` 读取，改动下一轮生效 | 需要时加变更通知 |
| 按动作参数过滤 | 只按工具名控制 | 无 |
| 沙盒窗口接入门控 | 本次范围是 `AgentHost` | 沙盒需要时复用同一 seam |
