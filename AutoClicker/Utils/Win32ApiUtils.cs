using System;
using System.Runtime.InteropServices;

namespace AutoClicker.Utils
{
    public static class Win32ApiUtils
    {
        [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
        internal static extern bool SetCursorPosition(int x, int y);

        [DllImport("user32.dll", EntryPoint = "mouse_event")]
        internal static extern void ExecuteMouseEvent(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        [DllImport("user32.dll", EntryPoint = "RegisterHotKey")]
        internal static extern bool RegisterHotkey(nint hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", EntryPoint = "UnregisterHotKey")]
        internal static extern bool DeregisterHotkey(nint hWnd, int id);

        // ===== SendInput: the modern, reliable way to inject a click (games see it better than mouse_event) =====

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;      // 0 = INPUT_MOUSE
            public MOUSEINPUT mi;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        private const uint GA_ROOT = 2;

        // Inject one mouse button event (a MOUSEEVENTF_* flag) at the current cursor position.
        internal static void SendMouseFlag(uint dwFlags)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = 0; // INPUT_MOUSE
            inputs[0].mi.dwFlags = dwFlags;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        // Root (top-level) window under the given screen point — used to avoid clicking our own window.
        internal static IntPtr GetRootWindowAt(int x, int y)
        {
            IntPtr h = WindowFromPoint(new POINT { X = x, Y = y });
            if (h == IntPtr.Zero)
                return IntPtr.Zero;
            return GetAncestor(h, GA_ROOT);
        }
    }
}
