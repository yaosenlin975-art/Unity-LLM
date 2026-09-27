# ADR-004: 工具与感知全部交给 LLM 自主选，用循环检测兜底而非候选集门控

## Status
Accepted

## Date
2026-09-19

## Context
内核可以选择"每轮先由规则算出可用动作集，再让 LLM 在集合内选"（候选集门控），或者"把所有注册工具一次性暴露给模型"。项目自己的调研报告（§8）推荐的是"规则保底 + 可选 LLM 扩展"，与"全自主"存在张力，需要显式记录为什么不采用门控。

## Decision
不做候选集门控。所有 `[Tool]` 与 `[AgentAction]` 一律暴露，模型自由选。作为交换，必须落三件护栏：

1. **循环检测 `LoopGuard`**（规格见设计文档 §循环检测）：`name` / `name+规范化 args` / `+结果哈希` 三级签名，WARN → BLOCK → HARD 分级，每个工具可单独配 `RepeatLimit` 与 `Idempotent`。
2. **兜底台词**：三种情况取 `FallbackLines` 之一（配了才有效）——Provider 全挂、墙钟超时、**最终累积文本为空**。最后一条天然覆盖内容审查拦截（`finish_reason = content_filter` 时正文本来就是空），因此不需要为它新增通道或枚举档；`outcome` 仍只区分 `Failed`（请求失败）与 `Timeout`（墙钟到点）。
3. **可查询项提示**：profile 配了 `QueryableHint` 就在核心快照之后固定追加一行（如"你可以查询：位置 / 背包 / 任务 / 周围实体"），未配则不注入。

工具总数（含 observe 类）控制在 **15 个以内**；超过就回来重开门控这条决定（这是复评触发器，不是运行时约束）。

## Alternatives Considered

### 候选集门控（perception → 可用动作集 → 只暴露候选工具）
- Pros：token 成本随状态收敛、误选面小、天然就是"规则保底"
- Cons：要维护"什么状态下哪些动作可用"的规则表，等于把决策逻辑写两遍（一遍给模型、一遍给门控）
- Rejected（本次）：先用全自主 + 循环检测验证模型选得对不对。**不宣称已有落点**——工具清单由 `LLMSession.BuildRequest` 经 `ToolRegistry.ToLLMTools()` 全量生成，`IToolExecutor` 只能"拒"不能"滤"；真要做门控得给 `LLMSession` 加 tools 提供者注入点，属于内核改动，届时另写新 ADR（编号顺延）

### 规则先定行为，LLM 只负责表达
- Pros：确定性与成本最好
- Cons：工具循环基本退化，与"共用 agent 内核"的目标不符
- Rejected：目标是让模型参与决策

## Consequences
- 提示词体积与误选风险由模型承担，`LoopGuard`、`MaxToolRounds` 与墙钟是仅有的三道自动闸
- **非幂等工具超阈值强制拦截，不提供关闭开关**——这条不是限流而是正确性：重复执行 `trade_player` 会真的扣两次钱。优先序上 `Idempotent = false` **压过** `RepeatLimit = -1`（显式关闭检测也拦不住非幂等工具的重复）；非幂等工具跳过 WARN 与 HARD 两档，**生效阈值恒为 1**（忽略 `RepeatLimit`/`GlobalRepeatLimit`，否则默认阈值 2 会让「两轮各扣一次钱」照样发生），第二次起 BLOCK 并在 session 内拉黑该签名。**只有执行成功（`Ok = true`）才计配额**：超时、异常、被叫停、锁等待失败都不消耗，否则一次失败会让该签名永久锁死
- 反向的副作用也存在：session 级拉黑会让"同一件货再买一次"永久失败。要支持合法的重复购买，宿主必须在 args 里带唯一订单 id（签名随之不同），或宿主自己按 actionId+args 做幂等键
- BLOCK 的签名黑名单只在本轮内生效。理由不是"模型会以为工具不存在"（工具清单每轮随请求全量重发，不存在这种遗忘），而是**下一轮世界状态可能已变，同名同参可能是合理重试**
- 但这条对**非幂等**工具留了洞：轮内清零 + 心跳自动起轮，`trade_player` 会在两轮里各扣一次钱。所以 `Idempotent = false` 的签名计数按 session 累计、不随轮清零
- 配置默认值：`RepeatLimit` 取 **0 = 继承全局、-1 = 关闭**（若把 -1 当默认，C# 字段默认值 0 会让未配置的工具静默关掉检测）；`Idempotent` 按属性给默认——`[Tool]` 为 `true`（**注意：本项目当前没有任何 `[Tool]` 标注的方法，这个默认只是给将来留的口径**），`[AgentAction]` 为 `false`
- **只有 BLOCK 需要回填 tool 消息**（该轮还会继续发请求，缺一条就 dangling）；HARD 直接结束往返且不再发请求，该轮整体丢弃，因此**不做 `[Aborted]` 补帧**——参考项目需要补帧是因为它把含 tool 消息的历史持久化并在下一轮续发，我们的 `AddRound` 只存 user/assistant 文本
- 内置的记忆三件套是 `[AgentAction]` 且显式 `Idempotent = true`，否则 `list_facts` 会被 session 级拉黑
- 门控被明确拒绝过，实现时不要"顺手加一个可用动作过滤器"

See also: [ADR-006](./ADR-006-cost-guardrails-and-cache.md)、[agent-kernel-design.md](../agent-kernel-design.md)
