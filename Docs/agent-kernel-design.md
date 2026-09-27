# Agent 内核设计（LLM.Runtime / Agent）

> 设计已定稿。实现进度见文末「实现进度」，代码起步前本节全部为规格。逐条决策理由见 `decisions/ADR-001 ~ ADR-011`。

## 目标

在在线 API 层（`LLMSession`：流式 + 工具往返 + 上下文压缩）之上，给"游戏内 NPC"和"UI 对话助手"提供**同一套** agent 运行内核，负责：触发与排队、世界状态注入、异步动作执行、循环与失控保护、轮次可观测。

## 非目标（明确不做，实现时不要顺手加）

| 不做 | 原因 |
| --- | --- |
| RAG / 向量检索 / embedding | 已舍弃，基建不存在（ADR-005） |
| 候选集门控（可用动作过滤） | 选择全自主，靠循环检测兜底（ADR-004） |
| 并发数 / 每分钟轮数 / token 预算类运行时闸门 | 成本只做统计（ADR-006） |
| 反思式记忆、重要性打分、记忆桥 | 上一代因此膨胀被删（ADR-005） |
| 情绪/关系图/八卦事件总线/对话树 | 属世界与玩法层，不下沉进内核（ADR-002） |
| 模型自动降级路由 | 只保留主/fallback 双 Provider（ADR-006） |
| 录制回放式契约测试 | 提示词未稳定前不做 |
| 跨 agent 共享记忆 / `scope = world` | 记忆全私有，共同事实走世界快照（ADR-011） |
| 多键原子锁、优先级锁队列 | 一个动作一把锁 + `LockWaitSeconds` 兜饥饿（ADR-010） |

## 分层

```
宿主（NPC MonoBehaviour / 助手 UI / 编辑器沙盒）
  │  实现 IWorldContextProvider / IAgentOutput / IAgentActionRunner（按需子集）
  ▼
AgentCore（每个 agent 一个实例，由宿主持有，不是单例）
  ├── AgentProfile_SO        人设与参数
  ├── AgentMemory            事实槽 + 写/删/搜索三个记忆动作（[AgentAction]）
  └── AgentTrace             轮次事件 → 沙盒窗口 / 日志窗口
  ▼
LLMSession.ToolExecutor ← AgentActionExecutor（实现 IToolExecutor，查 AgentActionRegistry）
                        ← SyncToolRegistryExecutor（默认，包现有 ToolRegistry.Execute）
  ▼
LLMSession（既有）→ LLMDispatcher → OpenAIProvider → SSE
  ├── ContextManager / ContextCompressor（既有，压缩侧改动见 ADR-006）
  └── ContextPruning.SnipStaleToolResults（已被 LLMSession 调用）／ContextManager.PruneStaleLargeContent（A 步已接到软阈值，仅 token 模式生效）
```

## 类型定义

```csharp
// AgentInterfaces.cs
public interface IWorldContextProvider { string GetCoreSnapshot(); }   // 只放低频值，见 §注入排版

public interface IAgentOutput
{
    void OnToken(string delta);             // 实时转发，但内核逐条校验代际，作废轮不再上屏
    void OnTurnFinished(string answer, EAgentOutcome outcome);
}

public interface IAgentActionRunner
{
    UniTask<AgentActionResult> RunAsync(string actionId, string argsJson,
        AgentActionContext ctx, CancellationToken ct);
}

public enum EAgentOutcome { Completed, Stale, Timeout, Failed, LoopAborted }
// ADR-023：已删除 ToolRoundsExhausted / MaxToolRounds / LastTurnExhaustedRounds
// 防换词狂搜 = LoopGuard NameCap（PerNameToolCallLimit / NameRepeatLimit）；成本 = 墙钟

public readonly struct AgentActionResult
{
    public readonly bool Ok;          // false → Content 作为失败原因回填，前缀 [Action Failed]
    public readonly string Content;   // 成功=结果文本，失败=原因；不单设 Error 字段
}

public sealed class AgentActionContext      // 不携带任何宿主具体类型（ADR-002）
{
    public AgentCore Agent;
    public AgentProfile_SO Profile;
    public string SessionId;
}

// AgentCore.cs
public void Trigger(string input);                  // 玩家/用户输入
public void Notify(string worldEvent);              // 与 Trigger 共用 pending 槽与同一作废规则；
                                                    // 起轮时 userText = "【事件】" + worldEvent，故会作为一轮 user 消息入历史

// AgentCore 归属与构造（不是单例，宿主持有；instanceId 走构造参数，不挤进三窄接口）
public AgentCore(AgentProfile_SO profile, string instanceId, IWorldContextProvider ctx,
                 IAgentOutput output, IAgentActionRunner runner = null)
// profileKey 从 profile.ProfileKey 读，不单列参数（避免两处真值打架）
// 时钟集中在一个可替换的 internal 静态委托字段上（不是计算型属性——反射换不掉 getter）：
internal static System.Func<float> NowSeconds = ReadUnscaledTime;
private static float ReadUnscaledTime() => Time.unscaledTime;
// EditMode 单测用反射把 NowSeconds 整体换成自己的方法组，即可推进墙钟、锁等待与动作超时
// （LLM.Tests.Editor 是独立程序集且 LLM.Runtime 没有 InternalsVisibleTo，故走反射；不新增时钟接口）
// ⚠ 三类超时一律实现为「拿 NowSeconds 与截止时刻比较，到点主动 cts.Cancel()」，
//   不得用 CancellationTokenSource.CancelAfter —— 那是线程池真实计时器，假时钟推不动，
//   结果是「单测能过、真链路靠运气」或干脆没法测。

// AgentMemory.cs（事实槽部分，内存态；落盘由宿主负责）
public string ExportJson();                         // 随宿主存档序列化
public void ImportJson(string json);
```

`[AgentAction]` 与 `[Tool]` 一样**必须是 `public static` 方法**——`ToolRegistry` 的扫描用的就是 `BindingFlags.Public | BindingFlags.Static`，写成实例方法会被静默跳过。两者互斥：一个方法只标一个属性，避免同名工具重复注册。

**`[AgentAction]` 的签名约定**：`public static UniTask<AgentActionResult> Xxx(上下文, 参数...)`。

