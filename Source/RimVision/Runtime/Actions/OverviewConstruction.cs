using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly Queue<(Map Map, Rim3DCommand Command)> _overviewPlacements = new Queue<(Map, Rim3DCommand)>();
        MapView _overviewPreviewScene;
        MapView _overviewDragScene;

        void ClearOverviewPreview()
        {
            if (_overviewPreviewScene?.placementPreview != null) _overviewPreviewScene.placementPreview.SetActive(false);
            _overviewPreviewScene = null;
            _overviewDragScene?._constructionActions.CancelOverviewDrag();
            _overviewDragScene = null;
        }

        void UpdateOverviewConstruction()
        {
            if (ExternalInputBlocked || TextInputFocused || !Application.isFocused || eventCamera || cameraFlight || ConsumedApiKey(KeyCode.Mouse0))
            { ClearOverviewPreview(); return; }
            if (ViewBindings.Pressed(ViewAction.Construction))
            {
                constructionMenu = !constructionMenu;
                actionMenu = statusMenu = false;
            }
            if (ViewBindings.Pressed(ViewAction.CancelAction) || ViewBindings.MouseDown(1))
            {
                buildTool = null;
                ClearOverviewPreview();
                ResetBuildDrag();
                if (placementPreview != null) placementPreview.SetActive(false);
                return;
            }
            if (constructionMenu || actionMenu || statusMenu || buildTool == null)
            { ClearOverviewPreview(); return; }
            if (buildTool.PlacingDef is ThingDef def && def.rotatable && ViewBindings.Pressed(ViewAction.RotateOrSwap))
            {
                buildRotation = new Rot4((buildRotation.AsInt + 1) % 4);
                buildTool.SetRotation(buildRotation);
            }
            var worldRay = camera.ViewportPointToRay(new Vector3(Input.mousePosition.x / Mathf.Max(1, Screen.width),
                Input.mousePosition.y / Mathf.Max(1, Screen.height), 0));
            var ray = new Ray(worldRay.origin - root.transform.position, worldRay.direction);
            MapView targetScene = _overviewDragScene ?? this;
            if (targetScene._apiDisposed || !Current.Game.Maps.Contains(targetScene.map))
            { ClearOverviewPreview(); return; }
            float planeHeight = API.RimVisionSpatial.MapOffset(map, targetScene.map);
            if (!new Plane(Vector3.up, Vector3.up * planeHeight).Raycast(ray, out float distance))
            { ClearOverviewPreview(); return; }
            Vector3 point = ray.GetPoint(distance);
            var cell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.z));
            if (!cell.InBounds(targetScene.map)) { ClearOverviewPreview(); return; }
            if (_overviewPreviewScene != targetScene && _overviewPreviewScene?.placementPreview != null)
                _overviewPreviewScene.placementPreview.SetActive(false);
            _overviewPreviewScene = targetScene;
            Map previousMap = Current.Game.CurrentMap;
            var previousTool = targetScene.buildTool;
            Rot4 previousRotation = targetScene.buildRotation;
            try
            {
                Current.Game.CurrentMap = targetScene.map;
                targetScene.buildTool = buildTool;
                targetScene.buildRotation = buildRotation;
                targetScene.aimedThing = null;
                targetScene.aimedCell = cell;
                targetScene.aimingAtRoof = false;
                targetScene.UpdatePlacementPreview();
                if (ViewBindings.MouseDown(0))
                {
                    _overviewDragScene = targetScene;
                    targetScene._constructionActions.BeginOverviewDrag(cell);
                }
                if (ViewBindings.MouseUp(0))
                {
                    targetScene._constructionActions.FinishOverviewDrag(cell, this);
                    _overviewDragScene = null;
                }
            }
            finally
            {
                if (targetScene != this)
                {
                    targetScene.buildTool = previousTool;
                    targetScene.buildRotation = previousRotation;
                }
                Current.Game.CurrentMap = previousMap;
            }
        }

        void FlushOverviewPlacements()
        {
            if (_apiDisposed || _visualLayer || simulationOnly || Current.Game == null)
            { _overviewPlacements.Clear(); return; }
            int count = Mathf.Min(_overviewPlacements.Count, 64);
            while (count-- > 0)
            {
                var request = _overviewPlacements.Dequeue();
                if (request.Map == null || !Current.Game.Maps.Contains(request.Map)) continue;
                var command = request.Command;
                if (Rim3DMultiplayer.Active)
                    Rim3DMultiplayer.SendOverviewPlace?.Invoke(request.Map, command.BuildDef, command.Stuff, command.Cell, command.Rotation);
                else ExecuteOverviewPlacement(request.Map, command.BuildDef, command.Stuff, command.Cell, command.Rotation);
            }
        }

        public static bool ExecuteOverviewPlacement(Map targetMap, BuildableDef def, ThingDef stuff, IntVec3 cell, Rot4 rotation)
        {
            if (Current.Game == null || targetMap == null || !Current.Game.Maps.Contains(targetMap) || def == null ||
                !def.BuildableByPlayer || !cell.InBounds(targetMap) ||
                Rim3DMultiplayer.LocalInput || def.MadeFromStuff && (stuff?.IsStuff != true || !stuff.stuffProps.CanMake(def))) return false;
            Map previousMap = Current.Game.CurrentMap;
            try
            {
                Current.Game.CurrentMap = targetMap;
                var tool = new FPVBuildDesignator(def);
                if (!tool.Visible) return false;
                tool.SetRotation(def is ThingDef thingDef && thingDef.rotatable ? rotation : Rot4.North);
                if (def.MadeFromStuff) tool.SetStuffDef(stuff);
                if (!tool.CanDesignateCell(cell).Accepted) return false;
                tool.DesignateSingleCell(cell);
                return true;
            }
            finally { Current.Game.CurrentMap = previousMap; }
        }

        sealed partial class ConstructionActions
        {
            IntVec3 _overviewDragStart = IntVec3.Invalid;
            internal void BeginOverviewDrag(IntVec3 cell) => _overviewDragStart = cell;
            internal void CancelOverviewDrag() => _overviewDragStart = IntVec3.Invalid;

            internal void FinishOverviewDrag(IntVec3 cell, MapView owner)
            {
                IntVec3 start = _overviewDragStart;
                CancelOverviewDrag();
                if (!start.IsValid || _view.buildTool == null || owner._apiDisposed) return;
                bool terrain = _view.buildTool.PlacingDef is TerrainDef || IsConduit(_view.buildTool.PlacingDef);
                var rect = terrain ? CellRect.FromLimits(start, cell) : new CellRect(cell.x, cell.z, 1, 1);
                int count = 0;
                foreach (IntVec3 candidate in rect)
                {
                    if (++count > 4096) break;
                    if (!candidate.InBounds(_view.map)) continue;
                    var report = _view.buildTool.CanDesignateCell(candidate);
                    if (!report.Accepted) { if (!terrain) _view.SayAction(report.Reason); continue; }
                    owner._overviewPlacements.Enqueue((_view.map, new Rim3DCommand { Action = Rim3DAction.Place,
                        BuildDef = _view.buildTool.PlacingDef, Stuff = _view.buildTool.StuffDef,
                        Cell = candidate, Rotation = _view.buildRotation }));
                }
                ResetBuildDrag();
            }
        }
    }
}
