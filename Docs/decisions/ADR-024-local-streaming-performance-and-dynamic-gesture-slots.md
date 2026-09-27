# ADR-024: NPC 流式肢体表现使用本地导演与后端原生动态槽

## Status
Accepted

## Date
2026-09-21

## Context

NPC 需要在 LLM 流式输出台词期间同步做自然肢体动作，同时动作类型需要能持续增加。若把完整动画列表放入系统提示词或工具 schema，上下文成本会随资源规模增长，模型还会把实现词表误当成角色知识。若每个手势都走 `[AgentAction]`，工具参数必须先完成组装，无法及时配合正在输出的正文，并且会把纯表现事件写入聊天历史。

项目同时存在 Animator 与团结 AnimGraph 1.0.0 两种后端。现有 `IAgentAnimationDriver` 面向状态机状态与参数，适合显式剧情动作，但没有播放状态机外任意 `AnimationClip` 的统一能力。AnimGraph 包本身已经建立 PlayableGraph，并公开 `AnimSlotNode`、`AnimGraphManager.slotManager` 和 `SlotManager.PlayClip`；另建独立图会与现有图竞争同一角色的骨骼输出。Animator 则可以用 `AnimatorControllerPlayable` 保留原状态机，再通过 `AnimationLayerMixerPlayable` 混入动态 Clip。

## Decision

1. NPC 说话微动作由本地 `NpcSpeechPerformanceController` 监听流式文本旁路驱动，不注册为 Agent 工具，不进入提示词、消息历史或压缩摘要。
2. 流式文本按标点、长度、暂停和结束切成短句；本地分类器只产生少量抽象意图，本地目录负责从任意数量的动画中选择具体 `AnimationClip`。
3. 纯表现动作通过独立 `INpcGestureDriver` 执行；现有 `IAgentAnimationDriver` 与 `AgentAnimationTools` 继续负责模型显式发起的状态机动作，两者不合并语义。
4. AnimGraph 后端必须使用图内 `AnimSlotNode` 和 `SlotManager.PlayClip` 动态播放，不创建第二张输出到同一角色的 PlayableGraph。
5. Animator 后端使用单一顶层 PlayableGraph：`AnimatorControllerPlayable` 作为基础输入，动态 `AnimationClipPlayable` 作为手势输入，经 `AnimationLayerMixerPlayable` 和 AvatarMask 合成后输出到 Animator。
6. 首期只播放上半身、非 Additive 手势；移动可共存，战斗、受击、死亡和剧情状态由玩法侧调用 `SetSuppressed` 让位。全身与 Generic Rig 延后。
7. 首期分类器使用确定性规则。只有真实角色测试证明规则不足时，才增加返回抽象意图的小模型分类器；动作目录仍不提供给模型。

## Alternatives Considered

### 把完整动作目录注入系统提示词

- 优点：主模型可以直接选择精确动作。
- 缺点：token 和缓存前缀随资源增长；具体动画名污染角色提示词；资源改名会改变模型契约。
- 拒绝理由：动作目录是本地资源索引，不是角色知识。

### 每个说话手势调用 `[AgentAction]`

- 优点：复用现有工具执行、审计和错误回填。
- 缺点：工具调用需要完整组装后才执行，会打断正常正文生成；高频动作进入历史并消耗轮次预算。
- 拒绝理由：纯表现事件不应进入 Agent 决策闭环。

### 在台词流中嵌入隐藏动作标签

- 优点：主模型能在生成文本时直接标注动作位置。
- 缺点：标签可能泄漏到玩家正文；要求额外输出协议；失败会破坏 NPC 只说台词的约束。
- 拒绝理由：本地短句分类已能覆盖说话微动作，不值得扩大输出协议。

### 给 AnimGraph 外挂独立 PlayableGraph

- 优点：看似能与 Animator 后端共用实现。
- 缺点：AnimGraph 已拥有自己的图和输出；两个系统会竞争骨骼写入，且 Playable 节点不能跨图连接。
- 拒绝理由：AnimGraph 1.0.0 已提供专用 Slot API，直接复用更稳定。

### 为每个动作增加状态机状态

- 优点：完全沿用现有 `PlayState`。
- 缺点：状态机随动作库膨胀；Animator 构建包无法可靠枚举状态；资源增删需要改图。
- 拒绝理由：说话动作是数据目录，不应改变玩法状态机拓扑。

### Animator 使用动态 AnimatorOverrideController

- 优点：接入现有 Animator 最快，只需要一个占位状态。
- 缺点：运行时替换 Clip 会触发动画绑定重分配，高频说话手势可能造成主线程尖峰。
- 拒绝理由：只适合作为低成本原型，不作为高频正式后端。

### 从第一版开始使用额外 LLM 分类器

- 优点：对复杂句子的语义判断更强。
- 缺点：增加成本、延迟和新失败点；常见赞同、拒绝、疑问和强调可由规则覆盖。
- 拒绝理由：先用可预测规则建立基线，实测不足再增加可选分类器。

## Consequences

- 正向：动作目录可以扩大而不增加 LLM 上下文或聊天历史。
- 正向：流式正文和动作选择解耦，表现失败不会影响 NPC 回复。
- 正向：AnimGraph 复用包内 Slot，动态 Clip 不需要进入状态机。
- 正向：Animator 保留原 Controller 逻辑，同时获得可遮罩的动态手势层。
- 正向：说话微动作、剧情动作和玩法动作有明确所有权与优先级。
- 负向：Animator 后端需要把现有 Controller 纳入顶层 Playable，并让现有驱动委托给 `AnimatorControllerPlayable`，接入成本高于 OverrideController 原型。
- 负向：AnimGraph 资产必须预留命名一致的 `NpcGesture` Slot 与遮罩路径。
- 负向：动作目录需要维护标签、Rig 兼容性、冷却和手部占用等元数据。
- 约束：不得为了统一代码而让 AnimGraph 和 Animator 同时采用外挂第二张 PlayableGraph；统一边界是 `INpcGestureDriver`，不是底层图结构。
- 约束：首期只落地 AnimGraph 后端；Animator 顶层 PlayableGraph 已获接受，但在实际需要 Animator NPC 时再实现。