- 第一个参数**可选**，类型必须是 `AgentActionContext`，由 `ReflectionActionRunner` 注入，**不出现在 JSON schema 里**——生成 schema 时跳过所有非简单类型参数（只认 `int/float/bool/string` 及其可空形式），否则模型会看见一个假的 `ctx` 参数并尝试填它。
- 返回值固定 `UniTask<AgentActionResult>`；返回别的类型时在注册阶段报警并跳过，不留到运行时炸。
- **schema 由 `AgentActionRegistry` 自己生成，不复用 `ToolRegistry.BuildParameterSchema`**：那方法虽是 `private static string BuildParameterSchema(MethodInfo)`，但它对方法的**全部**参数一律生成属性、没有跳过复杂类型的口子，且 `GetJsonType` 会把不认识的类型兜底成 `string`——照搬正好会造出上面禁止的假 `ctx` 参数。内核自带一份 30 行的生成器，只认简单类型，并支持 `required` 列表。
- **`say` 是内核注入的伪参数**（ADR-008）：生成 schema 时对 `Idempotent = false` 的动作自动追加 `say` 并写进 `required`；`AgentActionExecutor` 从 `argsJson` 里读出 `say` 后**先移除再交给 runner**，否则 `ReflectionActionRunner` 会因参数个数不匹配炸掉。**`say` 是保留参数名**：动作方法自己声明 `say` 参数时，注册期 `Log.Error` 并跳过该注册——否则 executor 一律剥离会让它永远收不到值。
- **`say` 必须从 LoopGuard 签名里剥掉**（见 §循环检测），否则模型换一句措辞就绕开非幂等拦截。

**声明与执行分离**（职责定死，不留两条路）：

- **声明**：`[AgentAction]` 的元数据由 `AgentActionRegistry` 产出 `LLMTool` 列表，A 步给 `LLMSession` 加 `public List<LLMTool> ExtraTools { get; }`，在 `ToolRegistry.ToLLMTools()` 之后追加进 `request.Tools`。**追加前必须复制**：`ToLLMTools()` 返回的是缓存的同一个 List 实例（`cachedLLMTools`），就地 `Add` 会污染注册表缓存并让动作声明每轮重复累积。正确写法是 `request.Tools = new List<LLMTool>(ToolRegistry.ToLLMTools())` 再 `AddRange(ExtraTools)`。`EnableTools = false` 时 `ToLLMTools()` 与 `ExtraTools` **一并**不追加（否则关不掉动作）。**action 绝不能注册进 `ToolRegistry`**：那边 `ValidateMethod` 要求返回值是 `string`，异步动作返回 `UniTask<AgentActionResult>` 会在扫描期就被跳过；若绕过校验混进去，`ToolRegistry.Execute` 会 `Invoke` 出一个被丢弃的 `UniTask`，正是 ADR-007 拒绝的幽灵副作用。
- **裁决与路由**：`AgentActionExecutor`（实现 `IToolExecutor`）只做三件事——LoopGuard 裁决、包 per-action 超时、路由：`actionId` 命中 action 表交给 `IAgentActionRunner`，否则回落 `SyncToolRegistryExecutor` 同步执行普通 `[Tool]`。`Idempotent=false, Repeatable=true` 保留必填 `say`，但跨轮不占 session 一次性配额（ADR-027）。
- **反射在 runner 侧**：AOT 提供默认实现 `ReflectionActionRunner`（按 actionId 调 `public static` 方法并 await），宿主可整体换成自己的实现；宿主没有动作时注入 `NullActionRunner`（返回"该 agent 不支持动作"），内核不写 null 分支。
- `IToolExecutor.ExecuteAsync` 刻意不带 `tool_call id`，也不带 ctx——回填 `tool_call_id` 是 `LLMSession` 的职责；`AgentActionExecutor` 在**构造时**持有 `AgentCore`（借此拿 `IAgentOutput` 播 `say`、拿 `AgentMemory`、拿本轮代际），`AgentActionContext` 每次调用新建。
- `LoopAborted` 由 `AgentCore` 自己的 LoopGuard 裁决得出（格式错 Abort 等）；幂等重复类封顶 Block（ADR-023），不再 Abort。

**记忆动作必须是 `[AgentAction]` 而不是 `[Tool]`**：静态 `[Tool]` 拿不到当前 agent 上下文，多个 NPC 会串记忆。`write_fact` / `forget_fact` / `search_past_conversation` 一律经 `AgentActionContext.Agent` 定位实例，并显式 `Idempotent = true`。事实槽每轮全量注入 system，因此不再提供 `list_facts`（ADR-025）；`search_past_conversation` 只检索当前请求历史窗口之外的旧轮次与本 session 压缩归档，不搜索已注入事实；排序和 2 MiB 扫描边界见 ADR-020。内置记忆动作由 `AgentCore` 构造时登记，宿主不必手工注册。

## 注入排版规则

为了保住 provider 的前缀缓存（ADR-006），system 消息只放**整个会话不变**的内容，其余全部走每轮的 `ephemeralContext`：

```
SystemPrompt   = 人设 + 规则（不变）
tools 字段      = 工具与动作声明（注册表不变则不变，由服务端渲染进 prompt）
ephemeralContext（每轮重拼，顺序固定）：
  1. 事实槽整块        低频变
  2. 核心快照          低频变：身份、当前目标、关系等级（禁止放时间/坐标/周围实体）
  3. QueryableHint     常量（profile 配了才注入）："你可以查询：位置 / 背包 / 任务 / 周围实体"
```

每轮都变的东西只能来自 observe 类工具的调用结果，不进任何注入块。

注意这三段并不是独立消息：`LLMRequest.BuildSystemContent()` 会把它们拼到**同一条 system 消息的尾部**。省缓存靠的是「system 消息前缀逐字节稳定」，不是消息级隔离——所以 ADR-006 说的「别往 system 塞变化内容」与此并不矛盾：人设段在最前，后面的变化只会截断命中点之后的前缀。

**不能用 `LLMSession.AddContext()`**：它只有追加与整体 `ClearContext()`，事实槽在 `write_fact` 之后无法原位更新。所以三段全走 `AskAsync(ephemeralContext)`，由 `AgentCore` 每轮重拼。这段本来就因快照而每轮重拼，事实槽变化不会额外破坏 system 段与工具声明段的前缀。

## 循环检测规格（LoopGuard）

签名字段：

| 键 | 语义 | 用途 |
| --- | --- | --- |
| L1 `name` | 同工具反复调但参数在变 | 只作 trace 遥测，不驱动裁决 |
| L2 `name + 规范化 args`（剥空白、键排序，**并剔除内核注入的 `say` 字段**） | 同名同参 = 真重复 | 驱动阶梯 |
| L3 `L2 + 结果哈希` | 重复且结果相同 = 零新信息 | 命中即跳过 WARN，直接进 BLOCK 档 |

阶梯（`N = 生效的 RepeatLimit`，按 L2 计数）：

| 第几次命中 | 行为 |
| --- | --- |
| 1 … N | 正常执行 |
| N+1 | 执行，结果尾部追加 `[loop-warning] 你已用相同参数调用 X N 次…` |
| N+2 | 不执行，返回 `[Blocked] 重复调用已拦截` |
| N+3 及以后 | `AbortTurn`，本轮结束，`outcome = LoopAborted` |

L3 是插入规则：任一次执行后，若同一 L2 签名的结果哈希与上次相同，该签名立刻按 BLOCK 档处理，不再走 WARN。

⚠ 算签名前必须删掉 `say`：它是模型自由措辞，留在 args 里等于每次调用签名都不同，「非幂等阈值恒 1」会被一句话绕开，重复扣钱的洞重新打开。`ReflectionActionRunner` 同样收不到 `say`。

