# LLM — 在线 API 对话层

基于 OpenAI 兼容 HTTP 接口（`/chat/completions`）的最小对话层，供游戏内 NPC、助手、编辑器工具复用。
只覆盖五件事：**流式输出、工具调用、上下文注入、系统提示词注入、上下文阈值压缩**。不含 RAG / 向量检索。

## 程序集

| 程序集 | 目录 | 说明 |
| --- | --- | --- |
| `LLM.Runtime` | `Runtime/` | 运行时主体，引用 `UniTask` + `Lin.Runtime` |
| `LLM.Editor` | `Editor/` | `Lin/LLM/请求日志` 窗口 |
| `LLM.Tests.Editor` | `Tests/Editor/` | EditMode 测试（阈值、并发、降级、工具扫描、流式用量、工具往返注入点） |

## 分层

```
LLMSession            一次对话会话：注入 + 流式 + 工具循环 + 历史
├── LLMDispatcher     Provider 注册表 / 主备降级 / 请求日志事件
├── ContextManager    轮数或 token 阈值 → 分区折叠 → 摘要压缩 → 归档
│   └── ContextCompressor   LLM 生成摘要，失败机械降级
├── ContextPruning    旧 tool result 头尾截取，比摘要更省的一层
├── ToolRegistry      [Tool] 静态方法 → JSON Schema → 反射执行
└── OpenAIProvider    SSE 解析 + tool_calls 分片拼装
```

## 接入

1. Provider 配置：`Assets → Create → LLM → ...` 建一个 `LLMProviderConfig_SO` 资产，填 `Model` / `BaseUrl`，`apiKey` 留空则读环境变量 `LLM_API_KEY`（**不要把 Key 写进资产提交** — `Resources/` 下的资产恒打进包体，进了 git 就等于公开）。
   需要保底通道就再建一个资产挂到 `FallbackProviderConfig`。

2. 全局装配：建 `Assets → Create → LLM → Global Config`，存到 `Assets/Resources/LLM/LLMGlobalConfig.asset`，把上一步的 Provider 配置拖进 `ProviderConfig`，再分别勾 `AgentStateStore`（记忆与会话轮次走 `PrefsHelper` 落盘）与 `NpcAffinity`（NPC 好感度落盘）——两个开关各管各的，关一个不会连带关另一个。宿主侧只需一次：

```csharp
LLMRuntimeSettings.Install();   // 注册 Provider + 装状态存储；AgentHost.Activate 与沙箱窗口已经调了
```

3. 建会话并提问：

```csharp
using Cysharp.Threading.Tasks;
using LLM.Runtime;

public class MyChatRunner
{
    private LLMSession session;

    public async UniTask Run()
    {
        session = new LLMSession("npc_merchant", "你是镇上杂货铺老板，回答简短。",
            Resources.Load<CompressionConfig_SO>("CompressionConfig_SO"));
        session.AddContext(ZString.Format("玩家金币：{0}", 120));   // 上下文注入，每轮都带

        string answer = await session.AskAsync("这把剑多少钱？", OnChunk, ct: this.GetCancellationTokenOnDestroy());
    }

    private void OnChunk(LLMStreamChunk chunk)
    {
        if (!string.IsNullOrEmpty(chunk.ContentDelta))
            panel.AppendText(chunk.ContentDelta);                   // 流式增量
    }
}
```

`onChunkCallback` 传 `null` 即走非流式，返回同一份完整文本。

## 五项能力

**流式输出** — `OpenAIProvider` 用 `DownloadHandlerScript` 在主线程逐块解析 SSE，无需自建线程与同步上下文；`data: [DONE]` 与 `finish_reason` 都会收尾。空闲超时 60s（`SSEDownloadHandler`）兜住"一直连着但不结束"。`stream_options.include_usage` 打开后，真实 prompt/completion token 在**独立的 `choices: []` 事件**里回传（映射成一条 `IsDone = false`、只带 token 的 `LLMStreamChunk`），`data: [DONE]` 则另发一条不带 token 的收尾。⚠ 想拿用量必须跨 chunk 缓存，别只盯 `IsDone` 那条。`LLMDispatcher.EnqueueStreamAsync` 正是这么做的：请求期间攒 usage，流结束时统一发一条 `OnRequestCompleted` 日志（含 `CacheHitTokens`），失败那条也发一次带 `IsError` 的日志。

