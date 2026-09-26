/*
┌────────────────────────────┐
│　Description: 个体动态状态贡献者
│　Remark: 每轮都变，所以排在稳定段之后当尾巴
│　ClassName: ISelfStateContributor
└────────────────────────────┘
*/

namespace LLM.Runtime.Agent.World
{
    /// <summary>个体动态状态：生命值、当前动作。注册在所属 NpcAgentHost 上</summary>
    public interface ISelfStateContributor
    {
        int Order { get; }
        string RenderState();
    }
}
