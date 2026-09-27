# Agent 状态存储与装配设计

## 2026-09-22 强类型载荷修订

### 需求分析

当前持久化链路先由 `AgentMemory.ExportJson()` / `ContextManager.ExportRoundsJson()` 把内存对象序列化为字符串，再由 `PrefsHelper` 把包含 `string Json` 的 Blob 整体序列化。读取时同样需要两次反序列化。`AgentFact` 和 `ConversationRound` 已是稳定、可序列化的持久化结构，当前没有只能接收 JSON 字符串的存储后端，因此内层 JSON 是多余边界。

成功标准：

- `PrefsHelper` 一次反序列化后直接得到事实列表或轮次列表。
- 事实与历史仍使用两个 Blob 类型隔离归档，同一 `sessionId` 不互相覆盖。
- 现有 `Json` 存档不兼容，读取后按空状态处理；用户已明确不需要保留现有记忆。
- `ExportJson/ImportJson` 保留为显式 JSON 导入导出能力，但不再参与 Prefs 持久化主链。

### 方案选型

采用强类型 Blob：`AgentFactsBlob.Facts` 持有事实列表，`AgentRoundsBlob.Rounds` 持有轮次列表。Store 接口直接传递两种 Blob，`PrefsAgentStateStore` 只负责 `PrefsHelper.Get/Set`。旧 `Json` 字段直接删除，不引入只使用一次的迁移逻辑。

| 未采用方案 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 继续传递 JSON 字符串 | 在已有 `PrefsHelper` 序列化的前提下重复转换，且 Inspector 还要再解析一次 | 真实后端只接收 JSON 原文时，在该后端边界序列化 |
| 直接以 `List<T>` 作为 `PrefsHelper` 的归档类型 | 会改变归档类型和文件路径，并丢掉两种状态各有明确载荷名的可读性 | 无；保留两个 Blob 是更小改动 |
| 为强类型载荷新造 DTO 和映射层 | 现有两个数据类已是纯字段序列化结构，再映射没有实际边界收益 | 域模型出现不应落盘的行为或引用时 |

### 接口设计

`IFactStore.LoadFacts/SaveFactsAsync` 改为读写 `AgentFactsBlob`；`IConversationStore.LoadHistory/SaveHistoryAsync` 改为读写 `AgentRoundsBlob`。`AgentMemory` 和 `ContextManager` 各增加强类型快照的导入导出方法，快照复制元素，不与 `PrefsHelper` 的静态缓存共享可变对象。现有 JSON 方法保留，避免破坏调试、手工导入导出和已有测试。

### 数据结构

```csharp
[Serializable]
public class AgentFactsBlob
{
    public List<AgentFact> Facts;
}

[Serializable]
public class AgentRoundsBlob
{
    public List<ConversationRound> Rounds;
}
```

### 关键流程

1. 新档写入：内存对象生成独立强类型快照 → `PrefsHelper.Set` 一次序列化。
2. 新档读取：`PrefsHelper.Get` 一次反序列化 → 复制到内存对象。
3. 旧档读取：Newtonsoft 忽略已删除的 `Json` 字段，强类型列表为 null，按空状态处理；不迁移。
4. Inspector 直接读取 `AgentFactsBlob.Facts`，不再使用 `JsonConvert`。

### 非目标（明确不做）

| 非目标 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 删除 `ExportJson/ImportJson` | 它们仍是有用的显式交换口，本次只移除持久化主链的重复 JSON | 确认全仓无外部调用后单独删除 |
| 旧存档兼容与通用迁移框架 | 用户已明确旧记忆可丢弃，为它保留字段和分支没有价值 | 未来出现必须保留的真实存档升级时 |
| 服务器存储协议 | 尚无实现，不应继续为假设后端支付当前复杂度 | 引入第一个服务器 Store 时在边界转换 |

> 本节取代初始版本中“载荷一律是 JSON 字符串”的设计，下文接口、数据结构与流程已按强类型实现同步。

## 需求分析

会话历史与事实槽目前是**纯内存态，关场景即丢**。对码核实：`AgentMemory.ExportJson/ImportJson` 与 `ContextManager.ExportRoundsJson/ImportRoundsJson` 四个持久化 API 在运行时代码里**零调用**，唯一调用点是 `AgentKernelMatrixTests.cs:281-284` 的往返单测；宿主侧 `AgentHost` / `Learn/LLM/Scripts/` 全文没有 `File.` / `PlayerPrefs` / `Save` / `Load`。ADR-011 第 4 条把落盘责任推给"宿主存档"，而 Lin 框架里不存在任何存档抽象（`interface I*(Archive|Save|Persist|Storage)` 零命中），所以这条责任链是空的。

