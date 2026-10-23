using System;
using System.Collections.Generic;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        RimVisionExtensionSession _apiExtensions;
        readonly Queue<(Pawn Pawn, Rim3DCommand Command, Map TargetMap, int RequestId)> _apiRequests = new Queue<(Pawn, Rim3DCommand, Map, int)>();
        bool _apiDisposed;

        internal RimVisionViewState GetApiState() => new RimVisionViewState(map, ControlledPawn, FirstPerson,
            controller.Active, controller.InputBlocked || ExternalInputBlocked || constructionMenu || actionMenu || statusMenu || _orders?.Active == true,
            MortarAiming, aimedThing, aimedCell, camera != null ? camera.transform.localPosition : controller.Position, camera != null ? camera.transform.localRotation : Quaternion.identity,
            _viewCamera.Frame, _viewCamera.AimRay, cameraFlight || eventCamera, falling, controller, _aimBlend, _weaponRecoil,
            _apiExtensions?.BlockedChannels ?? RimVisionInputChannels.None);

        internal RimVisionInputBlockInfo[] GetApiInputBlocks() => _apiExtensions?.GetInputBlocks() ?? Array.Empty<RimVisionInputBlockInfo>();

        internal void OpenApiExtensions()
        {
            if (simulationOnly || _visualLayer) return;
            var spatial = new SpatialViewBindings(layer => layer == map ? (float?)0 : _spatialMaps?.Offset(layer),
                layer => layer == map ? root.transform : _spatialMaps?.Root(layer), TryPickSpatialView,
                RequestLayerAction, CreateExtensionSurfaceMaterial, layer => layer == map || _spatialMaps?.Prepare(layer) == true);
            var operations = new ExtensionViewOperations(RequestApiNavigation, GetApiAttachment, SubmitApiAction, GetApiHudLayout, TryPickSpatialView);
            if (_apiExtensions == null)
                _apiExtensions = new RimVisionExtensionSession(GetApiState, RequestExtensionAction,
                    cell => FlyTo(cell), OnPawnChanged, thing => OnThingChanged(map, thing), root.transform, camera, spatial, operations);
            else
                _apiExtensions.Rebind(GetApiState, RequestExtensionAction,
                    cell => FlyTo(cell), OnPawnChanged, thing => OnThingChanged(map, thing), root.transform, camera, spatial, operations);
        }

        Material CreateExtensionSurfaceMaterial(Texture texture, Color color)
        {
            var material = new Material(shader) { hideFlags = HideFlags.DontSave };
            material.mainTexture = texture != null ? texture : Texture2D.whiteTexture;
            material.color = color;
            material.SetFloat("_Opaque", 1);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
            return material;
        }

        internal void UpdateApiExtensions()
        {
            _apiExtensions?.Update();
            _combatActions.FlushNativeInput();
            FlushOverviewPlacements();
            int count = _apiRequests.Count;
            while (count-- > 0 && _apiRequests.Count > 0)
            {
                var request = _apiRequests.Dequeue();
                bool result = false;
                string reason = null;
                if (!_apiDisposed && !Rim3DMultiplayer.Active && FirstPerson && request.Pawn == ControlledPawn)
                {
                    if (request.RequestId != 0)
                        result = request.TargetMap == null
                            ? TryExecuteExtensionAction(request.Command.Kind, request.Command.Target, request.Command.Cell, request.Command.Count, out reason, request.RequestId, NotifyApiJobResult)
                            : TryApplyLayerAction(request.Command.Kind, request.TargetMap, request.Command.Target, request.Command.Cell, request.Command.Count, out reason, request.RequestId, NotifyApiJobResult);
                    else if (request.TargetMap == null)
                        ExecuteExtensionAction(request.Command.Kind, request.Command.Target, request.Command.Cell, request.Command.Count);
                    else ApplyLayerAction(request.Command.Kind, request.TargetMap, request.Command.Target, request.Command.Cell, request.Command.Count);
                }
                if (request.RequestId != 0) NotifyApiResult(request.RequestId, result, reason);
            }
            FlushApiRequests();
        }
        internal void DrawApiHud(Rect viewport) => _apiExtensions?.DrawHud(viewport);

        internal bool RequestExtensionAction(string id, Thing target, IntVec3 cell, int value)
        {
            if (_apiDisposed || _visualLayer || simulationOnly || !FirstPerson || !controller.Active || Rim3DMultiplayer.Simulating) return false;
            var context = new RimVisionActionContext(map, ControlledPawn, target, cell, value);
            Rand.PushState();
            bool accepted;
            string reason;
            try { accepted = RimVisionActionExecution.TryValidate(id, context, out _, out reason); }
            finally { Rand.PopState(); }
            if (!accepted)
            {
                SayAction(reason ?? "Rim3D_ActionUnavailable".Translate().ToString());
                return false;
            }
            if (Rim3DMultiplayer.Active)
            {
                if (Rim3DMultiplayer.Send == null) return false;
                Rim3DMultiplayer.Send(this, new Rim3DCommand { Action = Rim3DAction.Work,
                    Kind = RimVisionApi.CommandPrefix + id, Target = target, Cell = cell, Count = value });
                return true;
            }
            _apiRequests.Enqueue((ControlledPawn, new Rim3DCommand { Kind = id, Target = target, Cell = cell, Count = value }, null, 0));
            return true;
        }

        bool ExecuteExtensionAction(string id, Thing target, IntVec3 cell, int value)
        {
            bool result = TryExecuteExtensionAction(id, target, cell, value, out var reason);
            if (!result) SayAction(reason ?? "Rim3D_ActionUnavailable".Translate().ToString());
            return result;
        }

        bool TryExecuteExtensionAction(string id, Thing target, IntVec3 cell, int value, out string reason, int requestId = 0, Action<RimVisionJobEvent> jobResult = null)
        {
            reason = null;
            if (!controller.Active || Rim3DMultiplayer.LocalInput) return false;
            try
            {
                var context = new RimVisionActionContext(map, ControlledPawn, target, cell, value);
                if (!RimVisionActionExecution.TryValidate(id, context, out var registration, out reason)) return false;
                return registration.CreateJob != null ? RimVisionJobs.Order(controller, registration.CreateJob(context), requestId, id, jobResult) : registration.Execute(context);
            }
            catch (Exception error)
            {
                Log.Error("[RimVision API] Action failed: " + id + "\n" + error);
                reason = "Rim3D_ActionUnavailable".Translate().ToString();
                return false;
            }
        }

        sealed partial class ActionMenus
        {
            void AddExtensionActions()
            {
                var context = new RimVisionActionContext(_view.map, _view.ControlledPawn,
                    _view.contextTarget, _view.aimedCell, 0);
                foreach (var entry in RimVisionApi.Actions.Entries)
                {
                    if (!entry.Active) continue;
                    Rand.PushState();
                    try
                    {
                        if (!entry.Value.Visible(context)) continue;
                        var label = entry.Value.Label(context);
                        if (string.IsNullOrEmpty(label)) continue;
                        bool accepted = RimVisionActionExecution.TryValidate(entry.Id, context, out _, out var reason);
                        if (!accepted && !string.IsNullOrEmpty(reason)) label += " (" + reason + ")";
                        var option = new ContextEntry(ContextAction.Extension, label)
                        { ExtensionId = entry.Id, Cell = context.Cell };
                        if (!accepted) option.Option.Disabled = true;
                        _contextEntries.Add(option);
                    }
                    catch (Exception error) { Log.Error("[RimVision API] Action menu failed: " + entry.Id + "\n" + error); }
                    finally { Rand.PopState(); }
                }
            }

            void RunExtensionAction(ContextEntry entry)
            {
                if (_view.contextPawn != _view.ControlledPawn) { _view.DismissActionMenu(); return; }
                var target = _view.contextTarget;
                _view.DismissActionMenu();
                _view.RequestExtensionAction(entry.ExtensionId, target, entry.Cell, 0);
            }
        }
    }
}

namespace Recovery.Rim3D.API
{
    internal static class RimVisionActionExecution
    {
        internal static bool TryValidate(string id, RimVisionActionContext context,
            out RimVisionApi.ActionRegistration registration, out string reason)
        {
            registration = null;
            reason = null;
            if (context.Map == null || RimVisionControl.MapFor(context.Pawn) != context.Map ||
                !RimVisionApi.Actions.TryGet(id, out registration)) return false;
            if (context.Target != null && (!context.Target.Spawned || context.Target.Destroyed ||
                context.Target.Map != context.Map || context.Map.fogGrid.IsFogged(context.Target.Position))) return false;
            if (context.Cell.IsValid && (!context.Cell.InBounds(context.Map) || context.Map.fogGrid.IsFogged(context.Cell))) return false;
            Rand.PushState();
            try
            {
                var report = registration.Validate(context);
                reason = report.Reason;
                return report.Accepted;
            }
            catch (Exception error)
            {
                Log.ErrorOnce("RimVision action validation " + id + ": " + error, id.GetHashCode());
                reason = "Rim3D_ActionUnavailable".Translate().ToString();
                return false;
            }
            finally { Rand.PopState(); }
        }
    }
}
