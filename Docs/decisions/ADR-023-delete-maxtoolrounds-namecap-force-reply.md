# ADR-023: 删除 MaxToolRounds，换参狂搜归 NameCap，停手必须强制收口

## Status

Accepted（实现已落地 2026-09-27，待 Test Runner 行为验收）

## Date

2026-09-27

## Context

`MaxToolRounds` / `EAgentOutcome.ToolRoundsExhausted` 最初动机是：模型在记忆、人设中找不到答案时（例：「你喜欢吃什么」），把问题拆成「吃」「食物」「美食」等对 `search_memory` 换参狂搜，观感像卡死。

实现却做成了 **一轮内所有工具往返的执行上限**（默认 3），耗尽时丢弃当前批 `tool_calls`、不发收尾正文请求，`ResolveFinalText` 对该 outcome 空文本回 `""`。游戏测试表现为：多步操作腰斩且 agent 无回复。

`LoopGuard` 对 **换参** 原本是盲区：L2 键为 `name + 规范化 args`，L1 `nameHits` 仅遥测（[ADR-004](./ADR-004-tool-selection-llm-autonomous.md)）。防狂搜被错误压在总闸上。

用户确认三条口径：

1. NameCap 默认全局 **4**、记忆类 **3**；
2. 凡停手指令强制 **「没话也说一句」**；
3. **`MaxToolRounds` 整项删除，没有必要**。

相关：[ADR-004](./ADR-004-tool-selection-llm-autonomous.md)（LoopGuard / MaxToolRounds / 墙钟三道闸）、[ADR-006](./ADR-006-cost-guardrails-and-cache.md)（MaxToolRounds 为配置面板成本护栏）、[ADR-010](./ADR-010-cross-agent-resource-locks.md)（曾写防重试靠 MaxToolRounds 封顶）、[agent-kernel-design.md](../agent-kernel-design.md)。

## Decision

### 1. 删除轮次总闸

删除运行时与配置面：

- `AgentProfile_SO.MaxToolRounds`
- `LLMSession.MaxToolRounds`、`LastTurnExhaustedRounds` 及 `RunToolLoopAsync` 耗尽分支
- `EAgentOutcome.ToolRoundsExhausted` 及 `AgentCore` / 宿主 UI / 单测中的映射与文案

**不**以其它名字恢复「整轮工具次数」运行时闸。成本由墙钟 `TurnDeadlineSeconds` 与 LoopGuard 承担。

### 2. NameCap（换参狂搜）

- Profile：`PerNameToolCallLimit`，默认 **4**，`0 = 关闭`。
- 工具元数据：`NameRepeatLimit`（`0 = 继承`，`-1 = 该工具关闭 NameCap`）；与 `RepeatLimit`（同参）正交。
- 内置 `search_memory` / `list_facts` / `write_fact` / `forget_fact`：`NameRepeatLimit = 3`。
- 键为 **工具名**；`BeginTurn` 清零本轮 name 计数。

### 3. 阶梯与强制收口

- 非幂等：阈值恒 1，第二次起 Block（不变，优先序仍最高）。
- 幂等 NameCap / L2：Warn → **持续 Block**；**重复类裁决不再 Abort**。
- `ELoopVerdict.Abort` 保留给格式错等非重复路径。
- Warn / Block 回灌文案必须包含：**即使没有结果，也请直接根据已有信息简短回复玩家**。

### 4. 空文本

`ResolveFinalText`：仅 `Completed` 无累积文本时回 `""`；Timeout / Failed / LoopAborted 等空文本走 `FallbackLines`。

完整接口与流程见 [agent-namecap-force-reply-design.md](../agent-namecap-force-reply-design.md)。

## Alternatives Considered

### 保留 MaxToolRounds，只调默认值或只计 search

- Pros：改动小 / 贴近原始动机。
- Cons：与 NameCap 职责重叠；总闸仍会腰斩合法游戏操作；预算按工具类别分裂则成本闸失真。
- Rejected：用户确认删除该设定。
- 将来的唯一落点：若再出现成本事故，另写新 ADR，不得在本条下静默加回。

### 耗尽 FinalContentPass（无工具收尾请求）

- Pros：腰斩时仍可能拿到模型正文。
- Cons：触发点是已删除的总闸；没有总闸则无「额度耗尽」。
- Rejected：随 MaxToolRounds 一并取消。强制收口改由 NameCap/L2 回灌文案承担。

### 重复类保留 HARD Abort

- Pros：与 ADR-004 原阶梯一致，极端循环立刻停。
- Cons：Abort 丢历史、易静默，与口径 2「没话也说一句」冲突。
- Rejected：幂等重复封顶为 Block，模型仍在本轮内可收口；真失控靠墙钟。
- ADR-004 原文 **不删改**；由本 ADR 说明「幂等重复类不再 Abort」的取代范围。

### NameCap 复用 GlobalRepeatLimit

- Pros：少一个配置。
- Cons：同参阈值与换参次数混用。
- Rejected。

### NameCap 跨 session 拉黑

- Cons：合法的「下一轮再查」失败。
- Rejected：无落点。

### 内核静默 + 仅宿主占位

- Cons：与失败轮可感知原则冲突；占位 ≠ NPC 说话。
- Rejected：收口责任在回灌指令 + FallbackLines。

### 废除 LastTurnExhaustedRounds 改结构化返回

- 随字段删除，该重构 **不再需要**。
- 无落点。

## Consequences

- 游戏 NPC 可在墙钟内完成任意步数的工具链，不再被 `MaxToolRounds=3` 腰斩。
- 换词狂搜由 NameCap（默认 4 / 记忆 3）拦截，且 Block 文案强制模型收口。
- 极端情况下一轮工具请求次数无次数闸，**token/耗时上升风险由墙钟承担**；`TurnDeadlineSeconds` 仍是硬顶。
- ADR-006「MaxToolRounds 作为循环天花板」、ADR-010「防无限重试靠 MaxToolRounds」在文档层面由本 ADR 取代，运行时改由 NameCap + 墙钟。
- Validate 中曾依赖 `MaxToolRounds` 的自洽公式改为编辑器警告级的固定假设（最多 8 次工具往返），**不**引入新的运行时旋钮。
- 删除 `ToolRoundsExhausted` 后宿主 `switch` / 单测须一次改净；项目内无 outcome 存档，可接受。
- 实现后同步：`agent-kernel-design.md`、README、`agent-argument-gate-design.md` 中过时表述、测试矩阵、项目 CHANGELOG。

See also: [ADR-004](./ADR-004-tool-selection-llm-autonomous.md)、[ADR-006](./ADR-006-cost-guardrails-and-cache.md)、[ADR-010](./ADR-010-cross-agent-resource-locks.md)、[agent-namecap-force-reply-design.md](../agent-namecap-force-reply-design.md)
