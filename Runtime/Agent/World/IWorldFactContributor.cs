/*
┌────────────────────────────┐
│　Description: 世界动态事实贡献者
│　Remark: 允许每轮变，但对所有 NPC 相同
│　ClassName: IWorldFactContributor
└────────────────────────────┘
*/

namespace LLM.Runtime.Agent.World
{
    /// <summary>世界动态事实：时间、天气。允许每轮变，但对所有 NPC 相同</summary>
    public interface IWorldFactContributor
    {
        int Order { get; }
        string RenderFact();
    }
}
