/*
 * Agent 测试脚手架 — 测试动作声明
 * AgentActionExecutor 按 AgentActionRegistry 路由：不登记的动作到不了 IAgentActionRunner，
 *只会回落同步 [Tool] 执行器。这里用 [AgentAction] 声明一组脚手架动作供矩阵行复用；
 * 方法体永远不执行（runner 被 FakeRunner 顶掉），只借注册表的元数据与路由
 */

using Cysharp.Threading.Tasks;
using LLM.Runtime.Agent;

namespace LLM.Tests.Editor.Agent
{
    public static class ScaffoldTestActions
    {
        [AgentAction("t_look", "看一眼（脚手架）", Idempotent = true)]
        public static UniTask<AgentActionResult> Look(AgentActionContext ctx, int x)
        {
            return new UniTask<AgentActionResult>(AgentActionResult.Success("fake-look"));
        }

        /// <summary>非幂等默认：内核自动注入 say，供先说后做 / BLOCK / 承诺收口类用例</summary>
        [AgentAction("t_trade", "交易（脚手架）")]
        public static UniTask<AgentActionResult> Trade(AgentActionContext ctx, string item)
        {
            return new UniTask<AgentActionResult>(AgentActionResult.Success("fake-trade"));
        }

        /// <summary>LongRunning：执行期不扣墙钟额度，供长动作超时类用例</summary>
        [AgentAction("t_long", "带路（脚手架）", LongRunning = true)]
        public static UniTask<AgentActionResult> Long(AgentActionContext ctx, string place)
        {
            return new UniTask<AgentActionResult>(AgentActionResult.Success("fake-long"));
        }

        /// <summary>带资源锁：LockKey 模板插值 item:{item}，供锁排队 / 等待超时 / 退队用例</summary>
        [AgentAction("t_lock", "占用资源（脚手架）", Idempotent = true, LockKey = "item:{item}")]
        public static UniTask<AgentActionResult> Lock(AgentActionContext ctx, string item)
        {
            return new UniTask<AgentActionResult>(AgentActionResult.Success("fake-lock"));
        }
    }
}
