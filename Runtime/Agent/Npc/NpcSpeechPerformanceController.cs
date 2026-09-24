/*
┌────────────────────────────┐
│　Description: NPC 流式台词肢体表现导演
│　Remark: 旁路监听文本，不产生工具调用
│　ClassName: NpcSpeechPerformanceController
└────────────────────────────┘
*/

using UnityEngine;

namespace LLM.Runtime.Agent.Npc
{
    [DisallowMultipleComponent]
    public sealed class NpcSpeechPerformanceController : MonoBehaviour
    {
        [SerializeField]
        [InspectorName("Agent 宿主")]
        private AgentHost agentHost;

        [SerializeField]
        [InspectorName("表现配置覆盖")]
        [Tooltip("留空时读取 NPC Profile 的说话表现配置")]
        private NpcPerformanceProfile_SO performanceProfileOverride;

        [SerializeField]
        [InspectorName("当前情绪")]
        private ENpcEmotion currentEmotion;

        private readonly GestureResolver resolver = new();
        private GestureClauseSegmenter segmenter;
        private INpcGestureDriver driver;
        private NpcPerformanceProfile_SO activeProfile;
        private bool speaking;
        private bool suppressed;
        private bool warnedDriverFailure;
        private int gestureCount;
        private float lastGestureAt = float.NegativeInfinity;
        private float gestureHoldUntil = float.NegativeInfinity;
        private GestureDefinition pendingGesture;

        public bool IsSpeaking => speaking;

        private void OnEnable()
        {
            ResolveReferences();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            EndSpeaking();
            // 组件禁用时把槽收干净，否则重新启用会停在旧手势姿势上。
            // 轮次正常结束不走这里，见 EndSpeaking 的说明。
            driver?.StopGesture(GetBlendOutSeconds());
        }

        private void Update()
        {
            if (!speaking || segmenter == null) return;

            if (segmenter.TryTakeTimedOut(Time.unscaledTime, out string clause))
                HandleClause(clause);

            if (!suppressed && pendingGesture != null && Time.unscaledTime >= gestureHoldUntil &&
                Time.unscaledTime - lastGestureAt >= activeProfile.MinimumGestureInterval)
            {
                GestureDefinition gesture = pendingGesture;
                pendingGesture = null;
                PlayGesture(gesture, Time.unscaledTime);
            }

            if (!suppressed && driver != null && !driver.IsGesturePlaying)
                PlaySpeakingLoop();
        }

        public void SetEmotion(ENpcEmotion emotion)
        {
            currentEmotion = emotion;
        }

        /// <summary>战斗、受击、死亡或剧情占用时由玩法侧抑制说话手势。</summary>
        public void SetSuppressed(bool value)
        {
            suppressed = value;
            if (suppressed)
            {
                pendingGesture = null;
                driver?.StopGesture(GetBlendOutSeconds());
            }
            else if (speaking)
                PlaySpeakingLoop();
        }

        private void ResolveReferences()
        {
            if (agentHost == null)
                agentHost = GetComponentInParent<AgentHost>();

            activeProfile = performanceProfileOverride;
            if (activeProfile == null && agentHost?.Profile is NpcAgentProfile_SO npcProfile)
                activeProfile = npcProfile.PerformanceProfile;

            driver = agentHost != null
                ? agentHost.GetComponentInChildren<INpcGestureDriver>(true)
                : GetComponentInChildren<INpcGestureDriver>(true);
            RebuildSegmenter();
        }

        private void RebuildSegmenter()
        {
            int maxCharacters = activeProfile != null
                ? activeProfile.MaxClauseCharacters
                : 20;
            float pauseSeconds = activeProfile != null
                ? activeProfile.ClausePauseSeconds
                : 0.35f;
            segmenter = new GestureClauseSegmenter(maxCharacters, pauseSeconds);
        }

        private void Subscribe()
        {
            if (agentHost == null) return;
            agentHost.TokenReceived += OnTokenReceived;
            agentHost.SayPresented += OnSayPresented;
            agentHost.TurnFinished += OnTurnFinished;
            agentHost.ActiveChanged += OnActiveChanged;
        }

        private void Unsubscribe()
        {
            if (agentHost == null) return;
            agentHost.TokenReceived -= OnTokenReceived;
            agentHost.SayPresented -= OnSayPresented;
            agentHost.TurnFinished -= OnTurnFinished;
            agentHost.ActiveChanged -= OnActiveChanged;
        }

