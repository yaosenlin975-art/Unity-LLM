/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent Transform 工具           │
 * │ Remark      : 查询所属 Agent 的世界变换       │
 * │ ClassName   : AgentTransformTools            │
 * └────────────────────────────────────────────┘
 */

using Cysharp.Text;
using UnityEngine;

namespace LLM.Runtime.Tools
{
    public sealed class AgentTransformTools : MonoBehaviour
    {
        #region - 公开接口 -

        [AgentTool("get_agent_transform", "获取当前 Agent 的世界坐标、旋转与缩放。")]
        public string GetAgentTransform()
        {
            Transform target = transform;
            Vector3 position = target.position;
            Vector3 rotation = target.eulerAngles;
            Vector3 scale = target.lossyScale;

            return ZString.Format(
                "position=({0:F2},{1:F2},{2:F2}) rotationEuler=({3:F1},{4:F1},{5:F1}) scale=({6:F2},{7:F2},{8:F2})",
                position.x, position.y, position.z,
                rotation.x, rotation.y, rotation.z,
                scale.x, scale.y, scale.z);
        }

        #endregion
    }
}