本次要做三件事：

1. 把**会话历史读写**与**事实槽读写**各自接口化，使本地文件之外的实现（宿主存档、将来的服务器）可整体替换。
2. 内核持有这些接口，在确定的时机自动读写，宿主不再手写 Save/Load 钩子。
3. 新增一个薄的全局配置资产，负责"选哪个实现 + 装配"，取代散落在各宿主里的 `Resources.Load<LLMProviderConfig_SO>("LLMProviderConfig")`。

**不在本次范围**：LLM API 通道。`ILLMProvider` 已经是接口、`OpenAIProvider` 已经是它的实现、`LLMDispatcher` 已按名注册并做主备降级（`ILLMProvider.cs:11`、`LLMDispatcher.cs:24,32-115`），这一层不需要再造接口。留下的唯一硬编码缺口记在下方非目标表。

成功标准：给 `AgentHost.instanceId` 填一个稳定值后，同一 agent 关场景重开能读回上次的对话与事实；把配置换成 `None` 后行为与今天逐字节一致。

## 术语

本模块没有独立术语表，术语在此定义一次，其余文档引用此处。

| 术语 | 含义 |
| --- | --- |
| **事实槽**（fact slot） | `AgentMemory` 里一组 `key/value/timestamp` 结构化条目，模型经 `write_fact` 显式写入，每轮全量注入（ADR-005）。不指代任何向量检索或"长期记忆"设施 |
| **会话轮次**（round） | `ContextManager` 里一对 user/assistant 文本 + 时间戳 + `IsSummary`/`IsPinned` 标记。**不含 tool 消息**（`AddRound` 只收文本） |
| **稳定实例 id** | 由宿主显式传入 `AgentCore` 构造参数的 `instanceId`。它是存档域的一部分，跨读档不变 |
| **临时实例** | 宿主没给 `instanceId`、由 `AgentCore` 用 `Guid.NewGuid()` 兜底的实例（`AgentCore.cs:89`）。可运行、可观测，但**不落盘** |
| **装配根** | `LLMRuntimeSettings.Install()`，读全局配置并把 Provider 与 store 实例塞进静态持有者的唯一入口 |
| **状态存储接口** | `IFactStore` + `IConversationStore`，分别读写事实与轮次的强类型 Blob |

## 方案选型

### 采用：内核持接口，同步预加载 + 写后落盘

`AgentCore` 构造末尾读一次（仅稳定实例），`write_fact`/`forget_fact` 成功后写事实、`FinishTurn` 后写历史。内核程序集提供一个开箱即用的本地文件实现，但默认不启用（持有者初始为 `NullAgentStateStore`），必须由装配根显式装上——写路径全程只有一条，不构成 ADR-011 当初担心的双通道。

### 未采用方案

| 方案 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 内核不持接口，只补宿主 Save/Load 钩子（ADR-011 现状） | 四个 Export/Import 零调用已经证明这条路走不通：责任没有实现载体，每个宿主都要重写一遍同样的装配 | 无；被本设计取代 |
| instanceId 由 ProfileKey 自动派生 | sessionId 退化成 `{key}#{key}`，同种 NPC 共享记忆——ADR-011 Alternatives 明确拒过（"两个商人会互相记得彼此与不同玩家的对话"）；ADR-012 的 `EnsureKey()` 派生的是资产级哈希，天然无法区分场景实例 | 单实例 agent（助手/沙盒）由宿主显式传 `instanceId = profile.ProfileKey`，这是宿主的合法选择而非内核默认 |
| 单接口 `IAgentStateStore` 一次搬整个 agent 状态 | 两者的可替换性不同：事实槽是要单独查看/改动的结构化数据（ADR-005 Consequences，走 `Lin/Prefs Manager`），历史是不透明文本流水。焊死后不能只换一半 | 出现"两份状态永远同生共死"的真实需求时再合并 |
| 两个接口 + 两个实现 + 两个文件 | 此刻数据形态完全相同（都是 JSON 数组），双实现只多一套代码和一个配置项 | 服务器侧历史与记忆确实要分后端时 |
| 接口全异步（含读） | 读点唯一且在构造期，异步化会逼 `AgentCore` 变成两阶段异步工厂，代价全落在内核；Unity 域卸载前 `OnDisable → Dispose` 也不给 await 的机会 | 见"退出时序"与下方非目标"服务器存储后端" |
| 自造 `LocalFileAgentStateStore` 直接写 `persistentDataPath` | 仓库里 `PrefsHelper` 已经做了同一件事（按类型分档、`lock`、XOR、WebGL 分支、`DeleteKey`/`Clear` 与配套编辑窗口），重写一遍是最典型的 slop；而且它会破开 ADR-011"内核不做文件 IO" | 无；本设计的本地实现就是 `PrefsAgentStateStore` |
| 事实和历史挤同一份归档（同一个 `T`） | 事实写在 tool 循环中途、历史写在轮末，两个写者共享一个 `Dictionary<string, T>` 就要读-改-写，两条写路径互相覆盖；用 `T = string` 还会撞上项目里其他 `<string>` 使用者 | 无；两个 `[Serializable]` blob 类型即两份归档 |

