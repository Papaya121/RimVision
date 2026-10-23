using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    internal enum ViewAction
    {
        Forward,
        Backward,
        Left,
        Right,
        Sprint,
        Sneak,
        Cursor,
        Toggle3D,
        Perspective,
        NextPawn,
        Plans,
        Acceleration,
        ResetCamera,
        Interact,
        CancelAction,
        Combat,
        RotateOrSwap,
        Store,
        Drop,
        UseItem,
        ItemActions,
        NextTarget,
        Construction,
        Character,
        Pause,
        CancelPlan,
        FireMode,
        Compass,
        Orders,
        Ascend,
        Descend,
    }

    internal static class ViewBindings
    {
        internal static readonly ViewAction[] Actions = (ViewAction[])Enum.GetValues(typeof(ViewAction));
        internal static readonly KeyCode[] Defaults =
        {
            KeyCode.W,
            KeyCode.S,
            KeyCode.A,
            KeyCode.D,
            KeyCode.LeftShift,
            KeyCode.LeftControl,
            KeyCode.LeftAlt,
            KeyCode.O,
            KeyCode.V,
            KeyCode.N,
            KeyCode.P,
            KeyCode.X,
            KeyCode.Home,
            KeyCode.E,
            KeyCode.Q,
            KeyCode.F,
            KeyCode.R,
            KeyCode.Z,
            KeyCode.G,
            KeyCode.T,
            KeyCode.Y,
            KeyCode.C,
            KeyCode.B,
            KeyCode.Tab,
            KeyCode.Space,
            KeyCode.Delete,
            KeyCode.H,
            KeyCode.M,
            KeyCode.J,
            KeyCode.PageUp,
            KeyCode.PageDown,
        };

        internal static KeyCode DefaultSecondary(ViewAction action) =>
            action == ViewAction.Sprint ? KeyCode.RightShift :
            action == ViewAction.Sneak ? KeyCode.RightControl :
            action == ViewAction.Cursor ? KeyCode.RightAlt : KeyCode.None;

        internal static bool Matches(ViewAction action, KeyCode key) => key != KeyCode.None && !Blocked(action) && ActiveView?.ConsumedApiKey(key) != true &&
            (Rim3DSettings.Current.Binding(action, false) == key || Rim3DSettings.Current.Binding(action, true) == key);

        internal static bool Held(ViewAction action) => !Blocked(action) &&
            (HeldKey(Rim3DSettings.Current.Binding(action, false)) || HeldKey(Rim3DSettings.Current.Binding(action, true)));

        internal static bool Pressed(ViewAction action) => !Blocked(action) &&
            (DownKey(Rim3DSettings.Current.Binding(action, false)) || DownKey(Rim3DSettings.Current.Binding(action, true)));

        internal static bool Released(ViewAction action) => !Held(action) &&
            (UpKey(Rim3DSettings.Current.Binding(action, false)) || UpKey(Rim3DSettings.Current.Binding(action, true)));

        static MapView ActiveView => Rim3DPreview.Instance?.ActiveWindow?.View;
        static bool Blocked(ViewAction action)
        {
            var channel = action <= ViewAction.Sneak || action == ViewAction.Ascend || action == ViewAction.Descend ? API.RimVisionInputChannels.Movement :
                action == ViewAction.Combat || action == ViewAction.FireMode ? API.RimVisionInputChannels.Combat :
                action >= ViewAction.Interact && action <= ViewAction.Construction || action == ViewAction.CancelPlan
                    ? API.RimVisionInputChannels.Interaction : API.RimVisionInputChannels.Shortcuts;
            return ActiveView?.ApiInputBlocked(channel) == true;
        }
        internal static bool MouseDown(int button) => ActiveView?.ConsumedApiKey((KeyCode)((int)KeyCode.Mouse0 + button)) != true && Input.GetMouseButtonDown(button);
        internal static bool MouseHeld(int button) => ActiveView?.ConsumedApiKey((KeyCode)((int)KeyCode.Mouse0 + button)) != true && Input.GetMouseButton(button);
        internal static bool MouseUp(int button) => ActiveView?.ConsumedApiKey((KeyCode)((int)KeyCode.Mouse0 + button)) != true && Input.GetMouseButtonUp(button);
        static bool HeldKey(KeyCode key) => key != KeyCode.None && ActiveView?.ConsumedApiKey(key) != true && Input.GetKey(key);
        static bool DownKey(KeyCode key) => key != KeyCode.None && ActiveView?.ConsumedApiKey(key) != true && Input.GetKeyDown(key);
        static bool UpKey(KeyCode key) => key != KeyCode.None && ActiveView?.ConsumedApiKey(key) != true && Input.GetKeyUp(key);

        internal static string KeyLabel(KeyCode key) => key == KeyCode.None ? "Rim3D_BindingNone".Translate().ToString() : key.ToString();

        internal static string Label(ViewAction action)
        {
            var first = Rim3DSettings.Current.Binding(action, false);
            var second = Rim3DSettings.Current.Binding(action, true);
            if (first == KeyCode.None) return KeyLabel(second);
            return second == KeyCode.None || second == first ? KeyLabel(first) : KeyLabel(first) + "/" + KeyLabel(second);
        }

        static readonly Regex _hintKeys = new Regex(@"\[\[(\w+)\]\]");

        internal static string TranslateBindings(this string key, params NamedArgument[] args)
        {
            var text = key.Translate(args).ToString();
            return _hintKeys.Replace(text, match =>
            {
                if (match.Groups[1].Value == "Movement")
                    return Label(ViewAction.Forward) + "/" + Label(ViewAction.Left) + "/" + Label(ViewAction.Backward) + "/" + Label(ViewAction.Right);
                return Enum.TryParse(match.Groups[1].Value, out ViewAction action) ? Label(action) : match.Value;
            });
        }
    }

    public sealed partial class Rim3DSettings
    {
        internal int BindingsVersion { get; private set; }
        Dictionary<string, KeyCode> _extensionPrimary = new Dictionary<string, KeyCode>();
        Dictionary<string, KeyCode> _extensionSecondary = new Dictionary<string, KeyCode>();
        internal KeyCode ExtensionBinding(string id, bool secondary, KeyCode fallback)
        {
            var bindings = secondary ? _extensionSecondary : _extensionPrimary;
            return bindings != null && bindings.TryGetValue(id, out var key) && (API.RimVisionInput.ValidKey(key) || API.RimVisionInput.Bindings.TryGet(id, out var entry) && entry.Gestures && API.RimVisionInput.ValidGestureKey(key)) ? key : fallback;
        }

        readonly KeyCode[] _primaryBindings = (KeyCode[])ViewBindings.Defaults.Clone();
        readonly KeyCode[] _secondaryBindings = CreateSecondaryBindings();

        static KeyCode[] CreateSecondaryBindings()
        {
            var keys = new KeyCode[ViewBindings.Actions.Length];
            foreach (var action in ViewBindings.Actions) keys[(int)action] = ViewBindings.DefaultSecondary(action);
            return keys;
        }

        internal KeyCode Binding(ViewAction action, bool secondary) => (secondary ? _secondaryBindings : _primaryBindings)[(int)action];

        void ExposeBindings()
        {
            ExposeAdvancedBindings();
            Scribe_Collections.Look(ref _extensionPrimary, "extensionPrimaryBindings", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _extensionSecondary, "extensionSecondaryBindings", LookMode.Value, LookMode.Value);
            if (_extensionPrimary == null) _extensionPrimary = new Dictionary<string, KeyCode>();
            if (_extensionSecondary == null) _extensionSecondary = new Dictionary<string, KeyCode>();
            foreach (var action in ViewBindings.Actions)
            {
                int index = (int)action;
                Scribe_Values.Look(ref _primaryBindings[index], "binding" + action, ViewBindings.Defaults[index]);
                Scribe_Values.Look(ref _secondaryBindings[index], "binding" + action + "Secondary", ViewBindings.DefaultSecondary(action));
                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    if (!ValidBinding(_primaryBindings[index])) _primaryBindings[index] = ViewBindings.Defaults[index];
                    if (!ValidBinding(_secondaryBindings[index])) _secondaryBindings[index] = ViewBindings.DefaultSecondary(action);
                }
            }
        }

        static bool ValidBinding(KeyCode key) => Enum.IsDefined(typeof(KeyCode), key) && key != KeyCode.Escape && key < KeyCode.Mouse0;

        void ResetBindings()
        {
            BindingsVersion++;
            _extensionPrimaryModifiers.Clear(); _extensionSecondaryModifiers.Clear(); _extensionDeadzones.Clear(); _extensionInverted.Clear();
            _extensionPrimary.Clear();
            _extensionSecondary.Clear();
            foreach (var action in ViewBindings.Actions)
            {
                _primaryBindings[(int)action] = ViewBindings.Defaults[(int)action];
                _secondaryBindings[(int)action] = ViewBindings.DefaultSecondary(action);
            }
        }

        void DrawBindings(Listing_Standard list)
        {
            list.GapLine();
            list.Label("Rim3D_BindingsTitle".Translate());
            list.Label("Rim3D_BindingsHint".Translate());
            if (list.ButtonText("Rim3D_BindingsReset".Translate())) ResetBindings();
            foreach (var action in ViewBindings.Actions)
            {
                if (action == ViewAction.Orders) continue;
                var row = list.GetRect(34);
                float width = Mathf.Min(140, row.width * .26f);
                Widgets.Label(new Rect(row.x, row.y, row.width - width * 2 - 12, row.height), ("Rim3D_Binding" + action).Translate());
                for (int slot = 0; slot < 2; slot++)
                {
                    bool secondary = slot == 1;
                    var button = new Rect(row.xMax - width * (2 - slot), row.y, width - 4, 30);
                    if (Widgets.ButtonText(button, ViewBindings.KeyLabel(Binding(action, secondary))))
                        Find.WindowStack.Add(new BindingWindow(this, action, secondary));
                }
            }
            DrawExtensionBindings(list);
            DrawAxisBindings(list);
            if (list.ButtonText("Rim3D_ApiDiagnostics".Translate())) Find.WindowStack.Add(new Dialog_MessageBox(API.RimVisionDiagnostics.Describe()));
        }

        void DrawExtensionBindings(Listing_Standard list)
        {
            foreach (var entry in API.RimVisionInput.Bindings.Entries)
            {
                if (!entry.Active) continue;
                var row = list.GetRect(34);
                float width = Mathf.Min(140, row.width * .26f);
                Widgets.Label(new Rect(row.x, row.y, row.width - width * 2 - 12, row.height), entry.Value.Label);
                for (int slot = 0; slot < 2; slot++)
                {
                    bool secondary = slot == 1;
                    var key = ExtensionBinding(entry.Id, secondary, secondary ? entry.Value.Secondary : entry.Value.Primary);
                    if (Widgets.ButtonText(new Rect(row.xMax - width * (2 - slot), row.y, width - 4, 30), API.RimVisionInput.GestureLabel(entry.Id, secondary)))
                        Find.WindowStack.Add(new BindingWindow(this, entry.Id, entry.Value.Label, secondary));
                }
            }
        }

        internal sealed class BindingWindow : Window
        {
            readonly Rim3DSettings _settings;
            readonly ViewAction _action;
            readonly bool _secondary;
            readonly string _extensionId, _label;
            public override Vector2 InitialSize => new Vector2(480, 220);

            internal BindingWindow(Rim3DSettings settings, ViewAction action, bool secondary)
            {
                _settings = settings;
                _action = action;
                _secondary = secondary;
                forcePause = preventCameraMotion = absorbInputAroundWindow = true;
                closeOnAccept = false;
                closeOnCancel = false;
                doCloseX = true;
            }

            internal BindingWindow(Rim3DSettings settings, string id, string label, bool secondary) : this(settings, default(ViewAction), secondary)
            { _extensionId = id; _label = label; }

            public override void DoWindowContents(Rect rect)
            {
                Widgets.Label(new Rect(0, 0, rect.width - 30, 32), _label ?? ("Rim3D_Binding" + _action).Translate().ToString());
                Widgets.Label(new Rect(0, 42, rect.width, 62), "Rim3D_BindingPressKey".Translate());
                var current = Event.current;
                bool gestures = _extensionId != null && API.RimVisionInput.Bindings.TryGet(_extensionId, out var binding) && binding.Gestures;
                if (gestures && current.type == EventType.MouseDown && current.mousePosition.y < 110)
                { var mouse = (KeyCode)((int)KeyCode.Mouse0 + current.button); current.Use(); Assign(mouse); return; }
                if (current.type == EventType.KeyDown)
                {
                    var key = current.keyCode;
                    current.Use();
                    if (key == KeyCode.Escape) { Close(false); return; }
                    if (gestures && (key == KeyCode.LeftControl || key == KeyCode.RightControl || key == KeyCode.LeftShift || key == KeyCode.RightShift || key == KeyCode.LeftAlt || key == KeyCode.RightAlt)) return;
                    if (key != KeyCode.None && (gestures ? API.RimVisionInput.ValidGestureKey(key) : _extensionId != null ? API.RimVisionInput.ValidKey(key) : ValidBinding(key))) { Assign(key); return; }
                }
                if (Widgets.ButtonText(new Rect(0, 120, rect.width, 32), "Rim3D_BindingClear".Translate())) Assign(KeyCode.None);
            }

            void Assign(KeyCode key)
            {
                if (_extensionId != null)
                {
                    var modifiers = API.RimVisionInputModifiers.None;
                    if (key != KeyCode.None && API.RimVisionInput.Bindings.TryGet(_extensionId, out var binding) && binding.Gestures)
                    {
                        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) modifiers |= API.RimVisionInputModifiers.Shift;
                        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) modifiers |= API.RimVisionInputModifiers.Control;
                        if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) modifiers |= API.RimVisionInputModifiers.Alt;
                    }
                    _settings.SetExtensionGesture(_extensionId, _secondary, new API.RimVisionInputGesture(key, modifiers));
                }
                else
                {
                    (_secondary ? _settings._secondaryBindings : _settings._primaryBindings)[(int)_action] = key;
                    _settings.BindingsVersion++;
                }
                Close(false);
            }
        }
    }
}
