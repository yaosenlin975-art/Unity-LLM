/*
┌────────────────────────────┐
│　Description: Agent 导航工具
│　Remark: 通过 NavMeshAgent 查询与移动
│　ClassName: AgentNavigationTools
└────────────────────────────┘
*/

using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Agent;
using UnityEngine;
using UnityEngine.AI;

namespace LLM.Runtime.Tools
{
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class AgentNavigationTools : MonoBehaviour
    {
        #region - 字段 -

        [Header("目标点吸附半径（米）")]
        [Tooltip("目标点不在 NavMesh 上时，先在这个半径内找最近的可行点；仍找不到才明确失败")]
        [SerializeField] private float destinationSnapRadius = 2f;

        [Header("走到玩家身边的停靠距离（米）")]
        [Tooltip("move_to_player 停在离玩家多远的位置")]
        [SerializeField] private float playerStopDistance = 1.5f;

        private NavMeshAgent agent;
        private NavMeshAgent Agent
        {
            get
            {
                agent ??= GetComponent<NavMeshAgent>();
                return agent;
            }
        }

        #endregion

        #region - 公开接口 -

        [AgentTool("get_navigation_status", "获取当前 Agent 的 NavMesh 导航状态。")]
        public string GetNavigationStatus()
        {
            if (Agent == null)
                return "[Tool Error] NavMeshAgent missing on this Agent.";

            if (!agent.enabled)
                return "status=disabled";

            if (!agent.isOnNavMesh)
            {
                Vector3 position = agent.transform.position;
                return ZString.Format("status=off_navmesh position=({0:F2},{1:F2},{2:F2})",
                    position.x, position.y, position.z);
            }

            string state = "idle";
            if (agent.pathPending)
                state = "path_pending";
            else if (agent.hasPath && agent.remainingDistance > agent.stoppingDistance)
                state = "moving";
            else if (agent.hasPath)
                state = "arrived_or_stopping";

            Vector3 destination = agent.destination;
            Vector3 currentPosition = agent.transform.position;

            return ZString.Format(
                "status={0} position=({1:F2},{2:F2},{3:F2}) destination=({4:F2},{5:F2},{6:F2}) remainingDistance={7:F2} speed={8:F2} stoppingDistance={9:F2}",
                state,
                currentPosition.x, currentPosition.y, currentPosition.z,
                destination.x, destination.y, destination.z,
                agent.remainingDistance, agent.speed, agent.stoppingDistance);
        }

        [AgentAction("move_to", "让当前 Agent 通过 NavMesh 移动到世界坐标。", Idempotent = false,
            Repeatable = true, LongRunning = true)]
        public async UniTask<AgentActionResult> MoveTo(AgentActionContext context,
            float worldX, float worldY, float worldZ)
        {
            if (Agent == null)
                return AgentActionResult.Failure("[Tool Error] NavMeshAgent missing on this Agent.");

            if (!IsFinite(worldX) || !IsFinite(worldY) || !IsFinite(worldZ))
                return AgentActionResult.Failure("目标坐标无效。");

            if (agent == null)
                return AgentActionResult.Failure("缺少 NavMeshAgent。");

            if (!agent.isActiveAndEnabled || !agent.isOnNavMesh)
                return AgentActionResult.Failure("当前 Agent 未在 NavMesh 上。");

            Vector3 destination = new Vector3(worldX, worldY, worldZ);
            if (!NavMesh.SamplePosition(destination, out NavMeshHit hit, destinationSnapRadius, NavMesh.AllAreas))
                return AgentActionResult.Failure(ZString.Format("目标点 {0:F1} 米内没有 NavMesh。", destinationSnapRadius));

            // ponytail: 吸附只保证"脚下有面"，不保证与本 Agent 连通（另一块 island 也算最近）。
            // 真出现"设了目标点却不动"再加一次 NavMesh.CalculatePath 的 status 判定
            if (!agent.SetDestination(hit.position))
                return AgentActionResult.Failure("NavMeshAgent 无法设置目标点。");

            agent.isStopped = false;
            return await WaitForArrival(context?.CancellationToken ?? default);
        }

        [AgentAction("move_to_player", "寻路走到玩家身边停下，用于当面搭话或递交。", Idempotent = false,
            Repeatable = true, LongRunning = true)]
        public async UniTask<AgentActionResult> MoveToPlayer(AgentActionContext context)
        {
            if (Agent == null)
                return AgentActionResult.Failure("[Tool Error] NavMeshAgent missing on this Agent.");

            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player == null)
                return AgentActionResult.Failure("场景里没有 tag 为 Player 的对象。");

            if (agent == null)
                return AgentActionResult.Failure("缺少 NavMeshAgent。");

            if (!agent.isActiveAndEnabled || !agent.isOnNavMesh)
                return AgentActionResult.Failure("当前 Agent 未在 NavMesh 上。");

            // 玩家多半站在地面而非 NavMesh 上，先把他脚下投影到导航面，再沿"玩家→本 Agent"方向退出一段停靠距离
            if (!NavMesh.SamplePosition(player.transform.position, out NavMeshHit playerPoint,
                    destinationSnapRadius, NavMesh.AllAreas))
                return AgentActionResult.Failure("玩家附近没有 NavMesh，走不过去。");

            Vector3 standoff = agent.transform.position - playerPoint.position;
            standoff.y = 0f;
            if (standoff.sqrMagnitude < 1e-4f)
                standoff = -transform.forward;

            if (standoff.magnitude <= playerStopDistance)
                return AgentActionResult.Success("已经在玩家身边了。");

            Vector3 wanted = playerPoint.position + standoff.normalized * playerStopDistance;
            if (!NavMesh.SamplePosition(wanted, out NavMeshHit stopPoint, destinationSnapRadius, NavMesh.AllAreas))
                return AgentActionResult.Failure("玩家身边没有可站立的 NavMesh 点。");

            if (!agent.SetDestination(stopPoint.position))
                return AgentActionResult.Failure("NavMeshAgent 无法设置目标点。");

            agent.isStopped = false;
            return await WaitForArrival(context?.CancellationToken ?? default);
        }

        [AgentAction("stop_navigation", "停止当前 Agent 的 NavMesh 导航。", Idempotent = true)]
        public UniTask<AgentActionResult> StopNavigation()
        {
            if (Agent == null)
                return Failure("[Tool Error] NavMeshAgent missing on this Agent.");

            if (!agent.isActiveAndEnabled || !agent.isOnNavMesh)
                return Failure("当前 Agent 未在 NavMesh 上。");

            agent.isStopped = true;
            agent.ResetPath();
            return Success("已停止导航。");
        }

        #endregion

        #region - 私有方法 -

        private async UniTask<AgentActionResult> WaitForArrival(System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                while (agent.pathPending || agent.hasPath &&
                       agent.remainingDistance > Mathf.Max(agent.stoppingDistance, 0.05f))
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);

                if (agent.pathStatus == NavMeshPathStatus.PathInvalid)
                    return AgentActionResult.Failure("导航路径无效。");

                agent.isStopped = true;
                agent.ResetPath();
                return AgentActionResult.Success("已到达目标。");
            }
            catch (System.OperationCanceledException)
            {
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    agent.isStopped = true;
                    agent.ResetPath();
                }

                throw;
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static UniTask<AgentActionResult> Failure(string reason) => new UniTask<AgentActionResult>(AgentActionResult.Failure(reason));

        private static UniTask<AgentActionResult> Success(string reason) => new UniTask<AgentActionResult>(AgentActionResult.Success(reason));

        #endregion
    }
}
