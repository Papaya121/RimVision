using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    [Flags]
    public enum RimVisionInputChannels { None = 0, Movement = 1, Look = 2, Combat = 4, Interaction = 8, Shortcuts = 16, All = 31 }
    public enum RimVisionInputPhase { Pressed, Held, Released, Cancelled }

    public static partial class RimVisionInput
    {
        internal sealed class Binding
        {
            internal string Label;
            internal KeyCode Primary, Secondary;
            internal RimVisionInputModifiers PrimaryModifiers, SecondaryModifiers;
            internal RimVisionInputContext Context = RimVisionInputContext.All;
            internal bool Gestures;
            internal RimVisionInputChannels Channel;
            internal Func<RimVisionViewState, bool> Available;
            internal Func<RimVisionViewState, RimVisionInputPhase, bool> Handle;
        }
        internal static readonly ApiRegistry<Binding> Bindings = new ApiRegistry<Binding>("input");

        public static IDisposable RegisterBinding(string id, string label, KeyCode primary, KeyCode secondary,
            RimVisionInputChannels channel, Func<RimVisionViewState, bool> available,
            Func<RimVisionViewState, RimVisionInputPhase, bool> handle, int priority = 0)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException(nameof(label));
            if (!ValidKey(primary) || !ValidKey(secondary)) throw new ArgumentException("Use a keyboard key other than Escape, F1 or F11.");
            if (channel == RimVisionInputChannels.None || (channel & ~RimVisionInputChannels.All) != 0) throw new ArgumentOutOfRangeException(nameof(channel));
            return Bindings.Add(id, new Binding { Label = label, Primary = primary, Secondary = secondary, Channel = channel,
                Available = available ?? throw new ArgumentNullException(nameof(available)),
                Handle = handle ?? throw new ArgumentNullException(nameof(handle)) }, priority);
        }

        public static string BindingLabel(string id)
        {
            if (!Bindings.TryGet(id, out var binding)) return string.Empty;
            var primary = GestureLabel(id, false);
            var secondary = GestureLabel(id, true);
            return primary == ViewBindings.KeyLabel(KeyCode.None) ? secondary : secondary == ViewBindings.KeyLabel(KeyCode.None) || primary == secondary ? primary : primary + "/" + secondary;
        }
        internal static string GestureLabel(string id, bool secondary)
        {
            if (!Bindings.TryGet(id, out var binding)) return string.Empty;
            var key = Rim3DSettings.Current.ExtensionBinding(id, secondary, secondary ? binding.Secondary : binding.Primary);
            var modifiers = binding.Gestures ? Rim3DSettings.Current.ExtensionModifiers(id, secondary, secondary ? binding.SecondaryModifiers : binding.PrimaryModifiers) : RimVisionInputModifiers.None;
            return new RimVisionInputGesture(key, modifiers).ToString();
        }

        internal static bool ValidKey(KeyCode key) => Enum.IsDefined(typeof(KeyCode), key) && key >= KeyCode.None && key < KeyCode.Mouse0 &&
            key != KeyCode.Escape && key != KeyCode.F1 && key != KeyCode.F11;
    }
}

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        public float NativeVerticalInput => controller.Active && !controller.InputBlocked && !constructionMenu && !actionMenu && !statusMenu && !ApiInputBlocked(API.RimVisionInputChannels.Movement) &&
            !ExternalInputBlocked && !TextInputFocused && Application.isFocused
                ? Mathf.Clamp((ViewBindings.Held(ViewAction.Ascend) ? 1 : 0) - (ViewBindings.Held(ViewAction.Descend) ? 1 : 0) + _viewInput.ControlAxis(API.RimVisionControlAxis.MoveY), -1, 1) : 0;
        internal bool ConsumedApiKey(KeyCode key) => _viewInput.Consumed(key);
        internal bool ConsumedApiAxis(API.RimVisionAxisSource source) => _viewInput.AxisConsumed(source);
        internal bool ApiInputBlocked(API.RimVisionInputChannels channels) =>
            ((_apiExtensions?.BlockedChannels ?? API.RimVisionInputChannels.None) & channels) != 0 ||
            ApiHudPointerBlocked && (channels & (API.RimVisionInputChannels.Look | API.RimVisionInputChannels.Combat | API.RimVisionInputChannels.Interaction)) != 0;

        sealed partial class ViewInput
        {
            readonly Dictionary<API.ApiRegistry<API.RimVisionInput.Binding>.Entry, bool> _extensionHeld =
                new Dictionary<API.ApiRegistry<API.RimVisionInput.Binding>.Entry, bool>();
            readonly List<API.ApiRegistry<API.RimVisionInput.Binding>.Entry> _extensionWork =
                new List<API.ApiRegistry<API.RimVisionInput.Binding>.Entry>();
            readonly HashSet<API.ApiRegistry<API.RimVisionInput.Binding>.Entry> _failedBindings = new HashSet<API.ApiRegistry<API.RimVisionInput.Binding>.Entry>();
            readonly HashSet<API.ApiRegistry<API.RimVisionInput.Binding>.Entry> _suspendedBindings = new HashSet<API.ApiRegistry<API.RimVisionInput.Binding>.Entry>();
            readonly HashSet<KeyCode> _consumedKeys = new HashSet<KeyCode>();
            internal bool Consumed(KeyCode key) => _consumedKeys.Contains(key);

            internal void UpdateExtensions(MapView view, bool cancel = false)
            {
                Array.Clear(_controlAxes, 0, _controlAxes.Length);
                DispatchingExtensions = true;
                try { DispatchExtensions(view, cancel); }
                finally
                {
                    DispatchingExtensions = false;
                    if (cancel)
                    {
                        Array.Clear(_controlAxes, 0, _controlAxes.Length);
                        _failedBindings.Clear(); _failedAxes.Clear(); _suspendedBindings.Clear(); _extensionHeld.Clear(); _axisValues.Clear(); _consumedKeys.Clear(); _consumedAxes.Clear();
                    }
                }
            }
            void DispatchExtensions(MapView view, bool cancel)
            {
                _consumedKeys.Clear();
                _extensionWork.Clear();
                _extensionWork.AddRange(API.RimVisionInput.Bindings.Entries);
                foreach (var entry in _extensionHeld.Keys)
                    if (!entry.Active) _extensionWork.Add(entry);
                UpdateAxes(view, cancel);
                if (_extensionWork.Count == 0) return;
                var state = view.GetApiState();
                foreach (var entry in _extensionWork)
                {
                    if (_failedBindings.Contains(entry)) { _extensionHeld.Remove(entry); continue; }
                    _extensionHeld.TryGetValue(entry, out bool wasHeld);
                    bool held = false;
                    bool cancelled = cancel || !entry.Active || !Application.isFocused || view.TextInputFocused ||
                        view.ExternalInputBlocked || view.cameraFlight || view.FallPresentationActive || view.MortarAiming ||
                        view.ApiInputBlocked(entry.Value.Channel) || !API.RimVisionInput.MatchesContext(entry.Value.Context, state);
                    var primary = Rim3DSettings.Current.ExtensionBinding(entry.Id, false, entry.Value.Primary);
                    var secondary = Rim3DSettings.Current.ExtensionBinding(entry.Id, true, entry.Value.Secondary);
                    bool physicallyHeld = primary != KeyCode.None && Input.GetKey(primary) || secondary != KeyCode.None && Input.GetKey(secondary);
                    if (!physicallyHeld) _suspendedBindings.Remove(entry);
                    if (cancelled && physicallyHeld) _suspendedBindings.Add(entry);
                    var movementBefore = new Vector3(_controlAxes[0], _controlAxes[1], _controlAxes[2]);
                    var cameraBefore = new Vector3(_controlAxes[3], _controlAxes[4], _controlAxes[5]);
                    Rand.PushState();
                    try
                    {
                        if (!cancelled) cancelled = !entry.Value.Available(state);
                        if (!cancelled && !_suspendedBindings.Contains(entry)) held = primary != KeyCode.None && !_consumedKeys.Contains(primary) && Input.GetKey(primary) && (!entry.Value.Gestures || API.RimVisionInput.ModifiersHeld(Rim3DSettings.Current.ExtensionModifiers(entry.Id, false, entry.Value.PrimaryModifiers))) ||
                            secondary != KeyCode.None && !_consumedKeys.Contains(secondary) && Input.GetKey(secondary) && (!entry.Value.Gestures || API.RimVisionInput.ModifiersHeld(Rim3DSettings.Current.ExtensionModifiers(entry.Id, true, entry.Value.SecondaryModifiers)));
                        if (!held && wasHeld && physicallyHeld && (_consumedKeys.Contains(primary) || _consumedKeys.Contains(secondary))) cancelled = true;
                        if (cancelled && physicallyHeld) _suspendedBindings.Add(entry);
                        if (held || wasHeld)
                        {
                            var phase = cancelled ? API.RimVisionInputPhase.Cancelled : held
                                ? wasHeld ? API.RimVisionInputPhase.Held : API.RimVisionInputPhase.Pressed : API.RimVisionInputPhase.Released;
                            if (entry.Value.Handle(state, phase)) { _consumedKeys.Add(primary); _consumedKeys.Add(secondary); }
                        }
                    }
                    catch (Exception error)
                    {
                        if (held || wasHeld) try { entry.Value.Handle(state, API.RimVisionInputPhase.Cancelled); } catch { }
                        RestoreControlAxes(movementBefore, cameraBefore);
                        API.RimVisionDiagnostics.ReportFault("input", entry.Id, error, true); held = false; _failedBindings.Add(entry);
                        Log.ErrorOnce("RimVision binding " + entry.Id + ": " + error, entry.Id.GetHashCode());
                    }
                    finally { Rand.PopState(); }
                    if (entry.Active && !cancel) _extensionHeld[entry] = held;
                    else { _extensionHeld.Remove(entry); _suspendedBindings.Remove(entry); }
                }
                _extensionWork.Clear();
                if (cancel) _failedBindings.Clear();
            }
        }
    }
}
