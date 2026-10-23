using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Recovery.Rim3D.API;
using UnityEngine;
using HarmonyLib;

namespace Recovery.Rim3D
{
    public sealed class ColonyOrder
    {
        public Map Map;
        public Designator Tool;
        public IntVec3[] Cells;
        public Thing[] Targets;
        public Area Area;
        public Zone Zone;
    }

    internal sealed class OrderContext : IDisposable
    {
        readonly Map _previousMap;
        readonly sbyte _previousMapIndex;
        readonly Area _previousArea;
        readonly List<object> _selection;

        internal OrderContext(Map map, Area area, bool selectZone = false, Zone zone = null)
        {
            _previousMap = Current.Game.CurrentMap;
            _previousMapIndex = Current.Game.currentMapIndex;
            _previousArea = Designator_AreaAllowed.selectedArea;
            Current.Game.currentMapIndex = (sbyte)Current.Game.Maps.IndexOf(map);
            Designator_AreaAllowed.selectedArea = area;
            if (!selectZone) return;
            _selection = new List<object>(Find.Selector.SelectedObjectsListForReading);
            Find.Selector.ClearSelection();
            if (zone != null) Find.Selector.Select(zone, false, false);
        }

        public void Dispose()
        {
            Current.Game.currentMapIndex = _previousMapIndex;
            Designator_AreaAllowed.selectedArea = _previousArea;
            if (_selection == null) return;
            Find.Selector.ClearSelection();
            foreach (var selected in _selection)
            {
                if (selected is Thing thing && (!thing.Spawned || thing.Map != _previousMap) ||
                    selected is Zone zone && (zone.Map != _previousMap || !zone.Map.zoneManager.AllZones.Contains(zone))) continue;
                Find.Selector.Select(selected, false, false);
            }
        }
    }

    internal static class OrderExecutor
    {
        internal const int MaxTargets = 4096;
        static readonly AccessTools.FieldRef<Designator_ZoneAdd, Type> _zoneType =
            AccessTools.FieldRefAccess<Designator_ZoneAdd, Type>("zoneTypeToPlace");

        internal static bool ZoneMatches(Designator tool, Zone zone) => zone == null ||
            !(tool is Designator_ZoneAdd add) || zone.GetType() == _zoneType(add);

        internal static bool Supported(Designator tool) => tool != null &&
            !(tool is Designator_Dropdown) && !(tool is Designator_Paint) &&
            (!(tool is Designator_Place) || tool is Designator_Build) &&
            !tool.GetType().Name.StartsWith("Designator_Plan", StringComparison.Ordinal);

        internal static bool Available(Designator tool) => Supported(tool) && tool.Visible && !tool.Disabled && tool.CanRemainSelected() &&
            (Current.Game.Rules == null || Current.Game.Rules.DesignatorAllowed(tool));

        internal static bool VisibleCell(Map map, IntVec3 cell) => cell.InBounds(map) && !map.fogGrid.IsFogged(cell);

        internal static bool VisibleThing(Map map, Thing thing) => thing != null && !thing.Destroyed &&
            thing.Spawned && thing.Map == map && VisibleCell(map, thing.Position);

