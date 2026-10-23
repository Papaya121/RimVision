using System;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    public enum RimVisionHudElement
    {
        Scope, Activity, Immersion, Time, Messages, Character, Needs, Acceleration, Minigame,
        Controls, Resources, Help, Crosshair, CombatFeedback, Letters, Extensions, Mortar, Fall,
        Portrait, Weapon, Colonists, Health, Pain, Stamina, Compass, Interaction
    }
    public enum RimVisionHudPhase { Before, After }

    public readonly struct RimVisionHudContext
    {
        public RimVisionViewState State { get; }
        public RimVisionHudElement Element { get; }
        public Rect Viewport { get; }
        internal RimVisionHudContext(RimVisionViewState state, RimVisionHudElement element, Rect viewport)
        { State = state; Element = element; Viewport = viewport; }
    }

    public struct RimVisionHudLayout
    {
        public Rect Viewport { get; set; }
        public Rect Bounds { get; set; }
        public Rect InputBounds { get; set; }
        public bool Visible { get; set; }
        public bool DrawDefault { get; set; }
        public bool Interactive { get; set; }
        internal bool Valid => RectValid(Viewport) && Viewport.width > 0 && Viewport.height > 0 && RectValid(InputBounds) && RectValid(Bounds);
        static bool RectValid(Rect value) => RimVisionControl.Finite(value.x) && RimVisionControl.Finite(value.y) &&
            RimVisionControl.Finite(value.width) && RimVisionControl.Finite(value.height) && value.width >= 0 && value.height >= 0;
    }

    public abstract class RimVisionHudProvider
    {
        public abstract bool Handles(RimVisionHudContext context);
        public virtual void Layout(RimVisionHudContext context, ref RimVisionHudLayout layout) { }
        public virtual void Draw(RimVisionHudContext context, RimVisionHudLayout layout, RimVisionHudPhase phase) { }
    }

    public static class RimVisionHud
    {
        internal static readonly ApiRegistry<RimVisionHudProvider> Providers = new ApiRegistry<RimVisionHudProvider>("hud");
        public static IDisposable RegisterProvider(string id, RimVisionHudProvider provider, int priority = 0) =>
            Providers.Add(id, provider ?? throw new ArgumentNullException(nameof(provider)), priority);

        internal static void Apply(RimVisionHudContext context, ref RimVisionHudLayout layout)
        {
            if (Providers.Entries.Length == 0) return;
            Rand.PushState();
            try
            {
                foreach (var entry in Providers.Entries)
                {
                    if (!entry.Active) continue;
                    try
                    {
                        if (!entry.Value.Handles(context)) continue;
                        var candidate = layout;
                        entry.Value.Layout(context, ref candidate);
                        if (candidate.Valid) layout = candidate;
                    }
                    catch (Exception error) { Report(entry.Id, error); }
                }
            }
            finally { Rand.PopState(); }
        }

        internal static void Draw(RimVisionHudContext context, RimVisionHudLayout layout, RimVisionHudPhase phase)
        {
            foreach (var entry in Providers.Entries)
            {
                if (!entry.Active) continue;
                var color = GUI.color;
                var matrix = GUI.matrix;
                bool enabled = GUI.enabled;
                var font = Text.Font;
                var anchor = Text.Anchor;
                bool wrap = Text.WordWrap;
                Rand.PushState();
                try
                {
                    GUI.enabled = enabled && layout.Interactive;
                    if (entry.Value.Handles(context)) entry.Value.Draw(context, layout, phase);
                }
                catch (Exception error) { Report(entry.Id, error); }
                finally
                {
                    Rand.PopState();
                    GUI.color = color; GUI.matrix = matrix; GUI.enabled = enabled;
                    Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap;
                }
            }
        }
        static void Report(string id, Exception error) { RimVisionDiagnostics.ReportFault("hud", id, error); Log.ErrorOnce("RimVision HUD " + id + ": " + error, id.GetHashCode()); }
    }
}

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        internal bool ApiHudPointerBlocked => _hudPresentation?.ApiPointerBlocked == true;
        internal API.RimVisionHudLayout? GetApiHudLayout(API.RimVisionHudElement element) => _hudPresentation?.GetLayout(element);
        internal void BeginApiHud() => _hudPresentation.BeginApiHud();
        internal void DrawHudElement(API.RimVisionHudElement element, Rect viewport, bool visible = true) =>
            _hudPresentation.DrawElement(element, viewport, visible && (element != API.RimVisionHudElement.Resources || !constructionMenu));

        sealed partial class HudPresentation
        {
            readonly API.RimVisionHudLayout[] _apiLayouts = new API.RimVisionHudLayout[Enum.GetValues(typeof(API.RimVisionHudElement)).Length];
            readonly int[] _apiLayoutFrames = new int[Enum.GetValues(typeof(API.RimVisionHudElement)).Length];
            internal API.RimVisionHudLayout? GetLayout(API.RimVisionHudElement element)
            {
                int index = (int)element;
                return index >= 0 && index < _apiLayouts.Length && !Rim3DSettings.Current.HideInterface && Time.frameCount - _apiLayoutFrames[index] <= 1 && _apiLayouts[index].Visible
                    ? (API.RimVisionHudLayout?)_apiLayouts[index] : null;
            }
            internal void ReportHudBounds(API.RimVisionHudElement element, Rect bounds)
            {
                var layout = _apiLayouts[(int)element];
                layout.Bounds = bounds;
                _apiLayouts[(int)element] = layout;
            }
            int _apiHudFrame = -10;
            internal bool ApiPointerBlocked
            {
                get
                {
                    if (Time.frameCount - _apiHudFrame > 1 || Rim3DSettings.Current.HideInterface) return false;
                    var point = new Vector2(Input.mousePosition.x / Prefs.UIScale, UI.screenHeight - Input.mousePosition.y / Prefs.UIScale);
                    for (int i = 0; i < _apiLayouts.Length; i++)
                    {
                        var layout = _apiLayouts[i];
                        if (Time.frameCount - _apiLayoutFrames[i] <= 1 && layout.Visible && layout.Interactive &&
                            layout.InputBounds.width > 0 && layout.InputBounds.height > 0 && layout.InputBounds.Contains(point)) return true;
                    }
                    return false;
                }
            }
            internal void BeginApiHud()
            {
                _apiHudFrame = Time.frameCount;
            }

            internal void DrawElement(API.RimVisionHudElement element, Rect viewport, bool visible)
            {
                var context = new API.RimVisionHudContext(_view.GetApiState(), element, viewport);
                var layout = new API.RimVisionHudLayout { Viewport = viewport, Bounds = _apiLayouts[(int)element].Bounds, Visible = visible, DrawDefault = true,
                    Interactive = !_view.FirstPerson || ViewBindings.Held(ViewAction.Cursor) };
                API.RimVisionHud.Apply(context, ref layout);
                _apiLayouts[(int)element] = layout;
                _apiLayoutFrames[(int)element] = Time.frameCount;
                if (!layout.Visible) return;
                API.RimVisionHud.Draw(context, layout, API.RimVisionHudPhase.Before);
                if (layout.DrawDefault) DrawDefaultElement(element, layout.Viewport);
                layout = _apiLayouts[(int)element];
                API.RimVisionHud.Draw(context, layout, API.RimVisionHudPhase.After);
                if (layout.Interactive && layout.InputBounds.Contains(Event.current.mousePosition) && Event.current.isMouse) Event.current.Use();
            }

            void DrawEventLetters(Rect viewport)
            {
                var letters = Find.LetterStack;
                if (letters == null || letters.LettersListForReading.Count == 0) return;
                // Reuse the live stack, including arrival animations, tooltips and bundled letters.
                // Alt releases the cursor; captured combat clicks must not open a letter.
                bool interactive = !_view.FirstPerson || ViewBindings.Held(ViewAction.Cursor);
                if (!interactive && Event.current.isMouse) return;
                bool enabled = GUI.enabled;
                var color = GUI.color;
                var font = Text.Font;
                var anchor = Text.Anchor;
                bool wordWrap = Text.WordWrap;
                try
                {
                    // Mouse events are filtered above; keep rendering enabled so labels stay bright.
                    Text.Font = GameFont.Small;
                    letters.LettersOnGUI(viewport.yMax - 12f);
                }
                finally
                {
                    GUI.enabled = enabled;
                    GUI.color = color;
                    Text.Font = font;
                    Text.Anchor = anchor;
                    Text.WordWrap = wordWrap;
                }
            }

            void DrawDefaultElement(API.RimVisionHudElement element, Rect viewport)
            {
                switch (element)
                {
                    case API.RimVisionHudElement.Interaction: _view._actionMenus.DrawApiInteraction(viewport); break;
                    case API.RimVisionHudElement.Scope: _view.DrawSniperScope(viewport); break;
                    case API.RimVisionHudElement.Activity: _view.DrawActivityOverlay(viewport); break;
                    case API.RimVisionHudElement.Immersion: _view.DrawImmersion(viewport); break;
                    case API.RimVisionHudElement.Time: _view.DrawTimeHud(viewport); break;
                    case API.RimVisionHudElement.Messages: _view.DrawTopHudMessages(viewport); break;
                    case API.RimVisionHudElement.Character: _view.DrawCharacterHud(viewport); break;
                    case API.RimVisionHudElement.Portrait: DrawCharacterPortrait(viewport); break;
                    case API.RimVisionHudElement.Weapon: DrawCharacterWeaponPanel(viewport); break;
                    case API.RimVisionHudElement.Colonists:
                        if (_view.FirstPerson && _view.ControlledPawn != null) DrawCharacterPawnList(viewport, CharacterPortraitRect(viewport));
                        break;
                    case API.RimVisionHudElement.Health:
                    case API.RimVisionHudElement.Pain:
                    case API.RimVisionHudElement.Stamina:
                        if (_view.FirstPerson && _view.ControlledPawn != null && Event.current.type == EventType.Repaint)
                            DrawCharacterVitals(CharacterPortraitRect(viewport), CharacterWeaponRect(viewport, out _), element);
                        break;
                    case API.RimVisionHudElement.Compass: DrawCompassHud(viewport); break;
                    case API.RimVisionHudElement.Needs: _view.DrawNeedsHud(viewport); break;
                    case API.RimVisionHudElement.Acceleration: _view.DrawActionAcceleration(viewport); break;
                    case API.RimVisionHudElement.Minigame: _view.DrawMinigame(viewport); break;
                    case API.RimVisionHudElement.Controls: _view.DrawFPVControls(viewport); break;
                    case API.RimVisionHudElement.Resources: _view.DrawResourceHud(viewport); break;
                    case API.RimVisionHudElement.Help: _view.DrawBottomControls(viewport); break;
                    case API.RimVisionHudElement.CombatFeedback: _view.DrawCombatFeedback(viewport); break;
                    case API.RimVisionHudElement.Letters: DrawEventLetters(viewport); break;
                    case API.RimVisionHudElement.Extensions: _view.DrawApiHud(viewport); break;
                    case API.RimVisionHudElement.Mortar: _view.DrawMortarTargeting(viewport); break;
                    case API.RimVisionHudElement.Fall: _view.DrawFallTransition(viewport); break;
                    case API.RimVisionHudElement.Crosshair:
                        var center = viewport.center;
                        Widgets.DrawBoxSolid(new Rect(center.x - 4, center.y - 1, 8, 2), new Color(1, 1, 1, .65f));
                        Widgets.DrawBoxSolid(new Rect(center.x - 1, center.y - 4, 2, 8), new Color(1, 1, 1, .65f));
                        break;
                }
            }
        }
    }
}
