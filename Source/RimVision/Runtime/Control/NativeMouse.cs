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
        [StructLayout(LayoutKind.Sequential)] struct ScreenPoint { public double X, Y; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11.so.6")] static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport("libX11.so.6")] static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
            out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
        [DllImport("libX11.so.6")] static extern int XWarpPointer(IntPtr display, IntPtr source, IntPtr destination,
            int sourceX, int sourceY, uint sourceWidth, uint sourceHeight, int destinationX, int destinationY);
        [DllImport("libX11.so.6")] static extern int XFlush(IntPtr display);
        static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;
        static IntPtr _display, _root;
        static ScreenPoint _anchor;
        static bool _anchored, _unavailable;
        static int _readFrame = -1;
        static Vector2 _delta;
        internal static bool Available => !_unavailable && (Application.platform == RuntimePlatform.WindowsPlayer ||
            Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.LinuxPlayer);
        internal static bool RequiresCursorLock => !Available || Application.platform == RuntimePlatform.OSXPlayer;
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
        static bool ReadPosition(out ScreenPoint point)
        {
            point = default;
            if (_display == IntPtr.Zero)
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero) throw new InvalidOperationException("Could not open the X11 display.");
                _root = XDefaultRootWindow(_display);
            }
            if (XQueryPointer(_display, _root, out _, out _, out int x, out int y, out _, out _, out _) == 0) return false;
            point.X = x; point.Y = y;
            return true;
        }
        static bool Warp(ScreenPoint point)
        {
            XWarpPointer(_display, IntPtr.Zero, _root, 0, 0, 0, 0, (int)point.X, (int)point.Y);
            XFlush(_display);
            return true;
        }
        static void Disable(Exception error)
        {
            Release();
            _unavailable = true;
            Verse.Log.Warning("[RimVision] Native mouse input unavailable on " + Application.platform + ": " + error.Message);
        }
        internal static void Reset()
        {
            _readFrame = -1; _delta = Vector2.zero; _anchored = false;
            if (!Available || !Application.isFocused) return;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer)
                {
                    if (Centre(out var centre)) SetCursorPos(centre.X, centre.Y);
                }
                else if (Application.platform == RuntimePlatform.OSXPlayer) MacMouse.Reset();
                else _anchored = ReadPosition(out _anchor);
            }
            catch (DllNotFoundException error) { Disable(error); }
            catch (EntryPointNotFoundException error) { Disable(error); }
            catch (BadImageFormatException error) { Disable(error); }
            catch (InvalidOperationException error) { Disable(error); }
        }
        internal static void Release()
        {
            _anchored = false; _readFrame = -1; _delta = Vector2.zero;
            if (Application.platform == RuntimePlatform.OSXPlayer) MacMouse.Release();
            if (_display == IntPtr.Zero) return;
            XCloseDisplay(_display);
            _display = _root = IntPtr.Zero;
        }
        internal static bool Read(out Vector2 delta)
        {
            delta = Vector2.zero;
            if (!Available || !Application.isFocused) return false;
            if (_readFrame == Time.frameCount) { delta = _delta; return true; }
            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer)
                {
                    if (!Centre(out var centre) || !GetCursorPos(out var point)) return false;
                    delta = new Vector2(point.X - centre.X, point.Y - centre.Y);
                    SetCursorPos(centre.X, centre.Y);
                }
                else if (Application.platform == RuntimePlatform.OSXPlayer) delta = MacMouse.Read();
                else
                {
                    if (!_anchored) { Reset(); return false; }
                    if (!ReadPosition(out var point)) return false;
                    if (!Warp(_anchor)) throw new InvalidOperationException("Could not recenter the mouse cursor.");
                    delta = new Vector2((float)(point.X - _anchor.X), (float)(point.Y - _anchor.Y));
                }
                _readFrame = Time.frameCount; _delta = delta;
                return true;
            }
            catch (DllNotFoundException error) { Disable(error); }
            catch (EntryPointNotFoundException error) { Disable(error); }
            catch (BadImageFormatException error) { Disable(error); }
            catch (InvalidOperationException error) { Disable(error); }
            return false;
        }
    }
}
