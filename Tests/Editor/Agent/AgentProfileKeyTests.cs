/*
 * Agent Tests — ProfileKey 自动生成（ADR-012）
 * 规则：为空时按人设内容派生（FNV-1a）+ 会话内/项目资产查重；已有 key 不动；AgentCore 构造兜底
 */

using System;
using System.Collections.Generic;
using Cysharp.Text;
using LLM.Runtime.Agent;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentProfileKeyTests
    {
        private readonly List<AgentProfile_SO> created = new();

        private AgentProfile_SO NewProfile(string persona)
        {
            var profile = ScriptableObject.CreateInstance<AgentProfile_SO>();
            profile.PersonaPrompt = persona;
            created.Add(profile);
            return profile;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < created.Count; i++) FakeProfiles.Destroy(created[i]);
            created.Clear();
        }

        [Test]
        public void EnsureKey_EmptyKey_GeneratesDerivedKey()
        {
            var profile = NewProfile("沙漠商队的向导，说话简短。");
            Assert.IsTrue(profile.EnsureKey(), "空 key 应触发生成");
            StringAssert.StartsWith("agent-", profile.ProfileKey);

            var before = profile.ProfileKey;
            Assert.IsFalse(profile.EnsureKey(), "已有 key 只登记不重新生成");
            Assert.AreEqual(before, profile.ProfileKey, "key 生成后与人设解耦，重复调用不得漂移");
        }

        [Test]
        public void EnsureKey_SamePrompt_DedupesAcrossProfiles()
        {
            var a = NewProfile("镇上铁匠，脾气火爆。");
            var b = NewProfile("镇上铁匠，脾气火爆。");
            a.EnsureKey();
            b.EnsureKey();

            Assert.AreNotEqual(a.ProfileKey, b.ProfileKey, "同内容两个 Profile 必须拿到不同 key");
            Assert.AreEqual(ZString.Concat(a.ProfileKey, "-2"), b.ProfileKey, "冲突按序号追加");
        }

        [Test]
        public void EnsureKey_ManualKey_Untouched()
        {
            var profile = NewProfile("x");
            profile.ProfileKey = "custom_key";
            Assert.IsFalse(profile.EnsureKey());
            Assert.AreEqual("custom_key", profile.ProfileKey, "手填的 key 是作者意图，不得覆盖");
        }

        [Test]
        public void AgentCore_Ctor_FillsMissingKey()
        {
            var profile = NewProfile("构造兜底用例的人设。");
            var agent = new AgentCore(profile, "k1", null, new FakeOutput(), new FakeRunner());
            try
            {
                Assert.IsFalse(string.IsNullOrEmpty(profile.ProfileKey));
                StringAssert.Contains(ZString.Concat(profile.ProfileKey, "#k1"), agent.SessionId,
                    "sessionId 应由补出来的 key 参与构成");
            }
            finally
            {
                agent.Dispose();
            }
        }
    }
}