## 接口设计

`Assets/Plugins/LLM/Runtime/Storage/AgentStorageInterfaces.cs`

```csharp
namespace LLM.Runtime.Storage
{
    /// <summary>事实槽读写口。</summary>
    public interface IFactStore
    {
        /// <summary>无数据返回 null。同步签名是刻意的，见设计稿"预加载"。</summary>
        AgentFactsBlob LoadFacts(string sessionId);

        UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob, CancellationToken ct);
    }

    /// <summary>会话轮次读写口。</summary>
    public interface IConversationStore
    {
        AgentRoundsBlob LoadHistory(string sessionId);

        UniTask SaveHistoryAsync(string sessionId, AgentRoundsBlob blob, CancellationToken ct);
    }
}
```

`Assets/Plugins/LLM/Runtime/Storage/AgentStateStores.cs` — 装配根写入的静态持有者，内核读它。沿用本仓库既有惯用法（`LLMDispatcher` 全局单例、`AgentActionRegistry` 静态注册表、`AgentCore.NowSeconds` 可替换静态字段），因此不新增构造参数、不碰 ADR-002 的三个窄接口。

```csharp
public static class AgentStateStores
{
    public static IFactStore Facts = new NullAgentStateStore();
    public static IConversationStore History = new NullAgentStateStore();

    /// <summary>测试 seam，与 AgentActionRegistry.Reset() 同一套路</summary>
    public static void Reset() { ... }
}
```

`NullAgentStateStore : IFactStore, IConversationStore` 一个实例挂两处：读恒 null、写丢弃。ADR-002 Consequences 要求"缺能力靠 `Null*` 默认对象表达"，内核里不出现 `if (store == null)`。

## 数据结构

### 本地实现 `PrefsAgentStateStore : IFactStore, IConversationStore`

复用 `Lin.Runtime/Helper/PrefsHelper.cs`，**本模块不写一行文件 IO**。`PrefsHelper` 的模型是"每个 `T` 类型一份归档，归档本体是 `Dictionary<string, T>`，Newtonsoft 整体序列化后落一个文件（编辑器 `EditorPrefs/{hash}`、真机 `{persistentDataPath}/Temps/{hash}`、WebGL 走 `PlayerPrefs`），读写带 `lock`，落盘前过一层 XOR"。

两个载荷各占一个 `[Serializable]` 类型 ⇒ 天然两份归档、天然满足"一个写者整段覆盖、不需要跨写者锁"：

```csharp
namespace LLM.Runtime.Storage
{
    /// <summary>事实槽载荷。独立类型即独立归档，与轮次天然分开。</summary>
    [Serializable]
    public class AgentFactsBlob
    {
        public List<AgentFact> Facts;
    }

    /// <summary>会话轮次载荷。</summary>
    [Serializable]
    public class AgentRoundsBlob
    {
        public List<ConversationRound> Rounds;
    }

    public sealed class PrefsAgentStateStore : IFactStore, IConversationStore
    {
        // ponytail: 每次 Set 整档重写（含全部 agent 的数据）。
        // 上限：全 NPC 合计到几百 KB 级会在每轮出现可感知卡顿；升级路径是加脏标记 + 定时合并写。
        public AgentFactsBlob LoadFacts(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            return PrefsHelper.Get<AgentFactsBlob>(sessionId);
        }

        public UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionId)) return UniTask.CompletedTask;
            PrefsHelper.Set(sessionId, blob);
            return UniTask.CompletedTask;
        }

        // LoadHistory / SaveHistoryAsync 同形，换 AgentRoundsBlob
    }
}
```

