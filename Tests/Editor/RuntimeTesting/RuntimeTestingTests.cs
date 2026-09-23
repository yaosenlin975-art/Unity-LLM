/*
 * ┌────────────────────────────────────────────┐
 * │ Description : RuntimeTesting 最小闭环测试    │
 * │ Remark      : 只覆盖白名单、摘要与安全门槛    │
 * │ ClassName   : RuntimeTestingTests            │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Reflection;
using Cysharp.Text;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.RuntimeTesting;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.RuntimeTesting
{
    [TestFixture]
    public sealed class RuntimeTestingTests
    {
        private GameObject commandObject;

        [TearDown]
        public void TearDown()
        {
            if (commandObject != null)
                UnityEngine.Object.DestroyImmediate(commandObject);
        }

        [Test]
        public void EmptyPerformanceSamples_AreUnavailable()
        {
            var summary = RuntimePerformanceSummary.Calculate(Array.Empty<float>());

            Assert.IsFalse(summary.Available);
            Assert.AreEqual(0, summary.Count);
        }

        [Test]
        public void PerformanceSamples_ComputeAverageAndPercentiles()
        {
            var summary = RuntimePerformanceSummary.Calculate(new[] { 1f, 2f, 3f, 4f, 100f });

            Assert.IsTrue(summary.Available);
            Assert.AreEqual(5, summary.Count);
            Assert.AreEqual(22f, summary.Average, 0.001f);
            Assert.AreEqual(100f, summary.P95, 0.001f);
            Assert.AreEqual(100f, summary.P99, 0.001f);
            Assert.AreEqual(100f, summary.Max, 0.001f);
        }

        [Test]
        public void PerformanceCapture_ReportsCurrentSampleCount()
        {
            var capture = new RuntimePerformanceCapture();
            capture.Start();
            capture.AddSample(12f);
            capture.AddSample(18f);

            Assert.AreEqual(2, capture.Count);
            Assert.AreEqual(2, capture.Stop().Count);
        }

        [Test]
        public void CommandRegistry_OnlyInvokesMarkedCommand()
        {
            commandObject = new GameObject("runtime-test-command");
            var provider = commandObject.AddComponent<TestCommandProvider>();
            var registry = new RuntimeTestCommandRegistry();

            registry.Register(provider);

            Assert.IsTrue(registry.TryInvoke("echo", "{\"text\":\"ok\"}", out var result));
            Assert.AreEqual("echo:ok", result);
            Assert.IsFalse(registry.TryInvoke("secret", "{}", out _));
        }

        [Test]
        public void RuntimeTestTools_AreMemberToolAndAction()
        {
            var type = typeof(RuntimeTestTools);
            var tool = type.GetMethod(nameof(RuntimeTestTools.ListTestCommands), BindingFlags.Public | BindingFlags.Instance);
            var action = type.GetMethod(nameof(RuntimeTestTools.InvokeTestCommand), BindingFlags.Public | BindingFlags.Instance);
            var snapshot = type.GetMethod(nameof(RuntimeTestTools.GetPerformanceSnapshot), BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(tool.GetCustomAttribute<AgentToolAttribute>());
            Assert.IsNotNull(action.GetCustomAttribute<AgentActionAttribute>());
            Assert.IsNotNull(snapshot.GetCustomAttribute<AgentToolAttribute>());
        }

        [Test]
        public void WindowBinding_RejectsRebind()
        {
            var binding = new RuntimeTestWindowBinding();

            Assert.IsTrue(binding.TryBind(12, new IntPtr(34)));
            Assert.IsFalse(binding.TryBind(13, new IntPtr(35)));
            Assert.AreEqual(12, binding.ProcessId);
            Assert.AreEqual(new IntPtr(34), binding.WindowHandle);
        }

        public sealed class TestCommandProvider : MonoBehaviour
        {
            [RuntimeTestCommand("echo", "测试回显")]
            public string Echo(string text)
            {
                return ZString.Concat("echo:", text);
            }

            public string Secret()
            {
                return "secret";
            }
        }
    }
}
