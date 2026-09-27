# ADR-002: 宿主用三个窄接口，而非胖接口或事件总线

## Status
Accepted

## Date
2026-09-19

## Context
内核要同时被三类宿主使用：场景里的 NPC（有身体、有动作）、UI 对话助手（只有输出）、编辑器沙盒（临时假宿主）。上一代 `LLM.Npc` 的 `IActionHandler.Execute(InteractionOption option, NPCBrain brain)` 把动作处理和具体宿主类型焊死，是它难以复用并最终被删的根因之一。

## Decision
内核只认三个窄接口：

```csharp
public interface IWorldContextProvider { string GetCoreSnapshot(); }
public interface IAgentOutput
{
    void OnToken(string delta);
    void OnTurnFinished(string answer, EAgentOutcome outcome);
}
public interface IAgentActionRunner
{
    UniTask<AgentActionResult> RunAsync(string actionId, string argsJson,
        AgentActionContext ctx, CancellationToken ct);
}
```

宿主按需实现子集：助手没有动作 → 复用 `NullActionRunner`；沙盒三个都实现。`AgentActionContext` 只携带 `AgentCore`、`AgentProfile_SO` 与 `SessionId`，不携带任何宿主具体类型。

## Alternatives Considered

### 单一胖接口 `IAgentHost`（三件事全包）
- Pros：内核只持一个引用，概念少一个
- Cons：助手宿主必须为空动作写桩；接口能力会随宿主类型反复扯皮
- Rejected：能力本就不对称，强行合并只会产出空实现

### 事件总线解耦（内核发请求事件，宿主订阅应答）
- Pros：多对多、彻底解耦
- Cons：多一跳路由；"没人应答"这类故障只能靠日志排查
- Rejected：为两个宿主引入总线是上一代模块的膨胀路径

## Consequences
- 内核里不出现 `if (runner == null)` 之类的分支，缺能力靠 `Null*` 默认对象表达
- 动作实现放热更程序集，`Game.Hotfix` 通过 `AgentActionRegistry.Register(typeof(X).Assembly)` 登记
- 若将来 NPC 之间需要互相感知，那是世界/事件层的职责，不下沉进内核

See also: [agent-kernel-design.md](../agent-kernel-design.md)
