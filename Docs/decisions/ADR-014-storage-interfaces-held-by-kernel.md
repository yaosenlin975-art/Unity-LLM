# ADR-014: 状态存储接口化并由内核持有，取代 ADR-011 的"时机全归宿主"

## Status

Accepted — 本决策**取代** [ADR-011](./ADR-011-memory-private-and-storage-keys.md) Decision 第 4 条中"落盘时机归宿主"的部分；ADR-011 第 1、2、3、5 条（记忆私有、跨 agent 走世界快照、sessionId 格式、ProfileKey 字符约束）继续有效。本 ADR 中“载荷一律是 JSON 字符串”的部分已被 [ADR-029](./ADR-029-typed-agent-state-payloads.md) 取代。

## Date

2026-09-20

## Context

ADR-011 §4 定了"内核不做文件 IO，事实槽与历史只提供 `ExportJson()/ImportJson()`，落盘路径、格式、**时机**全归宿主存档"，理由是避免与内核另开一条 `history.json` 形成双通道。

一年后（本次）对码核实：这条责任链根本没有实现载体。四个 Export/Import API 在运行时代码里零调用，唯一调用点是 `AgentKernelMatrixTests.cs:281-284` 的往返单测；宿主侧 `AgentHost`、`Learn/LLM/Scripts/` 无任何 `File.`/`PlayerPrefs`/`Save`/`Load`；Lin 框架里不存在存档抽象。结果是"记忆私有 + sessionId 定死格式"这两条规格在实务上悬空——状态从来没落过盘，也就从来没串过档，看起来像设计正确，其实只是功能没做。

同时出现了新的接入诉求：读写要能换成自定义实现（宿主存档、将来的服务器），API 请求侧也要能换。API 侧核实后确认**已经满足**：`ILLMProvider` 是接口、`OpenAIProvider` 是实现、`LLMDispatcher` 按名注册并做主备降级，无需再造一层。所以本次只动存储侧。

约束：内核在 AOT 程序集 `LLM.Runtime`（ADR-001），接口不得引用热更类型；宿主与内核之间只认三个窄接口（ADR-002），缺能力靠 `Null*` 表达而不是判空。

## Decision

1. **两个接口，一个默认实现类**：`IFactStore`（事实槽）与 `IConversationStore`（会话轮次）由 `PrefsAgentStateStore` 一个类实现（见第 6 条）。分开接口的理由是**可替换性不同**：事实槽是要单独查看与改动的结构化数据（ADR-005 Consequences），历史是会话轮次，宿主可能只想换掉其中一半。载荷已由 ADR-029 改为强类型 Blob。
2. **接口由内核持有，经静态装配根注入**：`AgentStateStores` 静态持有者，`LLMRuntimeSettings.Install()` 按 `LLMGlobalConfig_SO` 的选择填充，初值恒为 `NullAgentStateStore`。不给 `AgentCore` 加构造参数、不进 ADR-002 的三个窄接口——沿用本仓库既有惯用法（`LLMDispatcher` 全局单例、`AgentActionRegistry` 静态注册表）。
3. **读同步、写异步，且只在稳定实例上发生**：读点唯一且在 `AgentCore` 构造期（必须赶在第一轮之前），异步化会逼内核变成两阶段异步工厂，代价与收益不对称，故 `Load*` 是同步签名。写是 `UniTask`。宿主没传 `instanceId` 的**临时实例**（Guid 兜底，`AgentCore.cs:89`）既不读也不写，只告警一次。
4. **写时机定死在内核**：事实 write-through（`write_fact`/`forget_fact` 成功即写），历史 write-behind（`FinishTurn` 的 `AddRound` 之后）。压缩是轮末之后异步改写 `rounds` 的（`ContextManager.cs:255,298`），因此轮末落的是压缩前的历史——接受，代价上限是"读档后重新压缩一次、多花 token"，不丢对话内容。
5. **一个写者一份归档、整段覆盖**：`AgentFactsBlob` 与 `AgentRoundsBlob` 两个 `[Serializable]` 类型各占 `PrefsHelper` 的一份归档（它按 `T` 分文件），两条写路径（轮中途 / 轮末）互不覆盖，不需要跨写者锁。
6. **本地实现复用 `Lin.Runtime` 的 `PrefsHelper`，内核不写文件 IO**：`PrefsAgentStateStore` 只把强类型 Blob 交给 `PrefsHelper.Set/Get`，路径、锁、编码、XOR、WebGL 分支、删除与编辑窗口全部由既有 helper 承担。`sessionId` 因此是字典 key 而非文件名，**不需要任何字符校验**（`instanceId` 无校验这个洞随之消失，但它仍然是 `LLMAgent` 语义上的主键，宿主该给稳定值这条不变）。
7. **`LLMGlobalConfig_SO` 只做选择与引用**，不搬 `CompressionConfig_SO` / `AgentProfile_SO` 的任何字段。它引用的 Provider 配置是新增的抽象基类 `LLMProviderConfigBase_SO`（`CreateProvider` 与 `DefaultProviderName` 抽象、`RegisterToDispatcher` 与 `FallbackProviderConfig` 上收到基类），`LLMProviderConfig_SO` 变成它的派生类——否则全局配置会把 Provider 类型写死，加一种就连配置都要改。字段类型拓宽对既有资产安全：资产按脚本 guid 认类，`FallbackProviderConfig` 的序列化键名不变。`Resources/LLMProviderConfig.asset` 原位不动。

## Alternatives Considered

### 维持 ADR-011 §4：内核不持接口，宿主自己调 Export/Import
- Pros：零新抽象，与"内核不做 IO"字面一致
- Cons：一年零调用已经证伪这条路；自定义读写形态没有统一落点，每接一个宿主重写一遍
- Rejected：责任需要实现载体才成立

