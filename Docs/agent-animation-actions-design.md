# Agent 动画动作设计（Animator / AnimGraph 双后端）

> 决策依据见 `Docs/decisions/ADR-021-animation-driver-boundary.md`（本文只写规格与实测事实，不重复理由）。
> 本文所有"现有 API 如何如何"的说法都在 `cn.tuanjie.animgraph@1.0.0` 源码与 `Library/ScriptAssemblies` 编译产物上核过，路径缩写：MGR=`Runtime/AnimationGraph/AnimGraphManager.cs`，BIND=`Runtime/AnimationGraph/AnimGraphManager_Binding.cs`，RA=`Runtime/Graph/RootAsset.cs`，IDX=`Runtime/Graph/RootAsset.PathIndex.cs`，ABG=`Runtime/Graph/AnimBaseGraph.cs`，SMG=`Runtime/Graph/StateMachineGraph.cs`，SMS=`Runtime/Elements/SMState.cs`，NODE=`Runtime/Elements/AnimationNode.cs`，PB=`Runtime/Parameter/ParameterBase.cs`。

## 需求分析

现状（对码核实）：

- 成员工具模型已可用：`AgentToolSet.CollectHierarchyTools` 扫宿主层级上的实例 `[AgentTool]` / `[AgentAction]`，执行路由「成员动作 → 成员工具 → 全局动作 → 全局工具」（`Runtime/Agent/AgentActions.cs:571-634`）。
- 导航是直连组件的写法：`AgentNavigationTools` 自己 `GetComponent<NavMeshAgent>()`（`Runtime/Tools/AgentNavigationTools.cs:25`）。动画不能照抄，因为本工程有两套后端且其一是可选包（ADR-021 Context）。
- `Assets/Plugins/LLM` 下不存在任何动画相关代码（grep `Animator|AnimGraph|PlayAnimation` 只命中设计文档），本次是第一条动画线。
- `Assets/Learn/ARPG/Scripts/AnimGraph/` 下有两个课程探针（`AnimGraphParamProbe.cs` / `AnimGraphSpeedTest.cs`），证明 `SetBool/SetVector2/GetFloat` 这套按名字读写的用法在 Play 模式可用，也证明参数名写错**静默给 0**（探针注释即为此而写）。

目标：让模型能对挂了自己 `AgentHost` 的角色说"拔剑""进入跑步""跳一下"，并且**写错名字时会失败得有原因**，而不是安静地什么都不发生。

成功标准：同一个 `AgentHost` 下，Animator 角色与 AnimGraph 角色各挂对应驱动 + 同一份 `AgentAnimationTools`，模型侧看到的工具/动作名字与参数完全相同；只有词表内容随角色不同。给一个不在词表里的状态名，回填是 `[Action Failed] 状态 … 不在该 Agent 允许的动画状态里。`，Console 不报错也不播任何东西。

## 后端能力实测（决定了词表策略）

