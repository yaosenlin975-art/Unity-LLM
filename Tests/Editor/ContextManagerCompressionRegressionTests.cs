/*
┌────────────────────────────┐
│　Description: 上下文压缩回归测试
│　Remark: 短对话也必须进入折叠区
│　ClassName: ContextManagerCompressionRegressionTests
└────────────────────────────┘
*/

using System.Reflection;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Text;
using LLM.Runtime;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class ContextManagerCompressionRegressionTests
    {
        private static readonly FieldInfo k_compactLockField =
            typeof(ContextManager).GetField("_compactLock",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [Test]
        public async Task ShortRounds_OverThreshold_AreCompacted()
        {
            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            config.SlidingWindowRounds = 3;
            config.CompressThresholdMultiplier = 1.5f;
            config.MinCompactRounds = 2;
            config.MinRecentKeep = 2;
            config.EnableLLMCompression = false;
            config.EnableArchive = false;

            try
            {
                var manager = new ContextManager(config);
                for (int i = 0; i < 6; i++)
                    manager.AddRound(ZString.Concat("user", i), ZString.Concat("assistant", i));

                await WaitForCompaction(manager);

                var rounds = manager.SnapshotRounds();
                bool hasSummary = false;
                for (int i = 0; i < rounds.Count; i++)
                    hasSummary |= rounds[i].IsSummary;

                Assert.IsTrue(hasSummary,
                    "超过阈值的普通短对话必须被摘要，不能因每轮都被误判为 pinned 而静默丢出窗口");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void TokenMode_AmpleBudget_KeepsEveryRound()
        {
            // 出厂资产就是 token 模式（ContextWindowTokens>0），而这条路径此前零覆盖
            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            config.ContextWindowTokens = 65536;
            config.TailTokenBudget = 8192;
            config.MinRecentKeep = 2;
            config.SlidingWindowRounds = 20;
            config.CompressThresholdMultiplier = 1.5f;
            config.EnableLLMCompression = false;
            config.EnableArchive = false;

            try
            {
                var manager = new ContextManager(config);
                manager.AddRound("第一轮提问", "第一轮回答");
                manager.AddRound("第二轮提问", "第二轮回答");

                var messages = manager.GetHistoryMessages();
                bool hasFirstRound = false;
                for (int i = 0; i < messages.Count; i++)
                    hasFirstRound |= messages[i].Content != null && messages[i].Content.Contains("第一轮");

                Assert.IsTrue(hasFirstRound,
                    "预算充足时第一轮必须仍在请求里：尾区起点曾从 head+1 起算，把 rounds[head] 整条排除");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public async Task QueuedCompactions_RecomputeFoldAndPreserveNewRoundsInArchive()
        {
            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            config.SlidingWindowRounds = 3;
            config.CompressThresholdMultiplier = 1.5f;
            config.MinCompactRounds = 2;
            config.MinRecentKeep = 2;
            config.EnableLLMCompression = false;
            config.EnableArchive = true;
            string sessionId = ZString.Concat("compression-regression-", System.Guid.NewGuid().ToString("N"));
            var manager = new ContextManager(config, sessionId);
            var semaphore = (SemaphoreSlim)k_compactLockField.GetValue(manager);

            await semaphore.WaitAsync();
            try
            {
                for (int i = 0; i < 10; i++)
                    manager.AddRound(ZString.Concat("user", i), ZString.Concat("assistant", i));
            }
            finally
            {
                semaphore.Release();
            }

            try
            {
                await WaitForCompaction(manager);
                string archiveDirectory = Path.Combine(Application.persistentDataPath,
                    "LLMArchive", sessionId);
                var files = Directory.GetFiles(archiveDirectory, "*.json");

                Assert.AreEqual(1, files.Length,
                    "排队中的压缩任务取得锁后必须重算；第一次压缩收敛后其余任务应直接退出");
                StringAssert.Contains("user7", File.ReadAllText(files[0]),
                    "等待压缩期间新增且进入折叠区的轮次必须进入本次归档，不能被旧快照覆盖");
            }
            finally
            {
                manager.ClearArchive();
                Object.DestroyImmediate(config);
            }
        }

        private static async Task WaitForCompaction(ContextManager manager)
        {
            await Task.Delay(100);
            var semaphore = (SemaphoreSlim)k_compactLockField.GetValue(manager);
            using var cts = new CancellationTokenSource(5000);
            await semaphore.WaitAsync(cts.Token);
            semaphore.Release();
        }
    }
}
