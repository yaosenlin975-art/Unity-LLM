# ADR-013: 场景 AgentHost 持有唯一 AgentCore，UI 绑定宿主实例

## Status
Accepted

## Date
2026-09-20

## Context
`AgentCore` 已明确由 NPC MonoBehaviour、助手 UI 或编辑器沙盒持有，但运行时目前没有可挂到物体的宿主组件。若聊天窗口只接收 `AgentProfile_SO` 并自行创建 Core，同一角色会同时存在角色 Core 与 UI Core，两边的轮次、记忆、心跳和 sessionId 相互独立。

## Decision
在 `LLM.Runtime` 增加 `AgentHost` 作为场景生命周期适配器，每个挂载实例持有至多一个 `AgentCore`。表现层不根据 Profile 重建 Agent，而是绑定 `AgentHost`，通过它发送输入并订阅该实例的流式输出。Profile 仍由宿主公开，供窗口显示角色身份。

输出使用宿主实例上的 C# 事件转发，不新增全局事件总线，也不修改 `AgentCore` 的三个窄接口。

## Alternatives Considered

### UI 接收 Profile 并独立创建 AgentCore
- 优点：窗口可以脱离场景角色工作。
- 缺点：同一 Profile 不代表同一实例；记忆和历史分叉，关闭窗口还会销毁一条独立会话。
- 拒绝理由：破坏“每个 agent 一份 Core”的既有设计。

### 全局 Agent 注册表
- 优点：UI 可按 ID 查询任意 Agent。
- 缺点：增加注册、注销、跨场景生命周期和重复 ID 处理。
- 拒绝理由：当前触发器已有宿主直接引用，不需要全局查找。

### AgentCore 直接暴露事件
- 优点：少一层转发。
- 缺点：把 Unity 表现层订阅语义带进纯内核，并扩大现有接口。
- 拒绝理由：生命周期适配属于 `AgentHost`，不是内核职责。

## Consequences

- 角色、气泡和聊天窗口共享同一条会话与记忆。
- `AgentHost` 必须在禁用时释放 Core；UI 必须在解绑时退订实例事件。
- 纯 UI 助手若没有场景角色，也可把 `AgentHost` 挂到 UI 根对象使用。
- 本决策扩展 ADR-002 的宿主落地方式，不替代三个窄接口。
