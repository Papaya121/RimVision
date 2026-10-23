using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        int targetInputFrame = -1;
        bool fpvTargetingWasActive;

        readonly Event _targetInputEvent = new Event();
        static readonly AccessTools.FieldRef<Targeter, TargetingParameters> targetParameters =
            AccessTools.FieldRefAccess<Targeter, TargetingParameters>("targetParams");
        static readonly AccessTools.FieldRef<Targeter, ITargetingSource> targetSource =
            AccessTools.FieldRefAccess<Targeter, ITargetingSource>("targetingSource");
        public bool SelectingFPVTarget => FirstPerson && !controller.InputBlocked && Find.Targeter?.IsTargeting == true &&
            !ExternalInputBlocked && !actionMenu && !constructionMenu && !statusMenu;

        public LocalTargetInfo TargetUnderCrosshair(TargetingParameters parameters, ITargetingSource source) => _viewTargeting.TargetUnderCrosshair(parameters, source);

        public void HandleFPVTargetInput(Event current)
        {
            if (!SelectingFPVTarget || ApiInputBlocked(API.RimVisionInputChannels.Interaction)) return;
            fpvTargetingWasActive = true;
            if (targetInputFrame == Time.frameCount) return;
            if (current.type == EventType.Layout && Application.isFocused &&
                Time.frameCount != contextClosedFrame && Time.frameCount != mouseCaptureFrame)
            {
                bool cancel = Input.GetKeyDown(KeyCode.Escape) || ViewBindings.MouseDown(1);
                if (!cancel && (!mouseCaptured || !ViewBindings.MouseDown(0))) return;
                _targetInputEvent.type = EventType.MouseDown;
                _targetInputEvent.button = cancel ? 1 : 0;
                _targetInputEvent.modifiers = current.modifiers;
                _targetInputEvent.mousePosition = current.mousePosition;
                try
                {
                    Event.current = _targetInputEvent;
                    HandleFPVTargetInput(Event.current);
                }
                finally
                {
                    Event.current = current;
                }
                return;
            }
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape ||
                current.type == EventType.MouseDown && current.button == 1)
            {
                Find.Targeter.StopTargeting();
                targetInputFrame = Time.frameCount;
                current.Use();
                return;
            }
            if (!mouseCaptured || current.type != EventType.MouseDown || current.button != 0 ||
                Time.frameCount == mouseCaptureFrame) return;
            targetInputFrame = Time.frameCount;
            var source = targetSource(Find.Targeter);
            if (source?.GetVerb is Verb_CastBase cast && cast.CasterPawn == ControlledPawn)
            {
                var target = TargetUnderCrosshair(source.targetParams, source);
                current.Use();
                if (!target.IsValid || !source.ValidateTarget(target, true))
                {
                    SayAction("Rim3D_InvalidAimTarget".Translate().ToString());
                    return;
                }
                source.OrderForceTarget(target);
                if (!Rim3DMultiplayer.Active && ControlledPawn.CurJob?.verbToUse == cast)
                    controller.FollowNativeJob(ControlledPawn.CurJob);
                Find.Targeter.StopTargeting();
                return;
            }
            // Keep vanilla validation, confirmations, chained targeting and mod callbacks.
            // CurrentTargetUnderMouse is redirected to the 3D ray only in this mode.
            Find.Targeter.ProcessInputEvents();
            if (current.type != EventType.Used) current.Use();
        }

        void DrawFPVTargetSelection(Rect viewport) => _viewTargeting.DrawFPVTargetSelection(viewport);

        sealed partial class ViewTargeting
        {
            int _targetPickFrame = -1;

            LocalTargetInfo _crosshairTarget = LocalTargetInfo.Invalid;

            ITargetingSource _crosshairSource;

            bool _crosshairLocations;

            internal LocalTargetInfo TargetUnderCrosshair(TargetingParameters parameters, ITargetingSource source)
            {
                bool location = parameters?.canTargetLocations == true;
                if (_targetPickFrame != Time.frameCount || _crosshairSource != source || _crosshairLocations != location)
                {
                    _view.PickFPVTarget(location);
                    _targetPickFrame = Time.frameCount;
                    _crosshairSource = source;
                    _crosshairLocations = location;
                    _crosshairTarget = !location && _view.aimedThing != null ? new LocalTargetInfo(_view.aimedThing) : new LocalTargetInfo(_view.aimedCell);
                }
                var target = _crosshairTarget;
                if (!target.IsValid || !target.Cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(target.Cell) ||
                    parameters == null || !parameters.CanTarget(target.ToTargetInfo(_view.map), source))
                    return LocalTargetInfo.Invalid;
                return target;
            }

            internal void DrawFPVTargetSelection(Rect viewport)
            {
                if (!_view.SelectingFPVTarget) return;
                var source = targetSource(Find.Targeter);
                var target = _view.TargetUnderCrosshair(source?.targetParams ?? targetParameters(Find.Targeter), source);
                var oldColor = GUI.color;
                GUI.color = target.IsValid ? new Color(.35f, 1f, .5f) : new Color(1f, .4f, .3f);
                Widgets.DrawBox(new Rect(viewport.center.x - 16, viewport.center.y - 16, 32, 32), 2);
                if (target.HasThing)
                {
                    var point = target.Thing.DrawPos + _view.root.transform.position;
                    var lower = _view.camera.WorldToViewportPoint(point);
                    var upper = _view.camera.WorldToViewportPoint(point + Vector3.up * (target.Thing is Pawn pawn ? _view.PawnSpriteTop(pawn) : 1f));
                    if (lower.z > 0 && upper.z > 0)
                    {
                        float height = Mathf.Max(24, Mathf.Abs(upper.y - lower.y) * viewport.height);
                        float width = Mathf.Clamp(height * .65f, 20, viewport.width * .5f);
                        Widgets.DrawBox(new Rect(viewport.x + lower.x * viewport.width - width * .5f,
                            viewport.y + (1 - Mathf.Max(lower.y, upper.y)) * viewport.height, width, height), 2);
                    }
                }
                GUI.color = oldColor;
                string label = target.HasThing ? target.Thing.LabelCap.ToString() :
                    target.IsValid ? "Rim3D_AimPoint".Translate().ToString() : "Rim3D_InvalidAimTarget".Translate().ToString();
                _view.Badge(new Rect(viewport.center.x - 360, viewport.center.y + 24, 720, 48),
                    label + "\n" + "Rim3D_SelectAimTarget".Translate().ToString(), _view.activityStyle);
            }

        }
    }
}
