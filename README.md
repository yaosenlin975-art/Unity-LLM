# LLM — Unity 在线对话与 Agent 插件

[English](README_EN.md) | **中文**

把 OpenAI 兼容接口（`/chat/completions`）包成 Unity 侧可直接用的**对话层 + Agent 内核**：流式输出、工具调用、异步动作、长期记忆、上下文注入与阈值压缩。给游戏内 NPC、语音助手、编辑器工具共用。

**只管这五件事**：流式、工具、动作、记忆事实槽、上下文压缩。**不含** RAG / 向量检索、语音识别与 TTS、动画后端实现（三者都在玩法层）。

---

## 程序集与依赖

| 程序集 | 目录 | 引用 |
| --- | --- | --- |
| `LLM.Runtime` | `Runtime/` | `UniTask` |
| `LLM.Demo` | `Demo/` | `LLM.Runtime`、`UniTask`、`Lin.Runtime.Prefs` |
| `LLM.Editor` | `Editor/` | `LLM.Runtime`、`UniTask` |
| `LLM.Tests` / `LLM.Tests.Editor` | `Tests/` | 被测程序集 |

| 依赖 | 用在哪 | 怎么拿 |
| --- | --- | --- |
| [UniTask](https://github.com/Cysharp/UniTask) | 全部异步与流式回调 | UPM 加 git URL：`https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask` |
| [ZString](https://github.com/Cysharp/ZString#unity) | `Cysharp.Text` 零分配拼接，插件内的字符串拼装约定 | NuGet 包 `ZString` |
| [Unity-PlayerPrefsHelper](https://github.com/yaosenlin975-art/Unity-PlayerPrefsHelper) | **只有 `Demo/` 里的参考实现用它**（`PrefsAgentStateStore` / `PrefsNpcAffinityStore`） | 包名 `com.lin.runtime-prefs-helper`，内嵌或本地 UPM 安装；它自己声明依赖 `com.unity.nuget.newtonsoft-json` |
| `com.unity.nuget.newtonsoft-json` | 工具与动作的参数 JSON Schema | UPM 装机，通常由上一行带进来 |

内核 `LLM.Runtime` 只依赖 UniTask（外加 ZString / Newtonsoft 两个预编译程序集）。`Unity-PlayerPrefsHelper` 是**可选**的：不想要它就删掉 `Demo/` 整个目录，内核照常编译与空跑，只是下拉里没有 Prefs 这一档参考实现。

- **日志**用自带的 `LLM.Runtime.Log`，不依赖任何框架日志。Player 构建里日志调用点（连同参数求值）默认被编译消除，需要输出时在 `Scripting Define Symbols` 加 `LLM_LOG`；编辑器构建始终输出。
- 时钟：`AgentCore` 不是 MonoBehaviour，墙钟由 `AgentHost.Update` 每帧推。

---

## 三分钟跑通

1. `Assets → Create → LLM → Provider Config`，填 `Model` / `BaseUrl`。`apiKey` 留空则读环境变量 `LLM_API_KEY`。
2. `Assets → Create → LLM → Global Config`，存成 `Assets/Resources/LLM/LLMGlobalConfig.asset`，把上一步的资产拖进 `ProviderConfig`。这一步可以省：编辑器一加载（或每次域重载）发现该路径缺资产就补一个，并提示你去补 `ProviderConfig`。构建体里造不出资产，仍按报错处理。
3. 要落盘就在同一份资产的「状态存储」三栏里选实现（下拉自动列出可选类），**留空即不落盘**——自动补的资产和手工新建的资产出厂都是空。见下面的[状态存储](#状态存储怎么选实现)。
4. 宿主侧装配一次：

```csharp
LLMRuntimeSettings.Install();   // 注册 Provider + 装状态存储；AgentHost.Activate 与沙盒窗口已经调了
```

5. 建会话提问：

```csharp
using Cysharp.Threading.Tasks;
using LLM.Runtime;

private LLMSession session;

public async UniTask Run()
{
    session = new LLMSession("npc_merchant", "你是镇上杂货铺老板，回答简短。");
    session.AddContext("玩家金币：120");            // 长期注入，每轮都带

    string answer = await session.AskAsync("这把剑多少钱？", OnChunk,
        ct: this.GetCancellationTokenOnDestroy());
}

private void OnChunk(LLMStreamChunk chunk)
{
    if (!string.IsNullOrEmpty(chunk.ContentDelta))
        panel.AppendText(chunk.ContentDelta);       // 流式增量；传 null 即非流式
}
```

6. 完全不想写代码：菜单 `Lin/LLM/Agent 沙盒`，选人设资产即可对话、看每轮的注入与工具往返。

---

## 一 · LLM Provider

Provider = 一条可用通道的抽象，负责"怎么把一次请求发出去"，不管会话与上下文。

```csharp
public interface ILLMProvider
{
    string ProviderName { get; }
    bool IsAvailable { get; }              // 走默认地址且 Key 为空 → 判不可用；自建无鉴权网关改 BaseUrl 即放行
    bool SupportsStreaming { get; }
    bool SupportsToolCalling { get; }

    UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct);
    UniTask CompleteStreamAsync(LLMRequest request, Action<LLMStreamChunk> onChunk, CancellationToken ct);
    int EstimateTokens(string text);
}
```

**配置与注册。** `LLMProviderConfig_SO` 是资产配置（`Create → LLM → Provider Config`）：只放**连接参数** Key / `Model` / `BaseUrl`。`Temperature`、`MaxTokens`、`ToolChoice` 不在这里——生成参数只有一个能配的地方，在人设资产 `AgentProfile_SO` 上。任何 OpenAI 兼容服务（自建网关、Azure、国产模型）只改 `BaseUrl` 即可。

**主备降级。** 再建一个资产挂到 `FallbackProviderConfig`，`RegisterToDispatcher()` 会把它注册成 `<名字>_fallback` 并设为保底。主通道抛异常（取消除外）时同一条请求自动重发到保底，保底也没有才把异常抛回调用方。

**装配根。** `LLMRuntimeSettings.Install()` 读 `Resources/LLM/LLMGlobalConfig`，只在还没有 Provider 时注册，幂等。也可以完全绕开它自己装：

```csharp
var dispatcher = LLMDispatcher.GetInstance();
dispatcher.RegisterProvider(new OpenAIProvider(apiKey, "gpt-4o", "https://api.openai.com/v1", "openai"));
dispatcher.SetDefaultProvider("openai");
```

**直接发请求（不经 session / agent）。**

```csharp
var request = new LLMRequest
{
    SessionId = "item_summary",                     // 只用于请求日志归组
    SystemPrompt = "把输入压缩成 20 字以内。",
    Messages = { new LLMMessage("user", rawText) },
    MaxTokens = 64
};
request.AddContextBlock("当前语言：简体中文");        // 拼在 SystemPrompt 之后，顺序稳定利于前缀缓存

LLMResponse resp = await LLMDispatcher.GetInstance().EnqueueAsync(request, ct);
// resp.Content / resp.PromptTokens / resp.CacheHitTokens
```

流式用 `EnqueueStreamAsync(request, onChunk, ct)`。`LLMDispatcher` 每次请求（含降级那条、失败那条）都发 `OnRequestCompleted`，`Lin/LLM/请求日志` 窗口订阅它列耗时与 token。⚠ 流式的真实用量在**独立的 `choices: []` 事件**里回传（一条 `IsDone = false`、只带 token 的 chunk），别只盯 `IsDone` 那条，要跨 chunk 缓存。

**再加一类 Provider。** 继承 `LLMProviderConfigBase_SO`，回答两件事：`CreateProvider(name)` 怎么造、`DefaultProviderName` 叫什么。全局配置只认基类，新增 Provider 不必改装配根。

**Key 安全。** 别把 Key 写进资产提交：`Resources/` 下的资产恒打进包体，进了 git 就等于公开。开发期用环境变量 `LLM_API_KEY`。

---

## 二 · AgentTool（只读查询）

工具是**同步、无副作用、返回字符串**的查询。返回值原样作为 `role=tool` 回填给模型，所以它就是模型看到的全部事实。

```csharp
using LLM.Runtime;

public static class ShopTools
{
    [AgentTool("get_price", "查一件商品的售价，单位金币。")]
    public static string GetPrice(string itemId)
    {
        var item = Shop.Find(itemId);
        return item is null ? "商店里没有这件商品。" : item.Price.ToString();
    }
}
```

约束：`public static`、返回 `string`，参数只支持 `int / float / bool / string` 及其可空形式（JSON Schema 由方法签名反射生成，参数名即字段名）。不合规的方法在扫描期被跳过并 `Log.Warning` 说明原因，不会静默消失。

**全局与成员两分。**

| | 怎么声明 | 谁看得见 | 要不要登记 |
| --- | --- | --- | --- |
| 全局工具 | `public static` + `[AgentTool]` | 所有 agent，按开关逐个放行 | `LLM.Runtime` 内的自动扫；**自己程序集里的要显式登记** |
| 成员工具 | `public` 实例方法 + `[AgentTool]`，组件挂在 `AgentHost` 物体层级上 | 只有该 agent（`AgentToolSet` 私有） | 不用，宿主 `Activate` 时扫层级 |

自己工程里的静态工具/动作要在运行期登记，Inspector 的"扫描并同步"只服务编辑期枚举候选，别当装配点用：

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
private static void RegisterTools()
{
    AgentToolRegistry.Register(typeof(ShopTools).Assembly);
    AgentActionRegistry.Register(typeof(ShopTools).Assembly);   // 静态动作同理
}
```

**属性位（防失控，裁决由内核 LoopGuard 执行）。**

| 字段 | 含义 |
| --- | --- |
| `RepeatLimit` | 同一签名重复几次后告警。`0` = 继承 `GlobalRepeatLimit`，`-1` = 关闭该工具的 L2 |
| `NameRepeatLimit` | 同工具名本轮次数上限（换参数也计）。`0` = 继承 `PerNameToolCallLimit`，`-1` = 关闭 |
| `Idempotent` | 默认 `true`。标 `false` = 有副作用：同一签名第二次起一律拦截，阈值恒为 1 且不可关闭 |

Warn / Block 的回灌文案都带"即使没有结果，也请直接根据已有信息简短回复玩家，不要再调用该工具"，避免模型被拦后闭嘴。未注册的工具名以 `[Tool Error] Unknown tool` 回给模型，不会卡死循环。

**按实例门控。** `AgentHost` 持一份覆盖式开关表：未收录 = 可用，收录项按其 `Enabled`。声明层（不把关闭的工具放进 `request.Tools`）与执行层（再拒一次）都读它，所以关掉的工具既不可见也不可执行。运行期可改：`host.SetToolEnabled("get_price", false)`，下一轮起生效。

**三个注入点**（`LLMSession`）：`ToolExecutor` 换掉 `tool_call` 的落地方式（异步、拦截、超时都在这里，`AbortTurn` 为真则同批剩余调用不执行不回填）；`ExtraTools` 挂注册表之外的声明（内核动作走这里）；`AskAsync(..., writeHistory: false)` 由调用方决定这轮写不写历史。`EnableTools = false` 时声明与执行一并屏蔽。

---

## 三 · AgentAction（有副作用的动作）

动作是工具的另一张表，**不共用注册表**：动作返回 `UniTask<AgentActionResult>`、可以 await、可以被叫停、可以抢资源锁，这些都过不了工具那边"返回 string"的校验。

```csharp
[AgentAction("adjust_affinity", "根据本轮已经发生的互动调整对玩家的好感。没有充分理由时不要调用。",
    Idempotent = false)]
public UniTask<AgentActionResult> AdjustAffinity(AgentActionContext context, int delta, string reason)
    => AdjustFromModel(delta, reason, context.Agent.RoundSerial);
```

约束：`public static`（全局）或 `public` 实例方法（成员，挂宿主层级），返回 `UniTask<AgentActionResult>`，其余参数同工具的简单类型限制。首参声明成 `AgentActionContext` 就由 runner 注入、不进 schema——`ctx.Agent / ctx.Profile / ctx.SessionId / ctx.CancellationToken` 都在这里。

**`say` 是内核保留参数名。** 非幂等动作的 schema 会自动追加必填 `say`，模型必须先说一句"我这就去搬"，执行器在动作真正开跑前把它播出去（宿主从 `IAgentOutput.OnSay` / `AgentHost.SayPresented` 拿到），动作方法自己声明同名参数会在扫描期报错。

**属性位。**

| 字段 | 含义 |
| --- | --- |
| `Idempotent` | 默认 `false`（动作按有副作用对待）：追加必填 `say`，同签名第二次起拦截 |
| `Repeatable` | 有副作用但允许重复执行：保留 `say`，改用本轮 NameCap/L2，不占一次性配额 |
| `LongRunning` | 带路、演出、移动一类：走 `LongActionTimeoutSeconds`，且执行期不扣本轮墙钟额度 |
| `Interruptible` | 新输入进来时能否叫停，默认 `true` |
| `LockKey` | 跨 agent 资源锁键模板，必须形如 `资源类型:{参数名}`，例：`item:{itemId}`。粗到一整类资源的键在扫描期就被拒 |
| `RepeatLimit` / `NameRepeatLimit` | 同工具那两档 |

**超时与预算**（全部来自 `AgentProfile_SO`）：`TurnDeadlineSeconds` 是一轮的墙钟额度（LLM 往返 + 锁等待 + 非 `LongRunning` 动作的执行时间），到期本轮按 `Timeout` 收口；单个动作走 `ActionTimeoutSeconds`，`LongRunning` 动作走 `LongActionTimeoutSeconds`；抢不到锁按 `LockWaitSeconds` 排队，`0` = 不等待直接失败。**没有工具次数总闸**，成本与失控只靠墙钟 + 上面那几档循环检测。

失败原因回填：`AgentActionResult.Failure("缺少 NavMeshAgent。")` → 模型看到 `[Action Failed] 缺少 NavMeshAgent。`。宿主可整体换掉执行器：`new AgentCore(..., runner: myRunner)`，`IAgentActionRunner` 只有一个方法；默认 `ReflectionActionRunner`，纯对话场景传 `NullActionRunner`。

执行路由顺序：**成员动作 → 成员工具 → 全局动作 → 全局工具**。

---

## 四 · 把 Agent 挂进场景

1. `Create → LLM → AgentProfile_SO`（NPC 用 `LLM/NPC Profile`）填人设。生成参数 `Temperature` / `MaxTokens`、轮次与失控保护、记忆槽上限、`FallbackLines` 都在这一份资产上。`ProfileKey` 留空会按人设内容派生并查重，生成后与人设解耦。
2. 场景物体挂 `AgentHost`，拖入人设，填 **`instanceId`**——只有给了稳定 `instanceId` 的实例才读写持久化（事实槽、对话轮次、好感度），留空按临时实例运行并在日志里点名。
3. 订阅输出：

```csharp
host.TokenReceived += delta => bubble.AppendText(delta);      // 模型正文流式增量
host.SayPresented += (actionId, say) => PlaySubtitle(say);     // 动作前的台词
host.TurnFinished += (answer, outcome) => Debug.Log(outcome);  // Completed / Stale / Timeout / Failed / LoopAborted
host.ActiveChanged += active => panel.SetActive(active);

host.SetCoreSnapshot("背包：3 件物品，任务：找钥匙");             // 低频世界状态，每轮注入
host.Trigger("玩家点了「交谈」");                                // 起一轮
host.Notify("【事件】玩家离开了店铺");                           // 世界事件起轮，前缀会被标成事件
```

4. 每轮注入的世界状态由 `host.SetCoreSnapshot(...)` 供给：`AgentHost` 自己实现了 `IWorldContextProvider`，回给内核的是 `coreSnapshot` 叠上好感度那行；只有不用 `AgentHost` 时才轮到你实现这个接口。只放低频值（身份、目标、关系等级），每轮都变的东西走查询工具。`QueryableHint` 是人设上唯一的引导位，非空时固定注入一行"你可以查询：……"。
5. 不用 `AgentHost` 也能跑：`new AgentCore(profile, instanceId, ctx, output, runner, toolGate, toolSet)`，自己每帧调 `core.Tick()` 推墙钟，销毁时 `Dispose()`。
6. `AgentHost` 的 Inspector 带只读「持久化记忆」区：`AgentFactsStoreType` 配了且实现装得出来、又有稳定 `instanceId` 时，按 `{ProfileKey}#{instanceId}` 列回事实槽；没配与"配了但类型丢了"是两条不同提示。只读，不含对话历史。

---

## 状态存储怎么选实现

`LLMGlobalConfig_SO` 上三个字段各管一件事，值是实现的 `Type.FullName`，装配时反射实例化——**新增实现不改框架**：

| 字段 | 接口 | 落什么 |
| --- | --- | --- |
| `AgentFactsStoreType` | `IFactStore` | 事实槽（`write_fact` 成功当场写） |
| `AgentHistoryStoreType` | `IConversationStore` | 会话轮次（轮末写） |
| `NpcAffinityStoreType` | `INpcAffinityStore` | NPC 好感度 |

留空 = 不落盘（出厂值）。三栏可以各选不同实现，也可以只选一栏。下拉由反射收集：具体类 + public 无参构造 + 实现对应接口 + 非编辑器/测试程序集，并排除内核的 `Null*Store`（"不落盘"只能由空值表达）。`Demo/` 里的 `PrefsAgentStateStore` / `PrefsNpcAffinityStore` 与你自己的实现并列出现，无特权。

```csharp
using System.Threading;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Storage;

public sealed class SaveSystemFactStore : IFactStore
{
    // 读是同步签名：内核在 AgentCore 构造期取档，必须赶在第一轮之前
    public AgentFactsBlob LoadFacts(string sessionId) => GameSave.LoadFacts(sessionId);

    public UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob, CancellationToken ct)
    {
        GameSave.WriteFacts(sessionId, blob);
        return UniTask.CompletedTask;
    }
}
```

四条边界要认：

1. **实现类所在程序集须在首次 `Install()` 前已加载**。热更 / HybridCLR 程序集里的实现，下拉选得到、装配时解析不到，结果是一条 Error + 不落盘。
2. **载荷类型就是接口签名里的 `AgentFactsBlob` / `AgentRoundsBlob`**，别换格式——Inspector 的记忆预览按它读。
3. 若你的实现也走 `PrefsHelper`：**归档身份 = 载荷类型短名的哈希，不含命名空间**。短名撞车就是撞档，所以内核三个 Blob 的短名不许改，自造载荷类型请带工程前缀。
4. **IL2CPP 裁剪**：没有静态引用点的实现类可能被裁，自己加 `[UnityEngine.Scripting.Preserve]` 或 `link.xml`，内核不替你锚定。

装配守卫按槽判定：宿主或测试已经自装过的槽（`AgentStateStores.Facts = ...`）不被 `Install()` 覆盖，其余槽照常装。三槽失败攒成一条 `Log.Error`，同一 `(槽, 类名)` 只报一次——`Install()` 每次 `AgentHost.Activate` 都会跑。

改这三栏要在 Play 模式之外改，或改完 `Ctrl+S` 存资产（只 `SetDirty` 不落盘，停 Play 会回滚）；关掉 Enter Play Mode Options（不重载域）的工程里，静态装配跨 Play 粘滞，换配置后要重进一次域重载。

## 内置清单

| 类别 | 名字 | 载体 |
| --- | --- | --- |
| 全局工具 | `get_current_time` | `AgentTimeTools`（static） |
| 全局动作 | `write_fact` / `forget_fact` / `search_past_conversation` | `AgentMemory`（内核自登记，宿主不必手工调） |
| 成员工具 | `get_agent_transform` | `AgentTransformTools` |
| 成员工具 / 动作 | `get_navigation_status` / `move_to` / `move_to_player` / `stop_navigation` | `AgentNavigationTools`（带 `[RequireComponent(NavMeshAgent)]`） |
| 成员工具 / 动作 | `get_animation_state`、`list_animation_states` / `play_animation_state`、`set_animation_parameter`、`set_animation_speed` | `AgentAnimationTools`，只认 `IAgentAnimationDriver`，驱动实现在玩法层，缺席时统一回"未挂载动画驱动" |
| 成员工具 / 动作 | `get_affinity` / `adjust_affinity` | `NpcAffinityController` |

`move_to_player` 找 tag 为 `Player` 的对象、把它脚下投影到 NavMesh、沿"玩家→本体"方向退回 `playerStopDistance`（默认 1.5 米）站定，目标点吸附半径 `destinationSnapRadius`（默认 2 米）。事实槽每轮全量注入 system，模型不需要"列举记忆"工具；`search_past_conversation` 只搜当前历史窗口之外的旧轮次与本会话压缩归档，没有向量检索。

---

## 上下文与压缩

`CompressionConfig_SO`（`Create → LLM → CompressionConfig_SO`）挂到人设的 `CompressionConfig`：

- `ContextWindowTokens > 0` 走 token 模式，按 `SoftCompactRatio` / `CompactRatio` / `ForceCompactRatio` 三级阈值；`= 0` 退回轮数模式（`SlidingWindowRounds × CompressThresholdMultiplier`）。
- token 数是估算（CJK 约 1 字/token，其他 4 字符/token），再用响应里的真实 `prompt_tokens` 校准。
- token 模式的软阈值区间不做摘要，只把折叠区里超过 `MinPruneBytes` 的旧回复换成占位符（`PruneStaleLargeContent`）。⚠ 轮数模式下这是空操作。
- 折叠区从新到旧按 `TailTokenBudget` 保尾部，头部"摘要 + 首轮短提问"永久锚定；`MinFoldTokens` 以下不做无效折叠；连续两次压缩后置位暂停自动压缩。
- 摘要由 LLM 生成，**只试一次**，超时/失败退化为机械折叠。想用小模型跑压缩，在 `CompressionProviderName` 填它的注册名（留空 = 沿用默认 Provider）。原始轮次可选归档到 `persistentDataPath/LLMArchive/<session>/`。

## NPC 表现层（可选）

`NpcAgentProfile_SO`（身份 / 性格 / 说话风格 / 目标与价值观 / 知识边界 / `InitialAffinity`）、`NpcAffinityController`（好感度与关系等级，可被游戏侧 `AdjustFromGame` 直接调）、`NpcGestureCatalog_SO` + `NpcPerformanceProfile_SO` + `NpcSpeechPerformanceController`（本地流式导演：切句、表情、手势，不进模型提示词）。动画与手势的真正后端由玩法层实现 `IAgentAnimationDriver` / `INpcGestureDriver`，本插件不引用任何动画类型。

## 编辑器入口

- `Lin/LLM/Agent 沙盒` — 选人设直接对话，看每轮注入、声明的工具与动作。
- `Lin/LLM/请求日志` — 每次请求的耗时、token 用量、缓存命中、工具调用摘要。
- `AgentHost` Inspector — 工具门控三组（动作 / 公共工具 / 本体工具）、扫描并同步、快速添加成员工具组件、只读持久化记忆。

## 已知取舍

- 一个 `LLMSession` 同一时刻只允许一个在飞请求，重复调用直接抛异常。
- `[AgentTool]` 只支持同步方法；异步一律走 `[AgentAction]`。
- 流式过程中不做增量 JSON 校验，`AgentToolCallAssembler` 在参数闭合前不产出调用，因此工具结果一定晚于最后一个 token。
- 非 Play 模式与沙盒里的核心不推墙钟（`Tick` 只由 `AgentHost.Update` 驱动），编辑器沙盒的长动作不会因超时被切。
- 工具/动作只有简单类型能进 schema；复杂结构请序列化成 string 参数自己解析。

## 设计文档

分层形态、逐条决策与拒绝理由在 `Docs/`（`agent-kernel-design.md`、`agent-tool-model-design.md`、`agent-tool-gating-design.md`、`agent-animation-actions-design.md`、`decisions/ADR-0xx-*.md` 等）。这些文档目前只在框架工程的插件副本里（`Unity-Framework/Assets/Plugins/LLM/Docs/`），本目录未随包分发。改架构前先补 ADR，不要只改代码。
