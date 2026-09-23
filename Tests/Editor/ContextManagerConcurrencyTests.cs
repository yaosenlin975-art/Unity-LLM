/*
 * LLM Tests — ContextManager 并发安全测试
 * 验证 C5 修复：异步压缩期间用 SemaphoreSlim + lock(rounds) 保护 rounds 列表
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using LLM.Runtime;
using UnityEngine;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class ContextManagerConcurrencyTests
    {
        private static readonly FieldInfo k_compactLockField =
            typeof(ContextManager).GetField("_compactLock",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [Test]
        public void CompactLock_FieldExists_AsSemaphoreSlim()
        {
            Assert.IsNotNull(k_compactLockField,
                "C5: _compactLock 字段应存在");
            Assert.AreEqual(typeof(SemaphoreSlim), k_compactLockField.FieldType,
                "C5: _compactLock 应为 SemaphoreSlim 类型");
        }

        [Test]
        public void Snapshot_DoesNotShareMutableRounds()
        {
            var manager = new ContextManager();
            manager.AddRound("user", "assistant");

            var snapshot = manager.ExportSnapshot();
            snapshot.Rounds[0].UserMessage = "外部改动";
            Assert.AreEqual("user", manager.SnapshotRounds()[0].UserMessage);

            var restored = new ContextManager();
            restored.ImportSnapshot(snapshot);
            snapshot.Rounds[0].UserMessage = "二次改动";
            Assert.AreEqual("外部改动", restored.SnapshotRounds()[0].UserMessage);
        }

        [Test]
        public async Task ConcurrentAddRound_WithCompaction_DoesNotThrow()
        {
            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            config.SlidingWindowRounds = 4;
            config.CompressThresholdMultiplier = 1.5f; // CompressThreshold = ceil(4 * 1.5) = 6
            config.MinCompactRounds = 2;
            config.MinRecentKeep = 2;
            config.EnableLLMCompression = false; // 机械降级，不依赖 LLM provider
            config.EnableArchive = false; // 不写文件

            try
            {
                var manager = new ContextManager(config);

                // 预填充接近阈值
                for (int i = 0; i < 5; i++)
                    manager.AddRound($"user{i}", $"assistant{i}");

                var errors = new ConcurrentQueue<Exception>();
                var tasks = new List<Task>();
                for (int i = 0; i < 8; i++)
                {
                    int idx = i;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            for (int j = 0; j < 10; j++)
                                manager.AddRound($"u{idx}_{j}", $"a{idx}_{j}");
                        }
                        catch (Exception ex)
                        {
                            errors.Enqueue(ex);
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                // 等待后台压缩完成：先让所有 CompactRounds 启动并到达 WaitAsync
                await Task.Delay(200);
                var semaphore = (SemaphoreSlim)k_compactLockField.GetValue(manager);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await semaphore.WaitAsync(cts.Token);
                    semaphore.Release();
                }
                catch (OperationCanceledException) { }

                Assert.IsEmpty(errors,
                    "C5: 并发 AddRound 期间触发压缩抛出异常");
                Assert.Greater(manager.RoundCount, 0,
                    "C5: 压缩后应仍有轮次");
            }
            finally
            {
                ScriptableObject.DestroyImmediate(config);
            }
        }
    }
}
