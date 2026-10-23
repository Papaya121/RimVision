using System;
using HarmonyLib;
using Recovery.Rim3D;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.Compatibility
{
    internal static class InteractionBubblesCompatibility
    {
        private delegate bool ProjectPoint(MapView view, Vector3 position, Rect viewport, out Vector2 point, float maxDistance);

        private static Action _draw;
        private static ProjectPoint _project;
        private static Func<MapView, Pawn, float> _spriteTop;
        private static AccessTools.FieldRef<MapView, float> _nearbyAlpha;
        private static MapView _view;
        private static Rect _viewport;
        private static Pawn _labelPawn;
        private static Vector2 _labelPoint;
        private static bool _drawFailed;

        internal static void Initialize()
        {
            var bubbler = AccessTools.TypeByName("Bubbles.Core.Bubbler");
            var draw = AccessTools.Method(bubbler, "Draw", Type.EmptyTypes);
            var drawBubble = AccessTools.Method(bubbler, "DrawBubble", new[] { typeof(Pawn), typeof(bool), typeof(float) });
            var altitude = AccessTools.Method(bubbler, "GetAltitude", Type.EmptyTypes);
            var overlay = AccessTools.Method(typeof(MapView), "DrawActivityOverlay", new[] { typeof(Rect) });
            var project = AccessTools.Method(typeof(MapView), "OverlayPoint");
            var spriteTop = AccessTools.Method(typeof(MapView), "PawnSpriteTop", new[] { typeof(Pawn) });
            var nearbyAlpha = AccessTools.Field(typeof(MapView), "nearbyAlpha");
            var label = AccessTools.Method(typeof(GenMapUI), "LabelDrawPosFor", new[] { typeof(Thing), typeof(float) });
            if (bubbler == null || draw == null || drawBubble == null || altitude == null ||
                overlay == null || project == null || spriteTop == null || nearbyAlpha == null || label == null)
            {
                Log.Warning("[RimVision Compatibility] Interaction Bubbles API not recognized; compatibility disabled.");
                return;
            }
            _project = (ProjectPoint)Delegate.CreateDelegate(typeof(ProjectPoint), project);
            _spriteTop = (Func<MapView, Pawn, float>)Delegate.CreateDelegate(typeof(Func<MapView, Pawn, float>), spriteTop);
            _nearbyAlpha = AccessTools.FieldRefAccess<MapView, float>(nearbyAlpha);
            var harmony = new Harmony("papaya.rimvision.compatibility.bubbles");
            try
            {
                harmony.Patch(draw, prefix: Patch(nameof(CanDraw)));
                harmony.Patch(drawBubble, prefix: Patch(nameof(ProjectBubble)));
                harmony.Patch(altitude, prefix: Patch(nameof(GetAltitude)));
                harmony.Patch(label, prefix: Patch(nameof(GetLabelPosition)));
                harmony.Patch(overlay, postfix: Patch(nameof(DrawOverlay)));
                _draw = (Action)Delegate.CreateDelegate(typeof(Action), draw);
            }
            catch
            {
                harmony.UnpatchAll("papaya.rimvision.compatibility.bubbles");
                throw;
            }
        }

        private static HarmonyMethod Patch(string name) => new HarmonyMethod(typeof(InteractionBubblesCompatibility), name);

        private static bool CanDraw()
        {
            var window = Rim3DPreview.Instance?.ActiveWindow;
            return _view != null || window?.IsOpen != true || window.View == null;
        }

        private static void DrawOverlay(MapView __instance, Rect viewport)
        {
            if (_drawFailed || _draw == null || Event.current == null ||
                (Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout)) return;
            var window = Rim3DPreview.Instance?.ActiveWindow;
            if (window?.IsOpen != true || window.View != __instance || window.Map != Find.CurrentMap) return;
            var previousColor = GUI.color;
            var previousFont = Text.Font;
            var previousAnchor = Text.Anchor;
            bool previousWordWrap = Text.WordWrap;
            GUI.BeginGroup(viewport);
            Rand.PushState();
            try
            {
                _view = __instance;
                _viewport = new Rect(0, 0, viewport.width, viewport.height);
                _draw();
            }
            catch (Exception error)
            {
                _drawFailed = true;
                Log.Warning("[RimVision Compatibility] Interaction Bubbles 3D drawing disabled: " + error);
            }
            finally
            {
                _view = null;
                _labelPawn = null;
                Rand.PopState();
                GUI.EndGroup();
                GUI.color = previousColor;
                Text.Font = previousFont;
                Text.Anchor = previousAnchor;
                Text.WordWrap = previousWordWrap;
            }
        }

        private static bool ProjectBubble(Pawn __0)
        {
            if (_view == null) return true;
            _labelPawn = null;
            if (__0 == null || !__0.Spawned || __0.Map != Find.CurrentMap ||
                (_view.FirstPerson && __0 == _view.ControlledPawn)) return false;
            var position = __0.DrawPos;
            position.y = _spriteTop(_view, __0) + .15f;
            float previousAlpha = _nearbyAlpha(_view);
            try
            {
                if (!_project(_view, position, _viewport, out _labelPoint, float.MaxValue)) return false;
            }
            finally { _nearbyAlpha(_view) = previousAlpha; }
            _labelPawn = __0;
            return true;
        }

        private static bool GetLabelPosition(Thing __0, ref Vector2 __result)
        {
            if (_view == null || __0 != _labelPawn) return true;
            __result = _labelPoint;
            return false;
        }

        private static bool GetAltitude(ref float __result)
        {
            if (_view == null) return true;
            __result = Mathf.Max(1f, _view.CameraMapPosition.y);
            return false;
        }
    }
}
