/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Agent 对话沙盒窗口            │
 * │ Remark      : AgentCore 驱动 + AgentTrace    │
 * │               订阅 + 输入框起轮 + 输出区；   │
 * │               时钟泵只挂在 AgentHost 上（非   │
 * │               Play 与沙盒核心都不 tick），    │
 * │               起轮只由输入 Trigger/Notify 驱动 │
 * │ ClassName   : AgentSandboxWindow            │
 * └────────────────────────────────────────────┘
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
    /// <summary>菜单 Lin/LLM/Agent 沙盒：给 AgentProfile_SO 一个无宿主的对话调试入口（设计见 agent-kernel-design.md C 步）</summary>
    public class AgentSandboxWindow : EditorWindow, IAgentOutput, IWorldContextProvider
    {
        private const int k_maxLines = 200;
        private const string k_inputControl = "sandboxInput";

        private static readonly Color k_colorUser = new(0.65f, 0.8f, 1f);
        private static readonly Color k_colorAgent = new(0.55f, 0.9f, 0.7f);
        private static readonly Color k_colorOutcome = new(1f, 0.75f, 0.4f);

        #region - 字段 -

        private Vector2 chatScroll;
        private Vector2 traceScroll;

        private AgentProfile_SO profileAsset;
        private AgentProfile_SO tempProfile;
        private AgentCore agent;

        private string worldSnapshot = "";
        private string inputText = "";
        private bool sendAsEvent;

        /// <summary>流式气泡累积器。内核同款做法：长生命累积态用 StringBuilder，拼接表达式才走 ZString</summary>
        private readonly System.Text.StringBuilder streamingText = new();
        private bool turnOpen;

        private readonly List<ChatLine> chatLines = new();
        private readonly List<string> traceLines = new();
        private bool traceSubscribed;

        #endregion

        #region - 菜单 -

        [MenuItem("Lin/LLM/Agent 沙盒")]
        public static void ShowWindow()
        {
            GetWindow<AgentSandboxWindow>("Agent 沙盒");
        }

        #endregion

        #region - 生命周期 -

        private void OnEnable()
        {
            if (traceSubscribed) return;
            AgentTrace.OnEvent += HandleTraceEvent;
            traceSubscribed = true;
        }

        private void OnDisable()
        {
            if (traceSubscribed)
            {
                AgentTrace.OnEvent -= HandleTraceEvent;
                traceSubscribed = false;
            }

            if (agent != null)
            {
                agent.Dispose();
                agent = null;
            }

            // HideAndDontSave 的 SO 不随场景卸载，不手动销毁会一直驻留到编辑器重启
            if (tempProfile != null)
            {
                UnityEngine.Object.DestroyImmediate(tempProfile);
                tempProfile = null;
            }
        }

        #endregion

        #region - 私有方法：GUI -

        private void OnGUI()
        {
            DrawAgentHeader();
            EditorGUILayout.Space(4);
            DrawChatArea();
            EditorGUILayout.Space(4);
            DrawInputRow();
            EditorGUILayout.Space(4);
            DrawTraceArea();
        }

        private void DrawAgentHeader()
        {
            EditorGUILayout.LabelField("人设与 Agent", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            var picked = (AgentProfile_SO)EditorGUILayout.ObjectField(
                "Profile 资产", profileAsset, typeof(AgentProfile_SO), false);
            if (picked != profileAsset)
            {
                profileAsset = picked;
                DestroyAgent();
            }

            if (GUILayout.Button("创建临时人设", GUILayout.Width(96)))
            {
                CreateTempProfile();
            }

            if (GUILayout.Button("销毁重建", GUILayout.Width(72)))
            {
                DestroyAgent();
                EnsureAgent();
            }

            EditorGUILayout.EndHorizontal();

            var activeProfile = profileAsset != null ? profileAsset : tempProfile;
            if (activeProfile == null)
            {
                EditorGUILayout.HelpBox("拖入 AgentProfile_SO 资产，或点「创建临时人设」。", MessageType.Info);
                return;
            }

            if (activeProfile != tempProfile && tempProfile != null)
            {
                DestroyImmediate(tempProfile);
                tempProfile = null;
                DestroyAgent();
                activeProfile = profileAsset;
            }

            EditorGUILayout.LabelField(GetAgentStatus(activeProfile), EditorStyles.miniLabel);

            worldSnapshot = EditorGUILayout.TextField("核心快照（每轮注入）", worldSnapshot);
        }

        private void DrawChatArea()
        {
            EditorGUILayout.LabelField("对话", EditorStyles.boldLabel);

            // ExpandHeight 吃掉窗口剩余高度：输入行与日志栏自然锚定底部
            chatScroll = EditorGUILayout.BeginScrollView(chatScroll,
                GUILayout.MinHeight(120f), GUILayout.ExpandHeight(true));

            for (int i = 0; i < chatLines.Count; i++)
            {
                DrawChatLine(chatLines[i]);
            }

            if (turnOpen)
            {
                DrawChatLine(new ChatLine("Agent（流式中）", streamingText.ToString()));
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawChatLine(ChatLine line)
        {
            var tagColor = line.Role == "你" ? k_colorUser
                : line.Role.StartsWith("Agent") ? k_colorAgent
                : k_colorOutcome;

            var style = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 6, 6) };
            var tagStyle = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = tagColor } };
            var contentStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { wordWrap = true };

            EditorGUILayout.BeginVertical(style);
            EditorGUILayout.LabelField(line.Role, tagStyle);
            if (!string.IsNullOrEmpty(line.Text))
            {
                EditorGUILayout.LabelField(line.Text, contentStyle);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void DrawInputRow()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.SetNextControlName(k_inputControl);
            inputText = EditorGUILayout.TextField(inputText);

            HandleEnterSubmit();

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(inputText)))
            {
                if (GUILayout.Button(sendAsEvent ? "发事件" : "发送", GUILayout.Width(72)))
                {
                    SendInput();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            sendAsEvent = EditorGUILayout.ToggleLeft("作为世界事件发送（Notify，内核加【事件】前缀）", sendAsEvent, GUILayout.Width(280f));
            if (GUILayout.Button("清空对话", GUILayout.Width(72)))
            {
                chatLines.Clear();
                streamingText.Clear();
                turnOpen = false;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>输入框内回车 = 发送。焦点不在输入框（如改核心快照）时不劫持回车</summary>
        private void HandleEnterSubmit()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;
            if (e.keyCode != KeyCode.KeypadEnter && e.keyCode != KeyCode.Return) return;
            if (GUI.GetNameOfFocusedControl() != k_inputControl) return;
            if (string.IsNullOrEmpty(inputText)) return;

            e.Use();
            SendInput();
        }

        private void DrawTraceArea()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("AgentTrace 轨迹", EditorStyles.boldLabel);
            if (GUILayout.Button("清空轨迹", GUILayout.Width(72)))
            {
                traceLines.Clear();
            }
            EditorGUILayout.EndHorizontal();

            // 固定限高，钉在窗口底部；对话区弹性展开后剩余空间不归它
            traceScroll = EditorGUILayout.BeginScrollView(traceScroll,
                GUILayout.MinHeight(100f), GUILayout.MaxHeight(180f));
            for (int i = traceLines.Count - 1; i >= 0; i--)
            {
                EditorGUILayout.LabelField(traceLines[i], EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        #endregion

        #region - 私有方法：Agent 驱动 -

        private void EnsureAgent()
        {
            if (agent != null) return;

            EnsureRuntimeInstalled();

            var profile = profileAsset != null ? profileAsset : tempProfile;
            if (profile == null) return;

            agent = new AgentCore(profile, "sandbox", this, this, new ReflectionActionRunner());
            AppendTrace("沙盒已创建 " + agent.SessionId);
        }

        /// <summary>
        /// 全工程没有引导调用装配（Provider 注册是宿主初始化的职责），
        /// 沙盒作为零配置调试入口在这里兜底装一次，否则开箱即 Failed。
        /// </summary>
        private void EnsureRuntimeInstalled()
        {
            var hadProvider = LLMDispatcher.GetInstance().GetProvider() is not null;

            LLMRuntimeSettings.Install();

            if (hadProvider || LLMDispatcher.GetInstance().GetProvider() is null) return;

            AppendTrace(ZString.Concat("已通过 LLMGlobalConfig 装好 Provider 与状态存储，",
                "此后 Play 模式全局可用"));
        }

        private void DestroyAgent()
        {
            if (agent == null) return;

            agent.Dispose();
            agent = null;
            turnOpen = false;
            streamingText.Clear();
            AppendTrace("沙盒 agent 已销毁");
        }

        private string GetAgentStatus(AgentProfile_SO activeProfile)
        {
            if (agent == null)
            {
                return ZString.Format("Agent 未创建（ProfileKey: {0}）", activeProfile.ProfileKey);
            }

            return ZString.Format("{0} | 轮次 {1} | {2}",
                agent.SessionId, agent.TurnGeneration, agent.IsBusy ? "轮次进行中" : "空闲");
        }

        private void SendInput()
        {
            EnsureAgent();
            if (agent == null)
            {
                AppendChat("系统", "没有可用 Profile：先拖入资产或创建临时人设。");
                return;
            }

            var text = inputText;
            inputText = "";
            AppendChat("你", sendAsEvent ? ZString.Concat("【事件】", text) : text);

            // 先置流式状态再触发：EditMode 下轮可能同步收敛（如立即失败），
            // Trigger 返回时 OnTurnFinished 已经跑完，事后置 true 会留下幽灵「流式中」气泡
            turnOpen = true;
            streamingText.Clear();

            if (sendAsEvent)
            {
                agent.Notify(text);
            }
            else
            {
                agent.Trigger(text);
            }

            Repaint();
        }

        private void CreateTempProfile()
        {
            // 旧的临时人设先销毁，反复点击不累积孤儿 SO
            if (tempProfile != null)
            {
                DestroyImmediate(tempProfile);
                tempProfile = null;
            }

            var created = ScriptableObject.CreateInstance<AgentProfile_SO>();
            created.name = "SandboxTemp";
            created.hideFlags = HideFlags.HideAndDontSave;
            created.ProfileKey = "sandbox";
            created.PersonaPrompt = "你是编辑器沙盒里的对话助手，回答保持简短。";
            created.FallbackLines = new[] { "（沙盒）这一轮没有拿到回复。" };
            tempProfile = created;
            DestroyAgent();
        }

        private void AppendChat(string role, string text)
        {
            chatLines.Add(new ChatLine(role, text));
            while (chatLines.Count > k_maxLines)
            {
                chatLines.RemoveAt(0);
            }
            ScrollChatToBottom();
            Repaint();
        }

        /// <summary>新内容到达时跟随底部（调试工具不做复杂的手动滚动保护）</summary>
        private void ScrollChatToBottom()
        {
            chatScroll = new Vector2(chatScroll.x, float.MaxValue);
        }

        private static void Trim(List<string> lines)
        {
            while (lines.Count > k_maxLines)
            {
                lines.RemoveAt(0);
            }
        }

        private void AppendTrace(string line)
        {
            traceLines.Add(ZString.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, line));
            Trim(traceLines);
            Repaint();
        }

        #endregion

        #region - 接口实现：IAgentOutput -

        void IAgentOutput.OnToken(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;

            streamingText.Append(delta);
            ScrollChatToBottom();
            Repaint();
        }

        void IAgentOutput.OnSay(string actionId, string say)
        {
            if (string.IsNullOrEmpty(say)) return;

            streamingText.Append(say);
            ScrollChatToBottom();
            Repaint();
        }

        void IAgentOutput.OnTurnFinished(string answer, EAgentOutcome outcome)
        {
            // 优先用内核给的定稿文本；空则保留流式半截，至少不让气泡凭空消失
            var final = !string.IsNullOrEmpty(answer) ? answer : streamingText.ToString();
            var role = ZString.Format("Agent（{0}）", outcome);
            chatLines.Add(new ChatLine(role, final));
            while (chatLines.Count > k_maxLines)
            {
                chatLines.RemoveAt(0);
            }
            ScrollChatToBottom();

            turnOpen = false;
            streamingText.Clear();
            Repaint();
        }

        #endregion

        #region - 接口实现：IWorldContextProvider -

        string IWorldContextProvider.GetCoreSnapshot()
        {
            return worldSnapshot;
        }

        #endregion

        #region - 私有方法：事件 -

        private void HandleTraceEvent(AgentTraceEvent e)
        {
            AppendTrace(ZString.Format("R{0} {1} {2} ({3:F1}s)",
                e.Round, e.Kind, e.Detail, e.AtSeconds));
        }

        #endregion

        private readonly struct ChatLine
        {
            public readonly string Role;
            public readonly string Text;

            public ChatLine(string role, string text)
            {
                Role = role;
                Text = text;
            }
        }
    }
}
