/*
┌────────────────────────────┐
│　Description: 可被 NPC 感知的场景物体标记
│　Remark: 启用期间在册，禁用即出册
│　ClassName: WorldObservable
└────────────────────────────┘
*/

using UnityEngine;

namespace LLM.Demo.Agent.Npc
{
    /// <summary>
    /// 挂在玩家、NPC、道具等"希望被看一眼就知道"的物体上。只负责被感知与自我描述，不做任何决策。
    /// 有状态要说就覆盖 ObservableState，没有就留空串——查询方会省略这一项，不要写"未知"。
    /// </summary>
    public class WorldObservable : MonoBehaviour
    {
        [Header("观测名")]
        [Tooltip("给模型看的名字；留空则用物体名")]
        [SerializeField] private string observedLabel = "";

        public string ObservedLabel => string.IsNullOrEmpty(observedLabel) ? gameObject.name : observedLabel;

        /// <summary>观测原点。默认物体位置，高个子角色可覆盖成胸口高度。</summary>
        public virtual Vector3 ObservedPosition => transform.position;

        public virtual string ObservableState => string.Empty;

        private void OnEnable() => WorldObservableManager.Register(this);

        private void OnDisable() => WorldObservableManager.Unregister(this);
    }
}