        private void OnTokenReceived(string delta)
        {
            if (string.IsNullOrEmpty(delta) || activeProfile == null || driver == null)
                return;

            if (!speaking)
                BeginSpeaking();

            segmenter.Append(delta, Time.unscaledTime);
            while (segmenter.TryTakeClause(out string clause))
                HandleClause(clause);
        }

        private void OnSayPresented(string actionId, string say)
        {
            OnTokenReceived(say);
        }

        private void OnTurnFinished(string answer, EAgentOutcome outcome)
        {
            EndSpeaking();
        }

        private void OnActiveChanged(bool active)
        {
            if (!active)
            {
                EndSpeaking();
                return;
            }

            ResolveReferences();
        }

        private void BeginSpeaking()
        {
            speaking = true;
            gestureCount = 0;
            lastGestureAt = float.NegativeInfinity;
            gestureHoldUntil = float.NegativeInfinity;
            pendingGesture = null;
            warnedDriverFailure = false;
            segmenter.Reset();
            PlaySpeakingLoop();
        }

        private void EndSpeaking()
        {
            if (!speaking) return;

            speaking = false;
            pendingGesture = null;
            segmenter?.Reset();
            // 不主动 StopGesture：说话手势的 Clip 可能远长于本轮台词（如 Surprised 4s），
            // 在轮次结束时硬收会把正在播的动作掐成"抖一下再弹回 Idle"。槽是按 blendOutEnabled 播的
            // （RoleController.TryPlayGesture 传 !loop），clip 播完会自己融出，交给它收尾即可。
            // 需要立刻收的场景走显式 Stop：抑制走 SetSuppressed、组件禁用走 OnDisable。
        }

        private void HandleClause(string clause)
        {
            if (suppressed || activeProfile == null || driver == null)
                return;

            if (gestureCount >= activeProfile.MaxGesturesPerTurn)
                return;

            if (Random.value > activeProfile.GestureFrequency)
                return;

            float now = Time.unscaledTime;
            // 手势意图只按当前情绪选：写死的关键词表已删，要看场合挑动作交给模型
            GestureIntent intent = currentEmotion == ENpcEmotion.Angry
                ? new GestureIntent(EGestureIntent.AngryTalk, 0.8f)
                : new GestureIntent(EGestureIntent.NeutralTalk, 0.4f);
            GestureDefinition gesture = resolver.Resolve(
                activeProfile.GestureCatalog, intent, currentEmotion, now);
            if (gesture == null) return;

            if (now < gestureHoldUntil || now - lastGestureAt < activeProfile.MinimumGestureInterval)
            {
                pendingGesture = gesture;
                return;
            }

            PlayGesture(gesture, now);
        }

        private void PlayGesture(GestureDefinition gesture, float now)
        {
            if (!driver.TryPlayGesture(gesture.Clip,
                    activeProfile.BlendInSeconds,
                    activeProfile.BlendOutSeconds,
                    activeProfile.PlaybackSpeed,
                    gesture.UpperBodyOnly,
                    gesture.Additive,
                    false,
                    out string error))
            {
                WarnDriverFailure(error);
                return;
            }

            resolver.MarkPlayed(gesture, now);
            lastGestureAt = now;
            gestureHoldUntil = now + CalculateGestureHoldSeconds(
                gesture.Clip.length, activeProfile.PlaybackSpeed);
            gestureCount++;
        }

        private static float CalculateGestureHoldSeconds(float clipLength, float playbackSpeed)
        {
            return Mathf.Min(clipLength / Mathf.Max(0.01f, playbackSpeed) * 0.35f, 1.5f);
        }

        private void PlaySpeakingLoop()
        {
            if (suppressed || activeProfile == null || driver == null ||
                activeProfile.SpeakingLoop == null)
                return;

            if (!driver.TryPlayGesture(activeProfile.SpeakingLoop,
                    activeProfile.BlendInSeconds,
                    activeProfile.BlendOutSeconds,
                    activeProfile.PlaybackSpeed,
                    true,
                    false,
                    true,
                    out string error))
            {
                WarnDriverFailure(error);
            }
        }

        private void WarnDriverFailure(string error)
        {
            if (warnedDriverFailure) return;
            warnedDriverFailure = true;
            Log.Warning(nameof(NpcSpeechPerformanceController),
                string.IsNullOrEmpty(error) ? "NPC 说话动作播放失败" : error, this);
        }

        private float GetBlendOutSeconds()
        {
            return activeProfile != null ? activeProfile.BlendOutSeconds : 0.2f;
        }
    }
}
