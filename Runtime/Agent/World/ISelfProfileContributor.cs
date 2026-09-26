/*
┌────────────────────────────┐
│　Description: 个体静态能力贡献者
│　Remark: 只在"问我"时才需要，故不进世界侧全局表
│　ClassName: ISelfProfileContributor
└────────────────────────────┘
*/

namespace LLM.Runtime.Agent.World
{
    /// <summary>个体静态能力：该角色的可用动画清单、身份能力。注册在所属 NpcAgentHost 上，不进全局表</summary>
    public interface ISelfProfileContributor
    {
        int Order { get; }
        string RenderProfile();
    }
}
