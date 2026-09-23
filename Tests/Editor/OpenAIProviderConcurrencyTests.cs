/*
 * LLM Tests — OpenAIProvider 并发安全测试
 * 验证 C1 修复：_reusableChunkList 改为实例字段后，多实例并发调用 ParseStreamChunk 不互相污染
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using LLM.Runtime;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class OpenAIProviderConcurrencyTests
    {
        private static readonly MethodInfo k_parseStreamChunkMethod =
            typeof(OpenAIProvider).GetMethod("ParseStreamChunk",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo k_reusableChunkListField =
            typeof(OpenAIProvider).GetField("_reusableChunkList",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [Test]
        public void ReusableChunkList_IsInstanceField_NotStatic()
        {
            Assert.IsNotNull(k_reusableChunkListField,
                "_reusableChunkList 应该作为实例字段存在");
            Assert.IsFalse(k_reusableChunkListField.IsStatic,
                "_reusableChunkList 不应该是 static（C1 修复要求）");
        }

        [Test]
        public void ReusableChunkList_DistinctAcrossInstances()
        {
            var provider1 = new OpenAIProvider("key1", "model1");
            var provider2 = new OpenAIProvider("key2", "model2");

            var list1 = k_reusableChunkListField.GetValue(provider1);
            var list2 = k_reusableChunkListField.GetValue(provider2);

            Assert.AreNotSame(list1, list2,
                "两个 OpenAIProvider 实例应该持有独立的 _reusableChunkList，避免并发污染");
        }

        [Test]
        public void ParseStreamChunk_ConcurrentAcrossInstances_NoCrossContamination()
        {
            const int instanceCount = 8;
            const int iterationsPerInstance = 50;

            var providers = new OpenAIProvider[instanceCount];
            for (int i = 0; i < instanceCount; i++)
                providers[i] = new OpenAIProvider($"key{i}", $"model{i}");

            Assert.IsNotNull(k_parseStreamChunkMethod,
                "ParseStreamChunk 私有方法应该存在");

            var errors = new ConcurrentQueue<string>();

            Parallel.For(0, instanceCount, pi =>
            {
                for (int iter = 0; iter < iterationsPerInstance; iter++)
                {
                    string content = $"p{pi}_iter{iter}";
                    string json = $"{{\"choices\":[{{\"delta\":{{\"content\":\"{content}\"}},\"finish_reason\":null}}]}}";

                    var list = (List<LLMStreamChunk>)k_parseStreamChunkMethod.Invoke(
                        providers[pi], new object[] { json });

                    if (list == null)
                    {
                        errors.Enqueue($"Provider {pi} iter {iter}: 返回 null");
                        continue;
                    }

                    if (list.Count != 1)
                    {
                        errors.Enqueue($"Provider {pi} iter {iter}: 期望 count=1, 实际 count={list.Count}");
                        continue;
                    }

                    if (list[0].ContentDelta != content)
                    {
                        errors.Enqueue(
                            $"Provider {pi} iter {iter}: 期望 content='{content}', 实际='{list[0].ContentDelta}'");
                    }
                }
            });

            Assert.IsEmpty(errors,
                "并发调用 ParseStreamChunk 出现污染:\n" + string.Join("\n", errors));
        }

        [Test]
        public void ParseStreamChunk_SameInstance_SequentialCalls_ReuseList()
        {
            var provider = new OpenAIProvider("key", "model");

            string json1 = "{\"choices\":[{\"delta\":{\"content\":\"first\"},\"finish_reason\":null}]}";
            string json2 = "{\"choices\":[{\"delta\":{\"content\":\"second\"},\"finish_reason\":null}]}";

            var list1 = (List<LLMStreamChunk>)k_parseStreamChunkMethod.Invoke(
                provider, new object[] { json1 });
            var list2 = (List<LLMStreamChunk>)k_parseStreamChunkMethod.Invoke(
                provider, new object[] { json2 });

            Assert.AreSame(list1, list2,
                "同一实例顺序调用应复用同一个 List（保留原复用语义）");
            Assert.AreEqual(1, list2.Count);
            Assert.AreEqual("second", list2[0].ContentDelta,
                "复用 List 应在 Clear 后填入新数据，不残留旧数据");
        }
    }
}