        internal static int Execute(PawnControl control, ColonyOrder order)
        {
            if (Rim3DMultiplayer.LocalInput || control?.Active != true || control.Pawn?.Spawned != true ||
                control.Pawn.Faction != Faction.OfPlayer || order?.Map == null || Current.Game == null ||
                !Current.Game.Maps.Contains(order.Map) || !Supported(order.Tool)) return 0;
            if (order.Map != control.Pawn.Map &&
                !RimVisionSpatial.Connected(RimVisionSpatial.Resolve(control.Pawn.Map), control.Pawn.Map, order.Map)) return 0;
            long size = (long)(order.Cells?.Length ?? 0) + (order.Targets?.Length ?? 0);
            if (size == 0 || size > MaxTargets || order.Tool is Designator_AreaAllowed &&
                (order.Area == null || !order.Map.areaManager.AllAreas.Contains(order.Area)) ||
                order.Zone != null && !order.Map.zoneManager.AllZones.Contains(order.Zone) || !ZoneMatches(order.Tool, order.Zone)) return 0;
            using (new OrderContext(order.Map, order.Area, order.Tool is Designator_Zone, order.Zone))
            {
                if (!Available(order.Tool) || !TutorSystem.AllowAction(order.Tool.TutorTagDesignate)) return 0;
                if (order.Tool is Designator_Build build && (build.PlacingDef == null || !build.PlacingDef.BuildableByPlayer ||
                    build.PlacingDef.MadeFromStuff && (build.StuffDef?.IsStuff != true ||
                        !build.StuffDef.stuffProps.CanMake(build.PlacingDef)))) return 0;
                int count = 0;
                if (order.Targets != null)
                {
                    var seen = new HashSet<Thing>();
                    foreach (var target in order.Targets)
                    {
                        if (!seen.Add(target) || !VisibleThing(order.Map, target) ||
                            !order.Tool.CanDesignateThing(target).Accepted) continue;
                        order.Tool.DesignateThing(target);
                        count++;
                    }
                    if (count > 0) order.Tool.Finalize(true);
                }
                if (order.Cells != null)
                {
                    var cells = new List<IntVec3>(order.Cells.Length);
                    var seen = new HashSet<IntVec3>();
                    foreach (var cell in order.Cells)
                        if (seen.Add(cell) && VisibleCell(order.Map, cell) && order.Tool.CanDesignateCell(cell).Accepted)
                            cells.Add(cell);
                    if (cells.Count > 0)
                    {
                        order.Tool.DesignateMultiCell(cells);
                        count += cells.Count;
                    }
                }
                return count;
            }
        }
    }

    public sealed partial class MapView
    {
        OrderController _orders;

        void EnsureColonyOrders()
        {
            if (_orders != null || simulationOnly || _visualLayer) return;
            _orders = new OrderController(map, controller, _viewTargeting.TryPickOrderTarget,
                new OrderVisualization(root.transform, PlanMaterial, (material, geometry, label) =>
                {
                    var previousParent = geometryParent;
                    geometryParent = null;
                    try { return CreateMesh(material, geometry, label); }
                    finally { geometryParent = previousParent; }
                }), SayAction, ReleaseMouse);
        }

        bool UpdateColonyOrders()
        {
            if (!FirstPerson || controller.InputBlocked || MortarAiming || eventCamera || cameraFlight)
            {
                _orders?.Close();
                return false;
            }
            if (!TextInputFocused && !ExternalInputBlocked && ViewBindings.Pressed(ViewAction.Orders))
            {
                EnsureColonyOrders();
                _orders.Close();
                ResetManualFireInput(); ResetCarryInput(); ResetBuildDrag(); ResetRoofRemovalInput();
                _workActions.ResetSowingInput();
                CancelZoneTool();
                if (Find.Targeter?.IsTargeting == true) Find.Targeter.StopTargeting();
                fpvTargetingWasActive = false;
                buildTool = null;
                if (placementPreview != null) placementPreview.SetActive(false);
                constructionMenu = actionMenu = statusMenu = false;
                ReleaseMouse();
                _actionMenus.OpenOrdersMenu();
                return true;
            }
            if (_orders?.Active != true) return false;
            if (!ExternalInputBlocked && !TextInputFocused &&
                (ViewBindings.Pressed(ViewAction.Construction) || ViewBindings.Pressed(ViewAction.Character)))
            { _orders.Close(); return false; }
            if (!Rim3DMultiplayer.Active && !ExternalInputBlocked && !TextInputFocused && ViewBindings.Pressed(ViewAction.Pause))
                PausedByPlayer = !PausedByPlayer;
            _orders.Update(!ExternalInputBlocked && !TextInputFocused && mouseCaptured &&
                !ViewBindings.Held(ViewAction.Cursor) && Application.isFocused && Time.frameCount != mouseCaptureFrame);
            return true;
        }

        internal void FlushColonyOrders() => _orders?.Flush();

        public void ExecuteColonyOrder(ColonyOrder order)
        {
        }
    }
}
