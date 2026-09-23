/*
┌────────────────────────────┐
│　Description: Windows 测试窗口绑定与输入
│　Remark: 只接受 runner 首次绑定的窗口
│　ClassName: RuntimeTestWindowBinding
└────────────────────────────┘
*/

using System;
using System.Threading;
using Cysharp.Text;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System.Runtime.InteropServices;
using NativeMethods = LLM.Runtime.RuntimeTesting.RuntimeTestWindowInput.NativeMethods;
#endif

namespace LLM.Runtime.RuntimeTesting
{
    public sealed class RuntimeTestWindowBinding
    {
        public int ProcessId { get; private set; }
        public IntPtr WindowHandle { get; private set; }
        public bool IsBound => ProcessId > 0 && WindowHandle != IntPtr.Zero;

        public bool TryBind(int processId, IntPtr windowHandle)
        {
            if (processId <= 0 || windowHandle == IntPtr.Zero) return false;
            if (IsBound) return ProcessId == processId && WindowHandle == windowHandle;

            ProcessId = processId;
            WindowHandle = windowHandle;
            return true;
        }

        public bool TryGetInfo(out RuntimeTestWindowInfo info, out string error)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!TryValidate(out var clientWidth, out var clientHeight, out error))
            {
                info = default;
                return false;
            }

            info = new RuntimeTestWindowInfo(ProcessId, WindowHandle, clientWidth, clientHeight,
                RuntimeTestWindowInput.NativeMethods.GetForegroundWindow() == WindowHandle, false);
            return true;
#else
            info = new RuntimeTestWindowInfo(ProcessId, WindowHandle, 0, 0, false, false);
            error = "当前平台不支持 Windows 窗口输入";
            return false;
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        internal bool TryValidate(out int clientWidth, out int clientHeight, out string error)
        {
            clientWidth = 0;
            clientHeight = 0;
            error = null;

            if (!IsBound || !RuntimeTestWindowInput.NativeMethods.IsWindow(WindowHandle))
                return Fail("绑定窗口不存在", out error);
            if (RuntimeTestWindowInput.NativeMethods.IsIconic(WindowHandle) ||
                !RuntimeTestWindowInput.NativeMethods.IsWindowVisible(WindowHandle))
                return Fail("绑定窗口不可见或已最小化", out error);

            RuntimeTestWindowInput.NativeMethods.GetWindowThreadProcessId(WindowHandle, out var ownerProcessId);
            if (ownerProcessId != ProcessId)
                return Fail("绑定窗口已不属于 runner 进程", out error);

            if (!RuntimeTestWindowInput.NativeMethods.GetClientRect(WindowHandle, out var rect))
                return Fail("无法读取游戏客户区", out error);
            clientWidth = rect.Right - rect.Left;
            clientHeight = rect.Bottom - rect.Top;
            if (clientWidth <= 0 || clientHeight <= 0)
                return Fail("游戏客户区为空", out error);

            return true;
        }

        internal bool TryGetClientPoint(float x01, float y01,
            out RuntimeTestWindowInput.NativeMethods.Point point, out string error)
        {
            point = default;
            if (x01 < 0f || x01 > 1f || y01 < 0f || y01 > 1f)
            {
                error = "坐标必须在 0..1 范围内";
                return false;
            }

            if (!TryValidate(out var width, out var height, out error)) return false;
            point.X = (int)(x01 * (width - 1));
            point.Y = (int)(y01 * (height - 1));
            if (!RuntimeTestWindowInput.NativeMethods.ClientToScreen(WindowHandle, ref point))
            {
                error = "无法换算游戏客户区坐标";
                return false;
            }

            if (RuntimeTestWindowInput.NativeMethods.GetForegroundWindow() != WindowHandle)
            {
                error = "游戏窗口未处于前台，拒绝发送输入";
                return false;
            }

            return true;
        }

        private bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }

#endif
    }

    public readonly struct RuntimeTestWindowInfo
    {
        public readonly int ProcessId;
        public readonly IntPtr WindowHandle;
        public readonly int ClientWidth;
        public readonly int ClientHeight;
        public readonly bool IsForeground;
        public readonly bool IsMinimized;

        public RuntimeTestWindowInfo(int processId, IntPtr windowHandle, int clientWidth, int clientHeight,
            bool isForeground, bool isMinimized)
        {
            ProcessId = processId;
            WindowHandle = windowHandle;
            ClientWidth = clientWidth;
            ClientHeight = clientHeight;
            IsForeground = isForeground;
            IsMinimized = isMinimized;
        }
    }

    public sealed class RuntimeTestWindowInput
    {
        private readonly RuntimeTestWindowBinding binding;

        public RuntimeTestWindowInput(RuntimeTestWindowBinding binding)
        {
            this.binding = binding;
        }

        public bool Focus(out string error)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!binding.TryValidate(out _, out _, out error)) return false;
            NativeMethods.ShowWindow(binding.WindowHandle, NativeMethods.SW_RESTORE);
            if (!NativeMethods.SetForegroundWindow(binding.WindowHandle))
            {
                error = "无法聚焦游戏窗口";
                return false;
            }

            if (NativeMethods.GetForegroundWindow() != binding.WindowHandle)
            {
                error = "游戏窗口聚焦未生效";
                return false;
            }

            return true;