配置语义（`[Tool]` / `[AgentAction]` 上的字段）：

- `RepeatLimit`：**0 = 继承** profile 的 `GlobalRepeatLimit`；`-1 = 关闭检测`。默认 0，即未配置的工具一律受全局阈值管（若反过来用 `-1` 当默认，C# 字段默认值 0 会让未配置工具静默关掉检测）。
- `Idempotent`：默认值按属性区分——`[Tool]` 默认 `true`（本项目当前没有任何 `[Tool]`，此默认只是给将来留的口径），`[AgentAction]` 默认 `false`（动作按有副作用对待）。`Idempotent = false` 的工具**生效阈值恒为 1，忽略 `RepeatLimit` 与 `GlobalRepeatLimit`**：同一 L2 签名第二次起一律 BLOCK（跳过 WARN 档），**不提供关闭开关**。
- 优先序定死：`Idempotent = false` 的分支必须排在 `RepeatLimit < 0`（关闭检测）早退**之前**——非幂等工具即使显式写了「关闭检测」也照 BLOCK，阈值恒为 1，与 `RepeatLimit`、`GlobalRepeatLimit` 两档配置都无关。这条不冗余：它钉的是判定顺序，防的就是实现者把早退写在前面，从而「静默放行重复扣钱」。

作用域：

- L2 黑名单**仅本轮有效**（下一轮状态可能已变，同名同参可能是合理重试）。
- 但 **`Idempotent = false` 的签名计数按 session 累计**，不随轮清零，只在重载存档时归零。原因：轮内清零会让 `trade_player` 在玩家连着说两轮时各扣一次钱，这是 ADR-004 里"重复执行有副作用的动作"的真实漏洞。
- **只有真正执行过的调用才计数**：`AgentActionResult.Ok == true` 才累加 session 配额；超时、异常、被叫停、锁等待超时都不累加。否则一次排队失败就会把非幂等动作的唯一配额吃掉，同参数永久 `[Blocked]`，NPC 卡在死锁里。
- 非幂等工具**跳过 WARN 与 HARD 两档**：同一签名第二次起 BLOCK 并在 session 内拉黑（阈值恒为 1）。不走 HARD 是因为作废整轮会连已生成的正常文本一起丢，代价大于只拦这一笔有副作用的调用。
- **批内也要跳**：模型可以在一条 assistant 消息里并行回多个 `tool_call`。`AbortTurn` 置位后，同一批里剩余的调用一律**不执行、不回填**（该轮整体丢弃，补帧无意义）；否则会出现「轮已判作废却仍然扣钱、且模型永远收不到结果」。

**HARD 退出不补帧**：`AbortTurn` 让 `LLMSession` 立刻结束往返且**不再发请求**，该轮结果整体丢弃、不写历史。本轮的 tool 消息从不进 `ContextManager`（`AddRound` 只存 user/assistant 文本），所以不存在需要补 `tool_call_id` 的后续请求 —— 补帧在这里是死代码。参考项目 hippy-agent 的 `DanglingToolCallMiddleware` 之所以必要，是因为它把含 tool 消息的历史持久化并在下一轮继续发送，我们的模型不是这样。

对照：**BLOCK 必须回填**（该轮还会继续发请求，缺一条 tool 消息就会 dangling）；HARD 不再发请求，所以不需要。

## 失败语义表

| 前缀 | 生成方 | 本轮 L2 计数 | session 非幂等配额 | 同轮后续往返可见 |
| --- | --- | --- | --- | --- |
| `[Action Failed] timeout` | 动作超时 | 是 | **否** | 是（该轮正常收尾时） |
| `[Action Failed] {异常}` | 动作抛异常 | 是 | **否** | 是 |
| `[Action Failed] {Content}` | 动作执行成功但语义失败（余额不足等，`AgentActionResult.Ok = false` + Content） | 是 | **否** | 是 |
| `[Blocked] …` | LoopGuard BLOCK 档（幂等 N+2、非幂等第 2 次、或 L3 命中） | 是（自计数） | — | 是 |
| `[Tool Error] Unknown tool: x` | 既有 `ToolRegistry` | 是 | 否 | 是 |
| `[Action Failed] cancelled by new input` | `AgentCore` 叫停可打断动作（ADR-009） | 否 | 否 | **是**（同批必须回填以配对；整轮产出仍按 `Stale` 丢弃） |
| `[Action Failed] 目标被占用（排队 Ns 未获得锁）` | `AgentResourceLocks` 等待超时（ADR-010） | 否 | 否 | 是 |
| `（注意：你已对玩家说过「…」但未成功…）` | 承诺表指令，追加在失败回填尾部（ADR-008） | 不单独计数 | — | 是 |

> 「本轮 L2」管幂等工具的同参循环；「session 非幂等配额」**只在 `AgentActionResult.Ok == true` 时扣**（见 §作用域）。两列必须分开，否则一次超时就废掉该动作的终身配额。

> 轮次收尾时 `AddRound` 只写一条 user/assistant 文本，**tool 消息从不入历史**。
>
> **入不入历史只由 `EAgentOutcome` 决定**：`Completed` / `Timeout` / `Failed` / `LoopAborted`（LoopAborted 仍不写）→ 见下；`Stale` / `LoopAborted` → 不写。`AskAsync` 抛异常的分支必须由 `AgentCore` 自己补一次 `Context.AddRound(input, 最终上屏文本)`，否则玩家那句话就此消失、NPC 下一轮像没听见。无正文时：仅 `Completed` 回空串；Timeout/Failed 等走 `FallbackLines`（ADR-023）。

语义失败必须走 `AgentActionResult.Ok = false`，否则模型无法区分"做完了"和"没做成"。

## 一轮时序