- `key` 就是 `sessionId`，**不再参与构成路径**，所以 sessionId 的字符集不需要任何校验或清洗（原设计的文件名安全检查随本条作废）。
- `CheckType<T>` 要求引用类型带 `[Serializable]`，两个 blob 类都带。读缺失 key 用 `Get<T>(key)` 单参重载（返回 `default` 即 null）；**不要给 `Get<T>(key, null)` 传字面量 null**，`Get<T>(string, T)` 与 `Get<T>(string, Func<T>)` 会歧义。
- 写是同步完成的 ⇒ **不存在"退出时写未落地"的窗口**，调用方 `.Forget()` 安全。
- 硬依赖：`NEWTONSOFT_JSON` 由 `Lin.Runtime.asmdef` 的 `versionDefines`（`com.unity.nuget.newtonsoft-json`，manifest 3.2.1）提供，`PrefsHelper.cs` 编在 `Lin.Runtime` 内所以判定为真。缺该符号时 `PrefsHelper.Save` **跳过写盘并 `Log.Error`**（不再静默落 `"{}"` 覆盖成空档），所以本模块**不需要任何自检或探针**，故障由 `PrefsHelper` 自己报。注意 `LLM.Runtime.asmdef` 没声明这个符号，因此 LLM 侧不能用 `#if NEWTONSOFT_JSON` 判断任何事。
- 手改/查看走 `Lin/Prefs Manager` 窗口（`PrefsHelperWindow.cs`，JSON 编辑器 + 保存 + 删除），磁盘上是 XOR 后的字节，不再明文。ADR-005"宿主在存档里直接改 JSON"的兜底由这个窗口承担。
- **仓库副作用**：编辑器下归档落在 `<repo>/EditorPrefs/`，而该目录**当前被 git 跟踪**（`git ls-files EditorPrefs` 有货）。每轮一次写就会脏一次工作区，处理见非目标表。

### `LLMGlobalConfig_SO`（`Runtime/Config/`）

只做选择与引用，**不搬任何既有字段**：

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `ProviderConfig` | `LLMProviderConfigBase_SO` | null | 抽象基类引用：任何派生 Provider 配置都能挂，新增类型不必改装配根 |
| `StateStore` | `EAgentStateStoreKind { None, Prefs }` | `Prefs` | 选哪个实现；`None` = 今天的纯内存行为 |

路径与目录**不可配**：`PrefsHelper` 自己定死归档位置，配置项存在只会配了不生效。

**（落地订正）** 原稿写的是"`Resources/LLMProviderConfig.asset` 保持原位不动，只新增一个 `Resources/LLMGlobalConfig.asset`"。实际落点：全局配置在 `Resources/LLM/LLMGlobalConfig.asset`（= `LLMRuntimeSettings.CONFIG_PATH`），Provider 配置资产此后也被重整进同一目录（`Resources/LLM/Mimo.asset`、`Sensonova.asset`、`LMStudio-Gemma-E2B-{Home,Work}.asset`），原 `Resources/LLMProviderConfig.asset` 已不存在。消费点仍只有 `AgentHost.Activate` 与 `AgentSandboxWindow` 两处。资产缺失时 `Log.Error` 且不装配任何东西，不加兼容回落分支。

## 关键流程

1. **装配**：宿主启动路径调 `LLMRuntimeSettings.Install()` → `Resources.Load<LLMGlobalConfig_SO>(CONFIG_PATH)`（`CONFIG_PATH = "LLM/LLMGlobalConfig"`）→ 注册 Provider（已注册则早退，幂等靠这条判定而不是状态位；具体资产只要是 `LLMProviderConfigBase_SO` 的派生就行，装配根不认死实现类型）→ 按 `StateStore` 赋给 `AgentStateStores`。装配期不做任何存储自检（理由见"数据结构"）。
2. **预加载**：`AgentCore` 构造末尾，**仅当宿主传入的 `instanceId` 非空**时执行 `Memory.ImportSnapshot(AgentStateStores.Facts.LoadFacts(SessionId))` 与 `session.Context.ImportSnapshot(...)`。为空即临时实例：跳过读、跳过所有写，`Log.Warning` 一次说明"要持久化请宿主给稳定 instanceId"。`SessionId` 仍用 Guid 兜底以保证 trace 与归档可区分（保持 `AgentCore.cs:89` 现状）。
   - 内核只在构造期读一次。宿主之后自行调 `Import*` 会覆盖预加载结果——这仍是单写通道：落盘只经 store 一条路，`Import*` 是纯内存注入口。
