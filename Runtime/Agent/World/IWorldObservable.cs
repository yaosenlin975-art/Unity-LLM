/*
┌────────────────────────────┐
│　Description: 可被感知的场景身份
│　Remark: 世界侧靠它列在场者，个体侧靠它报自我状态
│　ClassName: IWorldObservable
└────────────────────────────┘
*/

using UnityEngine;

namespace LLM.Runtime.Agent.World
{
    /// <summary>
    /// "我在世界里长什么样"。判据：换一个 NPC 来问答案变不变——不变的内容属于世界，
    /// 这个接口报的是「关于我」，所以属个体侧，只是世界侧也要拿它列在场者。
    /// </summary>
    public interface IWorldObservable
    {
        /// <summary>给模型看的名字。别用 gameObject.name 直接兜底出 "Player (1)" 这种</summary>
        string ObservedLabel { get; }

        /// <summary>观测原点。默认物体位置，高个子角色可覆盖到胸口高度</summary>
        Vector3 ObservedPosition { get; }

        /// <summary>一句自我状态；无内容返回空串，不要写"未知"</summary>
        string ObservableState { get; }

        /// <summary>同列表内稳定排序用，默认 0</summary>
        int Order { get; }
    }
}
