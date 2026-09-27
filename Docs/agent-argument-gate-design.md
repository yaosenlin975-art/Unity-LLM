# Agent 入参格式校验与回灌设计

## 需求分析

模型给出的 `tool_call` 参数不合预期时，内核现在**不提醒、不重试，而是静默降级继续执行**。对码核实到三处，全都把错误咽掉了：

| 位置 | 坏输入 | 现在的行为 |
| --- | --- | --- |
| `AgentActions.cs` `InvokeAsync` | `argsJson` 不是合法 JSON | `catch → args = null`，随后**所有参数按默认值绑定，动作照跑** |
| `AgentActions.cs` `BindArguments` | 必填参数缺失（`!HasDefaultValue`） | `token == null` → `GetDefault(type)`（int→0、string→""），动作照跑 |
| `AgentToolRegistry.cs` `ConvertToken`（现已并入 `ParseArguments`） | 类型不符（`"count":"abc"` 给 int 参数） | `catch → GetDefault` → `count=0`，动作照跑 |

> 本表写的是**立项时的现状**，故保留当时的方法名与行号式描述；改代码后不要拿它当现状读，现状以代码为准。

后果比"没有重试"更坏：模型收到的是一个**语义错误但看起来成功**的结果，或一句不相干的非空校验失败（如 `write_fact` 的"key 与 value 都不能为空"），它据此以为参数没问题。

成功标准：给一段坏 JSON / 缺必填 / 类型错，动作**不执行**，模型在同轮的下一次请求里收到指名到具体参数的原因，并最多有 2 次重出机会；正常路径逐字节不变。

## 方案选型

### 采用：一道"校验即绑定"的闸 + 复用既有回填通道 + 配额挂 LoopGuard

三件事都复用现成的，不新增机制：

- **必填依据与 schema 同源**：`AgentActions.cs:379` 生成 `required` 的条件就是 `!param.HasDefaultValue`，闸直接看 `ParameterInfo.HasDefaultValue`，不再新增一份"必填"声明。
- **回灌通道现成**：`AgentActionResult.Failure(content)` 会被执行器作为 `tool` 消息回填给模型——`LoopGuard` 的 Block 档（`AgentActions.cs:502` 的 `[Blocked] …`）走的就是这条路。
- **配额的家现成**：`LoopGuard` 已经是"每轮状态 + `BeginTurn()` 清零 + 跨轮终身配额"的持有者，加一个计数字段即可，不新建 `RetryGuard`。

### 未采用方案

| 方案 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 给模型**正文输出**加格式契约（正则/前缀/模板校验，不合就要求重说） | 内核的正文是自然语言气泡，先说后做靠 `say` 参数承载（ADR-008）；给正文加形状约束要么靠脆判、要么靠 `tool_choice=required` 把气泡变成 JSON | 需要结构化输出时**再包一个 action**，让结构进参数而不是进正文 |
| 依赖网关的 structured outputs / JSON mode | 各家能力不齐且不可探测，会把内核绑到供应商特性上；本项目走的是 OpenAI 兼容网关 | 真接入支持该能力的网关时，在 Provider 实现内部处理，不上升到内核 |
| 保持宽容降级，只在日志里记一次 | 日志不是模型的输入，模型看不到就改不了；而降级跑出来的错误结果会被写进历史，污染后续轮次 | 无 |
| 新建 `AgentArgumentValidator` + `FormatRetryGuard` 两个类 | 校验与绑定本来就需要同一次解析和同一次类型转换，分开等于把 args 解析两遍；配额也无需独立生命周期 | 无 |
| 不限次重出 | 格式错本身有 Abort 档封顶；整轮工具次数总闸已删除（ADR-023），不再用 MaxToolRounds 兜 | 无 |

## 接口设计

唯一新文件 `Runtime/Actions/AgentArgumentGate.cs`（命名空间 `LLM.Runtime.Agent`）：

```csharp
/// <summary>
/// 校验而非绑定：动作路径还要 ctx 注入与 say 剥离，绑定留在各自的注册表里。
/// 必填依据与 schema 同源（ParameterInfo.HasDefaultValue），不另立声明。
/// </summary>
public static bool TryValidate(string argsJson, ParameterInfo[] parameters,
    string[] extraRequired, out JObject parsed, out string error);
```

### 落地位置与对规格的偏离

- **插入点是 `AgentActionExecutor.RunActionAsync`，不是 `AgentActionRegistry.InvokeAsync`**。理由：执行器本来就已经解析过一次 args（给 LockKey 与 TakeSay 用），闸替掉那次解析正好零额外成本；而且这一处同时覆盖"直接反射打 Target"与"交给宿主 runner"两条分叉——放在 registry 里就只能管住前者。
- **闸只做校验不做绑定**（原设计写的是 `TryBind`，落地时改名 `TryValidate`）。动作参数里有 `AgentActionContext` 要按 `TakesContext` 插到 0 位、`say` 要剥掉，这些是 action 特有的，塞进共享闸会让工具路径跟着变复杂。绑定仍各走各的，且 `BindArguments` / 工具侧 `ParseArguments` 里的 `catch → GetDefault` **保留**，作为自定义 runner 绕过闸时的兜底。
- **`say` 的必填顺带被拦**：`def.Idempotent == false` 时 `extraRequired = { "say" }`，与 `BuildSchema` 里 `required.Add(k_sayKey)` 同源。行为变化：以前有副作用的动作没给 `say` 也照跑（只是不播承诺），现在会被打回重出。
- **格式错配额只作用于动作路径**。`[Tool]` 那条没有 `ReportFormatError` 在手：内核路径上工具调用先过 `LoopGuard.Decide` 才进闸，所以坏参数照样吃掉一次 NameCap 计数，封顶靠 NameCap + 墙钟；而完全绕开内核的 `SyncAgentToolRegistryExecutor` 回落路径连这个计数都没有，只剩墙钟。两条都不另立格式闸计数器。
- 格式错在 `RunToolAsync` 的 ok 判定里算失败（与 `[Tool Error]` 同档），否则 `LoopGuard.NoteResult` 会把一次没执行成功的调用记成有效结果。

