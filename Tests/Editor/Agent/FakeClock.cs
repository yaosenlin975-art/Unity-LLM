/*
 * Agent 测试脚手架 — 假时钟
 * 反射整体替换 AgentCore.NowSeconds（与 AgentLoopGuardTests 同款写法），
 * 推进内核时钟即可驱动墙钟/动作超时/锁等待到期，不依赖真实计时器与帧循环
 */

using System;
using System.Reflection;
using LLM.Runtime.Agent;

namespace LLM.Tests.Editor.Agent
{
    public static class FakeClock
    {
        private const float k_defaultStart = 1000f;

        private static float now = k_defaultStart;
        private static Func<float> realClock;

        private static readonly FieldInfo NowSecondsField = typeof(AgentCore).GetField("NowSeconds",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>装上假时钟。幂等：已在位时不动，避免嵌套 Install 丢真钟</summary>
        public static void Install()
        {
            if (realClock != null) return;

            realClock = (Func<float>)NowSecondsField.GetValue(null);
            NowSecondsField.SetValue(null, (Func<float>)Read);
        }

        public static void Restore()
        {
            if (realClock == null) return;

            NowSecondsField.SetValue(null, realClock);
            realClock = null;
        }

        /// <summary>回到起点。新测试别继承上一条的时钟位置</summary>
        public static void ResetToStart()
        {
            now = k_defaultStart;
        }

        public static void Advance(float seconds)
        {
            now += seconds;
        }

        private static float Read()
        {
            return now;
        }
    }
}
