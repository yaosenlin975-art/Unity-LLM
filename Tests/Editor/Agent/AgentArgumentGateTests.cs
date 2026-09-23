/*
 * ┌────────────────────────────────────────────┐
 * │ Description : 入参格式闸与回灌配额测试       │
 * │ Remark      : 坏参数必须被拦下并指名参数，   │
 * │               不得静默按默认值执行          │
 * │ ClassName   : AgentArgumentGateTests        │
 * └────────────────────────────────────────────┘
 */

using System.Reflection;
using LLM.Runtime.Agent;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentArgumentGateTests
    {
        // 只借它的签名拿 ParameterInfo，方法体不会被调用
        private static void TwoRequired(int count, string key)
        {
            Assert.Fail("测试用的方法体不该被执行");
        }

        private static void WithDefault(int count, string note = "x")
        {
            Assert.Fail("测试用的方法体不该被执行");
        }

        private static ParameterInfo[] ParamsOf(string name)
            => typeof(AgentArgumentGateTests)
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                .GetParameters();

        private static bool Gate(string json, ParameterInfo[] ps, string[] extra, out string error)
            => AgentArgumentGate.TryValidate(json, ps, extra, out JObject _, out error);

        [Test]
        public void MalformedJson_IsRejectedWithReason()
        {
            Assert.IsFalse(Gate("{\"count\":1,,}", ParamsOf(nameof(TwoRequired)), null, out string error));
            StringAssert.Contains("不是合法 JSON", error);
        }

        [Test]
        public void MissingRequired_IsRejected_NamedPerParameter()
        {
            Assert.IsFalse(Gate("{\"count\":3}", ParamsOf(nameof(TwoRequired)), null, out string error));
            StringAssert.Contains("key", error, "缺哪个参数就该点名哪个");

            // 有默认值的参数缺失是合法的，不该误伤
            Assert.IsTrue(Gate("{\"count\":3}", ParamsOf(nameof(WithDefault)), null, out _));
        }

        [Test]
        public void ExplicitJsonNull_CountsAsMissing_NotAsGiven()
        {
            // JValue(Null) 不是 C# null：不判 Type 就会当"已给"放行，绑定再静默降级成 0/""
            Assert.IsFalse(Gate("{\"count\":3,\"key\":null}",
                ParamsOf(nameof(TwoRequired)), null, out string error));
            StringAssert.Contains("key", error);
        }

        [Test]
        public void WrongType_IsRejected_InsteadOfBecomingZero()
        {
            Assert.IsFalse(Gate("{\"count\":\"abc\",\"key\":\"k\"}",
                ParamsOf(nameof(TwoRequired)), null, out string error));
            StringAssert.Contains("count", error);
        }

        [Test]
        public void KernelInjectedRequired_IsCheckedToo()
        {
            Assert.IsFalse(Gate("{\"count\":3,\"note\":\"n\"}",
                ParamsOf(nameof(WithDefault)), new[] { "say" }, out string error));
            StringAssert.Contains("say", error);
        }

        [Test]
        public void EmptyArgs_AreLegal_WhenNothingIsRequired()
        {
            Assert.IsTrue(Gate(null, new ParameterInfo[0], null, out _));
            Assert.IsTrue(Gate("{}", new ParameterInfo[0], null, out _));
        }

        [Test]
        public void FormatErrorQuota_ResetsEachTurn()
        {
            var guard = new LoopGuard();

            Assert.IsFalse(guard.ReportFormatError(), "第 1 次给回灌机会");
            Assert.IsFalse(guard.ReportFormatError(), "第 2 次给回灌机会");
            Assert.IsTrue(guard.ReportFormatError(), "第 3 次超限，调用方应按 Abort 处理");

            guard.BeginTurn();
            Assert.IsFalse(guard.ReportFormatError(), "新一轮重新计数");
        }
    }
}
