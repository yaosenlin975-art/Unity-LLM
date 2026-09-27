# AgentHost Inspector 显示持久化记忆 — 设计

## 需求分析

- **目标**：在 `AgentHost` 的 Inspector 上直接看到该 agent 的持久化记忆（事实槽），用于调试「读档是否生效、事实是否正确」。
- **触发条件**：持久化后端为 Prefs（`LLMGlobalConfig_SO.StateStore == EAgentStateStoreKind.Prefs`）且该 agent 是持久实例（`instanceId` 非空）。
- **成功标准**：
  1. `StateStore = Prefs`、填好 profile + `instanceId`，Play 模式下让 agent 写几条事实，停止 Play 后选中该 `AgentHost`，Inspector 能显示这些事实。
  2. `StateStore = None`、`instanceId` 为空、`ProfileKey` 为空时给出明确提示，且**不误读别人的存档**。
  3. 选中对象不产生明显卡顿（不每帧读盘解析）。

## 现状与约束（已对码核实）

- 存档键 = `sessionId = {ProfileKey}#{instanceId}`（`AgentCore.cs:99`）。
- 事实存档：`PrefsAgentStateStore.LoadFacts(sessionId)` → `PrefsHelper.Get<AgentFactsBlob>(sessionId)`。
- `AgentFactsBlob.Facts` 直接是 `List<AgentFact>`，字段 `Key/Value/Timestamp`，Timestamp 是 **Unix 秒**。
- 编辑器下 `PrefsHelper` 落在 `<project>/EditorPrefs/<hash>`（`PrefsHelper.cs:242-251`），**无 `Application.isPlaying` 限制**，可编辑期直接读；首次访问惰性加载并静态缓存。
- Inspector 读取路径不再依赖 Newtonsoft，Store 返回后只复制列表与元素。
- `AgentStateStores.Facts` 在编辑器里可能仍是 Null 对象（未 `Install()`），**不能**当读取入口。
- 现有 `Lin/Prefs Manager`（`Lin/Editor/PrefsHelperWindow.cs`）只是无 agent 语义的原始 JSON 查看器，本功能不重复它。
- **现状盘点**：全仓 `.unity`/`.prefab` 中无任何 `AgentHost` 使用，无 `AgentProfile_SO` 资产；唯一真实消费方是 `AgentSandboxWindow`（`instanceId = "sandbox"`）。

## 方案选型

| 决策点 | 选择 | 备选与拒绝理由 |
|---|---|---|
| 读取入口 | `new PrefsAgentStateStore()` | 直接 `PrefsHelper.Get<AgentFactsBlob>`：更底层、与 Prefs 耦合更紧，将来换 store 要改 Inspector |
| 读取时机 | `OnEnable` 读一次 + 缓存 +「刷新」按钮 | 每次 `OnInspectorGUI` 都读：repaint 频繁会反复读盘 + JSON 解析 |
| sessionId 解析 | 有 `host.Core` 用 `Core.SessionId`（就是运行期实际用的值）；否则按运行期同一公式复现 | 不"选口径"——运行中读真实值，非运行中复现公式，两者必须一致 |
| ProfileKey 为空 | 只提示，不主动 `EnsureKey()` | 主动补 key：Inspector 产生改资产的副作用 |
| 展示内容 | 只显示「记忆（事实槽）」 | 对话历史不属于项目术语里的「记忆」，本次不做（见非目标） |
| 动 AgentHost 运行时 API | 不动 | 加 `public InstanceId`：暂无真实消费方，等有需要再加 |

## 接口设计

### 新增 `Assets/Plugins/LLM/Editor/AgentStateReader.cs`

静态、无 UI 依赖，便于单测：

```csharp
namespace LLM.Editor
{
    /// <summary>编辑器侧读取持久化 agent 状态的纯逻辑，不含 UI</summary>
    public static class AgentStateReader
    {
        /// <summary>全局配置是否启用 Prefs 持久化（读 Resources/LLM/LLMGlobalConfig）</summary>
        public static bool IsPrefsStoreEnabled();

        /// <summary>按运行期公式复现 sessionId：{ProfileKey}#{instanceId}；任一为空返回 null</summary>
        public static string ComposeSessionId(AgentProfile_SO profile, string instanceId);

        /// <summary>读事实槽；无存档或解析失败返回空列表</summary>
        public static List<AgentFact> ReadFacts(string sessionId);
    }
}
```