```
Trigger(input)
 ├─ 无在飞轮：turnGeneration++，建轮级 CTS（额度 = TurnDeadlineSeconds，见下「累计计时」）
 ├─ 有在飞轮：先 cancel 在飞的可打断动作（ADR-009）；不取消在飞 HTTP；
 │            turnGeneration++，input 覆盖 pending 单槽，等其自然收尾
 ├─ snapshot = 核心快照；ephemeral = 事实槽 + 快照 + QueryableHint
 ├─ session.AskAsync(input, onChunk, ephemeral, writeHistory: false, ct)
 │    └─ 每次往返之间，AgentActionExecutor 五步：
 │         1. LoopGuard.Decide(剥离 say 后的 L2/L3) → 放行 / WARN 追加 / BLOCK / HARD(置 AbortTurn)
 │         2. AgentResourceLocks.TryAcquireAsync(key, LockWaitSeconds, ct)
 │            → 失败则回填「目标被占用」并跳过 3-5（此时 say 未播，不涉及承诺收口）
 │         3. 取出并移除 say → 有正文则不播，否则 OnSay(actionId, say) 独立播出 + 登记承诺
 │         4. runner.RunAsync(actionId, args−say, ctx, actionCts)   ← 普通 15s / LongRunning 60s
 │         5. lease.Dispose()（幂等，finally 与轮级清理两处都调）；Ok=true 才计非幂等配额；失败走承诺收口（ADR-008）
 │      累计计时：墙钟不是单个 CancelAfter，而是「额度扣减」。进 LongRunning 动作前记下已耗、
 │      给动作独立 CTS、动作返回后按剩余额度重建轮级 CTS —— CTS 无法暂停，只能这么做才叫「不计入」
 ├─ 转发每个 chunk 前查代际：仍匹配才 onChunk 上屏，一旦被新输入顶掉就停止转发（宿主表现为半句收住）
 ├─ 代际小于当前 → OnTurnFinished(已攒文本, Stale)，跳过下面那次 AddRound，起 pending 那一轮
 │    （`say` 一经 OnSay 播出即视为已上屏，宿主只能收尾承诺、不得清空；
 │     被叫停的轮次承诺未兑现 → 内核立即播一次 FallbackLines 兜底，见 §先说后做）
 ├─ 代际匹配   → session.Context.AddRound(input, answer)
 ├─ 正常完成 → OnTurnFinished(answer, Completed)
 ├─ AbortTurn 为真 → 立即结束往返且不再发请求；该轮丢弃（不写历史），AgentCore 判 LoopAborted
 └─ 墙钟超时 / Provider 全挂 / 最终文本为空（含 content_filter）→
    AgentCore 自己从 onChunk 攒下的文本非空则用之
    （取消时 AskAsync 只抛异常不给返回），否则取 FallbackLines 之一；
    两者皆空 → 回传空文本（宿主自行决定沉默或给本地提示），不重试
    OnTurnFinished(..., Timeout | Failed)
```

`Stale` 轮不写历史**不能靠"调用时判断代际"**——调用瞬间代际必然匹配，判定发生在 `AskAsync` 返回之后。做法：`LLMSession.AskAsync` 新增 `bool writeHistory = true`（现在它无条件 `AddRound`），内核一律传 `false`，由 `AgentCore` 在确认代际匹配后自己调 `Context.AddRound`。属 A 步范围。

## 先说后做（ADR-008）

一句话：**非幂等动作的 schema 自动追加必填 `say`，executor 在执行前播出去**；播报登记进本轮承诺表 `List<(string actionId, string say)>`（`AgentCore` 私有，成功回填即移除、轮结束清空）。失败的收口分两条：

| 情形 | 谁负责补话 |
| --- | --- |
| 本轮还会继续发请求（普通失败） | 不播模板，只在失败回填尾部注入强制指令，交给模型自己圆 |
| 本轮产出已无意义：墙钟超时 / 往返耗尽 / HARD / Provider 全挂 / **被新输入叫停而判 `Stale`** | 内核立即播 `FallbackLines` 之一 |

`say` 为空 → 直接静默执行（不加 actionId 模板表：ADR-007 已拒绝按 actionId 建覆盖表，且 Unity SO 存不了字典）。"说了没做成"的兜底台词复用 `FallbackLines`，不另开池子。

## 动作时长与打断（ADR-009）

| 属性 | 默认 | 作用 |
| --- | --- | --- |
| `LongRunning` | false | 走 `LongActionTimeoutSeconds`（带路、演出、移动） |
| `Interruptible` | true | 新输入进来时被 cancel，回填 `[Action Failed] cancelled by new input` |

- 墙钟 `TurnDeadlineSeconds` 覆盖「LLM 往返 + 锁等待 + **非 `LongRunning` 动作的执行时间**」；只有 `LongRunning` 动作的执行期不计入（实现是额度累计扣减，不是暂停 CTS，见 §一轮时序），否则 90s 墙钟会误杀 60s 带路。
- 契约：所有动作必须响应 `ct`；不响应 `ct` 的不得标 `Interruptible = true`。
- ADR-003 的"不抢占"仅指不 cancel 在飞 HTTP，动作可被叫停。

## 跨 agent 资源锁（ADR-010）

LoopGuard 的拦截是单实例状态，两个 NPC 抢同一目标拦不住。`AgentActionExecutor` 执行前按动作声明的 `LockKey` 向 `AgentResourceLocks` 取锁，**同 key FIFO 排队**，执行后释放。

五条硬约束：一个动作只允许一把锁（多键原子操作见 §非目标）；`LockWaitSeconds` 上限（默认 5，扣墙钟额度；0 = 不等待直接失败）；`LockKey` 是 args 模板（`item:{itemId}`），引用不存在的键注册期 `Log.Error`；键中必须含 `:`（`资源类型:实例 id`），否则注册期 `Log.Error`；agent 销毁、判 `Stale`、墙钟到点、被叫停时必须退队/`Dispose`。

释放只走 `LockLease.Dispose()`（幂等、带持有者身份），**不提供 `Release(key)`**：executor 的 `finally` 与轮级清理是两个释放点，无身份的释放会让后者放掉下一个持有者的锁。**锁等待超时不计入 LoopGuard 计数**（动作根本没执行），模型可以再试；防无限重试由 NameCap 与墙钟封顶（ADR-023）。

`LockKey` 是**对 args 的模板**而非字面量：写成 `{argName}` 形式（如 `item:{itemId}`、`player:{playerId}`），executor 用本次 `argsJson` 插值出真实键。模板里引用了 args 没有的键 → 注册期 `Log.Error` 报警；插值结果为空 → 视作不加锁并 `Log.Warning`。粗粒度键禁令也要可判定：**键中不含 `:` 时注册期 `Log.Error`**。

`LongRunning` 与 `LockKey` 同时出现属可疑（带路独占的是 NPC 自己，该锁 `npc:{id}` 而不是玩家或商店的键），注册期 `Log.Warning`，避免排队方被挂满 60s。

## 标识与存储（ADR-011）

