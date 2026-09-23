/*
┌────────────────────────────────────────────┐
│　Description: AgentAnimationTools 转发行为测试
│　Remark: 用假驱动验工具层的分支，不依赖任何动画包
│ ClassName   : AgentAnimationToolsTests
└────────────────────────────────────────────┘
*/
using System.Collections.Generic;
using LLM.Runtime.Agent;
using LLM.Runtime.Tools;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    /// <summary>假驱动：把收到的参数原样记下来，供断言"工具层到底往下传了什么"</summary>
    public sealed class FakeAnimationDriver : MonoBehaviour, IAgentAnimationDriver
    {
        public readonly List<string> States = new List<string>();
        public readonly List<string> Parameters = new List<string>();
        public string LastPlayedState;
        public float LastFadeInSeconds = -999f;
        public string LastName;
        public string LastValue;
        public float LastSpeed = -999f;
        public string NextError;

        public string BackendName
        {
            get { return "Fake"; }
        }

        public void CollectStateNames(List<string> results)
        {
            for (int i = 0; i < States.Count; i++)
                results.Add(States[i]);
        }

        public void CollectParameterNames(List<string> results)
        {
            for (int i = 0; i < Parameters.Count; i++)
                results.Add(Parameters[i]);
        }

        public string DescribeCurrentState()
        {
            return "Base/Idle";
        }

        public string PlayState(string stateName, float fadeInSeconds)
        {
            LastPlayedState = stateName;
            LastFadeInSeconds = fadeInSeconds;
            return NextError;
        }

        public string SetParameter(string name, string value)
        {
            LastName = name;
            LastValue = value;
            return NextError;
        }

        public string SetSpeed(float speed)
        {
            LastSpeed = speed;
            return NextError;
        }
    }

    [TestFixture]
    public class AgentAnimationToolsTests
    {
        #region - 生命周期 -

        private GameObject host;
        private AgentAnimationTools tools;
        private FakeAnimationDriver driver;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject(nameof(AgentAnimationToolsTests));
            tools = host.AddComponent<AgentAnimationTools>();
            driver = host.AddComponent<FakeAnimationDriver>();
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null)
                Object.DestroyImmediate(host);
        }

        #endregion

        #region - 测试 -

        [Test]
        public void ListAnimationStates_JoinsStatesAndParameters()
        {
            driver.States.Add("Base/Idle");
            driver.States.Add("Base/Attack");
            driver.Parameters.Add("MoveSpeed:float");

            string text = tools.ListAnimationStates();

            Assert.That(text, Does.StartWith("states=[Base/Idle, Base/Attack]"));
            Assert.That(text, Does.Contain("MoveSpeed:float"));
        }

        [Test]
        public void ListAnimationStates_EmptyStates_SaysWhyInsteadOfJustBlankList()
        {
            // 只给参数词表、不给状态词表，正是 Animator 忘填白名单时的组合：
            // 光看 states=[] 模型会去猜名字，所以这里必须把原因说出来
            driver.Parameters.Add("MoveSpeed:float");

            Assert.That(tools.ListAnimationStates(), Does.Contain("没有可播的状态"));
        }

        [Test]
        public void GetAnimationState_ReportsWithBackendName()
        {
            Assert.That(tools.GetAnimationState(), Is.EqualTo("backend=Fake current=Base/Idle"));
        }

        [Test]
        public void SetAnimationSpeed_ForwardsValueAndSurfacesDriverError()
        {
            var ok = tools.SetAnimationSpeed(0f).GetAwaiter().GetResult();
            Assert.That(ok.Ok, Is.True);
            Assert.That(driver.LastSpeed, Is.EqualTo(0f));

            driver.NextError = "速率必须是 0 或更大的数值。";
            var bad = tools.SetAnimationSpeed(-1f).GetAwaiter().GetResult();
            Assert.That(bad.Ok, Is.False);
            Assert.That(bad.Content, Is.EqualTo("速率必须是 0 或更大的数值。"));
        }

        [Test]
        public void ListAnimationStates_WithoutDriver_ReturnsToolError()
        {
            Object.DestroyImmediate(driver);
            // 本地引用一并清掉：留着的话以后有人照着这条加断言，会读到已销毁组件上的旧字段值而空洞地绿
            driver = null;

            Assert.That(tools.ListAnimationStates(), Does.StartWith("[Tool Error]"));
        }

        [Test]
        public void PlayAnimationState_TrimsNameAndClampsNegativeFade()
        {
            var result = tools.PlayAnimationState("  Base/Attack  ", -1f).GetAwaiter().GetResult();

            Assert.That(result.Ok, Is.True);
            Assert.That(driver.LastPlayedState, Is.EqualTo("Base/Attack"));
            Assert.That(driver.LastFadeInSeconds, Is.EqualTo(0f));
        }

        [Test]
        public void PlayAnimationState_EmptyName_FailsWithoutTouchingDriver()
        {
            var result = tools.PlayAnimationState("", 0f).GetAwaiter().GetResult();

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Content, Does.Contain("状态名"));
            Assert.That(driver.LastPlayedState, Is.Null);
        }

        [Test]
        public void PlayAnimationState_DriverError_BecomesActionFailure()
        {
            driver.NextError = "状态不存在。";

            var result = tools.PlayAnimationState("Base/Nope", 0f).GetAwaiter().GetResult();

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Content, Is.EqualTo("状态不存在。"));
        }

        [Test]
        public void SetAnimationParameter_TrimsNameAndForwardsValue()
        {
            var result = tools.SetAnimationParameter("  IsMoving ", "true").GetAwaiter().GetResult();

            Assert.That(result.Ok, Is.True);
            Assert.That(driver.LastName, Is.EqualTo("IsMoving"));
            Assert.That(driver.LastValue, Is.EqualTo("true"));
        }

        #endregion
    }
}
