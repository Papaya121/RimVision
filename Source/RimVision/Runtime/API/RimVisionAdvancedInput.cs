using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    [Flags]
    public enum RimVisionInputModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }
    [Flags]
    public enum RimVisionInputContext { Overview = 1, Pawn = 2, NativeControl = 4, Cursor = 8, Menu = 16, All = 31 }
    public enum RimVisionControlAxis { MoveX, MoveZ, MoveY, LookYaw, LookPitch, Zoom }
    public enum RimVisionAxisSource { MouseX, MouseY, ScrollX, ScrollY, UnityAxis, Custom }
    public readonly struct RimVisionInputGesture
    {
        public KeyCode Key { get; }
        public RimVisionInputModifiers Modifiers { get; }
        public RimVisionInputGesture(KeyCode key, RimVisionInputModifiers modifiers = RimVisionInputModifiers.None)
        {
            if (!RimVisionInput.ValidGestureKey(key) || (modifiers & ~(RimVisionInputModifiers)7) != 0) throw new ArgumentOutOfRangeException();
            Key = key; Modifiers = modifiers;
        }
        public override string ToString() => (Modifiers == RimVisionInputModifiers.None ? "" : Modifiers + "+") + ViewBindings.KeyLabel(Key);
    }
    public readonly struct RimVisionAxisInput
    {
        public float Value { get; }
        public float DeltaSeconds => Mathf.Min(Time.unscaledDeltaTime, .1f);
        public RimVisionInputPhase Phase { get; }
        internal RimVisionAxisInput(float value, RimVisionInputPhase phase) { Value = value; Phase = phase; }
    }
    public static partial class RimVisionInput
    {
        public static bool SetControlAxis(RimVisionControlAxis axis, float value) => Rim3DPreview.Instance?.ActiveWindow?.View?.SetApiControlAxis(axis, value) == true;
        internal sealed class Axis
        {
            internal string Label, UnityAxis, SourceKey;
            internal Func<RimVisionViewState, float> Read;
            internal RimVisionAxisSource Source;
            internal RimVisionInputChannels Channel;
            internal RimVisionInputContext Context;
            internal float Deadzone;
            internal bool Invert;
            internal Func<RimVisionViewState, bool> Available;
            internal Func<RimVisionViewState, RimVisionAxisInput, bool> Handle;
        }
        internal static readonly ApiRegistry<Axis> Axes = new ApiRegistry<Axis>("input.axes");
        public static IDisposable RegisterGesture(string id, string label, RimVisionInputGesture primary, RimVisionInputGesture secondary,
            RimVisionInputChannels channel, RimVisionInputContext context, Func<RimVisionViewState, bool> available,
            Func<RimVisionViewState, RimVisionInputPhase, bool> handle, int priority = 0)
        {
            ValidateInput(channel, context, available, handle);
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException(nameof(label));
            return Bindings.Add(id, new Binding { Label = label, Primary = primary.Key, Secondary = secondary.Key,
                PrimaryModifiers = primary.Modifiers, SecondaryModifiers = secondary.Modifiers, Gestures = true,
                Channel = channel, Context = context, Available = available, Handle = handle }, priority);
        }
        public static IDisposable RegisterAxis(string id, string label, RimVisionAxisSource source, RimVisionInputChannels channel,
            RimVisionInputContext context, Func<RimVisionViewState, bool> available,
            Func<RimVisionViewState, RimVisionAxisInput, bool> handle, string unityAxis = null, float deadzone = .1f, bool invert = false, int priority = 0, Func<RimVisionViewState, float> read = null)
        {
            ValidateInput(channel, context, available, handle);
            if (!Enum.IsDefined(typeof(RimVisionAxisSource), source) || string.IsNullOrWhiteSpace(label) ||
                source == RimVisionAxisSource.UnityAxis && string.IsNullOrWhiteSpace(unityAxis) || source == RimVisionAxisSource.Custom && read == null ||
                !RimVisionControl.Finite(deadzone) || deadzone < 0 || deadzone >= 1) throw new ArgumentException("Invalid axis options.");
            return Axes.Add(id, new Axis { Label = label, Source = source, Channel = channel, Context = context,
                UnityAxis = unityAxis, Read = read, SourceKey = source == RimVisionAxisSource.UnityAxis ? unityAxis : source == RimVisionAxisSource.Custom ? id : AxisKey(source), Deadzone = deadzone, Invert = invert, Available = available, Handle = handle }, priority);
        }
        static void ValidateInput(RimVisionInputChannels channel, RimVisionInputContext context, Delegate available, Delegate handle)
        {
            if (channel == RimVisionInputChannels.None || (channel & ~RimVisionInputChannels.All) != 0 ||
                context == 0 || (context & ~RimVisionInputContext.All) != 0) throw new ArgumentOutOfRangeException();
            if (available == null || handle == null) throw new ArgumentNullException();
        }
        public static bool SetGesture(string id, bool secondary, RimVisionInputGesture gesture)
        {
            if (!Bindings.TryGet(id, out var binding) || !binding.Gestures) return false;
            Rim3DSettings.Current.SetExtensionGesture(id, secondary, gesture);
            Rim3DSettings.Current.Write();
            return true;
        }
        internal static bool ValidGestureKey(KeyCode key) => Enum.IsDefined(typeof(KeyCode), key) && (ValidKey(key) || key >= KeyCode.Mouse0 && key <= KeyCode.Joystick8Button19);
        static readonly string[] AxisNames = Enum.GetNames(typeof(RimVisionAxisSource));
        internal static string AxisKey(RimVisionAxisSource source) => AxisNames[(int)source];
        internal static bool ModifiersHeld(RimVisionInputModifiers modifiers) =>
            ((modifiers & RimVisionInputModifiers.Shift) == 0 || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) &&
            ((modifiers & RimVisionInputModifiers.Control) == 0 || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
            ((modifiers & RimVisionInputModifiers.Alt) == 0 || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));
        internal static bool MatchesContext(RimVisionInputContext context, RimVisionViewState state)
        {
            var current = state.InputBlocked ? RimVisionInputContext.Menu : state.FirstPerson
                ? RimVisionInputContext.Pawn | (state.NativeControl ? RimVisionInputContext.NativeControl : 0) : RimVisionInputContext.Overview;
            if (!state.InputBlocked && (!state.FirstPerson || ViewBindings.Held(ViewAction.Cursor))) current |= RimVisionInputContext.Cursor;
            return (context & current) != 0;
        }
    }
}

