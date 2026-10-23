using System;
using UnityEngine;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly ViewInput _viewInput = new ViewInput();

        public void HandleMouseEvent(Event current)
        {
            if (_viewInput.HandleMouseEvent(this, current)) PositionCamera();
        }

        sealed partial class ViewInput
        {
            Vector3 _previousMouse;
            int _lastAxisLookFrame = -10;
            int _lastVirtualLookFrame = -10;
            bool _mouseAxesAvailable = true;

            internal bool SneakHeld => ViewBindings.Held(ViewAction.Sneak);
            internal bool SprintHeld => ViewBindings.Held(ViewAction.Sprint);

            internal bool TogglePlansRequested(bool typing) => !typing && ViewBindings.Pressed(ViewAction.Plans);

            internal bool TogglePerspectiveRequested(bool typing) => !typing && ViewBindings.Pressed(ViewAction.Perspective);

            internal bool ToggleAccelerationRequested(MapView view, bool typing) =>
                view.FirstPerson && !typing && !view.constructionMenu && !view.actionMenu && !view.statusMenu && ViewBindings.Pressed(ViewAction.Acceleration);

            internal bool NextPawnRequested(MapView view, bool typing) =>
                !typing && !view.constructionMenu && !view.actionMenu && !view.statusMenu && ViewBindings.Pressed(ViewAction.NextPawn);

            internal void ReadMovement(bool typing, out float dx, out float dz)
            {
                dx = typing ? 0 : Mathf.Clamp((ViewBindings.Held(ViewAction.Right) ? 1 : 0) - (ViewBindings.Held(ViewAction.Left) ? 1 : 0) + ControlAxis(API.RimVisionControlAxis.MoveX), -1, 1);
                dz = typing ? 0 : Mathf.Clamp((ViewBindings.Held(ViewAction.Forward) ? 1 : 0) - (ViewBindings.Held(ViewAction.Backward) ? 1 : 0) + ControlAxis(API.RimVisionControlAxis.MoveZ), -1, 1);
            }

            internal void ResetMousePosition() => _previousMouse = Input.mousePosition;

            static float LookSensitivity(MapView view)
            {
                var settings = Rim3DSettings.Current;
                float zoomFactor = view.FirstPerson && view.camera != null
                    ? Mathf.Clamp01(Mathf.Tan(view.camera.fieldOfView * .5f * Mathf.Deg2Rad) /
                        Mathf.Tan(settings.FieldOfView * .5f * Mathf.Deg2Rad)) : 1;
                return settings.MouseSensitivity * zoomFactor * (view._viewCamera.HasFrame ? view._viewCamera.Frame.LookSensitivity : 1);
            }

            static float LookVertical(MapView view) => LookSensitivity(view) * (Rim3DSettings.Current.InvertMouseY ? -1 : 1);

            internal void UpdateLook(MapView view, bool typing)
            {
                if (view.ApiInputBlocked(API.RimVisionInputChannels.Look) || view._viewCamera.HasFrame && !view._viewCamera.Frame.AllowLook) { ResetMousePosition(); return; }
                if (_lastVirtualLookFrame != Time.frameCount && !typing && Application.isFocused && !view.controller.InputBlocked &&
                    !view.cameraFlight && !view.MortarAiming && !view.FallPresentationActive && (!view.FirstPerson || view.CanCaptureMouse && view.mouseCaptured))
                {
                    _lastVirtualLookFrame = Time.frameCount;
                    float delta = Mathf.Min(Time.unscaledDeltaTime, .1f);
                    view._viewCamera.AddYaw(ControlAxis(API.RimVisionControlAxis.LookYaw) * delta);
                    view._viewCamera.AddPitch(ControlAxis(API.RimVisionControlAxis.LookPitch) * delta,
                        view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.x : -75, view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.y : 75);
                }
                if (view.FirstPerson && !view.controller.InputBlocked && view.mouseCaptured && Time.frameCount != view.mouseCaptureFrame)
                {
                    if (NativeMouse.Available)
                    {
                        if (NativeMouse.Read(out var delta))
                        {
                            if (view.ConsumedApiAxis(API.RimVisionAxisSource.MouseX)) delta.x = 0;
                            if (view.ConsumedApiAxis(API.RimVisionAxisSource.MouseY)) delta.y = 0;
                            _lastAxisLookFrame = Time.frameCount;
                            view._viewCamera.AddYaw(delta.x * .25f * LookSensitivity(view));
                            view._viewCamera.AddPitch(delta.y * .2f * LookVertical(view), view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.x : -75, view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.y : 75);
                        }
                    }
                    else if (_mouseAxesAvailable)
                    {
                        try
                        {
                            float mouseX = view.ConsumedApiAxis(API.RimVisionAxisSource.MouseX) ? 0 : Input.GetAxisRaw("RimVision Mouse X");
                            float mouseY = view.ConsumedApiAxis(API.RimVisionAxisSource.MouseY) ? 0 : Input.GetAxisRaw("RimVision Mouse Y");
                            if (Mathf.Abs(mouseX) + Mathf.Abs(mouseY) > .00001f)
                            {
                                _lastAxisLookFrame = Time.frameCount;
                                view._viewCamera.AddYaw(mouseX * 2.5f * LookSensitivity(view));
                                view._viewCamera.SubtractPitch(mouseY * 2f * LookVertical(view), view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.x : -75, view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.y : 75);
                            }
                        }
                        catch (ArgumentException)
                        {
                            // An already-open editor may retain the old input settings.
                            // Use relative GUI motion until the settings are reloaded.
                            _mouseAxesAvailable = false;
                        }
                    }
                }
                else if (view._orders?.Active != true && (!view.FirstPerson || !view.controller.InputBlocked) && !typing && ViewBindings.MouseHeld(1) && (!view.FirstPerson ||
                    (!view.constructionMenu && !view.actionMenu && !view.statusMenu && !view.ExternalInputBlocked)))
                {
                    var delta = ViewBindings.MouseDown(1) ? Vector3.zero : Input.mousePosition - _previousMouse;
                    if (view.ConsumedApiAxis(API.RimVisionAxisSource.MouseX)) delta.x = 0;
                    if (view.ConsumedApiAxis(API.RimVisionAxisSource.MouseY)) delta.y = 0;
                    view._viewCamera.AddYaw(delta.x * .25f * LookSensitivity(view));
                    view._viewCamera.SubtractPitch(delta.y * .2f * LookVertical(view), view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.x : view.FirstPerson ? -75 : 20, view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.y : view.FirstPerson ? 75 : 80);
                }
                _previousMouse = Input.mousePosition;
            }

            internal void UpdateOverviewZoom(MapView view, bool typing)
            {
                if (view.ApiInputBlocked(API.RimVisionInputChannels.Look) || view._viewCamera.HasFrame && !view._viewCamera.Frame.AllowZoom) return;
                view._viewCamera.Zoom((view.ConsumedApiAxis(API.RimVisionAxisSource.ScrollY) ? 0 : Input.mouseScrollDelta.y) + ControlAxis(API.RimVisionControlAxis.Zoom));
                if (!view.FirstPerson && !typing && ViewBindings.Pressed(ViewAction.ResetCamera)) view._viewCamera.ResetOverview(view.home);
            }

            internal bool HandleMouseEvent(MapView view, Event current)
            {
                if (view.ApiInputBlocked(API.RimVisionInputChannels.Look) || view._viewCamera.HasFrame && !view._viewCamera.Frame.AllowLook || view.MortarAiming || view.cameraFlight || view.controller.InputBlocked || NativeMouse.Available || view.FallPresentationActive || !view.FirstPerson || !view.mouseCaptured || (_mouseAxesAvailable && Time.frameCount - _lastAxisLookFrame <= 1) ||
                    Time.frameCount == view.mouseCaptureFrame) return false;
                if (current.type != EventType.MouseMove && current.type != EventType.MouseDrag) return false;
                view._viewCamera.AddYaw((view.ConsumedApiAxis(API.RimVisionAxisSource.MouseX) ? 0 : current.delta.x) * .25f * LookSensitivity(view));
                view._viewCamera.AddPitch((view.ConsumedApiAxis(API.RimVisionAxisSource.MouseY) ? 0 : current.delta.y) * .2f * LookVertical(view), view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.x : -75, view._viewCamera.HasFrame ? view._viewCamera.Frame.PitchLimits.y : 75);
                return true;
            }
        }
    }
}
