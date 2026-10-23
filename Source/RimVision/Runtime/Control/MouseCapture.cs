using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly MouseCapture _mouseCapture = new MouseCapture();
        bool mouseCaptured => _mouseCapture.IsCaptured;
        int mouseCaptureFrame => _mouseCapture.CaptureFrame;

        void UpdateMouseCapture()
        {
            RefreshColonyTab();
            if (!CanCaptureMouse) { ReleaseMouse(); return; }
            _mouseCapture.Capture();
        }

        public void ReleaseMouse()
        {
            if (!_mouseCapture.Release()) return;
            _viewInput.ResetMousePosition();
        }
    }

    internal sealed class MouseCapture
    {
        bool _captured;
        CursorLockMode _previousCursorLock;
        bool _previousCursorVisible;
        int _captureFrame;
        Texture2D _hiddenCursor;

        internal bool IsCaptured => _captured;
        internal int CaptureFrame => _captureFrame;

        internal void Capture()
        {
            if (!_captured)
            {
                _previousCursorLock = Cursor.lockState;
                _previousCursorVisible = Cursor.visible;
                _captured = true;
                _captureFrame = Time.frameCount; NativeMouse.Reset();
                if (_hiddenCursor == null)
                {
                    _hiddenCursor = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
                    _hiddenCursor.SetPixel(0, 0, Color.clear);
                    _hiddenCursor.Apply();
                }
                // Also hide Unity's emulated custom cursor in the editor Game view.
                Cursor.SetCursor(_hiddenCursor, Vector2.zero, CursorMode.ForceSoftware);
            }
            var desiredLock = NativeMouse.Available ? CursorLockMode.None : CursorLockMode.Locked; if (Cursor.lockState != desiredLock)
            {
                Cursor.lockState = desiredLock;
                // A native lock reset is not a new session. Do not discard look every frame.
            }
            if (Cursor.visible) Cursor.visible = false;
        }

        internal bool Release()
        {
            if (!_captured) return false;
            _captured = false;
            if (Prefs.CustomCursorEnabled) CustomCursor.Activate();
            else CustomCursor.Deactivate();
            Cursor.lockState = _previousCursorLock;
            Cursor.visible = _previousCursorVisible;
            return true;
        }

        internal void Dispose()
        {
            if (_hiddenCursor != null) { UnityEngine.Object.Destroy(_hiddenCursor); _hiddenCursor = null; }
        }
    }
}