| 能力 | Animator | AnimGraph 1.0.0 |
| --- | --- | --- |
| 枚举全部状态名 | **构建包里做不到**：`AnimatorController`/`AnimatorStateMachine` 的托管绑定只在 `UnityEditor.CoreModule`；`RuntimeAnimatorController` 只暴露 `animationClips` | 做得到：`rootAsset.graphsByPath`（IDX:32，须先 `EnsurePathIndex()`，IDX:73）→ `StateMachineGraph.nodes`（ABG:50）→ `SMState.GetCustomName()`（SMS:12 / NODE:1328，`nodeCustomName` 是序列化字段，构建包里在） |
| 读当前在播状态名 | 只有 hash：`AnimatorStateInfo.shortNameHash`，**没有 `stateName` 属性**；用候选名 `Animator.StringToHash` 反查 | manager 上确实没有入口（只有 `TryGetState(path, name, out …)` MGR:2996/3004 与 `IsInTransition` MGR:2976/2983，`SMState.stateNameHash` 是 `internal` 且 `InternalsVisibleTo` 不含游戏程序集）——但 **`TryGetStateMachinePlayable(path, out playable)`（MGR:2965）是公开的，playable 自带 `GetCurrentAnimatorStateInfo()`** ⇒ 拿到的是 hash，同样用自己枚举的词表按 `Animator.StringToHash` 反查；反查不到就回 `unknown:<hash>`。参数值一并回读，因为模型改过参数后要能看到有没有落上 |
| 按名字切换状态 | `Play(string, layer, normalizedTime)` / `CrossFadeInFixedTime(string, seconds, layer)` | `PlayState(path, name, normalizedTime)`（MGR:3051）/ `CrossFadeStateInFixedTime(path, name, seconds, fixedTime)`（MGR:3126）——第三参单位是秒，故淡入一律走 InFixedTime 版本 |
| 枚举图层名 | `layerCount` 有、名字无 | **无图层名概念可枚举**：`AnimLayerMixerNode` 只有 `portCount`/`datas` |
| 读参数名与类型 | `Animator.parameters` → `AnimatorControllerParameter{name,type}` | `rootAsset.parameters` → `ParameterBase.name` / `.type`（PB:51，`AnimatorControllerParameterType`） |
| 写参数 | `SetBool/SetInteger/SetFloat/SetTrigger` | 同名一套（BIND:161-263）。**只有 string 与 int 两种重载，没有 `*Hash` 后缀**；参数名走 `NameAllocator.ComputeCRC32`、状态名走 `AnimationGraph.StringToHash`（BIND:154），是两套 hash ⇒ 一律传 string 重载 |
| 整体速率 | `Animator.speed` | `AnimGraphManager.speed`（BIND:482） |
| 已知坑 | 状态名可为 `"图层名/状态名"`，layerIndex 传 -1 的"跨层搜索"行为未经真机验证 | `SetTrigger` 内部就是 `SetBool(true)`，**不会自动消费**（BIND:236-250）；`TryGetTransition` 两个重载恒 false（MGR:3019-3039 → 死实现 MGR:3219），不要用；`AnimGraphManager` 会强制禁用同物体上的 `Animator`（MGR:4527）⇒ 两后端不能同时驱动一个角色 |

结论：**词表来源按后端分**。AnimGraph 以运行时自动枚举为主、白名单只做裁剪；Animator 白名单即词表。两条都在 ADR-021 决策 5。

## 边界与依赖方向

```
LLM.Runtime（插件，零动画引用）
├─ Runtime/Agent/AgentInterfaces.cs   + IAgentAnimationDriver
└─ Runtime/Tools/AgentAnimationTools.cs  2 条 [AgentTool] + 3 条 [AgentAction]
                    ▲ 只引用接口
Learn.AgentAnimation（玩法层，Assets/Learn/ARPG/Scripts/AgentAnimation/）
├─ AgentAnimationWhitelist.cs     状态名 / 参数名两套清单
├─ AgentAnimatorDriver.cs         Animator 后端
└─ AgentAnimGraphDriver.cs        AnimGraph 后端，整文件 #if ANIMGRAPH_ENABLED
```

- `Learn.AgentAnimation.asmdef`：引用 `LLM.Runtime / Lin.Runtime / UniTask / cn.tuanjie.AnimGraph.Runtime`，`versionDefines: cn.tuanjie.animgraph [1.0.0] → ANIMGRAPH_ENABLED`。最后一条**必须显式写**：asmdef 程序集不像预定义 `Assembly-CSharp` 会自动引用 `autoReferenced` 的包程序集（`Assets/Learn/ARPG/Scripts/AnimGraph/` 两个探针能裸 `using AnimGraph;` 正是那个原因）。离线 Roslyn 冒烟是手工塞 dll 的，**看不出这类缺引用**（本轮就这么漏过一次，`#if` 关上的时候照样报 OK）——包真被卸掉时这条引用会成未解析引用，届时的处理是把 AnimGraph 驱动挪进包条件目录或改 `precompiledReferences`，不要靠删引用蒙过去。所以"卸包也编得过"目前**只有 `#if` 那一半被验过**，包缺席那一半没有。
- 依赖方向单向：玩法层 → 插件。`LLM.Runtime.asmdef` 本次未改一个字。
- 后端不自动探测：`AgentAnimationTools` 只 `GetComponentInChildren<IAgentAnimationDriver>(true)`。挂两个驱动属于配置错误，不做仲裁。

