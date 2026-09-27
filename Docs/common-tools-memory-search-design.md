# 通用 Agent 工具与可检索对话记忆设计

> 状态：已实现  
> 日期：2026-09-20  
> 范围：五个通用工具、`search_memory`、上下文压缩正确性修复

> 2026-09-22 修订：ADR-025 已删除 `list_facts`，并将 `search_memory` 收窄、更名为 `search_past_conversation`。新动作只搜索当前请求不可见的旧轮次与压缩归档，不再重复搜索每轮全量注入的事实；本文后续旧名称与三源检索描述仅保留为原始设计背景。

## 1. 需求分析

本次落地三个目标：

1. 从 Mu 移植已确认的五个通用能力：当前时间、Agent Transform、导航状态、移动、停止导航。
2. 基于现有事实槽、会话历史与压缩归档设计 `search_memory`，能找回被上下文压缩移出的旧对话原文。
3. 修复调查中发现的压缩分区与异步替换问题，保证搜索语料和后续上下文不丢轮次。

成功标准：

- 当前时间工具不依赖场景对象；其余工具只作用于所属 `AgentHost` 层级实例。
- 查询能力使用 `[AgentTool]`，移动类副作用使用 `[AgentAction]`，继续享受先说后做、循环检测与超时保护。
- `search_memory` 只查询当前 agent 的事实、当前历史和本 session 的压缩归档，不能跨 agent。
- 压缩前后的聊天记录顺序稳定；压缩等待期间新增的轮次不会丢失或被旧快照覆盖。
- 压缩完成后的状态会再次持久化，重启后不会恢复成压缩前状态。

## 2. 现状调查

### 2.1 聊天记录会进入上下文

会。`LLMSession.AskAsync` 先调用 `ContextManager.GetHistoryMessages()`，再追加本轮 `user`。`AgentCore` 为避免作废轮污染历史，调用 `AskAsync(..., writeHistory: false)`，仅在本轮代际仍有效时执行 `Context.AddRound(input, finalText)` 并持久化。

最终发送顺序固定为：

```text
system：PersonaPrompt
        + LLMSession 长期 ContextBlocks
        + 本轮 EphemeralContext（事实槽 → 世界快照 → QueryableHint）
history：压缩摘要/保留的 user-assistant 历史
user：当前输入
```

工具调用只存在于当前 `AskAsync` 的工具循环：`assistant.tool_calls` 后跟 `tool` 结果，供同一轮的下一次模型请求使用。轮末只保存 `userText + 最终累计正文`，不保存 tool call、参数和 tool result；因此它们不会跨轮、持久化或进入压缩语料。

### 2.2 压缩后的拼接逻辑

`ContextManager` 保存 `ConversationRound`：

- 普通轮导出为 `user`、`assistant` 两条消息。
- 摘要轮导出为一条 `user` 消息，并包裹 `<compaction-summary>` 标签。

压缩分区为：

```text
固定头部（已有摘要 + 第一轮短对话）
  + 中段待压缩轮次
  + 最近尾部（轮数模式按 MinRecentKeep；token 模式按 TailTokenBudget）
```

中段会先按 JSON 原文写入：

```text
Application.persistentDataPath/LLMArchive/{sessionId}/*.json
```

随后由 LLM 生成结构化摘要；失败或关闭 LLM 压缩时使用机械摘要。运行态列表最终替换为：

```text
固定头部 + 被保留的特殊轮次 + 新摘要轮 + 最近尾部
```

### 2.3 已确认的正确性问题

1. `PartitionFold()` 把折叠区内所有短对话再次判为可固定，普通短聊天常出现 `fold = 0`；中间轮次不进入当前窗口，也没有生成摘要。
2. 折叠分区在取得压缩锁之前计算；LLM 摘要等待期间的新轮次可能被旧 `kept/fold` 快照覆盖，造成重复摘要、错序或丢失。
3. `AgentCore` 在 `AddRound()` 后立即保存，后台摘要完成后没有再次持久化；运行态与重启后的历史不一致。

这三项直接影响 `search_memory` 的完整性，必须与本功能同批修复。

## 3. 方案选型

### 方案 A：只搜索事实槽和当前活动历史

改动最小，但事实槽本来就每轮全量注入；压缩掉的旧原文无法找回，工具价值有限。

### 方案 B：搜索事实槽、活动历史和现有压缩归档（推荐）

复用已经存在的 `LLMArchive` JSON，不新增 MemoryEntry、索引文件或数据库。近期内容查内存，压缩掉的内容查冷归档，以确定性文本匹配排序。

代价是首次搜索需要文件读取；先用扫描预算限制最坏开销，确认出现性能瓶颈后再考虑缓存或索引。

### 方案 C：移植 Mu MemoryBridge / embedding / 向量检索

