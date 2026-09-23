/*
 * Agent 测试脚手架 — AgentProfile_SO 工厂
 * ScriptableObject.CreateInstance 造内存 profile，测完 DestroyImmediate
 */

using LLM.Runtime.Agent;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    public static class FakeProfiles
    {
        public static AgentProfile_SO Create(
            string profileKey = "t",
            string persona = "测试人设。",
            string fallbackLine = "（测试兜底）这一轮没有拿到回复。",
            int turnDeadlineSeconds = 90,
            int perNameToolCallLimit = 4,
            int globalRepeatLimit = 2,
            int actionTimeoutSeconds = 15,
            int lockWaitSeconds = 5)
        {
            var profile = ScriptableObject.CreateInstance<AgentProfile_SO>();
            profile.name = string.IsNullOrEmpty(profileKey) ? "t" : profileKey;
            profile.ProfileKey = profileKey;
            profile.PersonaPrompt = persona;
            profile.FallbackLines = new[] { fallbackLine };
            profile.TurnDeadlineSeconds = turnDeadlineSeconds;
            profile.PerNameToolCallLimit = perNameToolCallLimit;
            profile.GlobalRepeatLimit = globalRepeatLimit;
            profile.ActionTimeoutSeconds = actionTimeoutSeconds;
            profile.LockWaitSeconds = lockWaitSeconds;
            return profile;
        }

        public static void Destroy(AgentProfile_SO profile)
        {
            if (profile == null) return;
            Object.DestroyImmediate(profile);
        }
    }
}