- `sessionId = {profileKey}#{instanceId}`：`profileKey` 是 `AgentProfile_SO` 上的 string（**禁止含 `#`、`/`、`\`**，否则 sessionId 反解歧义、目录串档）；**留空时自动生成**（ADR-012：按当时人设 FNV-1a 派生 + 项目内/会话内查重，生成后与人设解耦，改人设不变更 key）；`instanceId` 由宿主存档提供稳定值，缺失时 `AgentCore` 构造参数为 null → 用 `Guid.NewGuid()` 生成并暴露。禁止只用 profileKey（同种 NPC 会串档）。
- 事实槽与活动历史通过存储接口落盘；`ContextManager` 另外把被折叠的原始轮次写入 `persistentDataPath/LLMArchive/{sessionId}/`。该归档既用于诊断，也作为 `search_past_conversation` 的冷数据源；`ClearHistory()` 会同步删除当前 session 归档（ADR-020）。
- 记忆一律私有；跨 agent 的共同事实由 `GetCoreSnapshot()` 以只读世界真相提供，不做共享作用域。

## 时钟泵

`AgentCore` 在 Play 模式构造末尾把自己挂到 `MonoRunner.GetInstance().AddListener(MonoRunner.EUpdateType.Update, Tick)`（`EUpdateType` 是 `MonoRunner` 的嵌套枚举），`Dispose()` 里摘掉。泵只做一件事：每帧调 `Tick()`，让墙钟额度、动作硬超时、锁排队到期有机会落地——内核刻意不用 `CancellationTokenSource.CancelAfter`（线程池真实计时器，假时钟推不动），所有超时都是"拿 `NowSeconds()` 与截止时刻比较后主动 Cancel"，所以这个泵是它们唯一的驱动源。

`MonoRunner` 是运行期 `MonoSingleton`，**非 Play 模式不 tick**，所以编辑器沙盒不注册；`AgentCore.Tick()` 留成 public 以便 EditMode 单测手工驱动（`FakeRunner` 就在调它）。

⚠ `MonoRunner.actions` 是 `Dictionary<EUpdateType, HashSet<Action>>`，会强引用注册的委托：**agent 销毁时必须 `RemoveListener`**，否则会在已销毁的死 agent 上继续泵。列为 B 步测试项。

> 这里原来还挂着"心跳自动起轮"（每 N 秒发一句"【心跳】一段时间没有发生对话。"让模型自由回复）。已整条删除，见 [ADR-015](./decisions/ADR-015-drop-heartbeat-auto-turn.md)：起轮入口只有 `Trigger` 与 `Notify` 两个，NPC 的主动性归宿主的世界调度。

## 默认值总表

`[Range]` 只是配置面板护栏，不是调度器。但下面标「运行时截断」的 5 项确实会在运行期掐断行为。

| 参数 | 区间 | 默认 | 档位 | 备注 |
| --- | --- | --- | --- | --- |
| `TurnDeadlineSeconds` | 10–300 | 200 | 运行时截断 | 一轮墙钟额度（ADR-023 已无工具次数总闸）；见下方自洽规则 |
| `PerNameToolCallLimit` | 0–12 | 4 | LoopGuard NameCap | 同工具名本轮次数（换参也计） |
| `GlobalRepeatLimit` | 1–5 | 2 | 运行时截断 | 同参 L2 未单独配置时生效 |
| `ActionTimeoutSeconds` | 3–60 | 15 | 运行时截断 | 普通动作硬超时 |
| `LongActionTimeoutSeconds` | 15–180 | 60 | 运行时截断 | 仅 `LongRunning`；执行期不扣墙钟额度 |
| `LockWaitSeconds` | 0–30 | 5 | 运行时截断 | 锁排队上限，扣墙钟额度；0 = 不等待直接失败 |
| `ProfileKey` | string | 空 | 纯配置 | 留空自动生成（ADR-012：人设派生 + 查重，生成后与人设解耦），禁含 `#` `/` `\`，参与 sessionId |
| `ExpectedLlmRttSeconds` | 1–30 | 5 | 纯配置 | 只用于下方自洽校验，运行期不生效 |
| `FactSlotMaxCount` | 10–120 | 60 | 纯配置 | 超出按时间戳淘汰 |
| `FallbackLines` | 最多 5 句 | 空 | 纯配置 | 两用兜底：请求失败/超时的空文本，以及被叫停的未兑现承诺（§先说后做）。空 = 回传空文本 |
| `QueryableHint` | string | 空 | 纯配置 | 非空时固定一行，是 ADR-004 护栏 3 的载体 |

**自洽规则**（profile 校验时断言，默认值必须满足）：

```
TurnDeadlineSeconds 建议 ≥ 8 × (ActionTimeoutSeconds + LockWaitSeconds + ExpectedLlmRttSeconds)（编辑器提示，不是硬闸；ADR-023）
默认：200 ≥ 8 × (15 + 5 + 5) = 200  ✓
出厂资产（Learn/LLM/Configs/test-helper.asset、test-npc.asset）同步提到 200；此前默认 90 配 8 的系数，每次建 agent 都刷一条"墙钟偏紧"，把告警变成了噪音。
`LongRunning` 动作的执行期不扣额度，故不在此式内（见 §动作时长与打断、§一轮时序 的累计计时）。
```

`CompressionProviderName` 不在本表——它属于 `CompressionConfig_SO`（Resources 下的共享资产），按 agent 放进 `AgentProfile_SO` 会跨会话互相污染。

## 实现顺序

| 步骤 | 内容 | 规模 |
| --- | --- | --- |
| A | `LLMSession`：`IToolExecutor` 注入点 + `ToolExecutionResult.AbortTurn`（为真则不再发下一次请求，且**同批剩余 tool_call 不执行不回填**）+ `writeHistory` 参数 + `LastTurnExhaustedRounds` + `ExtraTools`（承载 `[AgentAction]` 声明）；`ToolAttribute`/`ToolRegistry.RegisteredTool` 补 `RepeatLimit`/`Idempotent` 与 `TryGet`（动作属性 `LongRunning`/`Interruptible`/`LockKey` 走 `AgentActionRegistry`，不污染 `ToolRegistry`）；`ContextCompressor` 独立 provider + 尝试次数 2→1；`ContextManager` 软阈值接 `PruneStaleLargeContent`；**流式路径跨 chunk 缓存 usage 后补日志**（见下） | M |
| B | 内核 9 文件 + 单测 + 兜底台词、`say` 注入与播报、承诺表、`Interruptible` 叫停、`LockKey` 模板插值与 FIFO 排队、墙钟额度累计计时 | L |
| C | IMGUI 沙盒窗口 + 请求日志窗口补 `CacheHitTokens` 列 | M |

**A/C 步必须一并处理的既有缺口**（1–3 不补则 ADR-006 的命中率诊断恒为 0；4–5 是接线口径，错了会以为配置生效其实没生效）：

1. `LLMDispatcher.EnqueueStreamAsync` 目前不触发 `OnRequestCompleted`（只有非流式的 `EnqueueAsync` 发日志）。
2. `LLMStreamChunk` 没有 `CacheHitTokens`；且 usage 是独立的 `choices: []` chunk（见 `OpenAIProvider.ParseStreamChunk`），不是收尾那条 `isDone` chunk。流式必须先跨 chunk 缓存 usage、到 `[DONE]` 再统一发一次日志，否则单测能过、真链路恒 0。
3. `LLMRequestLog.CacheHitTokens` 结构体已有、非流式路径已填；缺的是 `LLMRequestLogWindow.LogEntry`/`OnGUI` 没这一列（C 步补）。
4. `ContextManager.PruneStaleLargeContent()` 只在 token 模式（`ContextWindowTokens > 0`）生效，轮数模式下接了也是空操作。
5. ~~`LLMSession.MaxToolRounds` 自身默认仍是 5；`AgentCore` 构造时必须把 `profile.MaxToolRounds`（默认 3）写进 session~~ **ADR-023 已删除该字段与整条缺口**。

## 测试矩阵（EditMode，FakeProvider + FakeRunner，不进网络）