召回能力更强，但会恢复已被 ADR-005 明确删除的 MemoryEntry、importance、缓存、索引和淘汰体系。当前没有 embedding 基建，不采用。

## 4. 推荐架构

### 4.1 通用工具

新增三个最小实现文件：

```text
Runtime/Tools/AgentTimeTools.cs
Runtime/Tools/AgentTransformTools.cs
Runtime/Tools/AgentNavigationTools.cs
```

接口：

```csharp
[AgentTool("get_current_time", "获取本机当前日期、时间与星期。")]
public static string GetCurrentTime();

[AgentTool("get_agent_transform", "获取当前 Agent 的世界坐标、旋转与缩放。")]
public string GetAgentTransform();

[AgentTool("get_navigation_status", "获取当前 Agent 的 NavMesh 导航状态。")]
public string GetNavigationStatus();

[AgentAction("move_to", "让当前 Agent 通过 NavMesh 移动到世界坐标。", Idempotent = false)]
public UniTask<AgentActionResult> MoveTo(float worldX, float worldY, float worldZ);

[AgentAction("move_to_player", "寻路走到玩家身边停下，用于当面搭话或递交。", Idempotent = false)]
public UniTask<AgentActionResult> MoveToPlayer();

[AgentAction("stop_navigation", "停止当前 Agent 的 NavMesh 导航。")]
public UniTask<AgentActionResult> StopNavigation();
```

约束：

- `AgentTransformTools`、`AgentNavigationTools` 是成员组件，只被所属 `AgentHost` 扫描。
- `AgentNavigationTools` 使用 `[RequireComponent(typeof(NavMeshAgent))]`。
- ~~不移植 Mu 的"到达后自动转向玩家"、Player 查找、展品、Luban 或切场景逻辑~~ **Player 查找这一半已被推翻**（2026-09-22，用户在 `AgentNavigationTools` 留的 TODO 2）：`move_to_player` 落在本插件的成员组件里，理由见 §8 该行的订正。"到达后自动转向玩家"、展品、Luban、切场景仍是不做的。
- 目标点不在 NavMesh 上时先在 `destinationSnapRadius`（Inspector 可调，默认 2 米）内吸附最近可行点，吸附不到才明确失败；**仍不做 Transform 传送兜底**（原口径不变，只是"找不到"之前多一次吸附）。
- 停靠点算法：把玩家脚下投影到 NavMesh → 沿"玩家→本 Agent"的水平方向退回 `playerStopDistance`（默认 1.5 米）→ 再吸附一次。**不写 `agent.stoppingDistance`**，否则会泄漏给 `move_to`（它按字面坐标理解目标点）。
- 字符串使用 ZString；Inspector 字段如有暴露，使用中文标签。

### 4.2 `search_memory`

由于查询必须定位当前 `AgentCore`，沿用 `list_facts` 的既有方式实现为只读 `[AgentAction]`，不扩展 `[AgentTool]` 的上下文注入协议：

```csharp
[AgentAction(
    "search_memory",
    "搜索当前 Agent 已记住的事实和过往对话。玩家提到以前说过的内容时使用。",
    Idempotent = true)]
public static UniTask<AgentActionResult> SearchMemory(
    AgentActionContext ctx,
    string query,
    int maxResults = 5);
```

输入边界：

- `query` Trim 后必须非空，最长 200 字符。
- `maxResults` 钳制到 `[1, 8]`。
- sessionId 只取 `ctx.Agent.SessionId`，模型不能指定其他 agent。

检索源：

1. `AgentMemory.Facts`：事实 key/value。
2. `ContextManager.SnapshotRounds()`：当前普通轮和压缩摘要。
3. `LLMArchive/{sessionId}`：已经被压缩或大内容修剪移出的原始轮次。

排序规则按稳定优先级比较，不引入不可解释的浮点评分：

1. 完整 query 子串命中优先。
2. 命中的 query 词数量多者优先。
3. 来源优先级：事实 key → 事实 value → 用户原话 → 助手原话 → 压缩摘要。
4. 同级按时间新者优先。

分词仅做大小写归一化与空白/中英文标点切分，最多取 8 个非空词。不使用 Regex、LINQ、编辑距离、LLM 重排或 embedding。

结果最多返回 8 条，每条包含来源、时间和裁剪后的原文片段。相同时间戳与相同规范化文本只保留一次，避免软修剪归档与后续压缩归档造成重复。

### 4.3 归档读取边界

- 从最新归档文件向旧文件扫描，单次最多读取 2 MiB；达到预算时在工具结果末尾说明“旧归档未完全扫描”。
- 不建立常驻索引或缓存。若真机分析证明 2 MiB 线性扫描不可接受，再增加 session 级惰性缓存。
- `EnableArchive = false` 时仍搜索事实槽、活动历史和摘要，但明确无法恢复已压缩原文。
- `ClearHistory()` 必须同时清理当前 session 的归档，否则玩家清空聊天后 `search_memory` 仍会召回旧内容。
- 归档目录必须通过统一方法解析并验证最终绝对路径仍位于 `persistentDataPath/LLMArchive` 下，删除时不得直接使用未经校验的 sessionId 路径。

