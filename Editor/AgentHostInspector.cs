/*
┌────────────────────────────┐
│　Description: AgentHost 自定义 Inspector
│　Remark: 暴露每个实例的工具门控开关、
│　　　　　 “扫描并同步”与成员组件快速添加、
│　　　　　 只读的持久化记忆（事实槽）展示
│　ClassName: AgentHostInspector
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime;
using LLM.Runtime.Agent;
using UnityEditor;
using UnityEngine;

namespace LLM.Editor
{
    /// <summary>AgentHost 的原生 Inspector，不依赖 Odin/Sirenix</summary>
    [CustomEditor(typeof(AgentHost))]
    public sealed class AgentHostInspector : UnityEditor.Editor
    {
        private const float k_memoryScrollHeight = 160f;

        private SerializedProperty profile;
        private SerializedProperty instanceId;
        private SerializedProperty coreSnapshot;
        private SerializedProperty toolToggles;

        private static readonly GUIContent k_profileLabel = new("Agent 人设");
        private static readonly GUIContent k_instanceIdLabel = new("实例标识（存档键）");
        private static readonly GUIContent k_coreSnapshotLabel = new("核心快照");
        private static readonly GUIContent k_quickAddLabel = new("快速添加成员工具/动作组件");

        private bool memoryFoldout = true;
        private string memorySessionId;
        private string memoryHint;
        private List<AgentFact> memoryFacts = new();
        private Vector2 memoryScroll;

        private void OnEnable()
        {
            profile = serializedObject.FindProperty("profile");
            instanceId = serializedObject.FindProperty("instanceId");
            coreSnapshot = serializedObject.FindProperty("coreSnapshot");
            toolToggles = serializedObject.FindProperty("toolToggles");

            ReloadMemory();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(profile, k_profileLabel);
            EditorGUILayout.PropertyField(instanceId, k_instanceIdLabel);
            EditorGUILayout.PropertyField(coreSnapshot, k_coreSnapshotLabel);

            DrawToolToggles();
            DrawMemory();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawToolToggles()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("LLM 可用工具", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "开 = 该工具进入本 Agent 的 request.Tools 且可执行；关 = 模型不可见、执行期被拒。\n覆盖式：未收录的工具默认可用，新增工具无需回头同步也不会误伤。\n动作来自全局 static [AgentAction] 与本物体层级的实例 [AgentAction]；工具来自全局 static [AgentTool] 与本物体层级的实例 [AgentTool]。\n本体工具/动作要靠组件承载：用下面的快速添加把声明了实例方法的 MonoBehaviour 挂到本物体。",
                MessageType.None);

            if (GUILayout.Button("扫描并同步工具列表"))
                SyncToolToggles();

            DrawQuickAdd();

            if (toolToggles == null || toolToggles.arraySize == 0)
            {
                EditorGUILayout.LabelField("（列表为空，点上方按钮扫描）", EditorStyles.miniLabel);
                return;
            }

            DrawGroup("动作（[AgentAction]）", EAgentToolSource.Action, EAgentToolSource.SelfAction);
            DrawGroup("公共工具（static [AgentTool]）", EAgentToolSource.PublicTool);
            DrawGroup("本体工具（实例 [AgentTool]）", EAgentToolSource.SelfTool);
        }

        private void DrawGroup(string title, params EAgentToolSource[] sources)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);

            bool any = false;
            for (int i = 0; i < toolToggles.arraySize; i++)
            {
                SerializedProperty entry = toolToggles.GetArrayElementAtIndex(i);
                SerializedProperty sourceProp = entry.FindPropertyRelative("Source");
                if (sourceProp == null || !ContainsSource(sources, sourceProp.intValue)) continue;

                any = true;
                SerializedProperty idProp = entry.FindPropertyRelative("ToolId");
                SerializedProperty enabledProp = entry.FindPropertyRelative("Enabled");
                string label = IsSelfSource(sourceProp.intValue)
                    ? ZString.Concat(idProp.stringValue, "（本体）")
                    : idProp.stringValue;

                enabledProp.boolValue = EditorGUILayout.ToggleLeft(label, enabledProp.boolValue);
            }

            if (!any)
                EditorGUILayout.LabelField("（无）", EditorStyles.miniLabel);
        }

        private static bool ContainsSource(EAgentToolSource[] sources, int value)
        {
            for (int i = 0; i < sources.Length; i++)
                if ((int)sources[i] == value) return true;
            return false;
        }

        private static bool IsSelfSource(int value)
        {
            return value == (int)EAgentToolSource.SelfTool || value == (int)EAgentToolSource.SelfAction;
        }

        private void DrawMemory()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("持久化记忆", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "读取 Prefs 中该 Agent 存档键下的事实槽（与运行期 PrefsAgentStateStore 同一路径）。\n只对编辑器 Play 模式写入的存档有效；独立构建写入 persistentDataPath 的数据不在此列。",
                MessageType.None);

            if (GUILayout.Button("刷新"))
                ReloadMemory();

            if (!string.IsNullOrEmpty(memoryHint))
            {
                EditorGUILayout.HelpBox(memoryHint, MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField("存档键", memorySessionId, EditorStyles.miniLabel);

            memoryFoldout = EditorGUILayout.Foldout(memoryFoldout,
                ZString.Format("记忆（事实槽）· {0} 条", memoryFacts.Count), true);

            if (!memoryFoldout) return;

            if (memoryFacts.Count == 0)
            {
                EditorGUILayout.LabelField("（无）", EditorStyles.miniLabel);
                return;
            }

            memoryScroll = EditorGUILayout.BeginScrollView(memoryScroll,
                GUILayout.MaxHeight(k_memoryScrollHeight));
            for (int i = 0; i < memoryFacts.Count; i++)
                EditorGUILayout.SelectableLabel(FormatFact(memoryFacts[i]), EditorStyles.label);
            EditorGUILayout.EndScrollView();
        }

        private void ReloadMemory()
        {
            memorySessionId = null;
            memoryHint = null;
            memoryFacts = new List<AgentFact>();

            if (!AgentStateReader.IsPrefsStoreEnabled())
            {
                memoryHint = "未启用持久化（LLMGlobalConfig 的 StateStore = None），无记忆可显示。";
                return;
            }

            // 运行中直接读内核实际使用的键，避免与写入侧不一致
            string sessionId = target is AgentHost host && host.Core is not null ? host.Core.SessionId : null;

            if (string.IsNullOrEmpty(sessionId))
            {
                var profileAsset = profile?.objectReferenceValue as AgentProfile_SO;
                if (profileAsset is null || string.IsNullOrEmpty(profileAsset.ProfileKey))
                {
                    memoryHint = "人设未设置 ProfileKey（选中资产或运行时校验后会自动生成）。";
                    return;
                }

                if (string.IsNullOrEmpty(instanceId?.stringValue))
                {
                    memoryHint = "未填实例标识，该 agent 为临时实例，记忆不落盘。";
                    return;
                }

                sessionId = AgentStateReader.ComposeSessionId(profileAsset, instanceId.stringValue);
            }

            memorySessionId = sessionId;
            memoryFacts = AgentStateReader.ReadFacts(sessionId);
        }

        private static string FormatFact(AgentFact fact)
        {
            string time = DateTimeOffset.FromUnixTimeSeconds(fact.Timestamp).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");
            return ZString.Format("- {0}：{1}    ({2})", fact.Key, fact.Value, time);
        }

        /// <summary>
        /// Play 模式与 Prefab 资产态不画：前者挂上去的组件既不落盘也进不了已冻结的声明表，
        /// 后者在预览场景里走 Undo 会报错。
        /// </summary>
        private void DrawQuickAdd()
        {
            if (target is not AgentHost host) return;
            if (EditorApplication.isPlaying || PrefabUtility.IsPartOfPrefabAsset(host)) return;

            Rect rect = GUILayoutUtility.GetRect(k_quickAddLabel, EditorStyles.popup);
            if (GUI.Button(rect, k_quickAddLabel, EditorStyles.popup))
                ShowQuickAddMenu(host, rect);
        }

        private void ShowQuickAddMenu(AgentHost host, Rect rect)
        {
            var providers = AgentToolScanner.CollectMemberProviders();
            var attached = CollectAttachedMemberIds(host);
            var menu = new GenericMenu();
            int listed = 0;

            for (int i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                if (attached.Contains(provider.Id)) continue;

                // '/' 在 GenericMenu 里是子菜单分隔符，除分层用途外的文案都不能带
                menu.AddItem(new GUIContent(ZString.Format("{0}（{1}）",
                    provider.Id, provider.Owner.Name)), false, OnQuickAddComponent, provider.Owner);
                listed++;
            }

            if (listed == 0)
                menu.AddDisabledItem(new GUIContent(providers.Count == 0
                    ? "（工程内没有可挂载的成员工具/动作组件）"
                    : "（成员工具/动作已全部挂载）"));

            menu.DropDown(rect);
        }

        /// <summary>本物体层级已提供的成员工具/动作名，与运行期 AgentToolSet.Collect 同一套扫描口径</summary>
        private static HashSet<string> CollectAttachedMemberIds(AgentHost host)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);

            var tools = AgentToolRegistry.ScanHierarchyTools(host.gameObject);
            for (int i = 0; i < tools.Count; i++)
                ids.Add(tools[i].Name);

            var actions = AgentActionRegistry.ScanHierarchyTools(host.gameObject);
            for (int i = 0; i < actions.Count; i++)
                ids.Add(actions[i].Id);

            return ids;
        }

        private void OnQuickAddComponent(object userData)
        {
            if (userData is not Type type || target is not AgentHost host) return;

            Undo.AddComponent(host.gameObject, type);
            SyncToolToggles();
            Repaint();
        }

        private void SyncToolToggles()
        {
            if (target is not AgentHost host) return;

            AgentToolScanner.RegisterProjectAssemblies();
            List<AgentToolToggle> candidates = CollectCandidates(host);

            Undo.RecordObject(host, "Sync Agent Tool Toggles");
            host.MergeToolToggles(candidates, removeMissing: true);
            MarkDirty(host);
            serializedObject.Update();
        }

        private static List<AgentToolToggle> CollectCandidates(AgentHost host)
        {
            var result = new List<AgentToolToggle>();
            var seen = new HashSet<string>();

            // 全局动作（内置记忆三件套 + 游戏侧注册的 static [AgentAction]）
            var globalActions = AgentActionRegistry.Snapshot();
            for (int i = 0; i < globalActions.Count; i++)
                AddCandidate(result, seen, globalActions[i].Id, EAgentToolSource.Action);

            // 全局静态工具
            var publicTools = AgentToolRegistry.Snapshot();
            for (int i = 0; i < publicTools.Count; i++)
                AddCandidate(result, seen, publicTools[i].Name, EAgentToolSource.PublicTool);

            GameObject root = host != null ? host.gameObject : null;

            // 本体动作/工具：挂在本物体层级上的实例方法
            var selfActions = AgentActionRegistry.ScanHierarchyTools(root);
            for (int i = 0; i < selfActions.Count; i++)
                AddCandidate(result, seen, selfActions[i].Id, EAgentToolSource.SelfAction);

            var selfTools = AgentToolRegistry.ScanHierarchyTools(root);
            for (int i = 0; i < selfTools.Count; i++)
                AddCandidate(result, seen, selfTools[i].Name, EAgentToolSource.SelfTool);

            return result;
        }

        private static void AddCandidate(List<AgentToolToggle> result, HashSet<string> seen,
            string toolId, EAgentToolSource source)
        {
            if (string.IsNullOrEmpty(toolId) || !seen.Add(toolId)) return;

            result.Add(new AgentToolToggle
            {
                ToolId = toolId,
                Source = source,
                Enabled = true
            });
        }

        /// <summary>场景对象走 SetDirty；Prefab 实例必须 Record，否则打包后读不到覆盖值</summary>
        private static void MarkDirty(AgentHost host)
        {
            EditorUtility.SetDirty(host);
            if (PrefabUtility.IsPartOfPrefabInstance(host))
                PrefabUtility.RecordPrefabInstancePropertyModifications(host);
        }
    }
}