**工具调用** — 给静态方法加 `[Tool("name","description")]`，`ToolRegistry` 反射生成 JSON Schema；参数仅支持 `int/float/bool/string` 及其可空形式，返回值必须是 `string`。`LLMSession` 内跑多轮循环：模型回 `tool_calls` → `ToolExecutor.ExecuteAsync`（默认 `SyncAgentToolRegistryExecutor`，即 `ToolRegistry.Execute`）→ 以 `role=tool` + `tool_call_id` 回填 → 再请求，**直到模型给出正文**（ADR-023 已删除 `MaxToolRounds` 总闸）。防循环靠 LoopGuard：`NameRepeatLimit`/`PerNameToolCallLimit`（同名换参 NameCap）与 `RepeatLimit`（同参 L2）；成本与失控靠墙钟 `TurnDeadlineSeconds`。未注册的工具名会以 `[Tool Error]` 文本回给模型，不会卡死循环。热更程序集里的工具需要显式登记：

```csharp
ToolRegistry.Register(typeof(MyHotfixTools).Assembly);
```

`[Tool]` 另有循环检测配置位：`RepeatLimit`（`0` = 继承全局同参阈值，`-1` = 关闭 L2）与 `NameRepeatLimit`（`0` = 继承 `PerNameToolCallLimit`，`-1` = 关闭 NameCap）与 `Idempotent`（默认 `true`；标 `false` 后同一签名第二次起一律拦截，阈值恒为 1 且不可关闭）。裁决由上层内核的 LoopGuard 执行，`ToolRegistry.TryGet` 供其读取单个工具的配置。Warn/Block 回灌均带「即使没有结果也要简短回复玩家」。

**按 agent 门控工具** — `AgentHost` 持有一份覆盖式开关表（`List<AgentToolToggle>{ToolId,Source,Enabled}`）：未收录的工具默认可用，收录项按 `Enabled` 生效。宿主实现 `IAgentToolGate`，经 `AgentCore` 的可选构造参数注入 `LLMSession.ToolGate`：声明期把关闭的工具挡在 `request.Tools` 之外，执行期 `AgentActionExecutor` 再拒绝一次（回 `[Tool Error]`）。Inspector 的「扫描并同步工具列表」由 `AgentToolScanner` 在编辑期登记引用 `LLM.Runtime` 的程序集后枚举 `[AgentTool]` / `[AgentAction]`；`AgentProfile_SO` 不含工具字段，能力按实例而非 Profile。沙盒等不传门的入口保持全部可用。原生 `UnityEditor` 实现，不依赖 Odin。

**全局与成员工具（对齐 Mu）** — 静态 `[AgentTool]` 是**全局工具**，与全局 `[AgentAction]` 一起默认全部列在 Inspector，按条目开关（覆盖式）决定谁进请求；`[AgentTool]` / `[AgentAction]` 的**实例方法**挂在 `AgentHost` 物体层级上时是**成员工具/动作**，由宿主 `Activate` 时扫描层级、按 agent 私有收集进 `AgentToolSet`，执行时反射打在实例上，不进全局注册表。执行路由：成员动作（打实例并 await）→ 成员工具（打实例）→ 全局动作（默认 `ReflectionActionRunner`）→ 全局工具。Inspector 分三组：动作 / 公共工具 / 本体工具。