## 5. 压缩正确性调整

### 5.1 分区修复

第一轮短对话只由 `PinnedPrefixLen()` 固定一次。`PartitionFold()` 的中段不再按 `IsPinnableUserTurn()` 保留普通短轮，只跳过已经存在的摘要轮。

### 5.2 异步替换修复

压缩任务取得 `_compactLock` 后重新判断阈值并重新计算分区，避免排队任务使用旧快照。

摘要完成后不再用“旧 kept + 当前 head/tail”重建整个列表，而是：

1. 在当前列表中定位本次 fold 的实际对象。
2. 只删除仍存在的这些对象。
3. 在第一个被删除位置插入摘要。
4. 保留等待摘要期间追加的所有新轮次。

若 fold 对象已经不存在，则放弃本次替换，不写重复摘要。

### 5.3 压缩后持久化

`ContextManager` 在成功替换摘要后发出无参数变更事件；`AgentCore` 用具名方法订阅并复用现有 `SaveHistory()`。这样先保存新增原始轮次，摘要完成后再保存最终压缩状态，不让 Context 层依赖具体存储实现。

## 6. 关键流程

### 6.1 普通对话与压缩

```text
本轮完成
  → AddRound(user, assistant)
  → 保存当前历史
  → 后台取得压缩锁并重新分区
  → 原始 fold 写入 LLMArchive
  → 生成摘要
  → 原位删除 fold、插入 summary（不碰新增轮次）
  → 变更事件触发再次保存
```

### 6.2 记忆搜索

```text
模型调用 search_memory
  → 校验 query / maxResults
  → 搜事实槽
  → 搜当前 Context rounds
  → 在 2 MiB 预算内搜压缩归档
  → 去重并按稳定规则取 Top N
  → 文本结果回灌当前工具循环
```

## 7. 数据结构

不增加持久化记忆实体。继续复用：

- `AgentFact { Key, Value, Timestamp }`
- `ConversationRound { UserMessage, AssistantMessage, Timestamp, IsSummary, IsPinned }`
- 现有归档 JSON：`List<ConversationRound>`

实现内部只需要一个非序列化候选结构，保存来源、文本、时间、完整命中与词命中数；它不写盘、不暴露给模型。

## 8. 非目标（明确不做）

| 不做 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| MemoryBridge / MemoryEntry / importance / recency 综合评分 | 上一代因此膨胀，ADR-005 已拒绝 | 只有确定需要大规模情节记忆时另写 ADR |
| embedding、RAG、向量数据库 | 当前没有基建 | 独立可替换的记忆检索后端 |
| 自动从每轮对话提炼并写事实槽 | 会增加模型调用和错误记忆 | 显式 `write_fact` |
| 保存跨轮 tool call 与 tool result | 当前 ConversationRound 不表达工具协议 | 若产品需要工具审计，独立 trace/audit 存储 |
| ~~玩家查找~~ → **已实现 `move_to_player`**（2026-09-22 用户 TODO 推翻本行前半） | 原拒绝理由是"属于玩法层"；实际落点选了插件成员组件，因为停靠算法与玩法无关（只读 tag + NavMesh 投影） | 自动面向玩家、展品、Luban、切场景仍按本行原判，落点在具体游戏的成员 Action 组件 |
| 为归档搜索预建索引或缓存 | 当前数据规模没有证据需要 | 性能分析证明线性扫描不足后再加 |

## 9. 验收与验证

最小自动检查：

1. 五个工具能被注册；不同 AgentHost 的成员工具不串实例。
2. `move_to`、`move_to_player`、`stop_navigation` 走 AgentAction；缺 NavMesh、未在 NavMesh、非法坐标、场景里没有 `Player` tag、玩家脚下吸附不到 NavMesh，都不执行并返回明确失败。
3. `search_memory` 能分别命中事实、当前用户原话、当前助手原话和归档原文；不同 session 互不可见。
4. 搜索排序稳定、结果数受限、重复归档不重复返回、2 MiB 截断有提示。
5. `ClearHistory()` 后旧归档不可再检索。
6. 普通短对话超过压缩阈值后确实生成摘要。
7. 摘要等待期间追加新轮次，最终顺序和内容均不丢失。
8. 压缩完成后保存最终摘要状态，重新构造 AgentCore 不会恢复压缩前历史。

需要人工 PlayMode 验收：

- NavMeshAgent 在真实烘焙 NavMesh 上移动、停止及状态文本是否符合预期。
- 使用真实 Provider 触发压缩后，模型能利用摘要继续对话，并能通过 `search_memory` 找回归档原文。
