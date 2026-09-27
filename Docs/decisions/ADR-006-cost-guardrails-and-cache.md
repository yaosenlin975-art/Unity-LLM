# ADR-006: 成本只统计不限流，但用排版与压缩策略把实际开销压下来

## Status
Accepted

## Date
2026-09-19

## Context
心跳 agent 的 token 成本由「轮数 × 每轮 prompt 体积」决定。设计讨论中提出过在 `LLMAgentGlobalConfig_SO` 里做全局并发上限、每分钟轮数上限等运行时闸门。同时存在一个更省钱的机制：DeepSeek / SenseNova 这类 OpenAI 兼容网关按**前缀命中**计价（`usage.prompt_tokens_details.cached_tokens`），一旦 prompt 前部掺入每轮都变的内容，缓存命中率归零，成本成倍上涨。

## Decision
**不做并发数 / 每分钟轮数 / token 预算类运行时闸门**。成本靠四条机制而非闸门：

1. **只统计**：`AgentTrace` 每轮带 `PromptTokens / CompletionTokens / CacheHitTokens`，沙盒窗口与请求日志窗口显示命中率百分比。看不见等于没有。
   ⚠ 前提是补齐既有缺口（A/C 步，见设计稿 §实现顺序）：`EnqueueStreamAsync` 不触发 `OnRequestCompleted`；`LLMStreamChunk` 没有 `CacheHitTokens`；日志窗口没有该列（`LLMRequestLog` 结构体已有且非流式已填）。心跳主要走流式，不补前两条则命中率诊断恒为 0。
2. **护栏写在配置面板而不是运行时**：心跳最小间隔 `[Range(10,300)]`（已由 [ADR-015](./ADR-015-drop-heartbeat-auto-turn.md) 随心跳一起删除）、单轮最大往返 `MaxToolRounds [Range(1,8)]`、每 agent 单飞（ADR-003）。这些是常量区间约束，不是调度器。
3. **前缀缓存排版规则**（硬约定，见设计文档 §注入排版）：system 消息**只放人设与规则**（工具声明由 `tools` 字段承载），事实槽/核心快照/可查询提示全部走每轮重拼的 `ephemeralContext`；核心快照只允许低频值，**时间、坐标、周围实体一律不进快照**，改由 observe 类工具现查。
4. **压缩成本倒挂修正**：`CompressionConfig_SO` 增 `CompressionProviderName`（摘要可挂便宜模型，留空回落主模型；不放 `AgentProfile_SO`，那是 Resources 共享资产，按 agent 配会跨会话污染）；摘要请求尝试次数从 2 降为 1（超时/失败后不再赌第二次，直接机械降级）。
   另接上 `ContextManager.PruneStaleLargeContent()`（现为死代码，软阈值时触发，且只在 token 模式生效）。**它的作用被高估过一次，须按实际语义理解**：折叠区的轮次根本不进 prompt（`GetHistoryMessages` 只取 head+tail），所以 prune **不减少发送 token**；但触发判定用的 `EstimateTotalTokens()` 累加全部 rounds（含折叠区），把长回复换成 `[elided]` 会降低估算值，确实能推迟触发，同时缩小摘要请求自身的输入体积。作用于「发出去的 tool 消息」的是 `ContextPruning.SnipStaleToolResults`，`LLMSession` 每轮已在调用。

## Alternatives Considered

### 全局限流器（并发轮数 / 每分钟轮数 / token 预算闸门）
- Pros：失控行为在源头被掐断
- Cons：多一层调度状态机与"为什么我的 NPC 这轮没反应"的排查负担；闸门阈值本身就是新的猜测量
- Rejected（本次）：单飞 + MaxToolRounds 已能挡住失控循环，成本问题优先用缓存命中解决

### 什么都不做，纯统计
- Pros：最省
- Cons：上下文越大压缩越贵，而压缩恰好发生在最贵的时刻；且实时值一旦进快照，缓存永久失效
- Rejected：第 3、4 条几乎零代码量，收益却最大

## Consequences
- 上线初期成本异常时，第一个要查的是命中率而不是并发数——`CacheHitTokens == 0` 基本等于有人往 system 或核心快照里塞了每轮变的内容
- 保留 `MaxToolRounds` 作为唯一的循环天花板，配合 `LoopGuard`（ADR-004）
- 不做模型自动降级/路由（hippy-agent 的 `FailoverEngine` 那套），只保留现有主/fallback 双 Provider 配置

See also: [ADR-004](./ADR-004-tool-selection-llm-autonomous.md)、[agent-kernel-design.md](../agent-kernel-design.md)
