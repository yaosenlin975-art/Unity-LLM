# NPC 在场感三条想法 × 既有决策：冲突表与合规落点

状态：设计稿，未开工。行号按本工程 `Assets/Plugins/LLM`（内嵌仓库）当前 HEAD 逐条读码核实；ADR 与非目标表读自同插件设计记录（`Docs/decisions/ADR-*.md`、`Docs/*-design.md`），引用时注明原文。

## 0. 结论先行

| 想法 | 判定 | 依据 |
|---|---|---|
| 1 每轮刷世界快照进 system | **原样做违反 ADR-006 §3**；拆成"低频进快照 + 高频现查"后可做，且几乎不加代码 | ADR-006 第 3 条硬约定原文：「核心快照只允许低频值，**时间、坐标、周围实体一律不进快照**，改由 observe 类工具现查」 |
| 2 编辑器配可用动画动作 → 注入 NPC 系统提示词 | **"注入提示词"这半句被明文禁掉**；"按 NPC 限制可用动作"这个需求可做，且落点在玩法层驱动，**插件零改动** | `npc-streaming-performance-design.md:12`：「具体动画名称和完整动作目录**不进入系统提示词、工具 schema**、聊天历史或压缩摘要」；同篇 `:451` 非目标表：「把完整动作列表注入系统提示词 → token 随资源增长，污染角色认知与缓存前缀」 |
| 3 靠近时概率主动搭话 | **可做**，但组件必须住在玩法层，不能进插件；ADR 已经为它预留了唯一入口 | ADR-015 尾条：「明确排除任何形式的定时起轮，包括'空闲 N 秒主动搭话'。将来的唯一落点是宿主的世界调度器调 `Notify()`……不下沉进内核」；`agent-host-design.md:90` 非目标：「角色点击、碰撞或距离检测 → 落点＝具体项目交互组件」 |

三条的共同形状：**要加的都不是插件里的新机制，而是宿主/玩法层的三小段代码 + 一个现成接口的正确用法。**

## 1. 注入链的真实形状（先摆事实，否则成本账算不平）

```
AgentCore.RunTurnAsync:241   await session.AskAsync(input, HandleChunk, BuildInjection(), false, ct)
AgentCore.BuildInjection:377 = Memory.RenderBlock()（事实槽）
                             + world?.GetCoreSnapshot()（宿主给的核心快照）
                             + profile?.QueryableHint
LLMSession:195-196           request.AddContextBlock(ephemeralContext)
LLMRequest.BuildSystemContent:52-68  把 persona 与各上下文块拼成【一条扁平 system 字符串】
OpenAIProvider:161-163       messages.Add({ role:"system", content: systemContent })
```

要点，也是上一版草稿我搞错的地方：

- **没有 content block 数组、没有 `cache_control` 断点**（第一份调研报告说的那套在本副本不存在，别照它设计）。system 是一整条字符串。
- 所以 system 里任何一个字每轮变，**它后面整段历史的前缀缓存全部作废**——不是"只影响 system 那一小段"。OpenAI 兼容网关按前缀命中计价，这正是 ADR-006 §3 立规的理由，也是它 Consequences 里那句「`CacheHitTokens == 0` 基本等于有人往 system 或核心快照里塞了每轮变的内容」的来由。
- `AgentHost` 已经把"宿主往快照里写东西"的口子开好了：`SetCoreSnapshot(string):148`，读出口 `GetCoreSnapshot():237-245`（现内容＝好感行 + Inspector 里那串 `coreSnapshot`）。

## 2. 想法 1：世界快照 → 拆成低频与高频两半

### 2.1 你要的其实是什么

"玩家开口的一瞬，NPC 得知道此刻世界上有谁、玩家在干嘛"。这个需求成立，且现在确实缺——**但缺的是"现查"的入口，不是"预塞"的通道**。

### 2.2 落点