| 用例 | 断言 |
| --- | --- |
| 动作超时 | 回填 `[Action Failed] timeout`，动作本身被 cancel |
| 动作语义失败 | 模型下一轮收到 `Ok=false` 的 Content 前缀 |
| L2 达阈值 | 测试动作显式 `Idempotent = true`（走默认 `[AgentAction]` 会因阈值为 1 直接 BLOCK），FakeRunner 每次返回不同文本（否则第 2 次就命中 L3），第 3 次（N=2 → N+1）结果含 `[loop-warning]` 且动作确实执行了 |
| L3 命中 | 同名同参且结果相同 → 直接进 BLOCK 档 |
| BLOCK | 不执行，返回 `[Blocked]`；该轮继续发下一次请求，且请求里 tool 消息条数 == tool_call 条数（缺一即 dangling） |
| HARD | 置 `AbortTurn` 后 `FakeProvider` 请求数不再增加，**同批剩余 `tool_call` 的 runner 未被调用**，`outcome = LoopAborted`，该轮不写历史（`Context.RoundCount` 不变） |
| 非幂等跨轮 | `Idempotent=false` 的签名第一轮执行 1 次后，同轮或后续任意轮的第 2 次即 BLOCK 且不再执行（阈值恒 1，改 `GlobalRepeatLimit` 不影响） |
| 墙钟超时 | 有累积文本用文本，空则取 FallbackLines |
| 在飞期间新输入 | 旧产出 `Stale` 且**不写历史**，新轮用 pending 输入 |
| 多步工具 | 无 MaxToolRounds；多轮不同工具可跑完，outcome=Completed |
| NameCap 换参 | 同 name 不同 args 超 PerNameToolCallLimit → Warn/Block，文案含强制收口 |
| Provider 全挂 | `outcome = Failed` + 兜底台词 |
| 事实槽 | `write_fact` 后 ephemeral 含该条；超容量淘汰最旧；JSON 导出导入往返一致 |
| 时钟泵反注册 | EditMode 下 `MonoRunner` 不 tick，故断言改为：销毁后反射读 `MonoRunner` 的 `actions[Update]` 集合，计数回退到注册前；再手工调 `AgentCore.Tick()` 不产生任何新起轮（泵只判到期，起轮入口只有 Trigger/Notify） |
| action 声明隔离 | `[AgentAction]` 不出现在 `ToolRegistry.ToLLMTools()` 里，只在 `ExtraTools` |
| 前缀稳定 | 事实槽与输入不变、仅时间推进时连续两轮，`ephemeralContext` 逐字节相等（证明没人往里塞时间/坐标） |
| 流式用量 | usage 落在 `choices` 为空的独立 chunk（不是 `isDone` 那条），跨 chunk 缓存后在 `[DONE]` 触发一次日志事件，prompt/completion 非零 |
| EnableTools=false | `ToLLMTools()` 与 `ExtraTools` 一并不追加，模型看不到动作声明 |
| 先说后做 | `tool_calls` 无 content 时，`say` 在 runner 被调用**之前**已从 `OnSay` 独立播出；模型正文仍只走 `OnToken` |
| 承诺收口·模型圆 | 动作失败且本轮续发请求 → 回填含强制指令，且内核未播任何兜底句 |
| 承诺收口·内核兜 | 墙钟 / HARD / 叫停路径下 `FallbackLines` 播出一次，且不与模型自己的话重复播 |
| say 不入签名 | 同一动作两次只改 `say` 措辞，L2 签名相同并照样进 BLOCK 档 |
| 叫停 | 新输入进来 → `Interruptible` 动作的 `ct` 被触发、本轮按 `Stale` 收尾且播出一次 `FallbackLines`（不依赖时钟，叫停由输入触发） |
| 叫停后重试 | 被叫停的动作**不吃配额**，下一轮同参数仍可执行 |
| 锁超时后重试 | 排队失败不计数，锁释放后同参数可再执行 |
| 超时不吃配额 | 非幂等动作超时后，同参数在后续轮仍能执行一次（替换 `AgentCore.NowSeconds` 推进时钟，不真等 15s） |
| 锁排队 | 两个 agent 同 `LockKey` 时按到达顺序执行，无并发进入 runner（`AgentResourceLocks` 需暴露 `WaitingCount(key)` 与 `Reset()` 作为测试 seam） |
| 锁等待超时 | 持有者不释放时，等待方在 `LockWaitSeconds` 后拿到 `[Action Failed]`，且**不计入 L2 计数**（不消耗非幂等配额）；时钟同样替换 `AgentCore.NowSeconds` |
| 退队 | 等待中销毁 agent / 判 `Stale` → `WaitingCount` 回退到注册前，无悬挂等待者 |
| 失败轮入历史 | Provider 全挂走兜底台词后，`Context.RoundCount` +1 且历史里该轮 assistant 为上屏的兜底句 |

EditMode 单测**覆盖不到**热更程序集在 IL2CPP 下的反射扫描（见 ADR-001 Consequences），A 步要加一次真机冒烟。

## 文件清单

| 文件 | 规模 | 职责 |
| --- | --- | --- |
| `Runtime/Agent/AgentInterfaces.cs` | S | 三窄接口、`AgentActionContext`、`AgentActionResult`、`EAgentOutcome` |
| `Runtime/Agent/AgentProfile_SO.cs` | S | 人设与上表参数 |
| `Runtime/Agent/AgentCore.cs` | L | 触发/排队/代际、墙钟、轮次编排、trace |
| `Runtime/Agent/AgentHost.cs` | S | MonoBehaviour 场景生命周期适配、输入入口与实例输出事件 |
| `Runtime/Agent/AgentActions.cs` | M | `[AgentAction]`、注册表、`AgentActionExecutor`、`ReflectionActionRunner`、`NullActionRunner` |
| `Runtime/Agent/AgentMemory.cs` | M | 事实槽 + `write_fact`/`forget_fact`/`search_past_conversation`（均为 `[AgentAction]`） |
| `Runtime/Tools/*.cs` | S | 时间、Transform 与 NavMesh 通用工具/动作 |
| `Runtime/Agent/AgentTrace.cs` | S | trace 结构与静态事件 |
| `Runtime/Agent/LoopGuard.cs` | M | L1/L2/L3 计数与阶梯裁决 |
| `Runtime/Agent/AgentResourceLocks.cs` | S | 同 key FIFO 队列、等待超时、退队（ADR-010） |
| `Editor/AgentSandboxWindow.cs` | M | IMGUI 沙盒（沿用 `LLMRequestLogWindow` 风格） |
| `Tests/Editor/Agent/*.cs` | L | 上表矩阵 |

## 实现进度

