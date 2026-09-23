/*
 * LLM Tests — ToolRegistry 扫描时机与范围测试
 * 验证 T6/T7 修复：Lazy 初始化只扫描一次、显式 Register(Assembly)、Refresh 方法、移除 Rescan
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using LLM.Runtime;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class ToolRegistryScanTests
    {
        private static readonly FieldInfo k_initializerField =
            typeof(AgentToolRegistry).GetField("_initializer",
                BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly FieldInfo k_registeredAssembliesField =
            typeof(AgentToolRegistry).GetField("registeredAssemblies",
                BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo k_registerMethod =
            typeof(AgentToolRegistry).GetMethod("Register",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Assembly) }, null);

        private static readonly MethodInfo k_refreshMethod =
            typeof(AgentToolRegistry).GetMethod("Refresh",
                BindingFlags.Public | BindingFlags.Static);

        private static readonly MethodInfo k_rescanMethod =
            typeof(AgentToolRegistry).GetMethod("Rescan",
                BindingFlags.Public | BindingFlags.Static);

        [Test]
        public void Initializer_FieldExists_AsLazy()
        {
            Assert.IsNotNull(k_initializerField,
                "T6: _initializer 字段应存在");
            Assert.IsTrue(
                k_initializerField.FieldType.IsGenericType &&
                k_initializerField.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>),
                "T6: _initializer 应为 Lazy<T> 类型（确保只扫描一次）");
        }

        [Test]
        public void Register_AssemblyMethodExists()
        {
            Assert.IsNotNull(k_registerMethod,
                "T7: Register(Assembly) 公开方法应存在（显式注册模式）");
        }

        [Test]
        public void Refresh_MethodExists()
        {
            Assert.IsNotNull(k_refreshMethod,
                "T6: Refresh() 公开方法应存在（手动刷新）");
        }

        [Test]
        public void Rescan_MethodRemoved()
        {
            Assert.IsNull(k_rescanMethod,
                "T6/T7: Rescan() 方法应已被 Refresh() 替代");
        }

        [Test]
        public void RegisteredAssemblies_FieldExists_AsList()
        {
            Assert.IsNotNull(k_registeredAssembliesField,
                "T7: registeredAssemblies 字段应存在");
            Assert.IsTrue(
                k_registeredAssembliesField.FieldType.IsGenericType &&
                k_registeredAssembliesField.FieldType.GetGenericTypeDefinition() == typeof(List<>) &&
                k_registeredAssembliesField.FieldType.GetGenericArguments()[0] == typeof(Assembly),
                "T7: registeredAssemblies 应为 List<Assembly>");
        }

        [Test]
        public void Register_DoesNotDuplicateScan()
        {
            // 触发 Lazy 初始化（注册默认 LLM.Runtime 程序集）
            AgentToolRegistry.ToLLMTools();

            var registeredAssemblies = (List<Assembly>)k_registeredAssembliesField.GetValue(null);
            int countBefore = registeredAssemblies.Count;

            // 重复注册 ToolRegistry 所在程序集（LLM.Runtime）不应增加计数
            AgentToolRegistry.Register(typeof(AgentToolRegistry).Assembly);
            int countAfter = registeredAssemblies.Count;

            Assert.AreEqual(countBefore, countAfter,
                "T6: 重复注册同一程序集不应重复扫描（幂等）");
        }

        [Test]
        public void Register_ExternalAssembly_AddsToRegistered()
        {
            AgentToolRegistry.ToLLMTools();

            // 注册测试程序集
            var testAssembly = typeof(ToolRegistryScanTests).Assembly;
            AgentToolRegistry.Register(testAssembly);

            var registeredAssemblies = (List<Assembly>)k_registeredAssembliesField.GetValue(null);
            Assert.Contains(testAssembly, registeredAssemblies,
                "T7: 显式注册的程序集应出现在已注册列表中");
        }

        [Test]
        public void ToLLMTools_TriggersInitializer_DoesNotScanAllAssemblies()
        {
            // ToLLMTools 应触发 Lazy 初始化，只注册默认程序集
            AgentToolRegistry.ToLLMTools();

            var registeredAssemblies = (List<Assembly>)k_registeredAssembliesField.GetValue(null);
            // 默认只应包含 LLM.Runtime 程序集（可能还包含之前测试注册的测试程序集）
            // 关键断言：不应包含所有 AppDomain 程序集
            Assert.LessOrEqual(registeredAssemblies.Count, 3,
                "T7: ToLLMTools 不应扫描所有程序集，只应包含显式注册的少量程序集");
        }
    }
}