| 内容 | 变化频率 | 落点 | 代码量 |
|---|---|---|---|
| 场景名、当前任务阶段、时段档位（白天/夜晚）、剧情标记 | 低频（一轮对话内基本不变） | 宿主调 `AgentHost.SetCoreSnapshot(...)`，`BuildInjection` 自动带上 | 玩法层拼模板串，**插件 0 行** |
| 玩家坐标、NPC 自身坐标、附近有谁、对方正在做什么 | 每轮甚至每帧变 | **observe 类工具现查**，只在这轮真需要时才付 token | 见下 |

现查工具已经有半个网络：`get_agent_transform`（自身坐标）、`get_current_time`、`get_navigation_status`、`get_affinity`。**唯一缺口是"周围实体"**——ADR-006 §3 点名要它走现查，但没人名。补一条成员工具即可，而且这不是我发明的规避：ADR-005 在拒绝"Generative Agents 式检索打分"时已经写好落点（`:29`）——「将来的落点是 observe 类工具的返回值——检索结果每次不同，属于每轮变的内容，不得进 `GetCoreSnapshot()`（那里只允许低频值）」。两篇 ADR 指向同一个口子。

```csharp
// 放玩法层（Learn），不放 LLM.Runtime：
// ADR-013 的 Alternatives 明确拒绝"全局 Agent 注册表"，而视野查询不需要它
[AgentTool("observe_scene", "查看附近有什么：实体名、距离、大致方位。要谈'谁在这儿'之前先调这个。")]
public string ObserveScene()   // Physics.OverlapSphereNonAlloc + 组件读取，按距离升序，上限 6 条
```

要点：
- 半径/上限用常量，超出写「另有 K 个未列出」，别默默截断——模型会以为场上没人。
- 玩家定位沿用既有做法 `GameObject.FindGameObjectWithTag("Player")`（`AgentNavigationTools.cs:116` 已经这么干），**不新造 `SetPlayer` 通道**，多一个约定就少一个真源。
- 一律 `ZString`，别顺手加 LINQ：本工程 `LLM` 目录现状是 0 处 `using System.Linq`，而工程内并未安装 ZLinq（`Library/PackageCache`、`Assets/Packages` 均无），一旦引入就没有零分配替代可用。
- 摘要文本由玩法层拼模板，**不要"让模型先总结一遍世界"**：那等于每轮多打一次计费请求，换回来的是又一份要进 system 的动态文本，缓存账直接倒亏。

### 2.3 若你仍要"每轮无条件刷进快照"

那是一次设计变更，需要新 ADR 取代 ADR-006 §3 的这一句，并附实测：先在沙盒/请求日志窗口看 `CacheHitTokens`（ADR-006 §1 已列出补齐流式用量上报的前置项 ⚠），拿到"刷与不刷"两种排版下的命中率差，再决定。经验量级：20 轮 × 每轮 3K 历史 token，缓存失效等于把已缓存部分按全价重付。

### 2.4 非目标

不持久化世界状态、不做世界事件流、不把快照写进 `AgentMemory`（ADR-005：记忆只有历史＋事实槽；检索结果不得进快照）、不做 AOI 分区、不考虑多玩家。

## 3. 想法 2：按 NPC 配动画动作

### 3.1 需求拆开后，"注入提示词"这半句是多余的

模型现在已经有拿到清单的正确途径：

```
[AgentTool("list_animation_states", "列出该 Agent 能播放的动画状态名，以及能写的动画参数名（名字必须逐字取自这里）。")]  AgentAnimationTools.cs:42
[AgentAction("play_animation_state", "…stateName 必须逐字取自 list_animation_states…")]  :71
[AgentAction("set_animation_parameter", "…名字必须取自 list_animation_states。")]        :90
```

清单按需查、名字逐字取自查询结果——这就是 `npc-streaming-performance-design.md:12` 与 ADR-006 §3 共同要的形状（工具声明走 `tools` 字段，不进 system）。把同一份清单再抄进 system 只会：token 随美术资源线性增长 + 缓存前缀被污染 + 与工具返回值不一致时模型拿到两份互相矛盾的清单（该篇 `:451` 拒绝的正是这个）。

### 3.2 真缺口：条目级白名单

