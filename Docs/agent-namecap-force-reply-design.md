# NameCap 防换参狂搜与工具停手收口设计

> 状态：已实现（2026-09-27，待 Test Runner 行为验收）  
> 关联 ADR：[ADR-023](./decisions/ADR-023-delete-maxtoolrounds-namecap-force-reply.md)  
> 触发现象：游戏操作做到一半被工具轮次总闸腰斩，agent 玩家侧无任何正文回复。

## 用户确认的三条口径

| # | 口径 |
| --- | --- |
| 1 | NameCap 默认：全局 **4**，记忆类工具 **3** |
| 2 | 凡「停手让模型收口」的指令，**强制「没话也说一句」** |
| 3 | **`MaxToolRounds` 整项删除**，没有必要 |

## 需求分析

### 原始意图

`MaxToolRounds` / 耗尽收尾最初要解决的是：

> LLM 在记忆、人设中都搜不到答案时，自行把问题拆词（「你喜欢吃什么」→「吃」「食物」「美食」…），对 `search_past_conversation` 一类工具换参狂搜，观感像卡死。

### 实现偏差

该动机被做成了 **一轮内所有工具往返的总上限**，而不是工具级防循环：

| 层 | 现状 | 问题 |
| --- | --- | --- |
| `LLMSession.MaxToolRounds` | 整轮 tool 执行封顶，耗尽时丢弃本批 `tool_calls`、不发收尾请求 | 游戏多步操作被腰斩；与「防搜旋钮」不是同一件事 |
| `LoopGuard` L2 | 仅 `name + 规范化 args` 同参计数 | 管不住换词（不同 args） |
| `LoopGuard` L1 | 仅遥测 | 同名换参在裁决层是盲区 |
| 耗尽收尾 | 无正文 → `ResolveFinalText` 回 `""` | 玩家静默；历史可能空/半截 |

### 目标

1. **删掉** 轮次总闸 `MaxToolRounds`（及由其派生的 `ToolRoundsExhausted` / `LastTurnExhaustedRounds` 耗尽路径）。
2. **换词狂搜** 由 **NameCap**（LoopGuard 按 `tool name` 的本轮上限）承担。
3. **凡停手收口**（NameCap / L2 的 Warn、Block，以及同类回灌文案）指令里写明：**即使搜不到也要直接、简短回复玩家**。
4. 成本失控交给既有 **墙钟** `TurnDeadlineSeconds` 与 LoopGuard 其它档；不再另设「整轮工具次数」旋钮。

## 方案选型

| 方案 | 结论 |
| --- | --- |
| 保留 `MaxToolRounds` 只改默认值/只计 search | 拒绝（用户确认删设定）：与防搜职责重叠，且会造成游戏操作腰斩 |
| 耗尽后 FinalContentPass（无工具收尾请求） | **随 `MaxToolRounds` 一并取消**：没有总闸就没有「额度耗尽」触发点 |
| NameCap 封顶用 Block，**不因同名/同参次数进入 Abort** | **采纳**：模型仍在本轮内，可按指令收口；真失控靠墙钟 |
| 停手文案强制「没话也说一句」 | **采纳**（口径 2） |
| 空正文时 Timeout/Failed 用 `FallbackLines` | 保持既有行为 |

## 决策概要

详见 ADR-023，摘要：

1. **删除** `AgentProfile_SO.MaxToolRounds`、`LLMSession.MaxToolRounds`、`LastTurnExhaustedRounds`，以及 `RunToolLoopAsync` 里 `round >= MaxToolRounds` 分支。
2. **删除** `EAgentOutcome.ToolRoundsExhausted`（枚举值、`AgentCore` 映射、宿主 `DescribeEmptyOutcome` 分支、相关单测）。
3. **新增 NameCap**（见接口设计）：全局默认 4；记忆三件套默认 3（`write_fact` / `search_memory` / `search_past_conversation`——`list_facts` 已由 ADR-025 删除）。
4. **LoopGuard 阶梯调整**：幂等工具（含 NameCap 与 L2）**最高只到 Block**，同名/同参重复 **不再 Abort**；非幂等阈值恒 1 仍 Block。格式错 Abort、墙钟 Timeout 保留。
5. **回灌文案**统一在 Warn/Block 中加入强制收口句。
6. **`ResolveFinalText`**：仅 `Completed` 无文本时回 `""`；其余空文本（Timeout/Failed/LoopAborted）走 `PickFallback()`（与现实现中非 Completed/Exhausted 分支一致，删掉 Exhausted 特例即可）。

## 接口设计

### 删除清单

| 位置 | 删除内容 |
| --- | --- |
| `AgentProfile_SO` | `MaxToolRounds` 字段；Validate 中以它为因子的断言改为见「墙钟自洽」 |
| `LLMSession` | `MaxToolRounds`、`LastTurnExhaustedRounds`、耗尽分支与相关日志 |
| `AgentInterfaces.EAgentOutcome` | `ToolRoundsExhausted` |
| `AgentCore` | 构造里写入 session.MaxToolRounds；`FinishTurn` 中 Exhausted 映射；`ResolveFinalText` 中 Exhausted 与 Completed 并列的空串特例 |
| `AgentChatPanel` | `DescribeEmptyOutcome` 的 ToolRoundsExhausted 分支 |
| 测试 / README / 内核设计稿 | 见「文档与测试同步」 |

