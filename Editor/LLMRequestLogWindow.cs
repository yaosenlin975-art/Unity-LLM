/*
┌────────────────────────────┐
│　Description: LLM 请求日志窗口
│　Remark: 订阅 LLMDispatcher.OnRequest
│　　　　　 Completed，显示用量与工具调用
│　ClassName: LLMRequestLogWindow
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime;
using UnityEditor;
using UnityEngine;

namespace LLM.Editor
{
    public class LLMRequestLogWindow : EditorWindow
    {
        private const int k_maxEntries = 200;

        private Vector2 scrollPosition;
        private readonly List<LogEntry> logEntries = new();
        private bool subscribed;

        [MenuItem("Lin/LLM/请求日志")]
        public static void ShowWindow()
        {
            GetWindow<LLMRequestLogWindow>("LLM 请求日志");
        }

        private void OnEnable()
        {
            if (subscribed) return;
            LLMDispatcher.OnRequestCompleted += HandleLog;
            subscribed = true;
        }

        private void OnDisable()
        {
            if (!subscribed) return;
            LLMDispatcher.OnRequestCompleted -= HandleLog;
            subscribed = false;
        }

        private void HandleLog(LLMRequestLog log)
        {
            logEntries.Add(new LogEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                SessionId = log.SessionId,
                Provider = log.Provider,
                LatencyMs = log.LatencyMs,
                PromptTokens = log.PromptTokens,
                CompletionTokens = log.CompletionTokens,
                CacheHitTokens = log.CacheHitTokens,
                ContentPreview = log.ContentPreview,
                ToolCallPreview = log.HasToolCall
                    ? BuildToolPreview(log.ToolNames, log.ToolSummary)
                    : "最近响应没有 tool_calls",
                IsError = log.IsError,
                ErrorMessage = log.ErrorMessage
            });

            if (logEntries.Count > k_maxEntries)
                logEntries.RemoveAt(0);

            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("清空")) logEntries.Clear();
            EditorGUILayout.LabelField(ZString.Format("条目: {0}", logEntries.Count));
            EditorGUILayout.EndHorizontal();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            for (int i = logEntries.Count - 1; i >= 0; i--)
            {
                var entry = logEntries[i];
                var style = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(10, 10, 6, 6)
                };
                var statusStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    normal = { textColor = GetStatusColor(entry) }
                };
                var contentStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                {
                    wordWrap = true
                };

                EditorGUILayout.BeginVertical(style);
                EditorGUILayout.LabelField(
                    ZString.Format("[{0}] {1}", entry.Timestamp, GetStatusLabel(entry)), statusStyle);
                EditorGUILayout.LabelField(
                    ZString.Format("会话: {0} | 供应商 {1}", entry.SessionId, entry.Provider),
                    EditorStyles.miniLabel);
                if (entry.CacheHitTokens > 0)
                {
                    EditorGUILayout.LabelField(
                        ZString.Format("延迟: {0:F0}ms | Tokens: {1}+{2}（缓存命中 {3}）",
                            entry.LatencyMs, entry.PromptTokens, entry.CompletionTokens, entry.CacheHitTokens),
                        EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        ZString.Format("延迟: {0:F0}ms | Tokens: {1}+{2}",
                            entry.LatencyMs, entry.PromptTokens, entry.CompletionTokens),
                        EditorStyles.miniLabel);
                }

                if (!string.IsNullOrEmpty(entry.ContentPreview))
                {
                    EditorGUILayout.LabelField("Content", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(Truncate(entry.ContentPreview, 160), contentStyle);
                }

                if (!string.IsNullOrEmpty(entry.ToolCallPreview))
                {
                    EditorGUILayout.LabelField("ToolCall", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(entry.ToolCallPreview, contentStyle);
                }

                if (entry.IsError)
                {
                    EditorGUILayout.LabelField("错误", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(Truncate(entry.ErrorMessage, 160), contentStyle);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : ZString.Concat(text[..max], "...");
        }

        private static string BuildToolPreview(string toolNames, string toolSummary)
        {
            if (string.IsNullOrWhiteSpace(toolNames)) return "模型返回了 tool_calls";
            if (string.IsNullOrWhiteSpace(toolSummary)) return ZString.Concat("工具: ", toolNames);

            return ZString.Format("工具: {0}\n摘要: {1}", toolNames, Truncate(toolSummary, 220));
        }

        private static string GetStatusLabel(LogEntry entry)
        {
            if (entry.IsError) return "请求失败";
            return entry.ToolCallPreview == "模型返回了 tool_calls" ? "响应成功 · 有工具调用" : "响应成功";
        }

        private static Color GetStatusColor(LogEntry entry)
        {
            if (entry.IsError) return new Color(1f, 0.35f, 0.35f);
            return entry.ToolCallPreview == "模型返回了 tool_calls"
                ? new Color(0.4f, 0.85f, 0.7f)
                : new Color(0.7f, 0.85f, 1f);
        }
    }

    public class LogEntry
    {
        public string Timestamp;
        public string SessionId;
        public string Provider;
        public float LatencyMs;
        public int PromptTokens;
        public int CompletionTokens;
        public int CacheHitTokens;
        public string ContentPreview;
        public string ToolCallPreview;
        public bool IsError;
        public string ErrorMessage;
    }
}