- ADR-016 的 `AgentHost.SetToolEnabled / IsToolEnabled` 门控粒度是**工具名**（`play_animation_state` 整体开/关），管不到"这个 NPC 不许播 `dance`"。
- 手势侧实例级只有"换整份 `NpcPerformanceProfile_SO` override"和 `SetSuppressed`，也没有条目级。
- 清单内容由驱动给：`IAgentAnimationDriver`（`AgentInterfaces.cs:56`），而驱动实现按 `agent-animation-actions-design.md:127` 住在 `Learn.AgentAnimation`（本插件副本内未见，符合设计）。

**落点**：白名单加在驱动实现侧，一处生效两条路径——

```csharp
// 玩法层 Learn.AgentAnimation 的驱动组件上
[SerializeField, TextArea] private List<string> allowedStateNames;   // 空 = 不限制
// ListStates() 只列白名单内的；PlayState() 对白名单外的名直接返回失败
```

于是：`list_animation_states` 返回的就是这个 NPC 真正会的那几个（模型看到的词表＝可执行的词表，不存在"能问出来但播不了"），`play_animation_state` 的失败回灌天然成为第二道闸。插件侧、schema 侧、提示词侧**均零改动**。

这与 `agent-animation-actions-design.md:134` 的既有结论一致：不做模糊匹配与自动纠正，"模型拿到的应当是确定的词表"——白名单正是让词表变确定的最小改动。

### 3.3 编辑器配置形态

Inspector 中文（AGENTS.md 硬约定），配置载体就是那个驱动组件上的列表。中文标签沿用本插件既有做法：`GestureDefinition` 的每个字段都用原生 `[InspectorName("中文")]`（`GestureDefinition.cs:17-28`）——不引 Odin（README:20-25 的依赖表里没有 Sirenix），也不写 `CustomPropertyDrawer`（本插件当前一个都没有）。要更好用就配一个 `Editor` 窗口从当前 AnimGraph/Animator 反查候选名填表——但那条已被 `agent-animation-actions-design.md:129` 记过一次非目标（"同一件事做两遍"），要做也放 `Learn.AgentAnimation` 的 Editor 扩展。第一期不做。

### 3.4 与手势目录不合并

`NpcGestureCatalog_SO` 服务"说话时自动比划"（台词→`RuleGestureIntentClassifier`→`GestureResolver`→`GestureDefinition[]` 取 `Clip`，`NpcGestureCatalog_SO.cs:19-21`），`AgentAnimationTools` 服务"模型点名要做"。共用的是 Animator 资源，不是职责；合并会让 resolver 的降级链（`NpcGesturePipelineTests.cs:114` 的 `Resolver_PicksHighestWeightMatchingUpperBodyGesture` 在断言它）被白名单语义污染。将来若真要共用词表，唯一落点是 gesture 目录引用同一份白名单，需新 ADR。

## 4. 想法 3：靠近概率搭话（本期只设计）

### 4.1 归属

判定与定时器全在玩法层组件（`agent-host-design.md:90` 的距离检测落点＝项目交互组件；ADR-015 要求主动起轮由宿主世界调度器发起）。插件提供的出口就一个：`AgentHost.Notify(string worldEvent)`（:141）→ `AgentCore.Notify:155` → `Enqueue(text, true)` 前缀「【事件】」。

### 4.2 闸门顺序（越便宜越靠前）

```
距离² ≤ radius²                       // 无开方
→ 朝向 Dot(forward, toPlayer) ≥ cos(半角)   // NPC 得"看着"他
→ 好感 ≥ 门槛                          // NpcAffinityController.CurrentValue / CurrentLevel（同步，:45-51）
→ 冷却 now - lastAt ≥ cooldown             // 运行时字段
→ 概率 Random.value < 该好感档的 chance    // 好感分档给概率，别用单一常数
→ 空闲判定 host.Core.IsBusy == false       // AgentCore.cs:76 已有，零改动
→ host.Notify("玩家 <name> 走到了你面前")
```