- [x] A `LLMSession` 注入点与流式用量缺口（2026-09-19）
- [x] B 内核 9 文件（2026-09-19，代码全落 + LoopGuard/资源锁单测；轮次编排类矩阵行待补，见下）
- [x] C 沙盒窗口（2026-09-20）
- [x] 四期 T1 首次真编辑器跑测（2026-09-19）：EditMode 50 条 **44 绿 6 红 0 跳过**——A 步 10 条全绿；B 步 `AgentLoopGuardTests` 12/13，红 = `BuildSignature_DifferentArgs_DifferentSignature`（不同 args 签名判等，待查）。其余 5 条红为既有用例（ContextManager 并发竞态 ×1、OpenAIProviderIdleTimeout 越界 ×4），按接力纪律只记录未修，明细在工程 `Docs/PENDING_TESTS.md`。前置修复：`LLM.Tests.Editor.asmdef` 补 `UnityEngine.TestRunner`/`UnityEditor.TestRunner` 引用（缺则 Test Framework 不识别程序集）+ `precompiledReferences` 补 `ZString.dll`（`overrideReferences:true` 下测试代码 `Cysharp.Text` 编译失败整程序集未加载）。FallbackTests 3 条断言文案错已随 c2553e9 修复，本轮确认全绿。
- [x] 四期 T3 矩阵脚手架（2026-09-20）：`Tests/Editor/Agent/` 落地 FakeClock / FakeProfiles / FakeKernelProvider / FakeOutput / FakeRunner / ScaffoldTestActions / AgentScaffoldSmokeTests（全轮同步收敛与 Provider 失败兜底两条烟测已绿）。「尚未覆盖」清单中的矩阵行现在全部可用脚手架写，唯二前提：测试动作必须 `[AgentAction]` 声明 + `Register(测试程序集)`（executor 按注册表路由，未登记动作到不了 runner）；FakeRunner 再入模式（triggerStale / timeoutAction）负责 Stale 与超时行的同步收敛。
- [x] 四期 T4 补矩阵行（2026-09-20）：`AgentKernelMatrixTests` 13 用例覆盖「尚未覆盖」清单全部行，EditMode 67/67 仅剩 T1 既有 5 红。**测试驱动的三处内核修复**：① `ContextManager` 退役 `ListPool`（线程不安全 + 并发 Get/Release 打穿静态池并污染全进程，C5 用例转绿）；② executor 锁等待挂独立 `lockWaitCts` 并接入 `InterruptCurrent`（排队发生在 actionCts 建立前，此前判 Stale 时等待者退不了队，ADR-010 落地）；③ `RunTurnAsync` 对轮间优雅取消补映射 Timeout（session 优雅返回不抛 OCE，否则墙钟超时被记成 Completed）。脚手架同步结论：取消必须模拟真实 HTTP 抛 OCE（`ct.ThrowIfCancellationRequested`）；外部恢复必须走 `ct.Register` 回调 + `Cancel()` 内联路径，直接 `TrySetResult` 在 EditMode 不同步。
- [x] 四期 T5 复审完赛 + 后续用户验收修复（2026-09-20）：T5 当场修复 2 项泄漏（lockWaitCts finally 兜底、沙盒临时人设 SO），记录并发 CompactRounds 陈旧快照限制；完赛后按用户验收补修——沙盒 Provider 兜底自注册（全工程无 RegisterToDispatcher 调用方导致开箱即 Failed）、幽灵「流式中」气泡、回车发送、布局锚定与自动滚底
- [x] ProfileKey 自动生成（2026-09-20，ADR-012）：`EnsureKey()` 空时按人设 FNV-1a 派生 + `knownKeys` 会话表/项目资产查重（冲突追加序号），`OnValidate`/`AgentCore` 构造双触发点，生成后与人设解耦；构建内退化为随机派生。用例 `AgentProfileKeyTests` 4 条全绿，EditMode 71 条 66 绿（剩 T1 既有 5 红）
- [x] 场景 AgentHost（2026-09-20，ADR-013）：每个挂载实例持有至多一个 `AgentCore`，OnEnable/OnDisable 对应激活/释放；`Trigger/Notify` 对外输入，实例事件转发流式 token 与轮次结束。UI 绑定宿主实例，不根据 Profile 重建第二份 Core
- [x] LoopGuard 签名规范化缺陷修复（2026-09-21，T1-R1）：`BuildSignature` 里 `WriteCanonical(JToken, Utf16ValueStringBuilder, int)` **按值**收了 ref struct 构造器，被调方推进的 index 回不到调用方，规范化那段被整段丢弃——所有 args 折叠成 `name()`，同名不同参（`move_to` 两个目标）从第二次起就被判重复、非幂等动作直接 `BLOCK`。改为 `ref` 传（`using var` 变量禁取 ref，CS1657，故构造器改 try/finally 兜 `Dispose`）。规格未变（§循环检测的「键排序 + 剥空白 + 只剥顶层 say」就是原意图），补了两条把意图钉死的断言：签名必须真的带上规范化 args、`say` 只在顶层剥。

A 步状态（2026-09-21 订正本段，原文的两处断言都已过期）：离线 Roslyn 编译通过（现在是 `ToolProjects/llm_offline_compile.sh`，9 步，含带/不带 `ANIMGRAPH_ENABLED` 与不给 `UNITY_EDITOR` 的 player 视角）；`LLMStreamUsageTests` 4 条 + `LLMSessionToolLoopTests` 6 条**早已在编辑器 Test Runner 跑过并全绿**（2026-09-19 四期 T1，见 `Docs/PENDING_TESTS.md` 的红绿总账），本轮又在 `ToolProjects/llm_test_runner` 里离树复跑确认。工程要求的 `2022.3.62t12` **本机已装**（`C:/Program Files/Tuanjie/Hub/Editor/2022.3.62t12/Editor`，离线编译与跑测用的就是它）—— 原文"本机三个编辑器版本都不一致"不再成立。**仍然有效的约束**：不要用不匹配的版本打开工程（会改写 `ProjectVersion.txt` 并触发全量重导入），也不要为跑测试另起 batch-mode 实例去抢已打开工程的锁；这类决定留给用户。

**A 步落地位置与对规格的偏离**：