#else
            error = "当前平台不支持 Windows 窗口输入";
            return false;
#endif
        }

        public bool Click(float x01, float y01, string button, CancellationToken cancellationToken, out string error)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (cancellationToken.IsCancellationRequested) return Cancelled(out error);
            if (!binding.TryGetClientPoint(x01, y01, out var point, out error)) return false;
            if (button != "left" && button != "right")
            {
                error = "只允许 left 或 right 鼠标键";
                return false;
            }

            if (!MoveCursorTo(point, out error)) return false;
            uint down = button == "left" ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_RIGHTDOWN;
            uint up = button == "left" ? NativeMethods.MOUSEEVENTF_LEFTUP : NativeMethods.MOUSEEVENTF_RIGHTUP;
            if (!SendMouseFlags(down, out error)) return false;
            if (cancellationToken.IsCancellationRequested)
            {
                SendMouseFlags(up, out _);
                return Cancelled(out error);
            }
            return SendMouseFlags(up, out error);
#else
            error = "当前平台不支持 Windows 窗口输入";
            return false;
#endif
        }

        public bool TypeText(string text, CancellationToken cancellationToken, out string error)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!binding.TryValidate(out _, out _, out error)) return false;
            if (NativeMethods.GetForegroundWindow() != binding.WindowHandle)
            {
                error = "游戏窗口未处于前台，拒绝发送输入";
                return false;
            }

            for (int i = 0; i < (text?.Length ?? 0); i++)
            {
                if (cancellationToken.IsCancellationRequested) return Cancelled(out error);
                if (!SendUnicodePair(text[i], out error)) return false;
            }

            return true;
#else
            error = "当前平台不支持 Windows 窗口输入";
            return false;
#endif
        }

        public bool PressKey(string key, int modifiers, CancellationToken cancellationToken, out string error)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!binding.TryValidate(out _, out _, out error)) return false;
            if (NativeMethods.GetForegroundWindow() != binding.WindowHandle)
            {
                error = "游戏窗口未处于前台，拒绝发送输入";
                return false;
            }

            if (!TryGetVirtualKey(key, out var virtualKey) || (modifiers & ~7) != 0)
            {
                error = "按键或修饰键不在白名单内";
                return false;
            }
            if ((modifiers & 4) != 0 && (key == "F4" || key == "f4"))
            {
                error = "禁止发送 Alt+F4";
                return false;
            }
            if ((modifiers & 4) != 0 &&
                (key == "Tab" || key == "Escape" || key.StartsWith("F", StringComparison.Ordinal)))
            {
                error = "禁止可能切出游戏窗口的 Alt 组合键";
                return false;
            }
            if ((modifiers & 2) != 0 && key == "Escape")
            {
                error = "禁止可能打开系统界面的 Ctrl+Escape";
                return false;
            }

            if (cancellationToken.IsCancellationRequested) return Cancelled(out error);

            bool modifiersAttempted = false;
            try
            {
                modifiersAttempted = true;
                if (!SendModifierKeys(modifiers, true, out error)) return false;
                if (!SendVirtualKeyPair(virtualKey, out error)) return false;
                error = null;
                return true;
            }
            finally
            {
                if (modifiersAttempted) SendModifierKeys(modifiers, false, out _);
            }
#else
            error = "当前平台不支持 Windows 窗口输入";
            return false;