内部：`new PrefsAgentStateStore().LoadFacts(sessionId)` 直接取 `Facts`；异常吞掉、返回空列表并 `Log.Warning`。

### `AgentHostInspector` 新增 `DrawMemory()`

在 `DrawToolToggles()` 之后调用（只读，不参与 `ApplyModifiedProperties`）：

- 标题「持久化记忆」+ sessionId 行（可复制）+「刷新」按钮。
- sessionId 取值：`host.Core?.SessionId ?? AgentStateReader.ComposeSessionId(profile, instanceId)`。
- 前置检查（任一不满足即 HelpBox 后返回）：
  - `StateStore != Prefs` → 「未启用持久化（StateStore = None），无记忆可显示」
  - sessionId 为 null → 「未填实例标识，该 agent 为临时实例，不落盘」/「ProfileKey 为空」
- `Foldout`「记忆（事实槽）· N 条」；内容为限高 `ScrollView` + `SelectableLabel`（只读可复制）；空显示「（无）」。
- 每条显示 `- {Key}：{Value}`，时间用 `DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime()`。
- `instanceId` 由 `serializedObject.FindProperty("instanceId").stringValue` 取，不新增运行时属性。

## 数据结构

复用现有类型，不新增持久化结构：

| 类型 | 字段 | 位置 |
|---|---|---|
| `AgentFactsBlob` | `List<AgentFact> Facts` | `PrefsAgentStateStore.cs` |
| `AgentFact` | `Key, Value, Timestamp(long 秒)` | `AgentMemory.cs:19` |

## 关键流程

1. 选中 `AgentHost` → `OnEnable` 绑定 SerializedProperty，并读一次状态（缓存 sessionId / 事实列表 / 前置检查结论）。
2. `OnInspectorGUI` → 画属性 + 工具门控 + `DrawMemory()`。
3. `DrawMemory()`：前置检查 → 用缓存（或点「刷新」重读）→ 画 sessionId + 事实折叠区。
4. 「刷新」→ 清缓存 + 重读 + `Repaint()`。

## 测试计划

新增 `Assets/Plugins/LLM/Tests/Editor/Agent/AgentStateReaderTests.cs`：

- `IsPrefsStoreEnabled`：默认配置（`StateStore = Prefs`）为 true。
- `ComposeSessionId`：profile + instanceId 齐全 → `{key}#{id}`；instanceId 空 → null；ProfileKey 空 → null。
- `ReadFacts`：用 `PrefsHelper.Set(sessionId, new AgentFactsBlob{...})` 造数据后读回；无 key 或 `Facts = null` → 空列表。
- 清理沿用 `PrefsAgentStateStoreTests` 的随机 sessionId + `DeleteKey` 模式。

## 非目标（明确不做）

| 不做 | 原因 | 将来的唯一落点 |
|---|---|---|
| 显示对话历史（轮次） | 项目术语「记忆」= 事实槽；轮次是 history，属另一个归档 | 需要时在同处加第二个折叠区 |
| Inspector 内编辑/新增事实 | 只读诊断；写入语义归 `write_fact` 动作，避免两套写入 | Agent 调试窗口 |
| 「删除存档」按钮 | destructive，误点丢档，且不在本需求内 | Agent 调试窗口 |
| 每轮/每帧自动刷新 | 反复读盘解析会卡编辑器 | 加「Play 中自动刷新」开关 |
| 显示 standalone 构建写入 `persistentDataPath` 的存档 | 编辑器读的是 `<project>/EditorPrefs/`，跨进程不可见 | 导入/导出工具 |
| 显示运行中 core 的内存态 | 本需求明确是「Prefs 持久化」；内存态走 `AgentSandboxWindow` | `AgentSandboxWindow` 扩展 |

## 已确认决策

1. 只显示事实槽（对话历史不做）。
2. 不加「删除存档」按钮。
3. sessionId 用运行期实际值（`Core.SessionId`），非运行期复现同一公式——不是二选一。
