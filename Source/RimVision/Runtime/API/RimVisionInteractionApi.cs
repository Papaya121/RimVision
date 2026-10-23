using System;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    public struct RimVisionInteraction
    {
        public string ActionId { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public Texture2D Icon { get; set; }
        public Map TargetMap { get; set; }
        public Thing Target { get; set; }
        public IntVec3 Cell { get; set; }
        public int Value { get; set; }
        public float HoldSeconds { get; set; }
        public bool OverrideDefault { get; set; }
        internal bool Valid => !string.IsNullOrEmpty(ActionId) && !string.IsNullOrEmpty(Label) && RimVisionControl.Finite(HoldSeconds) && HoldSeconds >= 0 && HoldSeconds <= 60;
    }
    public readonly struct RimVisionInteractionState
    {
        public RimVisionInteraction Interaction { get; }
        public bool Available { get; }
        public string Reason { get; }
        public float Progress { get; }
        internal RimVisionInteractionState(RimVisionInteraction interaction, bool available, string reason, float progress)
        { Interaction = interaction; Available = available; Reason = reason; Progress = progress; }
    }
    public abstract class RimVisionInteractionProvider
    {
        public abstract bool Evaluate(RimVisionViewState state, ref RimVisionInteraction interaction);
    }
    public static class RimVisionInteractions
    {
        internal static readonly ApiRegistry<RimVisionInteractionProvider> Providers = new ApiRegistry<RimVisionInteractionProvider>("interactions");
        public static IDisposable RegisterProvider(string id, RimVisionInteractionProvider provider, int priority = 0) =>
            Providers.Add(id, provider ?? throw new ArgumentNullException(nameof(provider)), priority);
        public static RimVisionInteractionState? Current => Rim3DPreview.Instance?.ActiveWindow?.View?.CurrentApiInteraction;
    }
}

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        internal API.RimVisionInteractionState? CurrentApiInteraction => _actionMenus.ApiInteraction;
        sealed partial class ActionMenus
        {
            API.RimVisionInteractionState? _apiInteraction;
            API.ApiRegistry<API.RimVisionInteractionProvider>.Entry _interactionProvider;
            Pawn _interactionPawn;
            float _interactionStart = -1;
            bool _interactionSent;
            int _interactionFrame = -10;
            internal API.RimVisionInteractionState? ApiInteraction => Time.frameCount - _interactionFrame <= 1 ? _apiInteraction : null;
            internal bool UpdateApiInteraction()
            {
                if (Time.frameCount - _interactionFrame > 1) { _interactionStart = -1; _interactionSent = false; }
                if (ViewBindings.Pressed(ViewAction.CancelAction) || ViewBindings.Pressed(ViewAction.Combat) || ViewBindings.Pressed(ViewAction.Store) ||
                    ViewBindings.Pressed(ViewAction.RotateOrSwap) || ViewBindings.Pressed(ViewAction.UseItem) || ViewBindings.Pressed(ViewAction.Drop) ||
                    ViewBindings.Pressed(ViewAction.CancelPlan)) { ResetApiInteraction(); return false; }
                if (API.RimVisionInteractions.Providers.Entries.Length == 0 || !_view.FirstPerson || !_view.controller.Active || _view.controller.InputBlocked ||
                    !_view.CanCaptureMouse || _view.TextInputFocused || _view.ExternalInputBlocked || _view.constructionMenu || _view.actionMenu || _view.statusMenu ||
                    _view.buildTool != null || _view.zoneTool != null || _view.SelectingFPVTarget || _view.ApiInputBlocked(API.RimVisionInputChannels.Interaction))
                { ResetApiInteraction(); return false; }
                if (ViewBindings.Pressed(ViewAction.Interact)) _view.PickFPVTarget();
                var state = _view.GetApiState();
                API.ApiRegistry<API.RimVisionInteractionProvider>.Entry selected = null;
                var candidate = new API.RimVisionInteraction { Target = state.AimedThing, Cell = state.AimedCell, OverrideDefault = true };
                Rand.PushState();
                try
                {
                    foreach (var entry in API.RimVisionInteractions.Providers.Entries)
                    {
                        if (!entry.Active) continue;
                        var proposed = candidate;
                        try
                        {
                            if (entry.Value.Evaluate(state, ref proposed) && proposed.Valid) { selected = entry; candidate = proposed; break; }
                        }
                        catch (Exception error) { API.RimVisionDiagnostics.ReportFault("interactions", entry.Id, error); }
                    }
                }
                finally { Rand.PopState(); }
                if (selected == null) { ResetApiInteraction(); return false; }
                var previous = _apiInteraction?.Interaction;
                if (_interactionProvider != selected || _interactionPawn != state.Pawn || !previous.HasValue || previous.Value.ActionId != candidate.ActionId ||
                    previous.Value.Target != candidate.Target || previous.Value.TargetMap != candidate.TargetMap || previous.Value.Cell != candidate.Cell || previous.Value.Value != candidate.Value || previous.Value.HoldSeconds != candidate.HoldSeconds || previous.Value.OverrideDefault != candidate.OverrideDefault)
                { _interactionStart = -1; _interactionSent = false; }
                _interactionProvider = selected; _interactionPawn = state.Pawn; _interactionFrame = Time.frameCount;
                string reason;
                bool available = candidate.TargetMap == null
                    ? API.RimVisionActionExecution.TryValidate(candidate.ActionId, new API.RimVisionActionContext(state.Map, state.Pawn, candidate.Target, candidate.Cell, candidate.Value), out _, out reason)
                    : API.RimVisionSpatial.ValidateAction(candidate.ActionId, new API.RimVisionLayerActionContext(state.Pawn, candidate.TargetMap, candidate.Target, candidate.Cell, candidate.Value), out _, out reason);
                bool pressed = ViewBindings.Pressed(ViewAction.Interact);
                bool held = ViewBindings.Held(ViewAction.Interact);
                if (!held || !available || state.Paused || !Application.isFocused || ViewBindings.Pressed(ViewAction.CancelAction))
                { _interactionStart = -1; _interactionSent = false; }
                else if (pressed) _interactionStart = Time.unscaledTime;
                float progress = _interactionStart < 0 ? 0 : candidate.HoldSeconds <= 0 ? 1 : Mathf.Clamp01((Time.unscaledTime - _interactionStart) / candidate.HoldSeconds);
                _apiInteraction = new API.RimVisionInteractionState(candidate, available, reason, progress);
                if (!candidate.OverrideDefault) return false;
                if (progress >= 1 && !_interactionSent)
                {
                    _view.SubmitApiAction(candidate.ActionId, candidate.TargetMap, candidate.Target, candidate.Cell, candidate.Value);
                    _interactionSent = true;
                }
                return pressed || held;
            }
            void ResetApiInteraction()
            { _apiInteraction = null; _interactionProvider = null; _interactionPawn = null; _interactionStart = -1; _interactionSent = false; }
            internal void DrawApiInteraction(Rect viewport)
            {
                var state = ApiInteraction;
                if (!state.HasValue) return;
                var item = state.Value;
                var bounds = new Rect(viewport.center.x - 180, viewport.center.y + 38, 360, 72);
                _view._hudPresentation.ReportHudBounds(API.RimVisionHudElement.Interaction, bounds);
                string label = "[" + ViewBindings.Label(ViewAction.Interact) + "] " + item.Interaction.Label;
                if (!item.Available && !string.IsNullOrEmpty(item.Reason)) label += "\n" + item.Reason;
                if (item.Interaction.Icon != null) GUI.DrawTexture(new Rect(bounds.x, bounds.y, 32, 32), item.Interaction.Icon, ScaleMode.ScaleToFit);
                Widgets.Label(new Rect(bounds.x + 36, bounds.y, bounds.width - 36, 52), label);
                if (!string.IsNullOrEmpty(item.Interaction.Description)) TooltipHandler.TipRegion(bounds, item.Interaction.Description);
                if (item.Interaction.HoldSeconds > 0)
                {
                    var bar = new Rect(bounds.x, bounds.yMax - 8, bounds.width, 4);
                    Widgets.DrawBoxSolid(bar, new Color(.12f, .12f, .12f, .8f));
                    bar.width *= item.Progress; Widgets.DrawBoxSolid(bar, new Color(.45f, .85f, .75f));
                }
            }
        }
    }
}
