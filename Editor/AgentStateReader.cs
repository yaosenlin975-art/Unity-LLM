/*
┌────────────────────────────┐
│　Description: 编辑器侧读取持久化 Agent 记忆
│　Remark: 纯逻辑无 UI，供 Inspector 调用
│　　　　　 与运行期 PrefsAgentStateStore
│　　　　　 走同一条读取路径
│　ClassName: AgentStateReader
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Storage;
using UnityEngine;

namespace LLM.Editor
{
    /// <summary>编辑器侧读取持久化 agent 状态的纯逻辑，不含 UI</summary>
    public static class AgentStateReader
    {
        /// <summary>全局配置是否启用 Prefs 持久化</summary>
        public static bool IsPrefsStoreEnabled()
        {
            var config = Resources.Load<LLMGlobalConfig_SO>(LLMRuntimeSettings.CONFIG_PATH);
            return config is not null && config.AgentStateStore == EAgentStateStoreKind.Prefs;
        }

        /// <summary>按运行期公式复现 sessionId：{ProfileKey}#{instanceId}；任一为空返回 null</summary>
        public static string ComposeSessionId(AgentProfile_SO profile, string instanceId)
        {
            if (profile is null || string.IsNullOrEmpty(profile.ProfileKey)) return null;
            if (string.IsNullOrEmpty(instanceId)) return null;

            return ZString.Concat(profile.ProfileKey, "#", instanceId);
        }

        /// <summary>读事实槽；无存档或读取失败返回空列表（不抛，仅告警）</summary>
        public static List<AgentFact> ReadFacts(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return new List<AgentFact>();

            AgentFactsBlob blob;
            try
            {
                blob = new PrefsAgentStateStore().LoadFacts(sessionId);
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(AgentStateReader),
                    ZString.Format("读取事实存档 {0} 失败: {1}", sessionId, ex.Message));
                return new List<AgentFact>();
            }

            if (blob?.Facts == null) return new List<AgentFact>();

            var result = new List<AgentFact>(blob.Facts.Count);
            for (int i = 0; i < blob.Facts.Count; i++)
            {
                var fact = blob.Facts[i];
                if (fact is null) continue;

                result.Add(new AgentFact
                {
                    Key = fact.Key,
                    Value = fact.Value,
                    Timestamp = fact.Timestamp
                });
            }

            return result;
        }
    }
}