## 接口形状

```csharp
// Runtime/Agent/AgentInterfaces.cs
public interface IAgentAnimationDriver
{
    string BackendName { get; }                                  // 只用于回填文案
    void CollectStateNames(List<string> results);                 // 可播状态词表
    void CollectParameterNames(List<string> results);             // "名字:类型" 词表
    string DescribeCurrentState();                                // 当前状态/参数回读
    string PlayState(string stateName, float fadeInSeconds);      // null = 成功
    string SetParameter(string name, string value);
    string SetSpeed(float speed);
}
```

约定：**写操作返回 null 表示成功，返回非空字符串即失败原因**（中文，由内核加 `[Action Failed]` 前缀回填）。这样接口不携带 `AgentActionResult`，也不引入 `bool + out string` 的两套返回。

AnimGraph 呈现给模型的状态名统一为 `"状态机路径/状态名"`，播放时按最后一个 `/` 拆回 `(stateMachinePath, stateName)` —— 因为 `PlayState` 要这两个参数，而模型只需要一个字符串。根状态机（路径为空串）时全名就是状态名本身。**但这条路径实际到不了**：`EnsurePathIndex` 把根 `AnimGraph` 索引在 `""`，而 `StateMachineGraph` 只是它的兄弟类型、一定挂在某个 `AnimStateMachineNode` 名下（`RootAsset.PathIndex.cs:84/175-181`），所以 `""` 从不会通过枚举里的 `is StateMachineGraph` 过滤，`TryGetStateMachinePlayable("")` 也永远拿不到东西。代码保留空串分支只为让 `FullName` 与拆分两侧对称，**不代表存在"根状态机可直接播的状态"**。嵌套状态机（`SMState.childAnimGraph`）是 `AnimGraph` 不是 `StateMachineGraph`，同样被枚举过滤掉。

## 五条模型可见成员

| 名字 | 种类 | 签名 | 成功回填 | 失败回填 |
| --- | --- | --- | --- | --- |
| `get_animation_state` | `[AgentTool]` 只读 | `()` | `backend=AnimGraph speed=1.00 playing=Base Layer/Attack(0.42) MoveSpeed=0.40 IsMoving=1` | `[Tool Error] 该 Agent 未挂载动画驱动（Animator 或 AnimGraph 驱动）。` |
| `list_animation_states` | `[AgentTool]` 只读 | `()` | `states=[Base/Idle, Base/Attack] parameters=[MoveSpeed:float, Hit:trigger]` | 同上 |
| `play_animation_state` | `[AgentAction]` 非幂等 | `(string stateName, float fadeInSeconds)` | `已切换到 Base/Attack。` | `状态 X 不在该 Agent 允许的动画状态里。` / `状态 X 在当前 AnimGraph 资产里不存在。` / `状态机 X 当前不可用（图未初始化或层级里没有这个状态机）。` / `状态 X 在该 Animator 上不存在。` / `状态名不能为空。` |
| `set_animation_parameter` | `[AgentAction]` 非幂等 | `(string name, string value)` | `已设置 MoveSpeed=0.6。` | `参数 X 不在该 Agent 允许的动画参数里。` / `参数 X 不存在。` / `参数 X 需要整数，收到 abc` |
| `set_animation_speed` | `[AgentAction]` 非幂等 | `(float speed)` | `动画速率已设为 0.00。` | `速率必须是 0 或更大的数值。` |