3. **事实写**（write-through）：`AgentMemoryActions.WriteFact` / `ForgetFact` 成功后 `AgentStateStores.Facts.SaveFactsAsync(agent.SessionId, agent.Memory.ExportSnapshot(), ct).Forget()`。模型一次工具调用落一次盘。
4. **历史写**（write-behind）：`AgentCore.FinishTurn` 在 `AddRound` 之后 `SaveHistoryAsync(...).Forget()`。`Stale` 轮走不到这里（`AgentCore.cs:235-244` 代际不符即提前 return），因此作废轮既不入内存也不落盘，语义一致。
5. **压缩落后于落盘，明确接受**：`CompactRounds` 是 `AddRound` 内部 `.Forget()` 出去的异步改写（`ContextManager.cs:255,298`），所以轮末落的是**压缩前**的历史。读档最坏情况是退回未压缩状态并重新压缩一次，不丢对话内容，代价是多花 token。不为此在 `ContextCompressor.OnCompactCompleted`（`ContextCompressor.cs:228`）上再挂一次写。
6. **不改动**：`AgentMemory` 与 `ContextManager` 的对外 API 一字不改；四个 `Export*/Import*` 保留（沙盒、调试、宿主存档都要用），store 的载荷就是它们的产物，因此不构成并列的第二条通道。

### 退出时序

不是问题：`PrefsHelper.Set` 在返回前已同步完成写盘（内部 `File.WriteAllBytes`），所以 `.Forget()` 不存在被域卸载吞掉的窗口，AGENTS.md 的惯用法在这里无需例外。真异步实现（服务器）才有这个窗口，落点见非目标表。

## 文件范围

新增：

- `Runtime/Storage/AgentStorageInterfaces.cs` — 两接口 + `NullAgentStateStore`
- `Runtime/Storage/PrefsAgentStateStore.cs` — store + 两个 `[Serializable]` blob 载荷类（同文件，只为让两份归档分开）
- `Runtime/Storage/AgentStateStores.cs` — 静态持有者 + `Reset()`
- `Runtime/Config/LLMProviderConfigBase_SO.cs` — Provider 配置抽象基类（`CreateProvider` + `DefaultProviderName` + 统一的 `RegisterToDispatcher`），`LLMProviderConfig_SO` 改为它的派生类
- `Runtime/Config/LLMGlobalConfig_SO.cs` — 含 `EAgentStateStoreKind`
- `Runtime/Config/LLMRuntimeSettings.cs` — 装配根
- `Resources/LLMGlobalConfig.asset` — **必须在编辑器里 Assets → Create → LLM → Global Config 生成**，不能手写：团结引擎把 `.meta` 的 guid 存成密文（`tag:yousandi.cn,2023:`），手编 `m_Script` 引用会造出 missing script。生成后把 `Assets/Resources/LLMProviderConfig.asset` 拖进 `ProviderConfig` 字段。在它存在之前 `LLMRuntimeSettings.Install()` 只报 Error 不装配，Provider 也不会再自动注册（原 `Resources/LLMProviderConfig` 零配置路径已被取代，故意不留兼容分支）。
- `Docs/decisions/ADR-014-...`（本篇配套）
- `Tests/Editor/Storage/PrefsAgentStateStoreTests.cs`

修改：

- `Runtime/Agent/AgentCore.cs` — 暴露"是否稳定实例"，构造末尾预加载，`FinishTurn` 写历史
- `Runtime/Agent/AgentMemory.cs` — 两个动作成功后落盘
- `Runtime/Agent/AgentHost.cs` — `EnsureProviderRegistered` 改为 `LLMRuntimeSettings.Install()`
- `Editor/AgentSandboxWindow.cs` — 同上
- `Docs/agent-host-design.md` — 非目标表"Agent 存档"一行改指本设计
- `Docs/decisions/ADR-011-*.md` — 第 4 条标注被 ADR-014 取代

## 测试

EditMode 两条。`PrefsHelper` 按 `Type` 静态缓存归档且没有失效口子，所以 `Set` 后 `Get` 走的是内存字典——**这测的是接口接线，不是磁盘落地**；真持久化留一次 Play 模式人工冒烟（跑一轮 → 关场景重开 → 看是否读回），按仓库惯例落 `docs/PENDING_TESTS.md`。不要为了可测性去改 `Lin.Runtime` 的私有缓存。

