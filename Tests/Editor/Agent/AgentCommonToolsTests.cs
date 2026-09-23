/*
┌────────────────────────────┐
│　Description: Agent 通用工具测试
│　Remark: 注册、层级扫描与契约行为
│　ClassName: AgentCommonToolsTests
└────────────────────────────┘
*/

using System;
using System.Globalization;
using System.Reflection;
using LLM.Runtime;
using LLM.Runtime.Agent;
using LLM.Runtime.Tools;
using NUnit.Framework;
using UnityEngine;

namespace LLM.Tests.Editor.Agent
{
    [TestFixture]
    public class AgentCommonToolsTests
    {
        #region - 字段 -

        private GameObject gameObject;

        #endregion

        #region - 生命周期 -

        [SetUp]
        public void SetUp()
        {
            gameObject = new GameObject("AgentCommonToolsTest");
        }

        [TearDown]
        public void TearDown()
        {
            if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
        }

        #endregion

        #region - 测试 -

        [Test]
        public void CurrentTime_IsLocalDateTimeWithChineseWeekday()
        {
            string result = AgentTimeTools.GetCurrentTime();
            bool parsed = DateTime.TryParseExact(result.Substring(0, 19),
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime actual);

            Assert.IsTrue(parsed);
            Assert.LessOrEqual(Math.Abs((DateTime.Now - actual).TotalSeconds), 2d);
            StringAssert.EndsWith(Cysharp.Text.ZString.Concat("星期", GetChineseWeekday(actual.DayOfWeek)),
                result);
        }

        [Test]
        public void StaticAndMemberTools_RegisterAndScan()
        {
            AgentToolRegistry.Register(typeof(AgentTimeTools).Assembly);

            Assert.IsTrue(AgentToolRegistry.TryGet("get_current_time", out var staticTool));
            Assert.AreEqual(typeof(AgentTimeTools), staticTool.Method.DeclaringType);

            var transformTools = gameObject.AddComponent<AgentTransformTools>();
            var navigationTools = gameObject.AddComponent<AgentNavigationTools>();
            var tools = AgentToolRegistry.ScanHierarchyTools(gameObject);
            var actions = AgentActionRegistry.ScanHierarchyTools(gameObject);

            Assert.IsTrue(HasTool(tools, "get_agent_transform", transformTools));
            Assert.IsTrue(HasTool(tools, "get_navigation_status", navigationTools));
            Assert.IsTrue(HasAction(actions, "move_to", navigationTools));
            Assert.IsTrue(HasAction(actions, "move_to_player", navigationTools));
            Assert.IsTrue(HasAction(actions, "stop_navigation", navigationTools));
        }

        [Test]
        public void MoveToPlayer_FailsLoudly_WithoutPlayerOrNavMesh()
        {
            // 测试场景里没有烘焙 NavMesh，两条前置守卫必然命中：不许抛异常，只能回失败原因
            var navigationTools = gameObject.AddComponent<AgentNavigationTools>();

            var result = navigationTools.MoveToPlayer(null).GetAwaiter().GetResult();

            Assert.IsFalse(result.Ok);
            Assert.IsNotEmpty(result.Content, "失败必须带原因，模型才知道该改什么");
        }

        [Test]
        public void NavigationTool_RequiresNavMeshAgentAndMoveIsRepeatableSideEffect()
        {
            Assert.IsNotNull(typeof(AgentNavigationTools).GetCustomAttribute<RequireComponent>());

            var method = typeof(AgentNavigationTools).GetMethod("MoveTo",
                BindingFlags.Public | BindingFlags.Instance);
            var attribute = method.GetCustomAttribute<AgentActionAttribute>();

            Assert.IsNotNull(attribute);
            Assert.IsFalse(attribute.Idempotent);
            Assert.IsTrue(attribute.Repeatable);
            Assert.IsTrue(attribute.LongRunning);
            Assert.AreEqual(typeof(AgentActionContext), method.GetParameters()[0].ParameterType);
            Assert.AreEqual(typeof(Cysharp.Threading.Tasks.UniTask<AgentActionResult>), method.ReturnType);

            var navigationTools = gameObject.AddComponent<AgentNavigationTools>();
            var result = navigationTools.MoveTo(null, float.NaN, 0f, 0f).GetAwaiter().GetResult();
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("坐标无效", result.Content);

            result = navigationTools.MoveTo(null, 0f, 0f, 0f).GetAwaiter().GetResult();
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("NavMesh", result.Content);
        }

        #endregion

        #region - 私有方法 -

        private static bool HasTool(System.Collections.Generic.List<AgentToolRegistry.RegisteredTool> tools,
            string name, object target)
        {
            for (int i = 0; i < tools.Count; i++)
                if (tools[i].Name == name && ReferenceEquals(tools[i].Target, target)) return true;
            return false;
        }

        private static bool HasAction(System.Collections.Generic.List<AgentActionDef> actions,
            string id, object target)
        {
            for (int i = 0; i < actions.Count; i++)
                if (actions[i].Id == id && ReferenceEquals(actions[i].Target, target)) return true;
            return false;
        }

        private static string GetChineseWeekday(DayOfWeek day)
        {
            return day switch
            {
                DayOfWeek.Monday => "一",
                DayOfWeek.Tuesday => "二",
                DayOfWeek.Wednesday => "三",
                DayOfWeek.Thursday => "四",
                DayOfWeek.Friday => "五",
                DayOfWeek.Saturday => "六",
                _ => "日"
            };
        }

        #endregion
    }
}
