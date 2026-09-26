/*
┌────────────────────────────┐
│　Description: 世界静态事实贡献者
│　Remark: 对所有 NPC 相同，故可当作 provider 前缀缓存
│　ClassName: IWorldLoreContributor
└────────────────────────────┘
*/

namespace LLM.Runtime.Agent.World
{
    /// <summary>世界静态事实：地图背景故事、场景规则。对所有 NPC 相同，只在注册列表变化时重算</summary>
    public interface IWorldLoreContributor
    {
        int Order { get; }
        string RenderLore();
    }
}