要点：

1. `value` 统一走 string，由驱动按词表里声明的类型转 bool/int/float/trigger。这样避免 `set_animation_bool/int/float/trigger` 四条重复动作。
2. `fadeInSeconds <= 0` 表示硬切；两条路径分别落到 `Play`/`PlayState` 与 `CrossFade*InFixedTime`，单位都是秒。
3 条动作都不等动画播完（与 `move_to` 同构：发起即成功）。真需要"播完再说话"时再按 ADR-009 加 `LongRunning` 等待动作，本次不做。
4. `AgentActionAttribute.Idempotent = false` ⇒ schema 追加必填 `say`，模型必须"先说后做"（ADR-008），与导航动作一致。
5. Trigger 类参数：AnimGraph 侧写完记一张待清理表，**下一帧 LateUpdate 自动 `ResetTrigger`**（BIND:236-250 的"不自动消费"红线）。Animator 侧由引擎自己消费，不补。

## 数据流与错误处理

```
模型 tool_call → AgentActionExecutor（门控 → 循环防护 → 路由）
   → AgentAnimationTools（参数修剪、空值与负数拦截、驱动缺失拦截）
     → IAgentAnimationDriver（白名单裁剪 → 后端 API → 中文失败原因）
```

- 工具层负责**输入边界**：`stateName`/`name` 去空白、空名直接拒、`fadeInSeconds` 负数钳到 0、速率拒绝 NaN/Inf/负数。模型输出不可信，这一层不省。
- 驱动层负责**词表校验**：不在白名单/枚举结果里的名字一律拒，不回落到"试试看"。AnimGraph 即使白名单为空也要对着自己枚举出的 `statePaths` 查一遍——这是"参数名写错静默返回 0"这类坑的唯一防线。
- 失败原因逐级上浮，最外层由内核加 `[Action Failed] ` 前缀；只读工具失败用 `[Tool Error] ` 前缀，与既有工具一致。
- 不新增日志：本层所有失败都会回填给模型，Console 再打一遍是噪音。真需要排查接线时看 `AgentStateInspector`（ADR-012~020 那条线）。

## 接线（策划/美术要做的事）

1. 角色根物体挂 `AgentHost`（已有）。
2. 同一层级里挂**一个**驱动：Animator 角色挂 `AgentAnimatorDriver`，AnimGraph 角色挂 `AgentAnimGraphDriver`（要求 `AnimGraphManager` 已在层级里且 `m_RootAsset` 已指资产）。**搜索方向**：工具找驱动只看**本物体及其子物体**，所以驱动要么和工具同一个物体、要么在它下面；挂到父物体上会回"未挂载动画驱动"。驱动找 `Animator`/`AnimGraphManager` 同样是本物体及子物体；只有白名单允许挂在父级（整个角色共用一份）。
3. 挂 `AgentAnimationTools`——它带 `[AgentTool]/[AgentAction]`，会出现在 `AgentHost` Inspector 的「快速添加成员工具/动作组件」下拉里；开关表同步自动完成。
4. 需要限制模型只能用哪几个状态/参数时，挂 `AgentAnimationWhitelist` 并填两张清单（留空 = 不限制）。Animator 后端**必须**填状态清单，否则模型看不到任何可播状态。

## 测试策略

