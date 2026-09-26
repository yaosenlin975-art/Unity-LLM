/*
┌────────────────────────────┐
│　Description: 轻量可观测物组件
│　Remark: 玩家/道具/群众专用——只被看见，不思考
│　ClassName: WorldObservable
└────────────────────────────┘
*/

using UnityEngine;

namespace LLM.Runtime.Agent.World
{
    /// <summary>
    /// 挂在玩家、道具、群众等"希望被看一眼就知道"的物体上。
    /// 会思考的 NPC 不挂这个——那个身份由 NpcAgentHost 自己承担。
    /// </summary>
    public class WorldObservable : MonoBehaviour, IWorldObservable
    {
        #region - 字段 -
        [Header("观测名")]
        [Tooltip("给模型看的名字；留空则用物体名")]
        [SerializeField] private string observedLabel = "";
        #endregion

        #region - 属性 -
        public string ObservedLabel => string.IsNullOrEmpty(observedLabel) ? gameObject.name : observedLabel;

        /// <summary>观测原点。默认物体位置，高个子角色可覆盖成胸口高度</summary>
        public virtual Vector3 ObservedPosition => transform.position;

        /// <summary>有状态要说就覆盖，没有就留空串——查询方会省略这一项</summary>
        public virtual string ObservableState => string.Empty;
        #endregion

        #region - 生命周期 -
        // 注册/注销绑在启用态上：禁用即出册，物体被 Destroy 时 OnDisable 也跟着走这条路，
        // 所以调用方不需要自己配对。WorldSnapshotService 侧的判活是第二道兜底
        private void OnEnable() => WorldSnapshotService.Register((IWorldObservable)this);

        private void OnDisable() => WorldSnapshotService.Unregister((IWorldObservable)this);
        #endregion
    }
}
