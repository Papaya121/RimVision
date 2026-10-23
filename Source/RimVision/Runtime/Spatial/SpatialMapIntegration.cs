using System;
using Recovery.Rim3D.API;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        bool _visualLayer;
        RimVisionSpatialProvider _spatialProvider;
        SpatialMapRendering _spatialMaps;
        CommandBuffer _layerStencilClear;
        internal Transform VisualLayerRoot => root.transform;
        internal Map SpatialMap => map;
        float SpatialRoofHeight
        {
            get
            {
                float height = _spatialProvider?.RoofHeight(map) ?? 2.06f;
                return RimVisionSpatial.Finite(height) && height > .12f ? height : 2.06f;
            }
        }

        bool PickSpatialThing(Thing thing) => _spatialProvider?.PickThing(thing) != false;
        bool DrawSpatialMountain(IntVec3 cell) => DrawSpatialRoof(cell) && _spatialProvider?.DrawMountain(map, cell) != false;
        bool DrawSpatialRoofTop(IntVec3 cell) => _spatialProvider?.DrawRoofTop(map, cell) != false;

        bool DrawSpatialFloor(IntVec3 cell) => _spatialProvider?.DrawFloor(map, cell) != false;
        bool DrawSpatialRoof(IntVec3 cell) => _spatialProvider?.DrawRoof(map, cell) != false;
        bool DrawSpatialThing(Thing thing) => _spatialProvider?.DrawThing(thing) != false;

        void UpdateSpatialMaps()
        {
            if (_visualLayer || simulationOnly || _apiDisposed) return;
            if (Current.Game == null || !Current.Game.Maps.Contains(map))
            {
                _spatialMaps?.Dispose();
                _spatialMaps = null;
                return;
            }
            var provider = RimVisionSpatial.Resolve(map);
            if (_spatialProvider != provider || provider != null && _spatialMaps == null)
            {
                bool changed = _spatialProvider != provider;
                _spatialMaps?.Dispose();
                _spatialProvider = provider;
                _spatialMaps = provider == null ? null : new SpatialMapRendering(this, provider);
                if (changed) InvalidateSpatialGeometry();
            }
            _spatialMaps?.Update();
        }

        internal bool TryActivateSpatialMap(Map destination, out MapView scene)
        {
            UpdateSpatialMaps();
            scene = null;
            return _spatialMaps != null && _spatialMaps.TryActivate(destination, out scene);
        }

        internal bool HasSpatialMap(Map destination) => RimVisionSpatial.Connected(_spatialProvider, map, destination);

        internal void WarmupSpatialMaps()
        {
            UpdateSpatialMaps();
            _spatialMaps?.Warmup();
        }

        readonly System.Collections.Generic.List<Map> _selectionMaps = new System.Collections.Generic.List<Map>();
        readonly System.Collections.Generic.List<Pawn> _selectionPawns = new System.Collections.Generic.List<Pawn>();

        System.Collections.Generic.List<Pawn> SpatialPawns()
        {
            _selectionPawns.Clear();
            if (_spatialProvider == null)
            {
                _selectionPawns.AddRange(map.mapPawns.FreeColonistsSpawned);
                RimVisionControl.CollectActors(map, _selectionPawns);
            }
            else
            {
                RimVisionSpatial.CollectGroup(map, _selectionMaps);
                foreach (var layer in _selectionMaps)
                {
                    _selectionPawns.AddRange(layer.mapPawns.FreeColonistsSpawned);
                    RimVisionControl.CollectActors(layer, _selectionPawns);
                }
            }
            _selectionPawns.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            return _selectionPawns;
        }

        internal void DemoteSpatialScene()
        {
            _viewInput.UpdateExtensions(this, true);
            _viewCamera.CloseApi(API.RimVisionCameraExitReason.MapChanged);
            CancelNativeCombatInput();
            _spatialMaps = null;
            _apiRequests.Clear();
            CloseApiRequests();
            ClearOverviewPreview();
            _workMinigame.Dispose();
            DisposeFirstPersonWeapon();
            ResetManualFireInput();
            ResetCarryInput();
            ResetAimCycle();
            _workActions.ResetSowingInput();
            carryWindow?.Close(false);
            carryWindow = null;
            billWindow?.Close(false);
            billWindow = null;
            CloseColonyTab();
            ReleaseMouse();
            DisposeAudioListener();
            _weatherParticles?.Hide();
            _lightningRenderer?.ClearVisuals();
            LightningStrikePatch.Struck -= OnLightningStrike;
            _neighborMapRendering?.Dispose();
            _neighborMapRendering = null;
            _visualLayer = true;
            root.SetActive(false);
        }

        internal void PrepareSpatialActivation()
        {
            if (_weatherParticles == null) _weatherParticles = new WeatherParticlesRenderer(root.transform, Layer, meshes, materials);
            if (_lightningRenderer == null) _lightningRenderer = new LightningRenderer(root.transform, Layer, materials, camera);
            if (_neighborMapRendering == null) _neighborMapRendering = new NeighborMapRendering(map, root.transform, shader, Layer);
        }

        internal void ActivateSpatialScene(MapView previous, SpatialMapRendering group, Vector3 origin)
        {
            _visualLayer = false;
            _spatialMaps = group;
            root.transform.position = origin;
            root.SetActive(true);
            if (_layerStencilClear != null)
            {
                camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, _layerStencilClear);
                _layerStencilClear.Release();
                _layerStencilClear = null;
            }
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.ResetProjectionMatrix();
            LightningStrikePatch.Struck += OnLightningStrike;
            Target = previous.Target;
            previous.Target = null;
            previous.camera.targetTexture = null;
            camera.targetTexture = Target;
            previous.TransferControlTo(this);
            _viewCamera.RestoreView(previous.focus, previous.yaw, previous.pitch, previous._viewCamera.Distance);
            camera.transform.SetPositionAndRotation(previous.camera.transform.position, previous.camera.transform.rotation);
            buildTool = previous.buildTool;
            buildRotation = previous.buildRotation;
            constructionMenu = previous.constructionMenu;
            previous.buildTool = null;
            previous.constructionMenu = false;
            while (previous._overviewPlacements.Count > 0)
                _overviewPlacements.Enqueue(previous._overviewPlacements.Dequeue());
            actionMenu = statusMenu = false;
            _viewInput.ResetMousePosition();
            _apiExtensions = previous._apiExtensions;
            previous._apiExtensions = null;
            OpenApiExtensions();
        }

        internal void InvalidateSpatialGeometry()
        {
            _mountainVertices = null;
            for (int z = 0; z < map.Size.z; z += ChunkSize)
            for (int x = 0; x < map.Size.x; x += ChunkSize)
                MarkCell(new IntVec3(x, 0, z));
            foreach (var pawn in map.mapPawns.AllPawnsSpawned) OnPawnChanged(pawn);
        }

        internal void RenderVisualLayer(Camera source, RenderTexture target, Vector3 origin)
        {
            if (!_visualLayer || _apiDisposed) return;
            root.transform.position = origin;
            root.SetActive(true);
            try
            {
                camera.enabled = false;
                camera.targetTexture = target;
                camera.clearFlags = CameraClearFlags.Nothing;
                camera.orthographic = source.orthographic;
                camera.orthographicSize = source.orthographicSize;
                camera.fieldOfView = source.fieldOfView;
                camera.aspect = source.aspect;
                camera.nearClipPlane = source.nearClipPlane;
                camera.farClipPlane = source.farClipPlane;
                camera.projectionMatrix = source.projectionMatrix;
                if (_layerStencilClear == null)
                {
                    _layerStencilClear = new CommandBuffer { name = "RimVision layer stencil" };
                    _layerStencilClear.ClearRenderTarget(RTClearFlags.Stencil, Color.clear, 1, 0);
                    camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _layerStencilClear);
                }
                camera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                _viewCamera.SetLook(source.transform.eulerAngles.y, source.transform.eulerAngles.x);
                UpdatePawnAppearances();
                UpdateWorldGeometry();
                UpdateHeldThings();
                UpdateSceneActors(camera.transform.localPosition);
                PrepareSceneRendering(out _, out _, out _, out _);
                camera.Render();
            }
            finally { camera.targetTexture = null; root.SetActive(false); }
        }

        internal bool TryPickSpatial(Ray ray, float maximumDistance, Pawn excludedPawn, out RimVisionSpatialHit hit) =>
            _viewTargeting.TryPickSpatial(ray, maximumDistance, excludedPawn, out hit, RimVisionPickFilter.All);

        internal bool TryPickSpatial(Ray ray, float maximumDistance, Pawn excludedPawn, out RimVisionSpatialHit hit, RimVisionPickFilter filter) =>
            _viewTargeting.TryPickSpatial(ray, maximumDistance, excludedPawn, out hit, filter);

        internal bool TryPickSpatialView(Ray ray, float maximumDistance, out RimVisionSpatialHit hit) =>
            TryPickSpatialView(ray, maximumDistance, RimVisionPickFilter.All, out hit);

        internal bool TryPickSpatialView(Ray ray, float maximumDistance, RimVisionPickFilter filter, out RimVisionSpatialHit hit)
        {
            hit = default;
            if (!RimVisionSpatial.Finite(maximumDistance) || maximumDistance <= 0 ||
                !RimVisionSpatial.Finite(ray.origin.x) || !RimVisionSpatial.Finite(ray.origin.y) || !RimVisionSpatial.Finite(ray.origin.z) ||
                !RimVisionSpatial.Finite(ray.direction.sqrMagnitude) || ray.direction.sqrMagnitude < .0001f) return false;
            ray = new Ray(ray.origin, ray.direction.normalized);
            maximumDistance = Mathf.Min(maximumDistance, 2048);
            bool found = TryPickSpatial(ray, maximumDistance, ControlledPawn, out hit, filter);
            if (found) maximumDistance = hit.Distance;
            return _spatialMaps?.TryPick(ray, maximumDistance, ControlledPawn, ref hit, filter) == true || found;
        }

        internal bool RequestLayerAction(string id, Map targetMap, Thing target, IntVec3 cell, int value)
        {
            if (_apiDisposed || _visualLayer || simulationOnly || !FirstPerson || !controller.Active || Rim3DMultiplayer.Simulating) return false;
            var context = new RimVisionLayerActionContext(ControlledPawn, targetMap, target, cell, value);
            Rand.PushState();
            bool accepted;
            try { accepted = RimVisionSpatial.ValidateAction(id, context, out _); }
            finally { Rand.PopState(); }
            if (!accepted) return false;
            if (Rim3DMultiplayer.Active)
            {
                if (Rim3DMultiplayer.SendLayerAction == null) return false;
                Rim3DMultiplayer.SendLayerAction(controller, id, targetMap, target, cell, value);
                return true;
            }
            _apiRequests.Enqueue((ControlledPawn, new Rim3DCommand { Kind = id, Target = target, Cell = cell, Count = value }, targetMap, 0));
            return true;
        }

        public bool ExecuteLayerAction(string id, Map targetMap, Thing target, IntVec3 cell, int value)
        {
            if (!Rim3DMultiplayer.Simulating || !controller.Active) return false;
            return ApplyLayerAction(id, targetMap, target, cell, value);
        }

        bool ApplyLayerAction(string id, Map targetMap, Thing target, IntVec3 cell, int value) =>
            TryApplyLayerAction(id, targetMap, target, cell, value, out _);

        bool TryApplyLayerAction(string id, Map targetMap, Thing target, IntVec3 cell, int value, out string reason, int requestId = 0, Action<RimVisionJobEvent> jobResult = null)
        {
            reason = null;
            if (_apiDisposed || !controller.Active || Rim3DMultiplayer.LocalInput) return false;
            try
            {
                var context = new RimVisionLayerActionContext(ControlledPawn, targetMap, target, cell, value);
                if (!RimVisionSpatial.ValidateAction(id, context, out var action, out reason)) return false;
                return action.CreateJob != null ? RimVisionJobs.Order(controller, action.CreateJob(context), requestId, id, jobResult) : action.Execute(context);
            }
            catch (Exception error)
            {
                Log.Error("[RimVision API] Layer action failed: " + id + "\n" + error);
                reason = "Rim3D_ActionUnavailable".Translate().ToString();
                return false;
            }
        }
    }
}
