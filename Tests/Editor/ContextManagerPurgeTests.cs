/*
 * LLM Tests — ContextManager purge 阈值配置化测试
 * 验证 T3 修复：FoldEconomics 的 minFoldTokens 从硬编码改为从 CompressionConfig_SO.MinFoldTokens 读取
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using LLM.Runtime;
using UnityEngine;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class ContextManagerPurgeTests
    {
        private static readonly FieldInfo k_minFoldTokensField =
            typeof(CompressionConfig_SO).GetField("MinFoldTokens");

        private static readonly MethodInfo k_foldEconomicsMethod =
            typeof(ContextManager).GetMethod("FoldEconomics",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo k_roundsField =
            typeof(ContextManager).GetField("rounds",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [Test]
        public void CompressionConfig_HasMinFoldTokensField_Default200()
        {
            Assert.IsNotNull(k_minFoldTokensField,
                "T3: CompressionConfig_SO 应有 MinFoldTokens 字段");
            Assert.AreEqual(typeof(int), k_minFoldTokensField.FieldType,
                "T3: MinFoldTokens 应为 int 类型");

            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            try
            {
                int defaultValue = (int)k_minFoldTokensField.GetValue(config);
                Assert.AreEqual(200, defaultValue,
                    "T3: MinFoldTokens 默认值应为 200（与原硬编码一致）");
            }
            finally
            {
                ScriptableObject.DestroyImmediate(config);
            }
        }

        [Test]
        public void FoldEconomics_ReadsMinFoldTokensFromConfig()
        {
            Assert.IsNotNull(k_foldEconomicsMethod,
                "T3: FoldEconomics 私有方法应存在");

            var config = ScriptableObject.CreateInstance<CompressionConfig_SO>();
            try
            {
                var manager = new ContextManager(config);

                // 通过 rounds 字段类型构造空 List<ConversationRound>
                Type conversationRoundType = k_roundsField.FieldType.GetGenericArguments()[0];
                Type listType = typeof(List<>).MakeGenericType(conversationRoundType);
                object emptyFold = Activator.CreateInstance(listType);

                // MinFoldTokens = 0，空 fold total=0，0 >= 0 → true
                config.MinFoldTokens = 0;
                bool resultZero = (bool)k_foldEconomicsMethod.Invoke(manager, new[] { emptyFold });
                Assert.IsTrue(resultZero,
                    "T3: MinFoldTokens=0 时空 fold 应返回 true（0>=0），证明读取了 config");

                // MinFoldTokens = 200，空 fold total=0，0 >= 200 → false
                config.MinFoldTokens = 200;
                bool resultDefault = (bool)k_foldEconomicsMethod.Invoke(manager, new[] { emptyFold });
                Assert.IsFalse(resultDefault,
                    "T3: MinFoldTokens=200 时空 fold 应返回 false（0<200），证明读取了 config");
            }
            finally
            {
                ScriptableObject.DestroyImmediate(config);
            }
        }

        [Test]
        public void FoldEconomics_ConfigNull_FallsBackToDefault200()
        {
            // config 为 null 时 FoldEconomics 应回退到默认值 200，不抛 NullReferenceException
            var manager = new ContextManager(null);

            // 通过 rounds 字段类型构造空 List<ConversationRound>
            Type conversationRoundType = k_roundsField.FieldType.GetGenericArguments()[0];
            Type listType = typeof(List<>).MakeGenericType(conversationRoundType);
            object emptyFold = Activator.CreateInstance(listType);

            // 空 fold total=0，0 >= 200(default) → false，且不应抛异常
            bool result = (bool)k_foldEconomicsMethod.Invoke(manager, new[] { emptyFold });
            Assert.IsFalse(result,
                "T3: config 为 null 时应回退到默认 200，空 fold 返回 false");
        }
    }
}