历史资产里若已序列化 `ToolRoundsExhausted` 枚举名：删除后反序列化可能失败；项目内无存档 outcome 字段，**可接受**。宿主若 `switch` 该值需在实现时编译期暴露。

### LLMSession（工具循环）

```text
RunToolLoopAsync:
  for round = 0..∞
    toolCalls = SendRoundAsync(...)
    if toolCalls 为空 → return 已累积正文
    AppendToolResultsAsync(...)   // AbortTurn 时提前 return
    // 无 MaxToolRounds 检查
```

不新增 `FinalContentPassAsync`。模型何时停手：自己给出无 tool_calls 的正文，或 LoopGuard Block 后按回灌指令改口，或墙钟取消。

### LoopGuard

```csharp
/// NameCap：0 = 不启用；>0 时按 name 本轮计数
public LoopDecision Decide(string signature, string name, bool idempotent, int repeatLimit);

// 内部裁决顺序（必须遵守）：
// 1) !idempotent → session 配额恒 1，第二次起 Block（不变）
// 2) NameCap（键 name）：hits <= limit Allow；hits == limit+1 Warn；hits >= limit+2 Block（不 Abort）
// 3) L2（键 signature）/ L3：阶梯同形，但 hits >= limit+2 之后 **保持 Block**，去掉 Abort 档
// 4) repeatLimit < 0 → 仅关闭「该工具的 L2 计数」，不关闭非幂等，不关闭 NameCap（NameCap 用 NameRepeatLimit < 0 单独关）
```

`BeginTurn`：清空 L2 计数、L3 哈希、格式错，并 **清空 name 计数**（NameCap 为本轮口径）。

`ELoopVerdict.Abort` **保留**（格式错等仍使用）；**幂等重复类裁决不再返回 Abort**。

### 配置

#### AgentProfile_SO

| 字段 | 值 |
| --- | --- |
| `PerNameToolCallLimit` | 新增，`[Range(0, 12)]`，默认 **4**，`0 = 关闭 NameCap` |
| `GlobalRepeatLimit` | 不变，仅管 L2 同参 |
| `MaxToolRounds` | **删除** |
| `FallbackLines` Tooltip | 含：请求失败/超时/循环拦截后仍无正文时的空文本 |
| 墙钟 Validate | 不再乘 `MaxToolRounds`；改为固定假设最多 8 次工具往返做 **警告级** 自洽提示（不是运行时闸） |

#### 工具元数据

`AgentToolAttribute` / action 声明增加：

```csharp
/// <summary>
/// 同工具名本轮次数上限（换参也计）。0 = 继承 PerNameToolCallLimit；-1 = 该工具关闭 NameCap。
/// 与 RepeatLimit（同参）正交。
/// </summary>
public int NameRepeatLimit { get; set; } = 0;
```

内置动作默认：

| 动作 | Idempotent | NameRepeatLimit | RepeatLimit |
| --- | --- | --- | --- |
| `search_past_conversation` | true | **3** | 0（继承 GlobalRepeatLimit） |
| `write_fact` / `forget_fact` | true | **3** | 0 |

### 强制收口文案（口径 2）

常量拼进 **Warn** 与 **Block**（NameCap 与 L2 共用句式）：

```text
// Warn
[loop-warning] 你已调用 {name} {hits} 次（含不同参数），重复或换词搜索不会再带来新信息。
即使没有结果，也请直接根据已有信息简短回复玩家，不要再调用该工具。

// Block
[Blocked] {name} 本轮调用次数已达上限，已拦截。
即使没有结果，也请直接根据已有信息简短回复玩家；本轮不要再调用该工具。
```

非幂等 Block：

```text
[Blocked] {name} 带副作用，同一签名本轮/session 只能成功执行一次，已拦截。
请根据已有信息直接回复玩家，不要反复尝试同一调用。
```

（实现时可微调措辞，**必须保留**「即使没有结果也要回复」语义。）

### AgentCore.ResolveFinalText

```csharp
if (!string.IsNullOrEmpty(answer)) return answer;
if (turnText.Length > 0) return turnText.ToString();
if (outcome == EAgentOutcome.Completed) return "";
return PickFallback(); // Timeout / Failed / LoopAborted / 其它
```

### EAgentOutcome（删除后）

```csharp
public enum EAgentOutcome
{
    Completed,
    Stale,
    Timeout,
    Failed,
    LoopAborted
}
```

## 数据结构与流程

### 流程 A：游戏多步操作（不再被次数腰斩）

```text
玩家:「去把箱子打开」
  observe → move_to → interact_open → observe …（可超过 3 次工具往返）
  只要墙钟未到、未触发 LoopGuard Block，模型可继续直到自行给出正文
  outcome = Completed，历史写正文
```

