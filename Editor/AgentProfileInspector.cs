/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 人设资产的中文 Inspector │
 * │ Remark      : 原生没有字段级 Label 特性，只 │
 * │               有 InspectorName 且它只管枚举  │
 * │ ClassName   : AgentProfileInspector          │
 * └────────────────────────────────────────────┘
 */

using System.Collections.Generic;
using LLM.Runtime.Agent;
using UnityEditor;
using UnityEngine;

namespace LLM.Editor
{
    /// <summary>
    /// 策划/美术看的是 Inspector，不是 C# 标识符：字段名默认换成中文再画，
    /// 面板右上角一个开关可切回原名（对照代码/grep 时用）。
    /// editorForChildClasses = true，NpcAgentProfile_SO 这些派生资产共用一套。
    /// 只改显示，不改序列化：字段仍按英文名存储，代码与 diff 不受影响。
    /// 注意：枚举项的中文走 [InspectorName]，是引擎级行为，不受这个开关影响。
    /// </summary>
    [CustomEditor(typeof(AgentProfile_SO), true)]
    public sealed class AgentProfileInspector : UnityEditor.Editor
    {
        /// <summary>属性名 → 中文标签。没收录的属性照原名显示，漏加不会画不出来</summary>
        private static readonly Dictionary<string, string> k_labels = new Dictionary<string, string>
        {
            { "ProfileKey", "档案键（存档用）" },
            { "PersonaPrompt", "人设与规则" },
            { "CompressionConfig", "上下文压缩配置" },
            { "Temperature", "采样温度" },
            { "MaxTokens", "单次回复最大 token" },
            { "QueryableHint", "可查询提示" },
            { "TurnDeadlineSeconds", "单轮墙钟额度（秒）" },
            { "PerNameToolCallLimit", "同工具名本轮调用上限" },
            { "GlobalRepeatLimit", "同参重复全局阈值" },
            { "ActionTimeoutSeconds", "动作超时（秒）" },
            { "LongActionTimeoutSeconds", "长动作超时（秒）" },
            { "LockWaitSeconds", "锁排队上限（秒）" },
            { "FactSlotMaxCount", "事实槽上限" },
            { "FallbackLines", "兜底台词" },
            { "ExpectedLlmRttSeconds", "预期往返耗时（秒）" },
            { "DisplayName", "显示名称" },
            { "Portrait", "肖像" },
            { "OpeningLine", "开场白" },
            { "Identity", "身份" },
            { "Personality", "性格" },
            { "SpeechStyle", "说话方式" },
            { "GoalsAndValues", "目标与价值" },
            { "KnowledgeBoundary", "知识边界" },
            { "InitialAffinity", "初始好感度" },
            { "PerformanceProfile", "说话表现配置" }
        };

        /// <summary>面板右上角的开关：开=中文标签，关=字段原名。存 EditorPrefs，全编辑器共享一次选择</summary>
        private const string k_prefsKey = "llm.agentprofile.chineseLabels";

        // Editor 本身是 ScriptableObject：读盘只能放 OnEnable，写在字段初始化器里会被序列化期拦下
        private bool chineseLabels = true;

        private void OnEnable()
        {
            chineseLabels = EditorPrefs.GetBool(k_prefsKey, true);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.FlexibleSpace();
            bool next = GUILayout.Toggle(chineseLabels, "中文标签", EditorStyles.toolbarButton);
            EditorGUILayout.EndHorizontal();

            if (next != chineseLabels)
            {
                chineseLabels = next;
                EditorPrefs.SetBool(k_prefsKey, chineseLabels);
            }

            var prop = serializedObject.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.name == "m_Script") continue;

                k_labels.TryGetValue(prop.name, out string label);
                // 只替标签文本；[Header] 分组、[Tooltip]、[Range]、[TextArea] 由 PropertyField 自己处理，
                // 手动再画一遍 Header 就会出现两份分组标题
                EditorGUILayout.PropertyField(prop,
                    new GUIContent(chineseLabels && !string.IsNullOrEmpty(label) ? label : prop.name, prop.tooltip));
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
