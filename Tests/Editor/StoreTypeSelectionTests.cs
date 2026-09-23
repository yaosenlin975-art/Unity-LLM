/*
┌────────────────────────────┐
│　Description: 存储实现反射选型的用例
│　Remark: 覆盖 ADR-030 的验收 2/3/5/6/9/10/11；
│　　　　　 共享的 GlobalConfig 资产用后必复原，
│　　　　　 否则会把"出厂全空"污染成脏值
│　ClassName: StoreTypeSelectionTests
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using Lin.Runtime.Helper;
using LLM.Demo.Agent.Npc;
using LLM.Demo.Storage;
using LLM.Editor;
using LLM.Runtime;
using LLM.Runtime.Agent.Npc;
using LLM.Runtime.Storage;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LLM.Tests.Editor
{
    // 只用来验"归档身份只看短名"：与下面那个同名、不同命名空间
    internal sealed class ArchiveNameProbe { }

    internal static class ArchiveNameProbeTwin
    {
        internal sealed class ArchiveNameProbe { }
    }

    [TestFixture]
    public class StoreTypeSelectionTests
    {
        private LLMGlobalConfig_SO config;
        private string savedFacts;
        private string savedHistory;
        private string savedAffinity;
        private readonly List<string> errors = new();

        // 配置面存的就是 FullName，这里用 typeof 取当前值：改名后用例跟着走，不写死字符串
        private static readonly string prefsStore = typeof(PrefsAgentStateStore).FullName;
        private static readonly string prefsAffinityStore = typeof(PrefsNpcAffinityStore).FullName;

        [SetUp]
        public void SetUp()
        {
            // 本组用例断言的就是那条 Log.Error；不豁免的话测试框架先替我们判失败（UTF 见任何 Error 即红）
            LogAssert.ignoreFailingMessages = true;

            // Reset() 连带清掉失败去重记录，用例之间才互不粘滞
            AgentStateStores.Reset();
            NpcAffinityStore.Reset();

            config = Resources.Load<LLMGlobalConfig_SO>(LLMRuntimeSettings.CONFIG_PATH);
            Assert.That(config is not null, "缺 Resources/LLM/LLMGlobalConfig 资产");

            savedFacts = config.AgentFactsStoreType;
            savedHistory = config.AgentHistoryStoreType;
            savedAffinity = config.NpcAffinityStoreType;

            Application.logMessageReceived += CatchError;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            Application.logMessageReceived -= CatchError;

            // SetUp 断言失败时 config 为 null：这里不判空会把真实失败信息盖成 NRE
            if (config is not null)
            {
                config.AgentFactsStoreType = savedFacts;
                config.AgentHistoryStoreType = savedHistory;
                config.NpcAffinityStoreType = savedAffinity;
            }

            AgentStateStores.Reset();
            NpcAffinityStore.Reset();
        }

        private void CatchError(string condition, string stackTrace, LogType type)
        {
            // 只数存储装配那一条：Provider 侧的报错由别的用例负责
            if (type == LogType.Error && (condition.Contains("Facts=") || condition.Contains("History=") ||
                                           condition.Contains("Affinity=")))
                errors.Add(condition);
        }

        [Test]
        public void Resolve_BlankIsTreatedAsUnconfiguredNotError()
        {
            Assert.IsNull(StoreTypeResolver.Resolve("", typeof(IFactStore), out string error), "空串=未配置");
            Assert.IsNull(error, "未配置不该带原因");

            Assert.IsNull(StoreTypeResolver.Resolve("   ", typeof(IFactStore), out error));
            Assert.IsNull(error);
        }

        [Test]
        public void Resolve_ReportsReasonForMissingType()
        {
            Assert.IsNull(StoreTypeResolver.Resolve("No.Such.Store", typeof(IFactStore), out string error));
            Assert.That(error, Does.Contain("No.Such.Store"), "原因要指名类名，否则日志没法定位");
        }

        [Test]
        public void Resolve_RejectsTypeNotImplementingInterface()
        {
            Assert.IsNull(StoreTypeResolver.Resolve(prefsStore, typeof(INpcAffinityStore), out string error));
            Assert.That(error, Does.Contain("未实现"));
        }

        [Test]
        public void Resolve_RejectsShapesThatCannotBeNewed()
        {
            Assert.IsNull(StoreTypeResolver.Resolve(typeof(IFactStore).FullName, typeof(IFactStore), out string error),
                "接口本身不可实例化");
            Assert.That(error, Does.Contain("不是可实例化的具体类"));

            Assert.IsNull(StoreTypeResolver.Resolve(typeof(PrivateCtorStore).FullName, typeof(IFactStore), out error));
            Assert.That(error, Does.Contain("没有公开无参构造"));
        }

        [Test]
        public void Create_SwallowsConstructorThrowAndReportsReason()
        {
            // §3.1 的硬保证：装配方永远不必为第三方的 ctor 异常加 try/catch
            Assert.IsNull(StoreTypeResolver.Create<IFactStore>(typeof(ThrowingStore).FullName, out string error));
            Assert.That(error, Does.Contain("构造即炸"), "原因要带回真异常，否则日志没法定位");
        }

        [Test]
        public void Picker_OmitsKernelNullStores()
        {
            // 三个下拉都得验：Null 对象同时实现两接口，漏一个就会把"不落盘"画成可选实现
            AssertNullsAbsent(StoreTypePicker.Collect(typeof(IFactStore)));
            AssertNullsAbsent(StoreTypePicker.Collect(typeof(IConversationStore)));
            AssertNullsAbsent(StoreTypePicker.Collect(typeof(INpcAffinityStore)));

            var facts = StoreTypePicker.Collect(typeof(IFactStore));
            Assert.That(facts.ConvertAll(c => c.FullName), Does.Contain(prefsStore), "Demo 的实现要可选");
        }

        private static void AssertNullsAbsent(List<StoreTypeCandidate> candidates)
        {
            Assert.That(candidates.Exists(c => c.FullName == typeof(NullAgentStateStore).FullName), Is.False,
                "不落盘只能由空值表达，不给第二条同义路径");
            Assert.That(candidates.Exists(c => c.FullName == typeof(NullNpcAffinityStore).FullName), Is.False);
        }

        [Test]
        public void Install_FactsPreinstalledByHost_StillFillsHistoryAndAffinity()
        {
            var hostFacts = new RecordingFactStore();
            AgentStateStores.Facts = hostFacts;

            config.AgentFactsStoreType = prefsStore;
            config.AgentHistoryStoreType = prefsStore;
            config.NpcAffinityStoreType = prefsAffinityStore;
            LLMRuntimeSettings.Install();

            Assert.AreSame(hostFacts, AgentStateStores.Facts, "宿主自装的事实槽不得被覆盖");
            Assert.That(AgentStateStores.IsHistoryDefault, Is.False, "好感与轮次不该被事实槽连坐");
            Assert.That(NpcAffinityStore.IsDefault, Is.False);
        }

        [Test]
        public void Install_SameTypeInTwoSlots_SharesOneInstance()
        {
            config.AgentFactsStoreType = prefsStore;
            config.AgentHistoryStoreType = prefsStore;
            LLMRuntimeSettings.Install();

            Assert.AreSame(AgentStateStores.Facts, AgentStateStores.History, "同类型必须同实例，否则共档语义丢了");
        }

        [Test]
        public void Install_AllBlank_StaysSilentAndUnpersisted()
        {
            config.AgentFactsStoreType = "";
            config.AgentHistoryStoreType = null;
            config.NpcAffinityStoreType = "  ";
            LLMRuntimeSettings.Install();

            Assert.That(errors, Is.Empty, "出厂全空是正常态，不该记账");
            Assert.That(AgentStateStores.IsFactsDefault && AgentStateStores.IsHistoryDefault &&
                        NpcAffinityStore.IsDefault, Is.True);
        }

        [Test]
        public void Install_BrokenType_ReportsOnceAcrossActivations()
        {
            config.AgentFactsStoreType = "Ghost.Store";

            LLMRuntimeSettings.Install();
            LLMRuntimeSettings.Install();
            Assert.AreEqual(1, errors.Count, "Install 每次 AgentHost.Activate 都跑，同一条错只许报一次");

            AgentStateStores.Reset();
            LLMRuntimeSettings.Install();
            Assert.AreEqual(2, errors.Count, "Reset() 之后允许重报");
        }

        [Test]
        public void ArchiveFileName_IgnoresNamespace()
        {
            Assert.AreEqual(PrefsHelper.GetArchiveFileName(typeof(ArchiveNameProbe)),
                PrefsHelper.GetArchiveFileName(typeof(ArchiveNameProbeTwin.ArchiveNameProbe)),
                "档名只取短名哈希——迁移换命名空间不丢档，代价是跨命名空间同名会撞档");
        }

        /// <summary>只用来占位与断言"没被覆盖"，不参与读写。</summary>
        private sealed class RecordingFactStore : IFactStore
        {
            public AgentFactsBlob LoadFacts(string sessionId) => null;

            public Cysharp.Threading.Tasks.UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob,
                System.Threading.CancellationToken ct) => Cysharp.Threading.Tasks.UniTask.CompletedTask;
        }

        /// <summary>测试装配用类型：在测试程序集里，所以永远不会进下拉（picker 过滤掉测试程序集）。</summary>
        private sealed class PrivateCtorStore : IFactStore
        {
            private PrivateCtorStore() { }

            public AgentFactsBlob LoadFacts(string sessionId) => null;

            public Cysharp.Threading.Tasks.UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob,
                System.Threading.CancellationToken ct) => Cysharp.Threading.Tasks.UniTask.CompletedTask;
        }

        private sealed class ThrowingStore : IFactStore
        {
            // 显式 public：隐式默认构造在 private 嵌套类上未必是 public，别让它把用例变成假绿
            public ThrowingStore() => throw new InvalidOperationException("构造即炸");

            public AgentFactsBlob LoadFacts(string sessionId) => null;

            public Cysharp.Threading.Tasks.UniTask SaveFactsAsync(string sessionId, AgentFactsBlob blob,
                System.Threading.CancellationToken ct) => Cysharp.Threading.Tasks.UniTask.CompletedTask;
        }
    }
}
