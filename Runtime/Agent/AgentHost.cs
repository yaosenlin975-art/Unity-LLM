/*
┌────────────────────────────┐
│　Description: Agent 场景生命周期宿主
│　Remark: 将 MonoBehaviour 生命周期映射到
│　　　　　 唯一 AgentCore
│　ClassName: AgentHost
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime.Agent.Npc;
using UnityEngine;

namespace LLM.Runtime.Agent
{
    /// <summary>挂到场景物体上的 AgentCore 生命周期适配器。</summary>
    public sealed class AgentHost : MonoBehaviour, IWorldContextProvider, IAgentToolGate
    {
        #region - 字段 -

        [SerializeField] private AgentProfile_SO profile;
        [SerializeField] private string instanceId;
        [SerializeField, TextArea] private string coreSnapshot;

        [Header("工具门控")]
        [Tooltip("覆盖式：未收录的工具默认可用，收录项按 Enabled 生效。点 Inspector 的“扫描并同步工具列表”刷新")]
        [SerializeField] private List<AgentToolToggle> toolToggles = new();

        private AgentCore core;
        private AgentToolSet toolSet;
        private NpcAffinityController affinityController;
        private uint outputGeneration;

        #endregion

        #region - 属性与事件 -

        public AgentProfile_SO Profile => profile;

        public AgentCore Core => core;

        public bool IsActive => core != null;

        /// <summary>本实例的工具开关表，仅供 Inspector 与外部只读展示</summary>
        public IReadOnlyList<AgentToolToggle> ToolToggles => toolToggles;

        /// <summary>本实例的成员工具/动作集合；未激活时为 null</summary>
        public AgentToolSet ToolSet => toolSet;

        public event Action<string> TokenReceived;

        public event Action<string, string> SayPresented;

        public event Action<string, EAgentOutcome> TurnFinished;

        public event Action<bool> ActiveChanged;

        #endregion

        #region - 生命周期 -

        private void OnEnable()
        {
            Activate(profile);
        }

        private void OnDisable()
        {
            Deactivate();
        }

        /// <summary>core 的墙钟/动作超时泵：内核不是 Mono，取时刻只能靠宿主每帧推</summary>
        private void Update() => core?.Tick();

        #endregion

        #region - 公开接口 -

        public void Activate(AgentProfile_SO newProfile)
        {
            Deactivate();
            profile = newProfile;

            if (profile == null)
            {
                Log.Error(nameof(AgentHost), "AgentProfile_SO 为空，无法创建 AgentCore", this);
                return;
            }

            LLMRuntimeSettings.Install();
            outputGeneration++;

            // 成员工具/动作来自本物体层级，per-agent 私有；agent 不纳入全局静态 [AgentTool]
            toolSet = new AgentToolSet();
            toolSet.Collect(gameObject);

            core = new AgentCore(profile, instanceId, this,
                output: new AgentOutputRelay(this, outputGeneration),
                runner: new ReflectionActionRunner(),
                toolGate: this,
                toolSet: toolSet);

            if (profile is NpcAgentProfile_SO npcProfile)
            {
                affinityController = GetComponentInChildren<NpcAffinityController>(true);
                if (affinityController is null)
                {
                    Log.Warning(nameof(AgentHost),
                        "NpcAgentProfile_SO 未配 NpcAffinityController：对话可运行，但不会注入或持久化好感", this);
                }
                else
                {
                    affinityController.Initialize(core.SessionId, npcProfile.InitialAffinity);
                }
            }
            NotifyActiveChanged(true);
        }

        public void Deactivate()
        {
            if (core == null) return;

            outputGeneration++;
            var activeCore = core;
            core = null;
            toolSet = null;
            affinityController = null;
            activeCore.Dispose();
            NotifyActiveChanged(false);
        }

        public void Trigger(string input)
        {
            if (core == null || string.IsNullOrEmpty(input)) return;

            core.Trigger(input);
        }

        public void Notify(string worldEvent)
        {
            if (core == null || string.IsNullOrEmpty(worldEvent)) return;

            core.Notify(worldEvent);
        }

        public void SetCoreSnapshot(string snapshot)
        {
            coreSnapshot = snapshot;
        }

        /// <summary>覆盖式判定：未收录 → 可用；收录 → 该条 Enabled</summary>
        public bool IsToolEnabled(string toolId)
        {
            if (string.IsNullOrEmpty(toolId)) return false;

            for (int i = 0; i < toolToggles.Count; i++)
            {
                var entry = toolToggles[i];
                if (entry != null && entry.ToolId == toolId)
                    return entry.Enabled;
            }

            return true;
        }

        /// <summary>运行时改开关；未收录时按需追加条目，下一轮起生效</summary>
        public void SetToolEnabled(string toolId, bool enabled)
        {
            if (string.IsNullOrEmpty(toolId)) return;

            for (int i = 0; i < toolToggles.Count; i++)
            {
                var entry = toolToggles[i];
                if (entry == null || entry.ToolId != toolId) continue;

                entry.Enabled = enabled;
                return;
            }

            toolToggles.Add(new AgentToolToggle { ToolId = toolId, Enabled = enabled });
        }

        /// <summary>
        /// Inspector 同步入口：保留既有条目的 Enabled，新增项默认可用；
        /// removeMissing 时丢弃已不存在的工具，否则把保留的旧条目一并带回
        /// </summary>
        public void MergeToolToggles(List<AgentToolToggle> candidates, bool removeMissing)
        {
            if (candidates == null) return;

            var merged = new List<AgentToolToggle>(candidates.Count);

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate == null || string.IsNullOrEmpty(candidate.ToolId)) continue;

                bool enabled = true;
                for (int j = 0; j < toolToggles.Count; j++)
                {
                    var existing = toolToggles[j];
                    if (existing == null || existing.ToolId != candidate.ToolId) continue;

                    enabled = existing.Enabled;
                    break;
                }

                merged.Add(new AgentToolToggle
                {
                    ToolId = candidate.ToolId,
                    Source = candidate.Source,
                    Enabled = enabled
                });
            }

            if (!removeMissing)
            {
                for (int i = 0; i < toolToggles.Count; i++)
                {
                    var existing = toolToggles[i];
                    if (existing == null || string.IsNullOrEmpty(existing.ToolId)) continue;
                    if (ContainsToggle(merged, existing.ToolId)) continue;
                    merged.Add(existing);
                }
            }

            toolToggles.Clear();
            toolToggles.AddRange(merged);
        }

        #endregion

        #region - 接口实现 -

        string IWorldContextProvider.GetCoreSnapshot()
        {
            if (affinityController is null) return coreSnapshot;

            var affinity = affinityController.RenderInjection();
            return string.IsNullOrEmpty(coreSnapshot)
                ? affinity
                : ZString.Concat(affinity, "\n", coreSnapshot);
        }

        #endregion

        #region - 私有方法 -

        private static bool ContainsToggle(List<AgentToolToggle> list, string toolId)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].ToolId == toolId) return true;
            return false;
        }

        private void ForwardToken(uint generation, string delta)
        {
            if (generation != outputGeneration || core == null) return;

            try
            {
                TokenReceived?.Invoke(delta);
            }
            catch (Exception ex)
            {
                Log.Error(nameof(AgentHost),
                    ZString.Format("TokenReceived 订阅者异常: {0}", ex.Message), this);
            }
        }

        private void ForwardSay(uint generation, string actionId, string say)
        {
            if (generation != outputGeneration || core == null) return;

            try
            {
                SayPresented?.Invoke(actionId, say);
            }
            catch (Exception ex)
            {
                Log.Error(nameof(AgentHost),
                    ZString.Format("SayPresented 订阅者异常: {0}", ex.Message), this);
            }
        }

        private void ForwardTurnFinished(uint generation, string answer, EAgentOutcome outcome)
        {
            if (generation != outputGeneration || core == null) return;

            try
            {
                TurnFinished?.Invoke(answer, outcome);
            }
            catch (Exception ex)
            {
                Log.Error(nameof(AgentHost),
                    ZString.Format("TurnFinished 订阅者异常: {0}", ex.Message), this);
            }
        }

        private void NotifyActiveChanged(bool active)
        {
            try
            {
                ActiveChanged?.Invoke(active);
            }
            catch (Exception ex)
            {
                Log.Error(nameof(AgentHost),
                    ZString.Format("ActiveChanged 订阅者异常: {0}", ex.Message), this);
            }
        }

        private sealed class AgentOutputRelay : IAgentOutput
        {
            private readonly AgentHost host;
            private readonly uint generation;

            public AgentOutputRelay(AgentHost host, uint generation)
            {
                this.host = host;
                this.generation = generation;
            }

            public void OnToken(string delta)
            {
                host.ForwardToken(generation, delta);
            }

            public void OnSay(string actionId, string say)
            {
                host.ForwardSay(generation, actionId, say);
            }

            public void OnTurnFinished(string answer, EAgentOutcome outcome)
            {
                host.ForwardTurnFinished(generation, answer, outcome);
            }
        }

        #endregion
    }
}
