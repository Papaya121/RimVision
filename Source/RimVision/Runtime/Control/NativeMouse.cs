using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Recovery.Rim3D
{
    internal static class NativeMouse
    {
        [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;
        internal static bool Available => Application.platform == RuntimePlatform.WindowsPlayer;
        static bool Centre(out Point centre)
        {
            centre = default;
            if (!Available || !Application.isFocused) return false;
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            GetWindowThreadProcessId(window, out var process);
            if (process != ProcessId || !GetClientRect(window, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
            centre.X = (rect.Left + rect.Right) / 2;
            centre.Y = (rect.Top + rect.Bottom) / 2;
            return ClientToScreen(window, ref centre);
        }
        internal static void Reset() { if (Centre(out var centre)) SetCursorPos(centre.X, centre.Y); }
        internal static bool Read(out Vector2 delta)
        {
            delta = Vector2.zero;
            if (!Centre(out var centre) || !GetCursorPos(out var point)) return false;
            delta = new Vector2(point.X - centre.X, point.Y - centre.Y);
            SetCursorPos(centre.X, centre.Y);
            return true;
        }
    }
}
