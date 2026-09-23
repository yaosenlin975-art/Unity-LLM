/*
┌────────────────────────────┐
│　Description: Agent 记忆搜索测试
│　Remark: 只检索当前上下文不可见的旧对话
│　ClassName: AgentMemorySearchTests
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Storage;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentMemorySearchTests
    {
        private static readonly FieldInfo k_compactLockField =
            typeof(ContextManager).GetField("_compactLock",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [Test]
        public void SearchMemory_ReturnsHiddenRound_ButNotInjectedFactOrVisibleRound()
        {
            var profile = FakeProfiles.Create("memory-search");
            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            config.EnableArchive = false;
            config.SlidingWindowRounds = 2;
            profile.CompressionConfig = config;
            var agent = new AgentCore(profile, "instance", null, null);

            try
            {
                agent.Memory.Write("青鸟事实", "这条已经在 system 里");
                agent.Session.Context.AddRound("开场", "你好");
                agent.Session.Context.AddRound("旧暗号是青鸟", "已经记下");
                agent.Session.Context.AddRound("最近也提到青鸟", "这是当前窗口内容");

                var result = AgentMemoryActions.SearchPastConversation(
                    new AgentActionContext { Agent = agent }, "青鸟", 5).GetAwaiter().GetResult();

                Assert.IsTrue(result.Ok);
                StringAssert.Contains("[user | ", result.Content);
                StringAssert.Contains("旧暗号是青鸟", result.Content);
                StringAssert.DoesNotContain("青鸟事实", result.Content);
                StringAssert.DoesNotContain("最近也提到青鸟", result.Content);
                StringAssert.Contains("压缩归档未启用", result.Content);
            }
            finally
            {
                agent.Dispose();
                UnityEngine.Object.DestroyImmediate(config);
                FakeProfiles.Destroy(profile);
            }
        }

        [Test]
        public void SearchMemory_ReadsArchiveAndClearHistoryDeletesIt()
        {
            var profile = FakeProfiles.Create("memory-archive");
            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            config.EnableArchive = true;
            profile.CompressionConfig = config;
            string instanceId = ZString.Concat("instance-", Guid.NewGuid().ToString("N"));
            var agent = new AgentCore(profile, instanceId, null, null);
            string directory = Path.Combine(Application.persistentDataPath,
                "LLMArchive", agent.SessionId);

            try
            {
                Directory.CreateDirectory(directory);
                var archived = new List<ConversationRound>
                {
                    new()
                    {
                        UserMessage = "归档暗号是青鸟",
                        AssistantMessage = "已经记下",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                };
                File.WriteAllText(Path.Combine(directory, "20260920-000000.000.json"),
                    JsonConvert.SerializeObject(archived));

                var found = AgentMemoryActions.SearchPastConversation(
                    new AgentActionContext { Agent = agent }, "青鸟", 5).GetAwaiter().GetResult();
                Assert.IsTrue(found.Ok);
                StringAssert.Contains("归档暗号是青鸟", found.Content);

                agent.ClearHistory();
                Assert.IsFalse(Directory.Exists(directory));
            }
            finally
            {
                agent.ClearHistory();
                agent.Dispose();
                UnityEngine.Object.DestroyImmediate(config);
                FakeProfiles.Destroy(profile);
            }
        }

        [Test]
        public async Task CompressionCompletion_SavesSummaryState()
        {
            var profile = FakeProfiles.Create("compression-save");
            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            config.SlidingWindowRounds = 3;
            config.CompressThresholdMultiplier = 1.5f;
            config.MinCompactRounds = 2;
            config.MinRecentKeep = 2;
            config.EnableLLMCompression = false;
            config.EnableArchive = false;
            profile.CompressionConfig = config;
            var store = new RecordingConversationStore();
            AgentStateStores.Reset();
            AgentStateStores.History = store;
            var agent = new AgentCore(profile, "instance", null, null);

            try
            {
                for (int i = 0; i < 6; i++)
                    agent.Session.Context.AddRound(
                        ZString.Concat("user", i), ZString.Concat("assistant", i));

                await Task.Delay(100);
                var semaphore = (SemaphoreSlim)k_compactLockField.GetValue(agent.Session.Context);
                using var cts = new CancellationTokenSource(5000);
                await semaphore.WaitAsync(cts.Token);
                semaphore.Release();

                Assert.Greater(store.SaveCount, 0);
                bool hasSummary = false;
                for (int i = 0; i < store.LastBlob.Rounds.Count; i++)
                    hasSummary |= store.LastBlob.Rounds[i].IsSummary;
                Assert.IsTrue(hasSummary);
            }
            finally
            {
                agent.Dispose();
                AgentStateStores.Reset();
                UnityEngine.Object.DestroyImmediate(config);
                FakeProfiles.Destroy(profile);
            }
        }

        private sealed class RecordingConversationStore : IConversationStore
        {
            public int SaveCount { get; private set; }

            public AgentRoundsBlob LastBlob { get; private set; }

            public AgentRoundsBlob LoadHistory(string sessionId) => null;

            public UniTask SaveHistoryAsync(string sessionId, AgentRoundsBlob blob, CancellationToken ct)
            {
                SaveCount++;
                LastBlob = blob;
                return UniTask.CompletedTask;
            }
        }
    }
}