### 流程 B：记忆换词狂搜

```text
玩家:「你喜欢吃什么」（记忆/人设皆无）
  search_past_conversation("吃")   → nameHits=1 Allow
  search_past_conversation("食物") → nameHits=2 Allow
  search_past_conversation("美食") → nameHits=3 Allow（记忆类 limit=3 → 第 3 次仍 Allow）
  search_past_conversation("饮食") → nameHits=4 → Warn（强制收口句）
  search_past_conversation("口味") → Block（强制收口句）
  → 模型应停止该工具，直接回复（人设兜底或「我不太确定」类）
  → 若模型改调其它工具：受该工具 NameCap / L2 / 墙钟约束
  → 全程不再依赖 MaxToolRounds
```

### 流程 C：Block/超时后仍无正文

```text
OnTurnFinished(finalText, outcome)
  finalText = PickFallback() 当 answer 与 turnText 皆空且 outcome != Completed
  FallbackLines 空 → ""（宿主 DescribeEmptyOutcome 占位）
```

## 测试要点

| 用例 | 断言 |
| --- | --- |
| 无 MaxToolRounds | `AgentProfile_SO` / `LLMSession` 无该属性；多步不同工具可执行超过 3 轮且 Completed |
| NameCap 换参 | 同 name 不同 args，第 limit+1 次含 Warn 文案且含「简短回复」；第 limit+2 次 Block 且含同句 |
| NameCap 记忆类 | `search_past_conversation` 按 3 计 |
| L2 同参 | 仍按 `GlobalRepeatLimit`；超限 Block；**不再**因次数 Abort |
| 非幂等优先序 | 第二次仍 Block；`NameRepeatLimit = -1` 不能放行非幂等 |
| 空文本兜底 | Timeout/Failed 无累积文本 → FallbackLines[0] |
| Completed 空文本 | 仍回 `""`（不编话） |
| 枚举 | 无 `ToolRoundsExhausted`；旧单测 `RoundsExhausted_*` 删除或改写 |
| 格式错 Abort | 仍 Abort（`k_maxFormatErrorsPerTurn`），与重复类 Block 分离 |

## 非目标（明确不做）

| 非目标 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 删除后又以别的名字加回「整轮工具次数闸」 | 与确认口径 3 冲突；防搜归 NameCap，成本归墙钟 | 确有成本事故再写新 ADR |
| MaxToolRounds 只计 search | 已随总闸删除 | 无 |
| 耗尽 FinalContentPass | 无触发点 | 无 |
| NameCap 跨 session | 狂搜是单轮现象 | 需要时再写 ADR |
| 修改非幂等恒 1、锁超时不吃配额 | ADR-004/010 已定 | 无 |
| session 层替模型写死台词 | Fallback 是配置口径 | 策划配 FallbackLines |
| AskAsync 结构化返回重构 | 与本次正交；`LastTurnExhaustedRounds` 随删除一并消失，无需再重构该信号 | 无 |

## 文档与测试同步（实现时）

1. `agent-kernel-design.md`：类型定义、时序图「往返耗尽」分支、失败语义表、配置总表、测试矩阵。
2. `README.md`：`MaxToolRounds` / `LastTurnExhaustedRounds` 表述删除，改为 NameCap + 墙钟。
3. `agent-argument-gate-design.md`：格式错「由 MaxToolRounds 封顶」改为「由格式错 Abort 档封顶」。
4. `AgentProfile_SO.Validate` 公式与 `npc-runtime-test-profiles-design.md` 中相关列。
5. 旧 ADR（004/006/010 等）**不删改**，由 ADR-023 引用说明被取代范围。
6. 实现后：项目 `Docs/CHANGELOG.md`；若有需人工验收项再写 `PENDING_TESTS.md`。

## 实现步骤（确认后编码）

1. LoopGuard：NameCap + 阶梯去掉重复类 Abort + 强制收口文案 + BeginTurn 清 name 计数。
2. 属性/配置：`NameRepeatLimit`、`PerNameToolCallLimit`；记忆动作默认 3。
3. 删除 `MaxToolRounds` / `ToolRoundsExhausted` / `LastTurnExhaustedRounds` 及 Core/Session/UI/测试引用。
4. `ResolveFinalText` 收口逻辑。
5. 跑 LLM 插件 EditMode 测试。
6. 同步上述文档与 CHANGELOG。

## 风险与开放点

| 点 | 说明 | 倾向 |
| --- | --- | --- |
| 无总闸后极端长工具链 | 只靠墙钟，token/时长可能升高 | 接受（用户确认）；靠 TurnDeadlineSeconds 与 NameCap |
| Block 后模型换另一个工具继续搜 | NameCap 按 name 隔离 | 可接受；文案已要求「不要再调用该工具」；墙钟封顶 |
| 删除枚举的编译影响 | 宿主 switch、测试 | 实现时一次改净 |
| Validate 固定假设 8 次 | 仅编辑器警告 | 不引入新的运行时旋钮 |
