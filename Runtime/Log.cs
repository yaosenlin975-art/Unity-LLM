/*
┌────────────────────────────────────────────┐
│　Description: 打印辅助
│　Remark: 打印系列方法在非编辑器构建中受 LLM_LOG  │
│　　　　　 宏控制：默认未定义时所有调用点连同参数   │
│　　　　　 求值一起被编译消除（零日志开销）；开发期 │
│　　　　　 在 Scripting Define Symbols 加 LLM_LOG │
│　　　　　 即恢复输出。编辑器构建不受影响，始终输出。│
│　　　　　 只保留 LLM 实际用到的 (title, message,  │
│　　　　　 context) 三档，不抄框架 Log 的全部重载。 │
└────────────────────────────────────────────┘
*/

using System.Runtime.CompilerServices;
using UnityEngine;

namespace LLM.Runtime
{
    public static class Log
    {
        // 控制台双击读的是堆栈字符串里第一个 " (at 路径:行号)"，那个串由引擎自己抓，消息里改不动。
        // HideInCallstack 在部分编辑器上藏不掉本文件的帧，所以三个方法一律要求内联：
        // 内联之后托管栈里没有本文件这一帧，第一个 (at ...) 自然就是调用处。
        // 每个方法必须是"直接调 UnityEngine.Debug.Log* 的单表达式"，多套一层就白内联了。
        [HideInCallstack]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if !UNITY_EDITOR
        [System.Diagnostics.Conditional("LLM_LOG")]
#endif
        public static void Debug(string title, object message, Object context = null, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0) => UnityEngine.Debug.Log($"<b>[{title}]</b> {message}{CallerTag(callerFile, callerLine)}", context);

        [HideInCallstack]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if !UNITY_EDITOR
        [System.Diagnostics.Conditional("LLM_LOG")]
#endif
        public static void Warning(string title, object message, Object context = null, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0) => UnityEngine.Debug.LogWarning($"<b>[{title}]</b> {message}{CallerTag(callerFile, callerLine)}", context);

        [HideInCallstack]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if !UNITY_EDITOR
        [System.Diagnostics.Conditional("LLM_LOG")]
#endif
        public static void Error(string title, object message, Object context = null, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0) => UnityEngine.Debug.LogError($"<b>[{title}]</b> {message}{CallerTag(callerFile, callerLine)}", context);

        // 消息尾部也带一份坐标：日志文件里可定位，堆栈里藏不掉时它至少是可读的
        private static string CallerTag(string callerFile, int callerLine)
        {
            if (string.IsNullOrEmpty(callerFile) || callerLine <= 0)
                return string.Empty;

            string path = callerFile.Replace('\\', '/');
            int index = path.IndexOf("Assets/", System.StringComparison.Ordinal);
            if (index < 0)
                index = path.IndexOf("Packages/", System.StringComparison.Ordinal);
            if (index < 0)
                index = path.LastIndexOf('/') + 1;

            return $" (at {path.Substring(index)}:{callerLine})";
        }
    }
}