namespace Recovery.Rim3D
{
    public sealed partial class Rim3DSettings
    {
        Dictionary<string, API.RimVisionInputModifiers> _extensionPrimaryModifiers = new Dictionary<string, API.RimVisionInputModifiers>();
        Dictionary<string, API.RimVisionInputModifiers> _extensionSecondaryModifiers = new Dictionary<string, API.RimVisionInputModifiers>();
        Dictionary<string, float> _extensionDeadzones = new Dictionary<string, float>();
        Dictionary<string, bool> _extensionInverted = new Dictionary<string, bool>();
        internal API.RimVisionInputModifiers ExtensionModifiers(string id, bool secondary, API.RimVisionInputModifiers fallback) =>
            (secondary ? _extensionSecondaryModifiers : _extensionPrimaryModifiers).TryGetValue(id, out var value) && ((int)value & ~7) == 0 ? value : fallback;
        internal void SetExtensionGesture(string id, bool secondary, API.RimVisionInputGesture gesture)
        {
            (secondary ? _extensionSecondary : _extensionPrimary)[id] = gesture.Key;
            (secondary ? _extensionSecondaryModifiers : _extensionPrimaryModifiers)[id] = gesture.Modifiers;
            BindingsVersion++;
        }
        void ExposeAdvancedBindings()
        {
            Scribe_Collections.Look(ref _extensionPrimaryModifiers, "extensionPrimaryModifiers", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _extensionSecondaryModifiers, "extensionSecondaryModifiers", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _extensionDeadzones, "extensionAxisDeadzones", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _extensionInverted, "extensionAxisInverted", LookMode.Value, LookMode.Value);
            if (_extensionPrimaryModifiers == null) _extensionPrimaryModifiers = new Dictionary<string, API.RimVisionInputModifiers>();
            if (_extensionSecondaryModifiers == null) _extensionSecondaryModifiers = new Dictionary<string, API.RimVisionInputModifiers>();
            if (_extensionDeadzones == null) _extensionDeadzones = new Dictionary<string, float>();
            if (_extensionInverted == null) _extensionInverted = new Dictionary<string, bool>();
        }
        internal float AxisDeadzone(string id, float fallback) => _extensionDeadzones.TryGetValue(id, out var value) && API.RimVisionControl.Finite(value) ? Mathf.Clamp(value, 0, .95f) : fallback;
        internal bool AxisInverted(string id, bool fallback) => _extensionInverted.TryGetValue(id, out var value) ? value : fallback;
        void DrawAxisBindings(Listing_Standard list)
        {
            foreach (var entry in API.RimVisionInput.Axes.Entries)
            {
                bool inverted = AxisInverted(entry.Id, entry.Value.Invert);
                list.CheckboxLabeled(entry.Value.Label + " –  " + "Rim3D_ApiInvert".Translate(), ref inverted);
                _extensionInverted[entry.Id] = inverted;
                float deadzone = AxisDeadzone(entry.Id, entry.Value.Deadzone);
                list.Label("Rim3D_ApiDeadzone".Translate(deadzone.ToString("0.00")));
                _extensionDeadzones[entry.Id] = list.Slider(deadzone, 0, .95f);
            }
        }
    }