**成员工具快速添加** — 成员工具/动作要靠组件承载，Inspector 的「快速添加成员工具/动作组件」下拉按**工具/动作名（声明它的组件类名）**逐条列出候选，如 `move_to（AgentNavigationTools）`（`AgentToolScanner.CollectMemberProviders`：`LLM.Runtime` 自身与所有引用它的程序集里，MonoBehaviour 上的每个 public 实例 `[AgentTool]`/`[AgentAction]` 方法摊平成一条，编辑器与测试程序集除外；本物体层级含子物体已经提供的名字不再列出，判定口径与运行期 `AgentToolSet.Collect` 一致）。选中即 `Undo.AddComponent` 把声明它的组件挂到 `AgentHost` 所在物体并自动跑一次同步，新条目随即出现在「本体工具 / 动作」组——同一组件的多个方法会一起进来，因此它们的候选项也同时消失。框架自带的候选是 `AgentTransformTools`、`AgentNavigationTools`（后者带 `[RequireComponent(NavMeshAgent)]`，挂载时连带补上）与 `AgentAnimationTools`（`get_animation_state`/`list_animation_states` + `play_animation_state`/`set_animation_parameter`/`set_animation_speed`，见 `Docs/agent-animation-actions-design.md`）。注意动画这套是**两件事**：下拉只会带出工具组件，真正的后端驱动（`Learn.AgentAnimation` 里的 `AgentAnimatorDriver` 或 `AgentAnimGraphDriver`）与 `AgentAnimationWhitelist` 不声明成员工具，因此**不进下拉，要手挂**；工具只认 `IAgentAnimationDriver`，驱动缺席时五条成员统一回"未挂载动画驱动"。`AgentTimeTools.get_current_time` 是 static 全局工具，不需要挂也就不进候选。只在编辑模式画这个按钮：Play 模式挂上去既不落盘、也进不了 `AgentCore` 构造时已冻结的声明表；Prefab 资产态在预览场景里走 Undo 会报错。

**内置通用能力** — 全局 `get_current_time`；成员 `get_agent_transform` / `get_navigation_status` / `move_to` / `move_to_player` / `stop_navigation`；记忆动作 `write_fact` / `forget_fact` / `search_past_conversation`。`move_to_player` 找 tag 为 `Player` 的对象、把它脚下投影到 NavMesh、沿"玩家→本体"方向退回 `playerStopDistance`（默认 1.5 米）站定；目标点吸附半径由 `destinationSnapRadius`（默认 2 米）控制。事实槽每轮全量注入 system，模型不需要列举工具；`search_past_conversation` 只搜当前请求历史窗口之外的旧轮次与本 session 压缩归档，不包含向量检索。

**Inspector 查看持久化记忆** — `AgentHost` 的 Inspector 有一块只读「持久化记忆」区：当 `LLMGlobalConfig` 的 `AgentStateStore = Prefs` 且该实例有稳定 `instanceId` 时，按存档键 `{ProfileKey}#{instanceId}`（运行中直接用 `Core.SessionId`）从 Prefs 读回事实槽并列成 `key：value`，供调试读档。读取逻辑在 `LLM.Editor.AgentStateReader`，与运行期 `PrefsAgentStateStore` 同一条路径；前置条件不满足时以提示代替（未启用持久化 / ProfileKey 空 / 临时实例不落盘）。只读，不含对话历史与编辑。

三个注入点：
- `ToolExecutor` — 换掉 tool_call 的落地方式（异步动作、拦截、超时都在这里），`ToolExecutionResult.AbortTurn` 为真则本轮不再发下一次请求，且**同批剩余的 `tool_call` 不执行、不回填**。
- `ExtraTools` — 注册表之外的工具声明（内核的 `[AgentAction]` 走这里，绝不进 `ToolRegistry`）。追加时 `BuildRequest` 会先复制 `ToLLMTools()` 返回的缓存 List，避免污染注册表。`EnableTools = false` 时两者一并屏蔽。
- `AskAsync(..., writeHistory: false)` — 由调用方在拿到结果后自己决定写不写历史（内核要按代际判 `Stale`）。无工具往返次数总闸（ADR-023）。