触发体积建议 `OnTriggerStay` + 0.2s 节流，而不是自己 `Update` 算距离：Collider 已经由关卡摆好，自算要额外维护"谁在附近"。代价写进配置约定：玩家需带 `Collider(isTrigger)`，不做代码兜底。

### 4.3 三条必须写进设计的约束

1. **`IsBusy` 必判**。`Notify` 在飞时不排队而是抬代际 + 顶 `pendingInput` 单槽（`AgentCore:172-180`），等于用一句招呼打断玩家正在等的回答。
2. **全局节流放在调度器侧**。ADR-006 §1 决定"成本只统计不限流"，运行时不会有闸门护着你：5 个 NPC 同帧各自 `Notify` = 5 次计费往返。宿主自己要有一个"一段时间内只允许一个 NPC 主动开口"的令牌，这是世界调度的一部分，不是内核闸门。
3. **好感没有玩家维度**。好感键＝该 NPC 的 `sessionId`（`NpcAffinityStore.cs:18-21`），一个 NPC 全局只有一份值。单玩家下成立；要"对不同玩家不同态度"就得改存储键，那是 ADR-011（记忆私有与存储 key）的领地，另开 ADR，不要顺手加。

### 4.4 冷却要不要持久化

本期用运行时字段（切场景即忘）。要跨会话记住，只能走 store 槽（`LLM.Runtime` 不引用 prefs 包），而 ADR-030 的反射装配加**新槽**要同步改三处：`LLMGlobalConfig_SO` 加 `[StoreTypeSelect]` string 字段、`LLMRuntimeSettings.InstallStateStore` 加一次 `InstallSlot` 调用、holder 加按槽哨兵（IL2CPP 侧自己加 `[Preserve]`）。新**实现**才是零登记（`StoreTypePicker` 用 TypeCache 收集）。为一句"上次几点打过招呼"付这个成本不值——列为非目标，将来落点就是上面这三处。

### 4.5 非目标

不做全局搭话优先级/排队、不做对话队列、不做 LOD 降频、不做"玩家喊名字"反向触发、不把 `TrySeedAssistantMessage`（`AgentCore:158-165`，要求 `RoundCount == 0`）拿来当主动开口的通用出口——它只够"首次见面的固定开场白"。

## 5. 落地顺序与验收

| 步 | 内容 | 位置 | 编译 | 人眼验收 |
|---|---|---|---|---|
| 1 | `AgentHost.SetCoreSnapshot` 填低频世界段（场景/任务/时段） | 玩法层 | `node Temp/vt/build-all.js` | 沙盒窗口看这轮 system 里有没有那段 |
| 2 | `observe_scene` 成员工具 | 玩法层 Learn | 同上 | 问 NPC"现在还有谁在"，看它是否先调工具再答 |
| 3 | 驱动侧动画白名单 | `Learn.AgentAnimation` | 同上（不涉及插件） | Inspector 配 3 个，让 NPC 播一个没配的，看它是否被拒并自我纠正 |
| 4 | `NpcGreetingTrigger` 闸门组件 | 玩法层 | 同上 | 走过去/背对走/低好感走三种情形；冷却窗口内不重复 |

第 2、3 步可以并行，且都**不改插件一行**——这是本次设计与上一版草稿最大的差别：不新增 `AgentWorldSense`、不新增 `NpcMoveSet` 类型、不往 `BuildSystemPrompt` 里追加清单。

## 6. 需要你拍板

1. 低频世界段（场景/任务/时段）由谁写：宿主脚本拼模板，还是策划在 `AgentProfile_SO.QueryableHint` 里填一句？（后者零代码但按角色配，前者按场景配）
2. `observe_scene` 的可见范围：只报名字+距离，还是带上"对方正在做什么"（`AgentHost` 侧现在没有现成状态行，要另加只读出口，插件就不是零改动了）。
3. 搭话的概率分档表放哪：组件上 `[SerializeField]`（预制体粒度，美术可改）还是 profile（角色粒度）？我倾向组件——它是场景行为，不是角色人格。