    public sealed partial class MapView
    {
        internal bool SetApiControlAxis(API.RimVisionControlAxis axis, float value) => _viewInput.SetControlAxis(this, axis, value);
        sealed partial class ViewInput
        {
            readonly float[] _controlAxes = new float[6];
            void RestoreControlAxes(Vector3 movement, Vector3 camera)
            { _controlAxes[0] = movement.x; _controlAxes[1] = movement.y; _controlAxes[2] = movement.z; _controlAxes[3] = camera.x; _controlAxes[4] = camera.y; _controlAxes[5] = camera.z; }
            internal bool DispatchingExtensions;
            internal float ControlAxis(API.RimVisionControlAxis axis) => _controlAxes[(int)axis];
            internal bool SetControlAxis(MapView view, API.RimVisionControlAxis axis, float value)
            {
                if (!DispatchingExtensions || !Enum.IsDefined(typeof(API.RimVisionControlAxis), axis) || !API.RimVisionControl.Finite(value)) return false;
                var channel = axis <= API.RimVisionControlAxis.MoveY ? API.RimVisionInputChannels.Movement : API.RimVisionInputChannels.Look;
                if (view.ApiInputBlocked(channel)) return false;
                float limit = axis <= API.RimVisionControlAxis.MoveY ? 1 : axis == API.RimVisionControlAxis.Zoom ? 100 : 720;
                _controlAxes[(int)axis] = Mathf.Clamp(_controlAxes[(int)axis] + value, -limit, limit);
                return true;
            }
            readonly Dictionary<API.ApiRegistry<API.RimVisionInput.Axis>.Entry, float> _axisValues = new Dictionary<API.ApiRegistry<API.RimVisionInput.Axis>.Entry, float>();
            readonly List<API.ApiRegistry<API.RimVisionInput.Axis>.Entry> _axisWork = new List<API.ApiRegistry<API.RimVisionInput.Axis>.Entry>();
            readonly HashSet<API.ApiRegistry<API.RimVisionInput.Axis>.Entry> _failedAxes = new HashSet<API.ApiRegistry<API.RimVisionInput.Axis>.Entry>();
            readonly HashSet<string> _consumedAxes = new HashSet<string>(StringComparer.Ordinal);
            internal bool AxisConsumed(API.RimVisionAxisSource source) => _consumedAxes.Contains(API.RimVisionInput.AxisKey(source));
            void UpdateAxes(MapView view, bool cancel)
            {
                _consumedAxes.Clear(); _axisWork.Clear();
                _axisWork.AddRange(API.RimVisionInput.Axes.Entries);
                foreach (var entry in _axisValues.Keys) if (!entry.Active) _axisWork.Add(entry);
                if (_axisWork.Count == 0) return;
                var state = view.GetApiState();
                foreach (var entry in _axisWork)
                {
                    if (_failedAxes.Contains(entry)) { _axisValues.Remove(entry); continue; }
                    var axis = entry.Value;
                    string source = axis.SourceKey;
                    _axisValues.TryGetValue(entry, out float previous);
                    bool cancelled = cancel || !entry.Active || !Application.isFocused || view.TextInputFocused || view.ExternalInputBlocked ||
                        view.cameraFlight || view.FallPresentationActive || view.MortarAiming || view.ApiInputBlocked(axis.Channel) ||
                        !API.RimVisionInput.MatchesContext(axis.Context, state) || _consumedAxes.Contains(source);
                    float value = 0;
                    bool invoked = false;
                    var movementBefore = new Vector3(_controlAxes[0], _controlAxes[1], _controlAxes[2]);
                    var cameraBefore = new Vector3(_controlAxes[3], _controlAxes[4], _controlAxes[5]);
                    Rand.PushState();
                    try
                    {
                        if (!cancelled) cancelled = !axis.Available(state);
                        if (!cancelled)
                        {
                            value = axis.Source == API.RimVisionAxisSource.Custom ? axis.Read(state) : axis.Source == API.RimVisionAxisSource.ScrollX ? Input.mouseScrollDelta.x :
                                axis.Source == API.RimVisionAxisSource.ScrollY ? Input.mouseScrollDelta.y :
                                Input.GetAxisRaw(axis.Source == API.RimVisionAxisSource.MouseX ? "RimVision Mouse X" : axis.Source == API.RimVisionAxisSource.MouseY ? "RimVision Mouse Y" : axis.UnityAxis);
                            if (!API.RimVisionControl.Finite(value)) value = 0;
                            if (Mathf.Abs(value) <= Rim3DSettings.Current.AxisDeadzone(entry.Id, axis.Deadzone)) value = 0;
                            if (Rim3DSettings.Current.AxisInverted(entry.Id, axis.Invert)) value = -value;
                        }
                        if (value != 0 || previous != 0)
                        {
                            var phase = cancelled ? API.RimVisionInputPhase.Cancelled : value == 0 ? API.RimVisionInputPhase.Released : previous == 0 ? API.RimVisionInputPhase.Pressed : API.RimVisionInputPhase.Held;
                            invoked = true;
                            if (axis.Handle(state, new API.RimVisionAxisInput(value, phase))) _consumedAxes.Add(source);
                        }
                    }
                    catch (Exception error)
                    {
                        if (previous != 0 || invoked) try { axis.Handle(state, new API.RimVisionAxisInput(0, API.RimVisionInputPhase.Cancelled)); } catch { }
                        RestoreControlAxes(movementBefore, cameraBefore);
                        value = 0; _failedAxes.Add(entry); API.RimVisionDiagnostics.ReportFault("input.axes", entry.Id, error, true);
                        Log.ErrorOnce("RimVision axis " + entry.Id + ": " + error, entry.Id.GetHashCode());
                    }
                    finally { Rand.PopState(); }
                    if (entry.Active && !cancel) _axisValues[entry] = value; else _axisValues.Remove(entry);
                }
                _axisWork.Clear(); if (cancel) _failedAxes.Clear();
            }
        }
    }
}
