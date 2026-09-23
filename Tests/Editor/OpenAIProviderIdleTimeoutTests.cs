/*
 * LLM Tests — OpenAIProvider IdleCts TOCTOU 安全测试
 * 验证 C2 修复：_idleCts 在 CompleteContent 置空后，IdleToken 访问不抛 NullReferenceException
 * Remark: T1-R3 四条红的根因在测试桩自己身上——SSEDownloadHandler 是私有嵌套类，构造函数却是 public，
 *         反射取构造函数只写 NonPublic 会把 public 成员过滤掉，拿到空数组再取 [0] 就是 IndexOutOfRangeException
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using LLM.Runtime;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class OpenAIProviderIdleTimeoutTests
    {
        /// <summary>0 秒空闲额度的落地等待上限：轮询到点即返回，不写固定 Sleep</summary>
        private const int k_idleDeadlineWaitMs = 3000;

        private static readonly Type k_handlerType =
            typeof(OpenAIProvider).GetNestedType("SSEDownloadHandler",
                BindingFlags.NonPublic);

        private static readonly FieldInfo k_idleCtsField =
            k_handlerType?.GetField("_idleCts",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly PropertyInfo k_idleTokenProp =
            k_handlerType?.GetProperty("IdleToken");

        private static readonly MethodInfo k_completeContentMethod =
            k_handlerType?.GetMethod("CompleteContent",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [Test]
        public void SSEDownloadHandler_Type_AndIdleCtsField_Exist()
        {
            Assert.IsNotNull(k_handlerType,
                "SSEDownloadHandler 内部类应该存在");
            Assert.IsNotNull(k_idleCtsField,
                "_idleCts 字段应该存在");
            Assert.IsNotNull(k_idleTokenProp,
                "IdleToken 属性应该存在");
            Assert.IsNotNull(k_completeContentMethod,
                "CompleteContent 方法应该存在");
        }

        [Test]
        public void IdleToken_InitialState_NotCancelled()
        {
            object handler = CreateHandler(idleTimeoutSeconds: 60);
            if (handler == null)
            {
                Assert.Ignore("当前环境无法创建 SSEDownloadHandler 实例");
                return;
            }

            var token = (CancellationToken)k_idleTokenProp.GetValue(handler);
            Assert.IsFalse(token.IsCancellationRequested,
                "初始状态 IdleToken 不应被取消");
        }

        [Test]
        public void IdleToken_AfterCompleteContent_ReturnsDefaultWithoutNRE()
        {
            object handler = CreateHandler(idleTimeoutSeconds: 60);
            if (handler == null)
            {
                Assert.Ignore("当前环境无法创建 SSEDownloadHandler 实例");
                return;
            }

            // 调用 CompleteContent，会将 _idleCts Dispose 并置 null
            k_completeContentMethod.Invoke(handler, null);

            // 属性里真抛 NRE 会被反射包成 TargetInvocationException，用例照样红，
            // 不必再套一层 Assert.DoesNotThrow（那层还得多养一个委托）
            var token = (CancellationToken)k_idleTokenProp.GetValue(handler);
            Assert.AreEqual(default(CancellationToken), token,
                "CompleteContent 后 _idleCts 已置空，IdleToken 应返回 default 而不是抛 NRE");
        }

        [Test]
        public void IdleToken_AfterIdleTimeout_Cancelled()
        {
            // 空闲超时靠 CancellationTokenSource 的线程池计时器落地：ADR-007 禁的是内核三类超时用它
            // （假时钟推不动），这里掐的是真实 HTTP 流，与压缩超时同类，规格明确保留。EditMode 既没有
            // 假时钟可推、也拿不到 WaitHandle（netstd 2.1 没导出），所以给 0 秒额度 + 有界轮询：
            // 到点即返回，不写固定 Sleep，真不触发也是带着说明失败而不是挂住。
            // 钉两件事：其一，构造时确实布了取消期限（删掉 CancelAfter 那行会红）；
            //          其二，到期被 Cancel 的正是 IdleToken 暴露的那个令牌（另起一个 CTS 也会红）——
            //          接线一断，CompleteStreamAsync 的 linkedCts 就永远等不到取消，
            //          "一直连着但不结束"的流再也掐不掉。
            object handler = CreateHandler(idleTimeoutSeconds: 0);
            if (handler == null)
            {
                Assert.Ignore("当前环境无法创建 SSEDownloadHandler 实例");
                return;
            }

            Assert.IsNotNull(k_idleCtsField.GetValue(handler),
                "0 秒额度也得先有 _idleCts，否则无从到期");
            Assert.IsTrue(WaitIdleTokenCancelled(handler, k_idleDeadlineWaitMs),
                "空闲额度到点后计时器应触发 Cancel，而且要能透过 IdleToken 看见");
        }

        [Test]
        public void CompleteContent_DisposesAndNullsIdleCts()
        {
            object handler = CreateHandler(idleTimeoutSeconds: 60);
            if (handler == null)
            {
                Assert.Ignore("当前环境无法创建 SSEDownloadHandler 实例");
                return;
            }

            Assert.IsNotNull(k_idleCtsField.GetValue(handler),
                "CompleteContent 前 _idleCts 不应为 null");

            k_completeContentMethod.Invoke(handler, null);

            Assert.IsNull(k_idleCtsField.GetValue(handler),
                "CompleteContent 后 _idleCts 应被置 null");
        }

        private static object CreateHandler(int idleTimeoutSeconds)
        {
            if (k_handlerType == null) return null;

            // 可见性那两个标志是"返回哪些可见性"的白名单，必须带上 Public：类型是私有的，
            // 构造函数本身却是 public，只写 NonPublic 就把它过滤掉了，空数组取 [0] 直接越界
            var constructors = k_handlerType.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotEmpty(constructors, "SSEDownloadHandler 应该有一个可用构造函数");

            Action<LLMStreamChunk> onChunk = OnTestChunk;
            Func<string, List<LLMStreamChunk>> parseChunk = ParseTestChunk;

            try
            {
                return constructors[0].Invoke(new object[]
                {
                    onChunk,
                    parseChunk,
                    idleTimeoutSeconds
                });
            }
            catch (TargetInvocationException)
            {
                // DownloadHandlerScript 可能无法在 Edit Mode 创建
                return null;
            }
        }

        /// <summary>轮询到 IdleToken 翻成取消态为止，最多 waitMs；超时返回 false 由用例判红</summary>
        private static bool WaitIdleTokenCancelled(object handler, int waitMs)
        {
            int deadline = Environment.TickCount + waitMs;
            do
            {
                var token = (CancellationToken)k_idleTokenProp.GetValue(handler);
                if (token.IsCancellationRequested) return true;
                Thread.Sleep(5);
            }
            while (Environment.TickCount - deadline < 0);

            return false;
        }

        // 桩回调写成具名方法：传方法组而不是闭包，测试里也不白给委托分配
        private static void OnTestChunk(LLMStreamChunk chunk)
        {
        }

        private static List<LLMStreamChunk> ParseTestChunk(string data)
        {
            return new List<LLMStreamChunk>();
        }
    }
}