### `instanceId` 由 ProfileKey 自动派生
- Pros：省掉宿主配置，Learn 场景不用手填 `instanceId`
- Cons：sessionId 退化成 `{key}#{key}`，同种 NPC 共享记忆与历史——ADR-011 明确拒过（两个商人互相记得彼此与不同玩家的对话）；ADR-012 的 key 是资产级 FNV 哈希，无法区分场景实例
- Rejected：与实例隔离冲突。单实例 agent 想要这个行为，由宿主显式传 `instanceId = profile.ProfileKey`，是宿主的选择而非内核默认

### 单接口 `IAgentStateStore` 一次搬整个 agent 状态
- Pros：一次 IO、一个配置项、最少概念
- Cons：把"策划手改的事实"和"黑盒文本流水"焊死，无法只替换一半
- Rejected：分开的代价只是装配根多赋一个字段

### 接口全异步（含读）
- Pros：形状统一，服务器实现拿来就能写
- Cons：读在构造期，Unity 域卸载前 `OnDisable → Dispose` 不给 await 机会；要么两阶段异步工厂，要么静默丢数据
- Rejected：为一个还不存在的实现付内核重构的成本。**已知天花板**：服务器实现无法在不阻塞主线程的前提下满足同步 `Load`；升级路径是把预加载责任上移到宿主装配期，届时要改的是 `AgentCore` 构造入口而不是 store 接口

### 自造本地文件实现（直接写 `persistentDataPath/LLMState/{sessionId}.json`）
- Pros：路径、格式、编码全在 LLM 手里，明文可手改，能开目录入参给测试用
- Cons：`Lin.Runtime` 的 `PrefsHelper` 已经做了同一件事（按类型分档、`lock`、XOR、WebGL 分支、`DeleteKey`/`Clear`、配套编辑窗口），重写是最典型的 slop；且它要求"内核不做文件 IO"让路
- Rejected：复用优先。代价是落盘位置与失败语义交给 Lin 侧，见 Consequences

### 内核写盘后由宿主存档系统统管生命周期
- Pros：只有一份存档格式
- Cons：宿主存档格式尚未定义（`agent-host-design.md` 非目标表），等它会把本次也悬空
- Rejected：本次先把可替换的口定下来

## Consequences

- **"内核不做文件 IO"只在新的 store 路径上成立**（落地后对码修正的原稿说法）：`PrefsAgentStateStore` 自身只调 `PrefsHelper`，不碰磁盘；但 `LLM.Runtime` 里本来就有 `System.IO`——`ContextManager` 写 `LLMArchive/` 调试转储、`AgentMemory` 读回该目录（见下方 Consequences 第 4 条与 `AgentMemory.cs:314`）。ADR-011 §4 的那句约束的是**事实槽与会话历史的落盘**，这条守住了：两个 store 的写路径全程只有 `PrefsHelper` 一条，落盘时机改由内核在 `write_fact` / `FinishTurn` 触发。被取代的正是"时机归宿主"。写路径仍只有一条，且默认不启用（`None` = 今天的纯内存行为），不构成 ADR-011 担心的双通道；`Export*/Import*` 四个 API 一字不改地保留，store 的载荷就是它们的产物。
- 宿主此后**必须**为需要持久化的 agent 提供稳定 `instanceId`，否则静默不存（只有一条告警）。这条从前是 ADR-011 Consequences 里的软要求，现在有了硬后果：不落盘。
- `AgentCore` 多一个"是否稳定实例"的判定，来源于构造参数原值是否非空。
- **依赖方向**：`LLM.Runtime` 依赖 `Lin.Runtime` 的 `PrefsHelper`（asmdef 早已引用，`ContextManager` 也在用 `Lin.Runtime.Helper.Log`），本次不新增引用。代价是 agent 状态的落盘位置、编码与失败语义由 Lin 侧决定，LLM 无法单独控制——例如 `PrefsHelper.Save` 在缺 `NEWTONSOFT_JSON` 时跳过写盘并 `Log.Error`，这个判断只能写在 `Lin.Runtime` 里，`LLM.Runtime.asmdef` 没有声明该符号所以 LLM 侧 `#if` 恒假。
- 状态落在 `PrefsHelper` 的归档里：编辑器 `<repo>/EditorPrefs/{类型哈希}`、真机 `{persistentDataPath}/Temps/{类型哈希}`、WebGL `PlayerPrefs`。与既有的调试转储目录 `persistentDataPath/LLMArchive/`（`ContextManager.cs:511`）并存，前者是权威状态、后者仍是单向转储，互不读取。两个后果要认：① 磁盘内容是 XOR 后的字节，查看与改动走 `Lin/Prefs Manager` 窗口，不是手改明文文件；② 编辑器下 `EditorPrefs/` **当前被 git 跟踪**，每轮一次写就脏一次工作区。
- 换 ProfileKey = 换存档域（ADR-012 的既有结论），现在多了个可见后果：旧 sessionId 的两条归档条目成为孤儿。清理走 `PrefsHelper.DeleteKey<T>` 或 Prefs 窗口。
- 明确排除：服务器后端、多端同步与冲突合并、内容审核、append-only 逐条写入、退出 flush 钩子、写入节流、事实按时间过期、归档位置可配。逐条落点见设计稿非目标表。

See also: [agent-state-storage-design.md](../agent-state-storage-design.md)、[ADR-011](./ADR-011-memory-private-and-storage-keys.md)（§4 被本篇取代）、[ADR-005](./ADR-005-memory-fact-slots-only.md)、[ADR-002](./ADR-002-three-narrow-host-interfaces.md)、[ADR-001](./ADR-001-kernel-in-aot-llm-runtime.md)
