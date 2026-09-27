# ADR-001: Agent 内核放现有 AOT 程序集 `LLM.Runtime`

## Status
Accepted

## Date
2026-09-19

## Context
需要在在线 API 层（`Assets/Plugins/LLM/`，入口 `LLMSession`）之上加一个 agent 内核，同时服务游戏内 NPC 与 UI 里的对话助手。项目基线是团结引擎 1.10 / Unity 2022.3 + HybridCLR 热更 + YooAsset，约定见 `AGENTS.md`。关键约束：热更程序集 `Game.Hotfix` 已经引用 `LLM.Runtime`（依赖方向只能是 hotfix → AOT）；上一代 `LLM.Npc` 模块因过度设计被整体删除，范围控制极敏感。

## Decision
内核代码放 `Assets/Plugins/LLM/Runtime/Agent/`，与对话层同程序集、同 AOT 边界。策略性的东西（动作实现、提示词、配置数值）全部留在 `Game.Hotfix` 与 ScriptableObject 资产里。

## Alternatives Considered

### 新建独立程序集 `LLM.Agent.Runtime`
- Pros：可单独移除或替换；与对话层彻底解耦
- Cons：多一个 asmdef 与 HybridCLR 配置面；当前没有任何"需要被独立裁剪"的消费方
- Rejected：解耦收益要到有多个宿主程序集时才兑现，现在只是多一个文件

### 内核进 `Game.Hotfix`
- Pros：连调度与超时逻辑都能在线热修
- Cons：编辑器工具（沙盒窗口）需要 asmref 热更程序集；HybridCLR 泛型收敛与 AOT 补充元数据要额外处理；内核与具体游戏代码同程序集容易被反向依赖污染
- Rejected：内核是变化慢的管道，不值得为此放弃 AOT 侧的工具链

## Consequences
- 内核 bug 需要出包才能修，因此它的代码量必须压在"管道"级别（内核 9 文件，均为 S/M/L 量级，见设计稿文件清单）
- `IToolExecutor`、`IWorldContextProvider`、`IAgentActionRunner` 定义在 AOT，实现放热更，接口不得引用热更类型
- 未来若要给第三方项目复用，再拆独立程序集，成本是一次机械搬运
- **HybridCLR 边界的三条真约束**（实现与测试都要按此处理）：
  1. AOT 侧反射扫描热更程序集（`ToolRegistry.Register(hotfixAssembly)`）方向正确——HybridCLR 对热更程序集做完整 interpret，`GetTypes()`/`GetCustomAttribute` 可用；反向（热更反射 AOT）才是坑。
  2. 但 **EditMode 单测跑在 Mono 上，测不到 IL2CPP 真机的扫描行为**，特性丢失只在设备上暴露，因此步骤 A 必须补一次真机 IL2CPP 冒烟。
  3. `LLM.Runtime` 未列入 `patchAOTAssemblies`。热更侧若对内核类型做反射或泛型化，需要补 AOT 通用参考/`link.xml`，否则裁剪报错；内核自身不得依赖 `[RuntimeInitializeOnLoadMethod]` 去发现热更工具，必须显式 `Register(assembly)`。

See also: [ADR-004](./ADR-004-tool-selection-llm-autonomous.md)、[agent-kernel-design.md](../agent-kernel-design.md)
