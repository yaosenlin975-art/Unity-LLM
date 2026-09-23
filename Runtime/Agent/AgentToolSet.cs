/*
┌────────────────────────────┐
│　Description: 单 agent 的成员工具/动作集合
│　Remark: 由宿主扫描自身层级收集，per-
│　　　　　 agent 私有，不进全局注册表
│　ClassName: AgentToolSet
└────────────────────────────┘
*/

using System.Collections.Generic;
using UnityEngine;

namespace LLM.Runtime.Agent
{
    /// <summary>
    /// 对齐 Mu 的 NpcToolPipeline.instanceTools：成员 [AgentTool]/[AgentAction] 是挂在
    /// 宿主 GameObject 层级上的实例方法，只属于该 agent，执行时反射打在实例上。
    /// </summary>
    public sealed class AgentToolSet
    {
        private readonly Dictionary<string, AgentToolRegistry.RegisteredTool> memberTools = new();
        private readonly Dictionary<string, AgentActionDef> memberActions = new();

        public int MemberToolCount => memberTools.Count;

        public int MemberActionCount => memberActions.Count;

        /// <summary>扫描 root（含自身）层级，收集成员工具与成员动作；重复调用整体重扫</summary>
        public void Collect(GameObject root)
        {
            memberTools.Clear();
            memberActions.Clear();

            if (root == null) return;

            var tools = AgentToolRegistry.ScanHierarchyTools(root);
            for (int i = 0; i < tools.Count; i++)
                memberTools[tools[i].Name] = tools[i];

            var actions = AgentActionRegistry.ScanHierarchyTools(root);
            for (int i = 0; i < actions.Count; i++)
                memberActions[actions[i].Id] = actions[i];
        }

        public bool TryGetTool(string name, out AgentToolRegistry.RegisteredTool tool)
        {
            return memberTools.TryGetValue(name, out tool);
        }

        public bool TryGetAction(string name, out AgentActionDef def)
        {
            return memberActions.TryGetValue(name, out def);
        }

        /// <summary>把成员工具/动作声明追加进列表，seen 用于与全局声明去重（成员优先入列）</summary>
        public void BuildDeclarations(List<LLMTool> into, HashSet<string> seen)
        {
            if (into == null) return;

            foreach (var kv in memberActions)
            {
                if (seen != null && !seen.Add(kv.Key)) continue;
                into.Add(new LLMTool(kv.Value.Id, kv.Value.Description, kv.Value.ParametersJsonSchema));
            }

            foreach (var kv in memberTools)
            {
                if (seen != null && !seen.Add(kv.Key)) continue;
                into.Add(new LLMTool(kv.Value.Name, kv.Value.Description, kv.Value.ParametersJsonSchema));
            }
        }
    }
}