#endif
        }

        public void ReleaseInputState()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!binding.TryValidate(out _, out _, out _)) return;
            if (NativeMethods.GetForegroundWindow() != binding.WindowHandle) return;
            SendMouseFlags(NativeMethods.MOUSEEVENTF_LEFTUP, out _);
            SendMouseFlags(NativeMethods.MOUSEEVENTF_RIGHTUP, out _);
            SendModifierKeys(7, false, out _);
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private bool MoveCursorTo(NativeMethods.Point target, out string error)
        {
            if (!NativeMethods.SetCursorPos(target.X, target.Y))
            {
                error = "无法移动鼠标到游戏客户区坐标";
                return false;
            }

            error = null;
            return true;
        }

        private static bool SendMouseFlags(uint flags, out string error)
        {
            var inputs = new[] { NativeMethods.Input.Mouse(flags) };
            if (NativeMethods.SendInput(1, inputs, NativeMethods.Input.Size) != 1)
            {
                error = "发送鼠标输入失败";
                return false;
            }

            error = null;
            return true;
        }

        private static bool SendUnicode(char character, bool keyUp, out string error)
        {
            var input = NativeMethods.Input.Unicode(character, keyUp);
            if (NativeMethods.SendInput(1, new[] { input }, NativeMethods.Input.Size) != 1)
            {
                error = "发送文本输入失败";
                return false;
            }

            error = null;
            return true;
        }

        private static bool SendUnicodePair(char character, out string error)
        {
            if (!SendUnicode(character, false, out error)) return false;
            if (SendUnicode(character, true, out error)) return true;

            // keyup 失败时尽力补发一次，避免取消/异常留下按键状态。
            SendUnicode(character, true, out _);
            return false;
        }

        private static bool SendVirtualKey(ushort virtualKey, bool keyUp, out string error)
        {
            var input = NativeMethods.Input.VirtualKey(virtualKey, keyUp);
            if (NativeMethods.SendInput(1, new[] { input }, NativeMethods.Input.Size) != 1)
            {
                error = "发送键盘输入失败";
                return false;
            }

            error = null;
            return true;
        }

        private static bool SendVirtualKeyPair(ushort virtualKey, out string error)
        {
            if (!SendVirtualKey(virtualKey, false, out error)) return false;
            if (SendVirtualKey(virtualKey, true, out error)) return true;

            SendVirtualKey(virtualKey, true, out _);
            return false;
        }

        private static bool SendModifierKeys(int modifiers, bool down, out string error)
        {
            if ((modifiers & 1) != 0 && !SendVirtualKey(0x10, !down, out error)) return false;
            if ((modifiers & 2) != 0 && !SendVirtualKey(0x11, !down, out error)) return false;
            if ((modifiers & 4) != 0 && !SendVirtualKey(0x12, !down, out error)) return false;
            error = null;
            return true;
        }

        private static bool TryGetVirtualKey(string key, out ushort virtualKey)
        {
            virtualKey = 0;
            if (string.IsNullOrEmpty(key)) return false;
            if (key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9')))
            {
                virtualKey = key[0];
                return true;
            }

            switch (key)
            {
                case "Enter": virtualKey = 0x0D; return true;
                case "Escape": virtualKey = 0x1B; return true;
                case "Tab": virtualKey = 0x09; return true;
                case "Space": virtualKey = 0x20; return true;
                case "Backspace": virtualKey = 0x08; return true;
                case "Left": virtualKey = 0x25; return true;
                case "Up": virtualKey = 0x26; return true;
                case "Right": virtualKey = 0x27; return true;
                case "Down": virtualKey = 0x28; return true;
                case "F1": virtualKey = 0x70; return true;
                case "F2": virtualKey = 0x71; return true;
                case "F3": virtualKey = 0x72; return true;
                case "F4": virtualKey = 0x73; return true;
                default: return false;
            }
        }

        private static bool Cancelled(out string error)
        {
            error = "输入已取消";
            return false;
        }

        internal static class NativeMethods
        {
            internal const int SW_RESTORE = 9;
            internal const uint MOUSEEVENTF_MOVE = 0x0001;
            internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
            internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
            internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
            internal const uint KEYEVENTF_KEYUP = 0x0002;
            internal const uint KEYEVENTF_UNICODE = 0x0004;

            [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
            [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
            [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
            [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
            [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int command);
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
            [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
            [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hWnd, ref Point point);
            [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] inputs, int size);

            [StructLayout(LayoutKind.Sequential)] internal struct Rect
            {
                internal int Left;
                internal int Top;
                internal int Right;
                internal int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)] internal struct Point
            {
                internal int X;
                internal int Y;
            }

            [StructLayout(LayoutKind.Explicit, Size = 40)] internal struct Input
            {
                [FieldOffset(0)] internal int Type;
                [FieldOffset(8)] internal MouseInput MouseData;
                [FieldOffset(8)] internal KeyInput KeyData;

                internal static int Size => IntPtr.Size == 8 ? 40 : 28;

                internal static Input Mouse(uint flags)
                {
                    return new Input
                    {
                        Type = 0,
                        MouseData = new MouseInput { Flags = flags }
                    };
                }

                internal static Input MouseMove(int x, int y)
                {
                    return new Input
                    {
                        Type = 0,
                        MouseData = new MouseInput { X = x, Y = y, Flags = MOUSEEVENTF_MOVE }
                    };
                }

                internal static Input Unicode(char character, bool keyUp)
                {
                    return new Input
                    {
                        Type = 1,
                        KeyData = new KeyInput
                        {
                            Scan = character,
                            Flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0)
                        }
                    };
                }

                internal static Input VirtualKey(ushort key, bool keyUp)
                {
                    return new Input
                    {
                        Type = 1,
                        KeyData = new KeyInput { VirtualKey = key, Flags = keyUp ? KEYEVENTF_KEYUP : 0 }
                    };
                }
            }

            [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
            {
                internal int X;
                internal int Y;
                internal uint MouseData;
                internal uint Flags;
                internal uint Time;
                internal IntPtr ExtraInfo;
            }

            [StructLayout(LayoutKind.Sequential)] internal struct KeyInput
            {
                internal ushort VirtualKey;
                internal ushort Scan;
                internal uint Flags;
                internal uint Time;
                internal IntPtr ExtraInfo;
            }
        }
#endif
    }
}