归档位置由 `PrefsHelper` 定死（编辑器写 `<repo>/EditorPrefs/`），测试没有目录 seam 可用，所以每条用例必须用**唯一 sessionId**（`ZString.Concat("t-", nameof(用例))`），并在 TearDown 里 `PrefsHelper.DeleteKey<AgentFactsBlob>(sid)` / `<AgentRoundsBlob>` 收干净。

1. **接线与隔离**：保存 `AgentFactsBlob` 后可直接读回 `Facts`；同一 sid 的 `LoadHistory` 仍为 null（证明事实与轮次确实是两份归档）；未写过的 sid 读回 null。
2. **稳定实例才落盘**：`new AgentCore(profile, null, ...)` 走完一轮后 `LoadFacts(SessionId)` 恒 null 且有一次告警；`new AgentCore(profile, "npc-1", ...)` 同流程后能读回非 null。

原设计的"文件名非法字符"用例作废：`sessionId` 已不参与路径。

## 非目标（明确不做）

| 不做项 | 原因 | 将来的唯一落点 |
| --- | --- | --- |
| 服务器存储后端 | 用户明确"先做好本地持久化和接口设计"，当前不为假设的 JSON 传输层保留双重序列化 | 新写一个 `IFactStore`/`IConversationStore` 实现，必要时在该后端边界序列化 |
| 异步预加载 | 见未采用方案表。当前天花板：服务器实现没法在不阻塞主线程的前提下满足同步 `Load` | 接服务器时把预加载责任上移到宿主装配期（宿主 await 后再构造 `AgentCore`），届时要改的是 `AgentCore` 构造入口而非 store 接口 |
| 退出时 flush | 本地实现写即完成，不存在未落地窗口；预埋 flush 钩子会为用一个不存在的实现 | 服务器实现接入时在 `OnApplicationQuit` 与存档点 await 未完成写 |
| 多端同步 / 冲突合并 / 内容审核 | 要求 append-only 逐条写入与 seq，接口形状完全不同 | 真要接服务器再设计，落点是新接口不是改本接口 |
| Provider 工厂去硬编码 | `LLMProviderConfig_SO.CreateProvider` 返回类型与注册名都写死 `OpenAIProvider`/`"openai"`（`:56-90`），今天只有一个实现用不上 | 接服务器代理 Provider 时把返回类型改 `ILLMProvider` + 加类型选择字段 |
| 孤儿条目清理 / 存档删除 API | 稳定 id 下不涨（一 agent 一条字典项，原地覆盖）；只有换 ProfileKey 或换存档才留残留条目 | `PrefsHelper.DeleteKey<AgentFactsBlob>(sessionId)`，或 `Lin/Prefs Manager` 窗口手删 |
| 归档位置与命名可配 | `PrefsHelper` 按 `typeof(T).Name` 哈希定死路径，配置项存在只会配了不生效 | 真要换位置就在 `Lin.Runtime` 侧改，不在 LLM 侧加假配置 |
| 把 `EditorPrefs/` 从版本库摘出去 | 编辑器下每条 agent 状态都会重写这个**已被 git 跟踪**的目录，每轮脏一次工作区。但它是全项目 `PrefsHelper` 共用目录，摘出去影响别的工具 | 与用户单独决定：`.gitignore` 补一行 + `git rm --cached`，或运行期不启 store |
| 写入节流 / 批量合并 | 一轮最多一次历史写 + N 次事实写，NPC 数量级下 IO 可忽略 | 多 agent 高频轮次出现实测卡顿时，改成脏标记 + 定时合并写 |
| 事实按时间过期 / 淘汰 | 现有淘汰只看 LRU 序（命中移到末尾、超 `FactSlotMaxCount` 从头淘汰），`Timestamp` 目前只写不读。加过期是新的产品语义不是补漏 | 真要"记不住就忘"时写进 ADR，改 `AgentMemory.Trim` 一处 |
| 存档加密 / 防篡改 | `PrefsHelper.Translate` 的 XOR 是**混淆不是加密**（偏移量由类型名长度与路径推出，可逆），但它已经是全项目统一口径，LLM 侧单独加一层只多一处密钥 | 上线前若真需防篡改，在 `Lin.Runtime` 侧统一升级，所有归档一起受益 |
| 跨 agent 共享记忆 | ADR-011 §1-2 已定：共同事实走世界快照 | 无 |