- `Assets/Plugins/LLM/Tests/Editor/Agent/AgentAnimationToolsTests.cs`：假驱动 `FakeAnimationDriver` 实现接口并记录收到的值。覆盖：无驱动 → `[Tool Error]`；词表拼接格式；名字修剪与负淡入钳零；空状态名不触碰驱动；驱动返回错误 → 动作失败且原因原样上浮。**全同步**（`UniTask.GetAwaiter().GetResult()`），不引入帧循环，遵守接力队列里的测试铁律。
- 驱动层不做 EditMode 单测：Animator/AnimGraph 的行为都要真物体真资产，属 Play 模式肉眼验收（下节）。
- 编译门禁：`bash ToolProjects/llm_offline_compile.sh` 已加四步 —— `Learn.AgentAnimation`（不带 define）、`Learn.AgentAnimation.AG`（带 `ANIMGRAPH_ENABLED` 且引用 `cn.tuanjie.AnimGraph.Runtime.dll`），外加**不给 `UNITY_EDITOR` 的真机视角** `LLM.Runtime.Player` / `Learn.AgentAnimation.Player`。2026-09-20 实测全脚本 9/9 OK。注意冒烟是手工塞 dll 的，**看不见 asmdef 缺引用**（本轮就漏过一次，已在 `references` 里补上 `cn.tuanjie.AnimGraph.Runtime`），真机验收仍不可省。

## 待人工验收（Play 模式，Agent 做不到）

1. **枚举探针（本设计唯一未证的硬前提）**：AnimGraph 角色 Play 模式下调 `list_animation_states`，确认 `graphsByPath` 在**真机构建包**里确实非空、状态名与编辑器里一致。枚举为空则本后端降级为"白名单即词表"（与 Animator 同规则），其余代码不用改。
2. Animator 后端 `Play(name, -1)` 的跨层搜索行为（不同层同名状态会打到哪一个）。
2b. **两个后端的状态名反查是否真能命中**：`AnimatorStateInfo.shortNameHash` 与 `Animator.StringToHash(状态名)` 是否同一口径（AnimGraph 的状态背后是 `AnimatorState`，理论上同口径；`SMState.stateNameHash` 那套 `AnimationGraph.StringToHash` 只在 manager 侧用）。命中不了的表现是回填 `playing=unknown:<int>` —— 那就改用 `info.IsName(候选名)` 逐个试，或退回只报参数值。
3. 白名单命中/未命中的回填文案是否够模型自我纠正（跑一轮对话看它会不会换名字）。
4. Trigger 下帧自动 `ResetTrigger` 是否会让短窗口迁移漏触发（若漏，改成"图求值后同帧 Reset"并加 Inspector 开关）。

## 非目标（明确不做）

| 不做 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 插件里引用 `cn.tuanjie.animgraph` 或 `UnityEngine.Animator` | 把可选包变成插件硬依赖，违反 ADR-021 | `Learn.AgentAnimation` |
| 后端自动探测 / 两驱动共存仲裁 | 同层级两个驱动时行为不确定，美术要的是显式接线 | `AgentAnimationTools.ResolveDriver` |
| 编辑器「一键从当前图扫描填充白名单」按钮 | AnimGraph 侧运行时已能枚举，再编一套编辑期工具是同一件事做两遍 | `Learn.AgentAnimation` 的 Editor 扩展 |
| `set_animation_bool/int/float/trigger` 四条分开 | 与 `set_animation_parameter` 重复，模型多四条要学的签名 | `AgentAnimationTools.SetAnimationParameter` |
| `wait_animation_end` / 播完回调 | 现在没有"必须等播完"的需求；等待语义牵涉超时、打断、资源锁三件套 | 新写一条 `LongRunning` 动作 + 新 ADR |
| 混合树 / 动画蓝图节点级控制（权重、`SetLayerWeight`、`FloatSmooth`） | 图作者该把这些封成参数暴露出来，模型不该直接摸图内部 | 玩法层自建参数 |
| 运行时搭建/修改 AnimGraph 拓扑 | `AnimStateMachineNode.Enable()` 整体在 `#if UNITY_EDITOR`，构建包里做不到 | 无（平台限制） |
| 动画状态名/参数名的模糊匹配与自动纠正 | 掩盖配置错误；模型拿到的应当是确定的词表 | 无 |
| 跨 Agent 共享动画资源锁 | ADR-010 已否决多键原子锁 | 无 |
