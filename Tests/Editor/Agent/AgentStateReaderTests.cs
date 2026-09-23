/*
 * ┌────────────────────────────────────────────┐
 * │ Description : AgentStateReader 单元测试      │
 * │ Remark      : 覆盖存档键复现与事实读取容错，  │
 * │               不含 UI 层                      │
 * │ ClassName   : AgentStateReaderTests         │
 * └────────────────────────────────────────────┘
 */

using Cysharp.Text;
using Lin.Runtime.Helper;
using LLM.Editor;
using LLM.Runtime.Agent;
using LLM.Runtime.Storage;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentStateReaderTests
    {
        private string sessionId;
        private AgentProfile_SO profile;

        [SetUp]
        public void SetUp()
        {
            sessionId = ZString.Concat("t-", System.Guid.NewGuid().ToString("N"));
            profile = ScriptableObject.CreateInstance<AgentProfile_SO>();
            profile.ProfileKey = "merchant";
        }

        [TearDown]
        public void TearDown()
        {
            PrefsHelper.DeleteKey<AgentFactsBlob>(sessionId);
            if (profile is not null) Object.DestroyImmediate(profile);
        }

        [Test]
        public void ComposeSessionId_JoinsProfileKeyAndInstanceId()
        {
            Assert.AreEqual("merchant#shop-1", AgentStateReader.ComposeSessionId(profile, "shop-1"));
        }

        [Test]
        public void ComposeSessionId_NullWhenInstanceIdMissing()
        {
            Assert.IsNull(AgentStateReader.ComposeSessionId(profile, ""));
            Assert.IsNull(AgentStateReader.ComposeSessionId(profile, null));
        }

        [Test]
        public void ComposeSessionId_NullWhenProfileKeyMissing()
        {
            profile.ProfileKey = "";
            Assert.IsNull(AgentStateReader.ComposeSessionId(profile, "shop-1"));
            Assert.IsNull(AgentStateReader.ComposeSessionId(null, "shop-1"));
        }

        [Test]
        public void ReadFacts_RoundTrip()
        {
            PrefsHelper.Set(sessionId, new AgentFactsBlob
            {
                Facts = new() { new AgentFact { Key = "color", Value = "红", Timestamp = 1 } }
            });

            var facts = AgentStateReader.ReadFacts(sessionId);

            Assert.AreEqual(1, facts.Count);
            Assert.AreEqual("color", facts[0].Key);
            Assert.AreEqual("红", facts[0].Value);
            Assert.AreEqual(1, facts[0].Timestamp);
        }

        [Test]
        public void ReadFacts_EmptyWhenNoArchive()
        {
            Assert.AreEqual(0, AgentStateReader.ReadFacts(sessionId).Count);
            Assert.AreEqual(0, AgentStateReader.ReadFacts(null).Count);
            Assert.AreEqual(0, AgentStateReader.ReadFacts("").Count);
        }

        [Test]
        public void ReadFacts_EmptyWhenFactsAreNull()
        {
            PrefsHelper.Set(sessionId, new AgentFactsBlob { Facts = null });

            Assert.AreEqual(0, AgentStateReader.ReadFacts(sessionId).Count, "null 列表按空状态处理");
        }
    }
}
