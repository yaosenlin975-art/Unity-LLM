/*
 * ┌────────────────────────────────────────────┐
 * │ Description : Runtime 测试成员工具门控组件    │
 * │ Remark      : 只在 Editor/Development 注册    │
 * │ ClassName   : RuntimeTestTools               │
 * └────────────────────────────────────────────┘
 */

using System;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Agent;
using UnityEngine;
using UnityEngine.Profiling;

namespace LLM.Runtime.RuntimeTesting
{
    public sealed class RuntimeTestTools : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private readonly RuntimeTestCommandRegistry commandRegistry = new();
        private readonly RuntimeTestWindowBinding windowBinding = new();
        private RuntimeTestWindowInput windowInput;
        private RuntimePerformanceCapture performanceCapture;
        private bool runnerActivated;
        private RuntimePerformanceSummary lastSummary;

        public bool IsRunnerActivated => runnerActivated;

        private void Update()
        {
            if (runnerActivated && performanceCapture != null && performanceCapture.IsCapturing)
                performanceCapture.AddSample(Time.unscaledDeltaTime * 1000f);
        }

        private void OnDisable()
        {
            StopPerformanceCapture();
            windowInput?.ReleaseInputState();
            commandRegistry.Clear();
            runnerActivated = false;
        }

        public bool ActivateForRunner(int processId, IntPtr windowHandle)
        {
            if (runnerActivated) return false;
            if (!windowBinding.TryBind(processId, windowHandle)) return false;

            windowInput = new RuntimeTestWindowInput(windowBinding);
            performanceCapture = new RuntimePerformanceCapture();
            runnerActivated = true;
            return true;
        }

        public bool RegisterCommandProvider(MonoBehaviour provider)
        {
            if (!runnerActivated || provider == null) return false;
            commandRegistry.Register(provider);
            return true;
        }

        [AgentTool("list_test_commands", "列出 runner 注册的 Runtime 测试命令")]
        public string ListTestCommands()
        {
            if (!runnerActivated) return "[Test Error] 测试 runner 未激活";
            return commandRegistry.DescribeCommands();
        }

        [AgentAction("invoke_test_command", "调用已显式注册的 Runtime 测试命令", Idempotent = false)]
        public UniTask<AgentActionResult> InvokeTestCommand(AgentActionContext context, string commandId, string argsJson)
        {
            if (!runnerActivated)
                return Failure("测试 runner 未激活");
            if (!commandRegistry.TryInvoke(commandId, argsJson, out var result))
                return Failure(string.IsNullOrEmpty(result)
                    ? ZString.Concat("未找到测试命令：", commandId)
                    : result);
            return Success(result);
        }

        [AgentTool("get_test_window", "读取 runner 绑定的游戏窗口状态")]
        public string GetTestWindow()
        {
            if (!runnerActivated) return "[Test Error] 测试 runner 未激活";
            if (!windowBinding.TryGetInfo(out var info, out var error))
                return ZString.Concat("[Test Error] ", error);

            return ZString.Format(
                "{{\"processId\":{0},\"windowHandle\":\"{1}\",\"clientWidth\":{2},\"clientHeight\":{3},\"foreground\":{4},\"minimized\":{5}}}",
                info.ProcessId, info.WindowHandle, info.ClientWidth, info.ClientHeight,
                info.IsForeground ? "true" : "false", info.IsMinimized ? "true" : "false");
        }

        [AgentAction("focus_test_window", "聚焦 runner 绑定的游戏窗口", Idempotent = true)]
        public UniTask<AgentActionResult> FocusTestWindow(AgentActionContext context)
        {
            if (!runnerActivated) return Failure("测试 runner 未激活");
            return windowInput.Focus(out var error) ? Success("窗口已聚焦") : Failure(error);
        }

        [AgentAction("click_game_window", "在绑定游戏窗口客户区内点击", Idempotent = false)]
        public UniTask<AgentActionResult> ClickGameWindow(AgentActionContext context, float x01, float y01, string button)
        {
            if (!runnerActivated) return Failure("测试 runner 未激活");
            return windowInput.Click(x01, y01, button, CancellationToken.None,
                out var error) ? Success("点击已发送") : Failure(error);
        }

