/*
┌────────────────────────────┐
│　Description: PrefsAgentStateStore 存储测试
│　Remark: PrefsHelper 按类型静态缓存归档，
│　　　　　 故这里测接口接线而非磁盘落地
│　ClassName: PrefsAgentStateStoreTests
└────────────────────────────┘
*/

using System;
using Cysharp.Text;
using Lin.Runtime.Helper;
using LLM.Demo.Storage;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Storage;
using NUnit.Framework;

namespace LLM.Tests.Editor.Storage
{
    [TestFixture]
    public class PrefsAgentStateStoreTests
    {
        private static AgentFactsBlob Facts => new()
        {
            Facts = new() { new AgentFact { Key = "color", Value = "红", Timestamp = 1 } }
        };

        private static AgentRoundsBlob Rounds => new()
        {
            Rounds = new() { new ConversationRound { UserMessage = "你好", AssistantMessage = "在的" } }
        };

        private readonly PrefsAgentStateStore store = new();

        private string sessionId;

        [SetUp]
        public void SetUp()
        {
            sessionId = ZString.Concat("t-", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            PrefsHelper.DeleteKey<AgentFactsBlob>(sessionId);
            PrefsHelper.DeleteKey<AgentRoundsBlob>(sessionId);
        }

        [Test]
        public void Facts_RoundTrip_And_ArchivesStayIsolated()
        {
            Assert.IsNull(store.LoadFacts(sessionId), "没写过的 sessionId 必须读回 null");

            var facts = Facts;
            store.SaveFactsAsync(sessionId, facts, default).GetAwaiter().GetResult();
            Assert.AreEqual("color", store.LoadFacts(sessionId).Facts[0].Key);

            Assert.IsNull(store.LoadHistory(sessionId), "事实与轮次各占一份归档，不能互相看见");

            store.SaveHistoryAsync(sessionId, Rounds, default).GetAwaiter().GetResult();
            Assert.AreEqual("你好", store.LoadHistory(sessionId).Rounds[0].UserMessage);
            Assert.AreEqual("color", store.LoadFacts(sessionId).Facts[0].Key, "写轮次不能覆盖事实");
        }

        [Test]
        public void EmptySessionId_TouchesNothing()
        {
            store.SaveFactsAsync(null, Facts, default).GetAwaiter().GetResult();
            store.SaveHistoryAsync("", Rounds, default).GetAwaiter().GetResult();

            Assert.IsNull(store.LoadFacts(null));
            Assert.IsNull(store.LoadHistory(""));
        }
    }
}
