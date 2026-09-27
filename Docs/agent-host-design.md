# Agent 场景宿主设计

## 需求分析

- 在 `LLM.Runtime` 提供一个可挂到普通物体或角色上的 `MonoBehaviour`。
- 组件接收 `AgentProfile_SO`，负责创建、持有和释放该对象唯一的 `AgentCore`。
- 场景 UI 或交互脚本能够向它发送输入，并订阅流式输出和轮次结束。
- 不把聊天 UI、角色移动或具体玩法动作写进通用插件。

成功标准：把组件挂到 GameObject、配置 Profile 后进入 Play Mode，组件可被外部调用 `Trigger`，并逐段发出 Agent 输出；禁用或销毁物体时不残留时钟泵监听或在飞轮次。

## 方案选型

### 采用：AgentHost 作为 AgentCore 的场景生命周期适配器

新增 `AgentHost : MonoBehaviour, IWorldContextProvider`。它只做四件事：

1. 将 Unity 生命周期映射为 `AgentCore` 的创建与释放。
2. 暴露 `Trigger` / `Notify`。
3. 通过带代际的私有 `IAgentOutput` relay 把回调转成实例事件，供气泡、聊天窗或日志按需订阅。
4. 提供一个可由 Inspector 或外部脚本更新的低频核心快照字符串。

这延续 ADR-002 的三个窄接口，不引入事件总线；事件只存在于单个 `AgentHost` 实例上。

### 未采用方案

| 方案 | 拒绝原因 | 将来的唯一落点 |
| --- | --- | --- |
| 每个 UI 自己用 Profile 创建 AgentCore | 同一角色会出现多份记忆、历史与会话状态 | 无；UI 必须绑定现有宿主 |
| 全局 AgentManager | 当前没有跨场景查询或集中调度需求 | 明确需要统一存档/批量调度时再设计 |
| 角色专用基类 | 会迫使现有角色继承体系迁移 | 需要通用角色框架集成时用组合组件适配 |
| 在宿主内实现移动、动画和业务动作 | 通用插件无法知道游戏玩法语义 | 由热更层的 `IAgentActionRunner` 实现。**2026-09-20 口径澄清**：本行否决的始终是"把玩法语义写进宿主/插件本体"；移动与动画后来是以「插件内薄工具组件 + 玩法层驱动实现窄接口」落地的（成员工具模型见 ADR-018，动画的依赖方向见 ADR-021），插件至今不引用 `NavMeshAgent` 之外的玩法类型、也不引用任何动画包 |

## 接口设计

```csharp
public sealed class AgentHost : MonoBehaviour
{
    public AgentProfile_SO Profile { get; }
    public AgentCore Core { get; }
    public bool IsActive { get; }

    public event Action<string> TokenReceived;
    public event Action<string, EAgentOutcome> TurnFinished;
    public event Action<bool> ActiveChanged;

    public void Activate(AgentProfile_SO profile);
    public void Deactivate();
    public void Trigger(string input);
    public void Notify(string worldEvent);
    public void SetCoreSnapshot(string snapshot);
}
```

- Inspector 序列化 `profile` 与可选 `instanceId`；`OnEnable` 用该 Profile 调用 `Activate`。
- `Activate` 先释放旧 Core，再创建新 Core；传空 Profile 时只记录错误，不创建半有效实例。
- `Trigger` / `Notify` 在未激活或文本为空时直接返回。
- `OnDisable` 调用 `Deactivate`，由 `AgentCore.Dispose` 摘掉时钟泵监听并取消在飞轮次。
- 切换或停用时提升输出代际，旧 Core 的迟到回调被丢弃；事件订阅者异常只记录日志，不反向打断 Core 收尾。
- Provider 沿用现有零配置约定：Dispatcher 尚无 Provider 时加载 `Resources/LLMProviderConfig` 并注册；资源不存在时记录错误。
- 动作 runner 固定注入 `ReflectionActionRunner`（反射执行 static `[AgentAction]`，含内置记忆动作）；成员动作不经 runner，直接反射打实例。取代本文件原「默认 NullActionRunner」的留白，见 ADR-017 / ADR-019。

## 数据结构

- `AgentProfile_SO profile`：Inspector 初始配置，也是当前激活 Profile。
- `string instanceId`：可选稳定实例标识；为空时交给 `AgentCore` 生成唯一值。
- `string coreSnapshot`：每轮注入的低频只读快照，默认空。
- `AgentCore core`：仅运行时存在，不序列化。
- 三个实例事件：不保存历史，只转发当前 Core 输出和激活状态。

## 关键流程

1. `OnEnable` → `Activate(profile)`。
2. `Activate` 校验 Profile → 确保 Provider → 创建当前代际的输出 relay 与 `AgentCore`。
3. 外部交互调用 `Trigger` 或 `Notify`。
4. `AgentCore` 调用 relay 的 `IAgentOutput.OnToken` / `OnTurnFinished`；代际仍有效时，`AgentHost` 转发为实例事件。
5. `OnDisable` / 显式切换 Profile → `Deactivate` → `AgentCore.Dispose`。

## 文件范围

- `Assets/Plugins/LLM/Runtime/Agent/AgentHost.cs`
- `Assets/Plugins/LLM/Tests/Editor/Agent/AgentHostTests.cs`
- `Assets/Plugins/LLM/Docs/decisions/ADR-013-scene-host-owns-agent-core.md`

## 非目标（明确不做）

| 不做项 | 原因 | 将来的唯一落点 |
| --- | --- | --- |
| 自动寻找聊天窗口 | 插件不能依赖 Learn 示例层 | Learn 触发器负责绑定 |
| 角色点击、碰撞或距离检测 | 输入系统与交互规则属于游戏 | 具体项目交互组件 |
| Agent 存档 | 尚无宿主存档格式 | 已由 [agent-state-storage-design.md](./agent-state-storage-design.md) 设计：内核经 `IFactStore`/`IConversationStore` 自己读写，宿主只负责给稳定 `instanceId` |
| 动态动作 runner 配置 | 当前验收仅为聊天 | 首个真实角色动作接入时设计 |
| 全局广播输出 | 单 Agent 实例订阅已满足需求 | 出现跨 Agent 观测需求时另建诊断层 |