回灌文案（`AgentArgumentGate` 内的常量，风格对齐既有 `[Blocked]` 那条）：

```
[格式错误] 参数不是合法 JSON（{异常首行}）。请按工具 schema 重新给出参数。
[格式错误] 缺少必填参数 {name}（类型 {jsonType}）。
[格式错误] 参数 {name} 应为 {jsonType}，收到 {token 原文截断 40 字}。
[格式错误] 参数连续 {n} 次不合 schema，本轮不再执行任何动作。   ← Abort 档
```

## 关键流程

1. 模型返回 `tool_call`，`LLMSession.AppendToolResultsAsync` 交给执行器。
2. 执行器先问 `LoopGuard.ReportFormatError()` 之前的状态，再调 `TryValidate`。
3. 绑定失败 → 计数 +1 → 未超限：返回 `Failure(error)`，作为 `tool` 消息回填，本轮继续发请求，模型可重出。
4. 超限：返回 `AgentToolExecutionResult(error, abortTurn: true)`，走既有 HARD 档语义——同批剩余不执行、本轮收尾、`outcome = LoopAborted`、不写历史、播 `FallbackLines[0]`（`AgentCore.cs:246-263`）。
5. 绑定成功 → 一切照旧，`turnFormatErrors` 不扣分（格式错与"重复调用"是两条独立计数）。

## 文件范围

- 新增 `Runtime/Actions/AgentArgumentGate.cs`
- 修改 `Runtime/Agent/AgentActions.cs`（`RunActionAsync` 走闸 + 新增 `FormatFailure`；删掉已无调用方的 `ParseArgs`；`RunToolAsync` 的 ok 判定认格式错前缀）
- 修改 `Runtime/Actions/AgentToolRegistry.cs`（`Execute` 走闸，`ParseArguments` 改收已解析的 `JObject`）
- 修改 `Runtime/Agent/LoopGuard.cs`（`turnFormatErrors` + `ReportFormatError()` + `BeginTurn` 清零）
- 新增 `Tests/Editor/Agent/AgentArgumentGateTests.cs`（6 条）
- `ToolProjects/llm_offline_compile.sh`：Tests 步骤补 `LLM.Editor.dll` 引用（否则任何 `using LLM.Editor` 的测试在离线冒烟里都是假红）
- 本文档 + （若你要）`ADR-016`

## 测试

EditMode 六条，全部只依赖反射拿到的 `ParameterInfo`，不需要场景对象：

1. 坏 JSON → false，error 含"不是合法 JSON"。
2. 缺必填 → false 且点名该参数；带默认值的参数缺失仍算合法（不误伤）。
3. 类型错 → `"count":"abc"` 对 int 参数返回 false，**不再变成 0**。
4. `say` 必填（`extraRequired`）缺失 → false 且点名 say。
5. 空 argsJson / `{}` 且无必填 → true。
6. 配额 → 同一轮第 3 次 `ReportFormatError()` 返回 true；`BeginTurn()` 后重新计数。

闸本身不测"动作未被执行"（那要跑完整轮次，已由 `AgentKernelMatrixTests` 的 fixture 覆盖，需要时在那边加一行）；测试用的两个方法体里放 `Assert.Fail`，一旦被反射调用就会红。

## 非目标（明确不做）

| 不做项 | 原因 | 将来的唯一落点 |
| --- | --- | --- |
| 正文输出格式契约 | 见未采用方案表 | 结构化需求包成 action 参数 |
| JSON mode / structured outputs | 网关能力不齐且不可探测 | Provider 实现内部处理 |
| 多余字段（模型塞了 schema 里没有的键）报错 | 无害，绑定时天然忽略；报错只会多烧一轮 | 出现"模型靠多余字段绕约束"的实例时再拦 |
| 复杂类型参数（数组/对象）校验 | 现在 `GetJsonType` 只允许 int/float/bool/string，注册期就挡下了（`AgentActions.cs:272`），运行期不存在这种输入 | 内核要支持复合参数时连同 schema 一起设计 |
| 格式错误配额做成 profile 字段 | 没有需要按角色调的场景，配了没人调 | 真出现"某类 agent 需要更多次重出"时进 `AgentProfile_SO` |
| 跨轮累计格式错 | 每轮独立看待：`LoopGuard` 的终身配额是给"有副作用的动作"防重复扣钱用的，语义不同，混用会把两条机制都搞脏 | 无 |