**系统提示词注入** — `LLMRequest.SystemPrompt`，拼装时固定为第一条 `system` 消息。

**上下文注入** — `LLMRequest.AddContextBlock()`（或 `LLMSession.AddContext` 长期注入、`AskAsync(ephemeralContext)` 单轮注入），按顺序拼在系统提示词之后，顺序稳定以最大化 provider 前缀缓存命中。

**阈值控制与压缩** — `CompressionConfig_SO`：
- `ContextWindowTokens > 0` 走 token 模式，按 `SoftCompactRatio` / `CompactRatio` / `ForceCompactRatio` 三级阈值；`= 0` 退回轮数模式（`SlidingWindowRounds × CompressThresholdMultiplier`）。
- token 数是估算值（CJK 约 1 字/token，其他 4 字符/token），并由响应里的真实 `prompt_tokens` 通过 `FeedActualTokenCount` 校准。
- 折叠区从新到旧按 `TailTokenBudget` 保留尾部，头部"摘要 + 首轮短提问"永久锚定；`MinFoldTokens` 以下不做无效折叠；连续两次压缩后置 `compactStuck` 暂停自动压缩。
- token 模式下的软阈值（`SoftCompactRatio` ~ `CompactRatio` 之间）不做摘要，只跑 `PruneStaleLargeContent()`——把折叠区里超过 `MinPruneBytes` 的旧回复换成占位符，零成本先省一轮。⚠ 该修剪只在 token 模式生效，轮数模式下是空操作。
- 摘要由 LLM 生成（`EnableLLMCompression`），**只试一次**，超时/失败即退化为机械折叠；想用小模型跑压缩，在 `CompressionProviderName` 里填它的注册名（留空 = 沿用默认 Provider）。原始轮次可选归档到 `persistentDataPath/LLMArchive/<session>/`。

## 设计文档

- `Docs/agent-kernel-design.md` — 上层 agent 内核的形态、规格与默认值总表（实现进度看该文档末尾的勾选清单）
- `Docs/agent-host-design.md` — AgentHost 场景宿主的形态
- `Docs/agent-tool-gating-design.md` — AgentHost 按实例门控可用工具的形态与 ADR-016
- `Docs/agent-tool-model-design.md` — 全局静态 + 成员实例两分模型与 ADR-018
- `Docs/agent-animation-actions-design.md` — 动画动作（Animator / AnimGraph 双后端）的接口形状、词表策略与实测后端能力，见 ADR-021
- 交接快照统一放仓库根 `Docs/HANDOFF.md`（全工程唯一一份），插件目录不再单独维护
- `Docs/decisions/ADR-001~011.md` — 逐条决策与其拒绝理由（内核位置 / 宿主接口 / 驱动模型 / 工具全自主 / 记忆边界 / 成本与缓存 / 异步动作 / 先说后做 / 长动作与打断 / 跨 agent 资源锁 / 记忆私有与存储 key）
- 改架构前先补 ADR，不要只改代码

## 已知取舍

- 一个 `LLMSession` 同一时刻只允许一个进行中的请求，重复调用直接抛异常。
- `[Tool]` 本身只支持同步静态方法；异步动作走 `LLMSession.ToolExecutor` 注入点（注入点已落地，带循环检测与异步动作的执行器见 `Docs/decisions/ADR-007`，内核 B 步实现）。
- 流式过程中不做增量 JSON 校验，`ToolCallAssembler` 在参数闭合前不会产出调用，因此工具结果一定晚于最后一个 token。
- 长回复的增量拼装用 `System.Text.StringBuilder`（跨 chunk 累积的实例字段），`Utf16ValueStringBuilder` 是 ref struct 不能做字段、也没有 `Remove`，所以这几处是项目 ZString 规则的**成文例外**：`AgentCore.turnText`、`LLMSession.answerBuilder`、`OpenAIProvider.SSEDownloadHandler._buffer`、`AgentToolCallAssembler`。除此以外的拼接一律 ZString。
