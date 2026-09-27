# ADR-012: ProfileKey 留空自动生成，生成后与人设解耦

## Status
Accepted

## Date
2026-09-20

## Context
ADR-011 定死 `sessionId = {profileKey}#{instanceId}` 并要求 profileKey 必填且禁止 `#`、`/`、`\`，但把填写负担留给了资产作者：手填容易撞 key（两个 Profile 同 key + 同 instanceId 即串档），忘填则 Validate 报错但不自动恢复。同时存在一个危险的直觉方案——"key 根据 Prompt 变化"：若 key 随人设内容漂移，改一句人设就会变更 sessionId，该类 NPC 的记忆与存档全部失联，与"key 是存档键"的本质冲突。

## Decision
1. `ProfileKey` 留空时自动生成，作者零填写成本：编辑器下按**当时**的人设内容做 FNV-1a 64 派生（`agent-<hash8>`），与项目内其他 Profile 资产（`AssetDatabase.FindAssets("t:AgentProfile_SO")` 遍历）及本会话已用 key（静态 `knownKeys` 表）查重，冲突追加 `-2/-3/…` 序号；构建内无资产遍历能力，退化为随机派生。
2. **生成后与人设解耦**：只在 key 为空时生成一次，之后改人设不变更 key。改 key = 换存档域，必须显式手改。已有 key 时 `EnsureKey()` 仅登记查重表，不做修改。
3. 三个触发点：`OnValidate`（编辑器 Inspector 路径）、`AgentCore` 构造兜底（`profile?.EnsureKey()`，此时代码创建的内存 Profile 也能补齐）、`Validate()` 必填检查保留为最后防线（EnsureKey 之后理论上不再触发）。
4. 查重两个来源：静态 `knownKeys`（会话内全部活实例，含手动填写的 key；域重载清零）与磁盘资产遍历（仅编辑器）。

## Alternatives Considered

### key 跟随 Prompt 内容实时变化
- Pros：key 自解释，能看出对应哪份人设
- Cons：改人设 = sessionId 漂移 = 存档失联；与"key 是存档键"本质冲突
- Rejected：人设内容只作为**一次性**派生源，生成后解耦

### 构建内也做全量资产遍历查重
- Cons：构建内没有 AssetDatabase，需要自建资产清单（额外序列化一份全量 key 表）
- Rejected：构建内随机派生 + instanceId 的 Guid 兜底已满足唯一性；跨档风险只存在于编辑器改资产阶段

## Consequences
资产作者不再需要理解 key 规则；跨域重的查重依赖磁盘资产遍历（仅编辑器，OnValidate/EnsureKey 时生效）；`knownKeys` 为会话级，同一次运行内 CreateInstance 出来的内存 Profile 之间也能查重。

## 关联
- 取代 ADR-011 Decision 5 中「profileKey 必填」的手填语义（字符约束与 sessionId 构成不变）