        [AgentAction("type_game_text", "向已聚焦的绑定游戏窗口输入文本", Idempotent = false)]
        public UniTask<AgentActionResult> TypeGameText(AgentActionContext context, string text)
        {
            if (!runnerActivated) return Failure("测试 runner 未激活");
            if (text != null && text.Length > 256) return Failure("单次文本输入最多 256 个字符");
            return windowInput.TypeText(text, CancellationToken.None,
                out var error) ? Success("文本已发送") : Failure(error);
        }

        [AgentAction("press_game_key", "向已聚焦的绑定游戏窗口发送白名单按键", Idempotent = false)]
        public UniTask<AgentActionResult> PressGameKey(AgentActionContext context, string key, int modifiers)
        {
            if (!runnerActivated) return Failure("测试 runner 未激活");
            return windowInput.PressKey(key, modifiers, CancellationToken.None,
                out var error) ? Success("按键已发送") : Failure(error);
        }

        [AgentTool("get_performance_snapshot", "读取上一完整帧的实时性能快照")]
        public string GetPerformanceSnapshot()
        {
            if (!runnerActivated) return "[Test Error] 测试 runner 未激活";

            float frameMilliseconds = Time.unscaledDeltaTime * 1000f;
            float fps = frameMilliseconds > 0f ? 1000f / frameMilliseconds : 0f;
            return ZString.Format(
                "{{\"available\":true,\"frameMilliseconds\":{0:0.###},\"fps\":{1:0.###},\"managedMemoryBytes\":{2},\"totalAllocatedMemoryBytes\":{3}}}",
                frameMilliseconds, fps, GC.GetTotalMemory(false), Profiler.GetTotalAllocatedMemoryLong());
        }

        [AgentAction("start_performance_capture", "开始按帧采样性能", Idempotent = true)]
        public UniTask<AgentActionResult> StartPerformanceCapture(AgentActionContext context)
        {
            if (!runnerActivated) return Failure("测试 runner 未激活");
            if (performanceCapture.IsCapturing) return Failure("性能采样已经开始");
            performanceCapture.Start();
            return Success("性能采样已开始");
        }

        [AgentTool("get_performance_capture_status", "读取当前性能采样状态")]
        public string GetPerformanceCaptureStatus()
        {
            if (!runnerActivated) return "[Test Error] 测试 runner 未激活";
            var count = performanceCapture.IsCapturing ? performanceCapture.Count : lastSummary.Count;
            return ZString.Format("{{\"capturing\":{0},\"count\":{1},\"available\":{2}}}",
                performanceCapture.IsCapturing ? "true" : "false", count,
                lastSummary.Available ? "true" : "false");
        }

        [AgentAction("stop_performance_capture", "停止采样并返回性能摘要", Idempotent = true)]
        public UniTask<AgentActionResult> StopPerformanceCaptureAction(AgentActionContext context)
        {
            if (!runnerActivated) return Failure("测试 runner 未激活");
            if (!performanceCapture.IsCapturing) return Failure("当前没有活动的性能采样窗口");
            lastSummary = StopPerformanceCapture();
            return Success(FormatSummary(lastSummary));
        }

        private RuntimePerformanceSummary StopPerformanceCapture()
        {
            if (performanceCapture == null || !performanceCapture.IsCapturing)
                return lastSummary;
            return performanceCapture.Stop();
        }

        private static string FormatSummary(RuntimePerformanceSummary summary)
        {
            if (!summary.Available)
                return "{\"available\":false,\"count\":0,\"average\":null,\"p95\":null,\"p99\":null,\"max\":null}";

            return ZString.Format(
                "{{\"available\":true,\"count\":{0},\"average\":{1:0.###},\"p95\":{2:0.###},\"p99\":{3:0.###},\"max\":{4:0.###}}}",
                summary.Count, summary.Average, summary.P95, summary.P99, summary.Max);
        }

        private static UniTask<AgentActionResult> Success(string content)
        {
            return new UniTask<AgentActionResult>(AgentActionResult.Success(content));
        }

        private static UniTask<AgentActionResult> Failure(string reason)
        {
            return new UniTask<AgentActionResult>(AgentActionResult.Failure(reason));
        }
#endif
    }
}
