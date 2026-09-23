/*
┌────────────────────────────┐
│　Description: NPC 流式动作管线纯逻辑测试
│　Remark: 不创建 Unity 原生对象，离线可跑
│　ClassName: NpcGesturePipelineTests
└────────────────────────────┘
*/

using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Agent.Npc;
using NUnit.Framework;
using System.Reflection;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public sealed class NpcGesturePipelineTests
    {
        [Test]
        public void ClauseSegmenter_SplitsAtChinesePunctuationAcrossChunks()
        {
            var segmenter = new GestureClauseSegmenter(20, 0.35f);

            segmenter.Append("我不", 0f);
            segmenter.Append("同意。后面", 0.1f);

            Assert.That(segmenter.TryTakeClause(out string clause), Is.True);
            Assert.That(clause, Is.EqualTo("我不同意。"));
            Assert.That(segmenter.TryTakeClause(out _), Is.False);
            Assert.That(segmenter.TryTakeRemaining(out string remaining), Is.True);
            Assert.That(remaining, Is.EqualTo("后面"));
        }

        [Test]
        public void ClauseSegmenter_FlushesLongTextWithoutPunctuation()
        {
            var segmenter = new GestureClauseSegmenter(4, 0.35f);

            segmenter.Append("一二三四五", 0f);

            Assert.That(segmenter.TryTakeClause(out string clause), Is.True);
            Assert.That(clause, Is.EqualTo("一二三四"));
            Assert.That(segmenter.TryTakeRemaining(out string remaining), Is.True);
            Assert.That(remaining, Is.EqualTo("五"));
        }

        [Test]
        public void ClauseSegmenter_FlushesAfterStreamPause()
        {
            var segmenter = new GestureClauseSegmenter(20, 0.35f);

            segmenter.Append("让我想想", 1f);

            Assert.That(segmenter.TryTakeTimedOut(1.34f, out _), Is.False);
            Assert.That(segmenter.TryTakeTimedOut(1.35f, out string clause), Is.True);
            Assert.That(clause, Is.EqualTo("让我想想"));
        }

        [TestCase("不行，我不同意。", EGestureIntent.Reject)]
        [TestCase("你为什么这样问？", EGestureIntent.Question)]
        [TestCase("当然可以。", EGestureIntent.Agree)]
        [TestCase("请看那边。", EGestureIntent.Indicate)]
        [TestCase("我来说明原因。", EGestureIntent.Explain)]
        [TestCase("今天风很舒服。", EGestureIntent.NeutralTalk)]
        public void RuleClassifier_ReturnsExpectedIntent(string clause, EGestureIntent expected)
        {
            GestureIntent result = RuleGestureIntentClassifier.Classify(clause, ENpcEmotion.Neutral);

            Assert.That(result.Type, Is.EqualTo(expected));
            Assert.That(result.Intensity, Is.InRange(0f, 1f));
        }

        [Test]
        public void RuleClassifier_UsesEmotionForNeutralSpeech()
        {
            GestureIntent result = RuleGestureIntentClassifier.Classify("随便吧。", ENpcEmotion.Angry);

            Assert.That(result.Type, Is.EqualTo(EGestureIntent.AngryTalk));
        }

        [Test]
        public void PerformanceController_DeclaresNoAgentToolsOrActions()
        {
            MethodInfo[] methods = typeof(NpcSpeechPerformanceController).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);

            for (int i = 0; i < methods.Length; i++)
            {
                Assert.That(methods[i].IsDefined(typeof(AgentToolAttribute), false), Is.False,
                    methods[i].Name);
                Assert.That(methods[i].IsDefined(typeof(AgentActionAttribute), false), Is.False,
                    methods[i].Name);
            }
        }

        [TestCase(10f, 1f, 1.5f)]
        [TestCase(2f, 1f, 0.7f)]
        [TestCase(2f, 2f, 0.35f)]
        public void PerformanceController_HoldUsesThirtyFivePercentWithCap(
            float clipLength, float playbackSpeed, float expected)
        {
            MethodInfo method = typeof(NpcSpeechPerformanceController).GetMethod(
                "CalculateGestureHoldSeconds", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null, "控制器需要统一计算手势最短占用时间");
            float actual = (float)method.Invoke(null, new object[] { clipLength, playbackSpeed });
            Assert.That(actual, Is.EqualTo(expected).Within(0.001f));
        }

        [Test]
        public void Resolver_PicksHighestWeightMatchingUpperBodyGesture()
        {
            NpcGestureCatalog_SO catalog = CreateCatalog(
                CreateGesture("low", 1f, true, false),
                CreateGesture("high", 3f, true, false),
                CreateGesture("full-body", 99f, false, false));

            try
            {
                var resolver = new GestureResolver();
                GestureDefinition result = resolver.Resolve(catalog,
                    new GestureIntent(EGestureIntent.Explain, 0.5f), ENpcEmotion.Neutral, 10f);

                Assert.That(result?.Id, Is.EqualTo("high"));
            }
            finally
            {
                DestroyCatalog(catalog);
            }
        }

        [Test]
        public void Resolver_UsesCooldownToAvoidImmediateRepeat()
        {
            NpcGestureCatalog_SO catalog = CreateCatalog(
                CreateGesture("first", 3f, true, false),
                CreateGesture("second", 1f, true, false));

            try
            {
                var resolver = new GestureResolver();
                var intent = new GestureIntent(EGestureIntent.Explain, 0.5f);
                GestureDefinition first = resolver.Resolve(catalog, intent, ENpcEmotion.Neutral, 10f);
                resolver.MarkPlayed(first, 10f);

                GestureDefinition second = resolver.Resolve(catalog, intent, ENpcEmotion.Neutral, 11f);

                Assert.That(first?.Id, Is.EqualTo("first"));
                Assert.That(second?.Id, Is.EqualTo("second"));
            }
            finally
            {
                DestroyCatalog(catalog);
            }
        }

        private static GestureDefinition CreateGesture(string id, float weight,
            bool upperBodyOnly, bool additive)
        {
            return new GestureDefinition
            {
                Id = id,
                Clip = new AnimationClip(),
                Intents = new[] { EGestureIntent.Explain },
                SelectionWeight = weight,
                CooldownSeconds = 4f,
                UpperBodyOnly = upperBodyOnly,
                Additive = additive
            };
        }

        private static NpcGestureCatalog_SO CreateCatalog(params GestureDefinition[] gestures)
        {
            NpcGestureCatalog_SO catalog = ScriptableObject.CreateInstance<NpcGestureCatalog_SO>();
            FieldInfo field = typeof(NpcGestureCatalog_SO).GetField("gestures",
                BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(catalog, gestures);
            return catalog;
        }

        private static void DestroyCatalog(NpcGestureCatalog_SO catalog)
        {
            GestureDefinition[] gestures = catalog.Gestures;
            for (int i = 0; i < gestures.Length; i++)
                if (gestures[i]?.Clip != null) Object.DestroyImmediate(gestures[i].Clip);
            Object.DestroyImmediate(catalog);
        }
    }
}
