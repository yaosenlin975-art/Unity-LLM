/*
┌────────────────────────────┐
│　Description: 编辑器侧读取持久化 Agent 记忆
│　Remark: 纯逻辑无 UI，供 Inspector 调用；按 GlobalConfig
│　　　　　 配的类名反射装载，与运行期同一条读取路径，
│　　　　　 不点名任何具体实现（ADR-030）
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
    /// <summary>预览面板要分辨的三种状态：没配 / 配了但装不出来 / 就绪。</summary>
    public enum EStorePreviewState
    {
        Off,
        Missing,
        Ready
    }

    /// <summary>编辑器侧读取持久化 agent 状态的纯逻辑，不含 UI</summary>
    public static class AgentStateReader
    {
        /// <summary>
        /// 事实槽配置的三态；bool 不够用，"没配"与"配了但类型丢了"要分开显示。
        /// configuredType 回填磁盘上的原值，Missing 文案要指名是哪个类名失效。
        /// </summary>
        public static EStorePreviewState GetFactsStoreState(out string configuredType)
        {
            var config = Resources.Load<LLMGlobalConfig_SO>(LLMRuntimeSettings.CONFIG_PATH);
            configuredType = config is null ? null : config.AgentFactsStoreType;
            if (string.IsNullOrWhiteSpace(configuredType))
                return EStorePreviewState.Off;

            return StoreTypeResolver.Resolve(configuredType, typeof(IFactStore), out _) is null
                ? EStorePreviewState.Missing
                : EStorePreviewState.Ready;
        }

        /// <summary>按运行期公式复现 sessionId：{ProfileKey}#{instanceId}；任一为空返回 null</summary>
        public static string ComposeSessionId(AgentProfile_SO profile, string instanceId)
        {
            if (profile is null || string.IsNullOrEmpty(profile.ProfileKey)) return null;
            if (string.IsNullOrEmpty(instanceId)) return null;

            return ZString.Concat(profile.ProfileKey, "#", instanceId);
        }

        /// <summary>读事实槽；未配置、无存档或读取失败都返回空列表（不抛，仅告警）</summary>
        public static List<AgentFact> ReadFacts(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return new List<AgentFact>();

            var config = Resources.Load<LLMGlobalConfig_SO>(LLMRuntimeSettings.CONFIG_PATH);
            if (config is null || string.IsNullOrWhiteSpace(config.AgentFactsStoreType))
                return new List<AgentFact>();

            var store = StoreTypeResolver.Create<IFactStore>(config.AgentFactsStoreType, out string error);
            if (store is null)
            {
                Log.Warning(nameof(AgentStateReader),
                    ZString.Format("事实槽存储 {0} 装不出来: {1}", config.AgentFactsStoreType, error));
                return new List<AgentFact>();
            }

            AgentFactsBlob blob;
            try
            {
                // Create 只兜构造函数，读档抛异常同样不能炸 Inspector
                blob = store.LoadFacts(sessionId);
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
