# ADR-021: 动画能力由玩法层驱动接口注入，LLM.Runtime 不引用任何动画包

## Status
Accepted

## Date
2026-09-20

## Context
Agent 需要"做动作"：让 NPC 播一个状态、改一个动画参数。成员工具模型（ADR-018）已经允许 `[AgentTool]` / `[AgentAction]` 直接 `GetComponent` 打场景组件——`AgentNavigationTools` 就是这么直连 `NavMeshAgent` 的。动画不能照这个路子写死，原因有三条，都是对码核实过的：

1. 本工程同时存在两套动画后端：传统 `UnityEngine.Animator`（`Assets/Samples/DogKnight` 那批角色）与团结引擎的 `cn.tuanjie.animgraph` 1.0.0（`Packages/manifest.json:3`，课程与 ARPG 角色在用）。两者没有共同基类，`AnimGraphManager` 也不是 `Animator` 的子类。
2. `cn.tuanjie.animgraph` 是**可选包**。`Assets/Plugins/LLM` 的定位是可独立复用的插件（README 与 ADR-016 都明确它不依赖 Odin/Sirenix），一旦 `LLM.Runtime.asmdef` 引入动画包引用，插件就被一个可选编辑器扩展绑死，换工程即编不过。
3. 两后端的"合法名字"集合差别很大：AnimGraph 的状态名在构建包里可枚举（`RootAsset.graphsByPath` + `AnimationNode.GetCustomName()`），而传统 Animator 的状态名在构建包里**拿不到**（`AnimatorController`/`AnimatorStateMachine` 的托管绑定只存在于 `UnityEditor.CoreModule`）。所以"词表从哪来"必须按后端分别决定，不能一套逻辑硬编两个后端。

另外 AnimGraph 的已知红线决定了校验不能省：参数名写错**静默返回 0**（`AnimGraphManager_Binding.cs:358-361`），Trigger 不自动消费（`:236-250`），`AnimGraphManager.TryGetTransition` 恒 false（`AnimGraphManager.cs:3212-3219`）。模型输出不可信，写错名字不会报错、只会没反应，这正是要在动作层挡住的事。

## Decision
1. **`LLM.Runtime` 只放抽象与工具，不放实现**：新增窄接口 `IAgentAnimationDriver`（`Runtime/Agent/AgentInterfaces.cs`，与 `IWorldContextProvider` / `IAgentToolGate` 同处一地），成员只收发字符串与 `List<string>`；新增成员工具组件 `Runtime/Tools/AgentAnimationTools.cs`，两条只读 `[AgentTool]` + 三条 `[AgentAction]`，全部转发给驱动。`LLM.Runtime.asmdef` 不新增任何引用。
2. **实现落在玩法层新程序集** `Learn.AgentAnimation`（`Assets/Learn/ARPG/Scripts/AgentAnimation/`，引用 `LLM.Runtime` / `Lin.Runtime` / `UniTask`）：`AgentAnimatorDriver`、`AgentAnimGraphDriver`、`AgentAnimationWhitelist` 三个组件都在这里。依赖方向单向：玩法层 → 插件。
3. **AnimGraph 用 `versionDefines` 隔离**：`Learn.AgentAnimation.asmdef` 声明 `cn.tuanjie.animgraph [1.0.0] → ANIMGRAPH_ENABLED`，`AgentAnimGraphDriver.cs` 整文件包在 `#if ANIMGRAPH_ENABLED` 里。卸掉包时该程序集仍然编得过，只是少一个驱动。
4. **后端不自动探测**：`AgentAnimationTools` 只做 `GetComponentInChildren<IAgentAnimationDriver>(true)`，挂哪个驱动就用哪个后端；同时挂两个属于配置错误，不做仲裁。
5. **词表策略按后端分**：AnimGraph 以运行时自动枚举为主、白名单为限制；Animator 白名单即词表（构建包无法枚举状态名），未配白名单时不做词表校验、只回填"已请求切换"。
6. **写前校验 + 中文失败回填**：状态名/参数名不在词表内直接返回失败原因（走内核既有的 `[Action Failed]` 前缀），不允许静默 no-op。

## Alternatives Considered

### `LLM.Runtime` 直接引用 AnimGraph 包，插件内自己探测后端
- 优点：一个程序集搞定，接线最少。
- 缺点：把可选包变成插件的硬依赖，插件不能再独立复用；且 Animator 与 AnimGraph 的分支会写进同一个编译单元。
- 拒绝理由：与 ADR-016 起的"插件不引入额外依赖"定位冲突。

### 反射探测动画包，避免 asmdef 引用
- 优点：既单程序集又零引用。
- 缺点：绕开编译期检查，包一升级就静默失效，且违反"能编译期确定就不要反射"的既有做法。
- 拒绝理由：动画是玩家能直接看见的功能，静默失效代价高。

### 一个驱动类里 `if (hasAnimGraph)` 双后端
- 优点：使用者只挂一个组件。
- 缺点：该文件必须同时引用两套类型，`versionDefines` 就失去意义。
- 拒绝理由：与决策 3 直接冲突。

### 工具自己 `GetComponent<Animator>()`，不要接口（像导航那样）
- 优点：少一个接口类型。
- 缺点：导航只有一个后端，动画有两个且一个是可选包；不做接口就得把包引用放进插件。
- 拒绝理由：本 ADR 的 Context 1/2。

### 自动探测后端（谁在用谁）+ 编辑器"一键扫描填充白名单"按钮
- 优点：接线更省事。
- 缺点：同层级两个驱动时行为不确定；扫描按钮与运行时自动枚举是同一件事做两遍。
- 拒绝理由：收益被决策 5 覆盖。将来若确实需要编辑期批量填表，落点在 `Learn.AgentAnimation` 的 Editor 扩展，不在插件里。

## Consequences
- 正向：插件保持零动画依赖；换后端/加后端不动内核与工具层；AnimGraph 缺席时工程照常编译；模型拿到的名字与后端真实词表一致，写错会得到可读的失败原因。
- 负向：多一个程序集与三个类型，接线要挂两件事（驱动 + 工具），配置成本比"直连组件"高；`#if ANIMGRAPH_ENABLED` 使 AnimGraph 驱动在编辑器的错误提示里默认不可见（要装包才看得懂）；Animator 后端的词表完全靠人工维护，漏填就等于对模型禁用该状态。
- 待验证：AnimGraph 侧"构建包里枚举状态名"目前只有托管侧证据（字段序列化 + player DLL 里存在 `RuntimeStateMachine.get_states`），**没有真机构建包实证**；实施第一步是在 Play Mode 跑一次枚举探针，枚举为空则按决策 5 的 Animator 规则降级为"白名单即词表"。