- `IToolExecutor` / `ToolExecutionResult` / `SyncToolRegistryExecutor` 落在 `Runtime/Actions/ToolExecutor.cs`；`LLMSession.ToolExecutor` 默认值即 `SyncToolRegistryExecutor`，所以不注入时行为与改造前一致。
- `LLMSession`：`AskAsync(…, bool writeHistory = true, CancellationToken ct = default)`（`ct` 顺位后移，现有调用都用 `ct:` 命名实参）；`ExtraTools` 是只读暴露的 `List`，`BuildRequest` 里先 `new List<LLMTool>(ToolRegistry.ToLLMTools())` 再 `AddRange`。`MaxToolRounds`/`LastTurnExhaustedRounds` 已删（ADR-023）。
- `ToolAttribute` / `RegisteredTool` 补 `RepeatLimit` / `Idempotent`，新增 `ToolRegistry.TryGet`。裁决本身仍在 B 步的 LoopGuard。
- **顺手修了一处会拿错供应商的洞**：`LLMDispatcher.GetProvider("")` 原先因 `providerName ?? defaultProvider` 只在 `null` 时回落，传空串会跳过默认 Provider 直接命中 fallback。现改为空串等同未配置。压缩器的 `CompressionProviderName`（SO 字段，未填就是空串）正好踩在这条路上。
- `ContextCompressor.SummarizeWithRetry` → `SummarizeOnce`（尝试 2→1），并按 `CompressionProviderName` 取供应商。原有的 `CancelAfter` 超时**保留**：ADR-007 禁的是内核三类超时用 `CancelAfter`，压缩超时不在这三类里，且它掐的是真实 HTTP。
- `ContextManager` 软阈值分支现在每轮调一次 `PruneStaleLargeContent()`（修剪前先 `ArchiveRounds` 存一份原文副本，否则就地替换后无从追查；摘要也就再也看不到被 elide 的内容）。顺带删掉 `softCompactNoticed` 字段——原实现里它刚被置上就被下一轮重置，起不到"只报一次"的作用。计数器的复位仍留在下面的统一分支，不改 `consecutiveCompacts` 的熔断口径。
- 流式日志：`LLMDispatcher` 内部 `StreamLogCollector` 跨 chunk 攒 usage + 内容预览 + 工具名，流结束发**一条**日志。与 `EnqueueAsync` 的唯一差别是降级成功那条也会发日志（确实又发了一次请求，不记就丢掉了成本诊断来源）。`LLMRequestLog.ToolSummary` 在流式路径下留空——args 分片要到 `ToolCallAssembler` 才拼得完整，不在 dispatcher 再实现一遍。
- ~~缺口 5（`profile.MaxToolRounds` 写进 session）已在 B 步 `AgentCore` 构造里落进 session~~ **字段已删除（ADR-023）**。

**B 步落地位置与对规格的偏离**（9 个文件全部落在 `Runtime/Agent/`，名称与 §文件清单一致）：

- **时钟泵常驻，与"要不要自动起轮"无关**。B 步落地时泵曾和心跳共用一个 `AgentHeartbeat`（`HeartbeatEnabled` 只管自动起轮，关掉也照样泵到期），后来自动起轮整条删除、泵并入 `AgentCore`，见 [ADR-015](./decisions/ADR-015-drop-heartbeat-auto-turn.md)。
- 非 Play 模式不注册 `MonoRunner`（`MonoRunner` 是运行期 `MonoSingleton`，编辑器里 `GetInstance()` 会造出场景对象且不 tick），单测直接调 `AgentCore.Tick()`。
- `AgentResourceLocks` 的等待队列用 `List<LockLease>` 而不是 `Queue<LockLease>`：`Queue<T>` 没有 `Remove`，而"叫停即退队"要能从队列中段摘人。释放仍只走 `LockLease.Dispose()`，未提供 `Release(key)`。
- `TryAcquireAsync` 比 §类型定义 多两个参数（`ownerId`、`generation`）——`LockLease` 要记"sessionId + 轮代际 + 序号"才能做到身份化幂等释放，这两项必须由调用方给。
- `LoopGuard.Decide` 每轮命中的自计数放在 `Decide` 里（BLOCK 档也计），`NoteResult` 只负责 L3 结果哈希与非幂等终身配额，`executed=false` 直接 return。签名规范化自己实现（键排序 + 剥空白 + 只剥顶层 `say`），不复用 `ToolRegistry.BuildParameterSchema`。
- 动作超时的可测性：`FakeRunner` 在动作内部推进假时钟后调 `ctx.Agent.Tick()`，泵会 `Cancel` 动作的 linked CTS，从而在不依赖帧循环的前提下走进 `[Action Failed] timeout` 分支。`Stale` 同理由"动作内部再入 `Trigger`"触发。
- `Executor` 在入口处 `ct.IsCancellationRequested` 即回填 `[Action Failed] cancelled by new input` 且不再进 runner——同批剩余调用的取消责任在 executor 层，`LLMSession` 不管取消语义（它只认 `AbortTurn`）。

**B 步测试覆盖**：`Tests/Editor/Agent/AgentLoopGuardTests.cs` 覆盖签名规范化、幂等阶梯 Allow→Warn→Block→Abort、`RepeatLimit=-1`、非幂等阈值恒 1 与 `say` 剥离、失败不吃配额、跨轮配额不清零、L3 直拦、L3 随轮清零，以及资源锁的 FIFO / 幂等 Dispose / `waitSeconds=0` 直接失败 / 取消退队 / 假时钟到期。

**C 步落地位置与对规格的偏离**（2026-09-20）：

- `Editor/AgentSandboxWindow.cs`：窗口自身实现 `IAgentOutput` + `IWorldContextProvider`（不另建嵌套类）；`AgentCore` 由「发送/销毁重建」时惰性创建，instanceId 固定 `sandbox`；Profile 走资产槽或「创建临时人设」（`CreateInstance` + `HideAndDontSave`，ProfileKey=`sandbox`）；输出区流式累积用 `System.Text.StringBuilder`（长生命累积态，与 `AgentCore.turnText` 同款取舍）；轨迹/对话各限 200 条。核心快照是窗口上一个可编辑文本框。
- `LLMRequestLogWindow` 补 `CacheHitTokens` 列：>0 时 tokens 行显示「缓存命中 N」，为 0 保持原样（避免老条目刷一列 0）。
- 编辑器 EditMode 下 UniTask 的 PlayerLoop 不驱动 HTTP 延续，沙盒真链路对话要 Play 模式验证；EditMode 可验布局、起轮 trace 与内核同步路径（PENDING_TESTS 已列）。

尚未覆盖（设计稿测试矩阵中需要真帧循环或再做一轮脚手架的行）：`先说后做` 的时序断言、`承诺收口·模型圆`、`叫停`、`往返耗尽`、`墙钟超时`、`Provider 全挂`、`事实槽注入`、`前缀稳定`、`action 声明隔离`、`锁排队无并发进入 runner`。这些都能用"动作内部再入"的写法测，工作量在脚手架（`FakeKernelProvider` + `FakeRunner` + `FakeOutput` + profile 工厂）而不在被测代码。

**待办**（已定但本轮不做）：

- 交接快照统一收在仓库根 `Docs/HANDOFF.md`（全工程唯一一份，原同目录 `HANDOFF-2026-09-19-agent-kernel.md` 已并入）。规格以本文件为准，进度以 `Docs/CHANGELOG.md` 与 `Docs/LLMAgent接力队列.md` 完赛段为准。
- `ToolAttribute` / `ToolRegistry` 更名 `AgentToolAttribute` / `AgentToolRegistry`（用户 2026-09-19 提出，明确"不用现在做"）。改名要点：`ToolRegistry` 有 `RuntimeInitializeOnLoadMethod` 自扫描与 SO 资产里可能存在的类型名引用，动手前先 grep `.meta` 与资产序列化。
- 新脚本的 `.cs.meta` 由编辑器下次导入生成（提交时缺 meta，GUID 会在打开工程那一次落定）。
- `Assets/Resources/LLMProviderConfig*.asset` 里的明文 Key 仍待处理（已进过 git 历史且远端公开 → 该轮换）。

