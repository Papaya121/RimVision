using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Recovery.Rim3D;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.Compatibility
{
    internal static class FacialAnimationCompatibility
    {
        private sealed class Capture
        {
            internal Pawn Pawn;
            internal ThingComp Controller, Drawing;
            internal Vector3 CameraOffset, PositionOffset;
            internal float CameraZoom, Angle, LastTime;
            internal Rot4 Rotation;
            internal bool RenderHead, RenderHeadgear, RenderClothes, Portrait, StylingStation;
            internal IReadOnlyDictionary<Apparel, Color> ApparelColors;
            internal Color? HairColor;
            internal int LastTick;
        }

        private static readonly ConditionalWeakTable<RenderTexture, Capture> _captures = new ConditionalWeakTable<RenderTexture, Capture>();
        private static Type _controllerType, _drawingType;
        private static MethodInfo _canAnimate;
        private static PropertyInfo _drawingEnabled;
        private static FieldInfo _pawnMaterial;
        private static int _captureDepth;
        private static bool _refreshFailed;

        internal static void Initialize()
        {
            _controllerType = AccessTools.TypeByName("FacialAnimation.FacialAnimationControllerComp");
            _drawingType = AccessTools.TypeByName("FacialAnimation.DrawFaceGraphicsComp");
            _canAnimate = AccessTools.Method(_controllerType, "CheckUpdateableInitial", Type.EmptyTypes);
            _drawingEnabled = AccessTools.Property(_drawingType, "EnableDrawing");
            _pawnMaterial = AccessTools.Field(AccessTools.Inner(typeof(MapView), "PawnView"), "Material");
            var capture = AccessTools.Method(typeof(MapView), "CapturePawnAppearance");
            var held = AccessTools.Method(typeof(MapView), "HeldThingMaterial");
            var update = AccessTools.Method(typeof(MapView), "UpdatePawnView");
            var render = AccessTools.Method(typeof(PawnCacheRenderer), "RenderPawn");
            if (_controllerType == null || _drawingType == null || _canAnimate == null ||
                _drawingEnabled == null || _pawnMaterial == null || capture == null ||
                held == null || update == null || render == null)
            {
                Log.Warning("[RimVision Compatibility] Facial Animation API not recognized; compatibility disabled.");
                return;
            }
            var harmony = new Harmony("papaya.rimvision.compatibility.facialanimation");
            try
            {
                harmony.Patch(capture, prefix: Patch(nameof(BeginCapture)), finalizer: Patch(nameof(EndCapture)));
                harmony.Patch(held, prefix: Patch(nameof(BeginCapture)), postfix: Patch(nameof(RefreshHeld)), finalizer: Patch(nameof(EndCapture)));
                harmony.Patch(render, postfix: Patch(nameof(RegisterCapture)));
                harmony.Patch(update, postfix: Patch(nameof(RefreshPawn)));
            }
            catch
            {
                harmony.UnpatchAll("papaya.rimvision.compatibility.facialanimation");
                throw;
            }
        }

        private static HarmonyMethod Patch(string name) => new HarmonyMethod(typeof(FacialAnimationCompatibility), name);

        private static void BeginCapture(out bool __state)
        {
            __state = false;
            Rand.PushState();
            _captureDepth++;
            __state = true;
        }

        private static Exception EndCapture(Exception __exception, bool __state)
        {
            if (__state)
            {
                _captureDepth--;
                Rand.PopState();
            }
            return __exception;
        }

        private static void RegisterCapture(Pawn pawn, RenderTexture renderTexture, Vector3 cameraOffset,
            float cameraZoom, float angle, Rot4 rotation, bool renderHead, bool renderHeadgear,
            bool renderClothes, bool portrait, Vector3 positionOffset,
            IReadOnlyDictionary<Apparel, Color> overrideApparelColor, Color? overrideHairColor, bool stylingStation)
        {
            if (_captureDepth == 0 || pawn == null || renderTexture == null) return;
            if (!_captures.TryGetValue(renderTexture, out var capture))
            {
                ThingComp controller = null, drawing = null;
                var comps = pawn.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    if (_controllerType.IsInstanceOfType(comps[i])) controller = comps[i];
                    if (_drawingType.IsInstanceOfType(comps[i])) drawing = comps[i];
                }
                if (controller == null || drawing == null) return;
                capture = new Capture { Pawn = pawn, Controller = controller, Drawing = drawing };
                _captures.Add(renderTexture, capture);
            }
            capture.CameraOffset = cameraOffset;
            capture.CameraZoom = cameraZoom;
            capture.Angle = angle;
            capture.Rotation = rotation;
            capture.RenderHead = renderHead;
            capture.RenderHeadgear = renderHeadgear;
            capture.RenderClothes = renderClothes;
            capture.Portrait = portrait;
            capture.PositionOffset = positionOffset;
            capture.ApparelColors = overrideApparelColor;
            capture.HairColor = overrideHairColor;
            capture.StylingStation = stylingStation;
            capture.LastTick = Find.TickManager.TicksGame;
            capture.LastTime = Time.unscaledTime;
        }

        private static void RefreshPawn(Pawn pawn, IDictionary ___pawnViews, Dictionary<Pawn, Transform> ___pawnCards)
        {
            if (_refreshFailed || pawn == null || !___pawnViews.Contains(pawn) ||
                !___pawnCards.TryGetValue(pawn, out var card) || card == null ||
                !card.gameObject.activeInHierarchy || !card.GetComponent<Renderer>().isVisible) return;
            Refresh(_pawnMaterial.GetValue(___pawnViews[pawn]) as Material);
        }

        private static void RefreshHeld(Material __result) => Refresh(__result);

        private static void Refresh(Material material)
        {
            if (_refreshFailed) return;
            try { RefreshTexture(material); }
            catch (Exception error)
            {
                _refreshFailed = true;
                Log.Warning("[RimVision Compatibility] Facial Animation texture updates disabled: " + error);
            }
        }

        private static void RefreshTexture(Material material)
        {
            if (material == null || !(material.mainTexture is RenderTexture texture) ||
                texture == null || !_captures.TryGetValue(texture, out var capture)) return;
            int tick = Find.TickManager.TicksGame;
            if (capture.Pawn.Destroyed || capture.Pawn.Dead || tick == capture.LastTick ||
                (tick > capture.LastTick && tick - capture.LastTick < 3) ||
                Time.unscaledTime - capture.LastTime < 1f / 30f) return;
            capture.LastTick = tick;
            capture.LastTime = Time.unscaledTime;
            if (!(bool)_drawingEnabled.GetValue(capture.Drawing) ||
                !(bool)_canAnimate.Invoke(capture.Controller, null)) return;
            var camera = Find.PawnCacheCamera;
            var previousTarget = RenderTexture.active;
            var previousClear = camera.clearFlags;
            var previousColor = camera.backgroundColor;
            int previousMask = camera.cullingMask;
            var previousPosition = camera.transform.position;
            float previousSize = camera.orthographicSize;
            var previousCameraTarget = camera.targetTexture;
            Rand.PushState();
            try
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.cullingMask = 0;
                Find.PawnCacheRenderer.RenderPawn(capture.Pawn, texture, capture.CameraOffset,
                    capture.CameraZoom, capture.Angle, capture.Rotation, capture.RenderHead,
                    capture.RenderHeadgear, capture.RenderClothes, capture.Portrait,
                    capture.PositionOffset, capture.ApparelColors, capture.HairColor, capture.StylingStation);
            }
            finally
            {
                camera.transform.position = previousPosition;
                camera.orthographicSize = previousSize;
                camera.targetTexture = previousCameraTarget;
                camera.clearFlags = previousClear;
                camera.backgroundColor = previousColor;
                camera.cullingMask = previousMask;
                RenderTexture.active = previousTarget;
                Rand.PopState();
            }
        }
    }
}
