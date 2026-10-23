using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Recovery.Rim3D.API;

namespace Recovery.Rim3D
{
    internal sealed class OrderController : IDisposable
    {
        internal delegate bool PickTarget(out RimVisionSpatialHit hit);
        static readonly AccessTools.FieldRef<Designator_Place, Rot4> _rotation =
            AccessTools.FieldRefAccess<Designator_Place, Rot4>("placingRot");
        readonly Map _sourceMap;
        readonly PawnControl _control;
        readonly PickTarget _pick;
        readonly OrderVisualization _visuals;
        readonly Action<string> _notice;
        readonly ColonistCommandController _colonists;
        readonly HashSet<IntVec3> _cells = new HashSet<IntVec3>();
        readonly HashSet<Thing> _things = new HashSet<Thing>();
        readonly HashSet<IntVec3> _preview = new HashSet<IntVec3>();
        readonly HashSet<Thing> _highlights = new HashSet<Thing>();
        readonly Queue<(Pawn Pawn, ColonyOrder Order)> _requests = new Queue<(Pawn, ColonyOrder)>();
        RimVisionSpatialHit _hover;
        RimVisionSpatialHit _anchor;
        bool _hasHover, _dragging;
        float _nextPreview;
        Pawn _owner;

        internal Designator Tool { get; private set; }
        internal Area Area { get; private set; }
        internal Zone Zone { get; private set; }
        internal Map Map { get; private set; }
        internal bool Active => Tool != null || _colonists.Active;
        internal string Label => Tool?.LabelCap ?? _colonists.Label;
        internal bool Personal => _colonists.Active;
        internal int SelectionCount => _cells.Count + _things.Count;

        internal OrderController(Map map, PawnControl control, PickTarget pick, OrderVisualization visuals,
            Action<string> notice, Action releaseMouse)
        {
            _sourceMap = map; _control = control; _pick = pick; _visuals = visuals; _notice = notice;
            _colonists = new ColonistCommandController(control, notice, releaseMouse);
        }

        internal void Select(Designator tool, Area area = null, Zone zone = null)
        {
            Close();
            if (!OrderExecutor.Supported(tool)) return;
            Tool = tool; Area = area; Zone = zone; Map = area?.Map ?? zone?.Map;
            _owner = _control.Pawn;
            Rand.PushState();
            try { using (new OrderContext(Map ?? _sourceMap, Area)) tool.Selected(); }
            finally { Rand.PopState(); }
            _notice("");
        }

        internal void SelectColonist(Pawn pawn)
        {
            Close();
            _owner = _control.Pawn;
            _colonists.Select(pawn);
        }

        internal void Close()
        {
            Tool?.Deselected();
            Tool = null; Area = null; Zone = null; _owner = null;
            _colonists.Close();
            ClearSelection();
        }

        void ClearSelection()
        {
            _cells.Clear(); _things.Clear(); _preview.Clear(); _highlights.Clear();
            _dragging = _hasHover = false;
            Map = Area?.Map ?? Zone?.Map;
            _visuals.Hide();
            _nextPreview = 0;
        }

        internal bool HandleInput(Event current)
        {
            if (!Active || current.type != EventType.KeyDown || current.keyCode != KeyCode.Escape) return false;
            Cancel();
            current.Use();
            return true;
        }

        void Cancel()
        {
            if (SelectionCount > 0 || _dragging) ClearSelection();
            else Close();
        }

        internal void Update(bool input)
        {
            Rand.PushState();
            try { UpdateInput(input); }
            catch (Exception error)
            {
                Log.Error("[RimVision] Order preview failed: " + error);
                Close();
                _notice("Rim3D_ActionUnavailable".Translate());
            }
            finally { Rand.PopState(); }
        }

        void UpdateInput(bool input)
        {
            if (_owner != _control.Pawn || !_control.Active) { Close(); return; }
            if (!input)
            {
                _dragging = false;
                _visuals.Hide();
                return;
            }
            if (ViewBindings.MouseDown(1) || ViewBindings.Pressed(ViewAction.CancelAction)) { Cancel(); return; }
            if (ViewBindings.Pressed(ViewAction.CancelPlan))
            { Select(new Designator_Cancel()); return; }
            _hasHover = _pick(out _hover) && OrderExecutor.VisibleCell(_hover.Map, _hover.Cell);
            if (_colonists.Active)
            {
                _colonists.Update(_hasHover, _hover);
                return;
            }
            if (Tool is Designator_Build build && build.PlacingDef is ThingDef def && def.rotatable &&
                ViewBindings.Pressed(ViewAction.RotateOrSwap))
            {
                _rotation(build) = new Rot4((_rotation(build).AsInt + 1) % 4);
                ClearSelection();
            }
            if (ViewBindings.MouseDown(0) && _hasHover)
            {
                if (!ViewBindings.Held(ViewAction.Sneak)) ClearSelection();
                _hasHover = true;
                if (Map != null && Map != _hover.Map) { _notice("Rim3D_OrdersSameMap".Translate()); return; }
                Map = _hover.Map;
                _anchor = _hover;
                _dragging = true;
            }
            if (_dragging && ViewBindings.MouseUp(0))
            {
                _dragging = false;
                if (_hasHover && _hover.Map == Map) StageSelection();
            }
            if (ViewBindings.Pressed(ViewAction.Interact) && SelectionCount > 0) Confirm();
            if (Time.unscaledTime < _nextPreview) return;
            _nextPreview = Time.unscaledTime + .1f;
            RefreshPreview();
        }

        void StageSelection()
        {
            using (new OrderContext(Map, Area, Tool is Designator_Zone, Zone))
            {
                if (!OrderExecutor.Available(Tool)) return;
                if (_anchor.Cell == _hover.Cell && OrderExecutor.VisibleThing(Map, _anchor.Thing) &&
                    Tool.CanDesignateThing(_anchor.Thing).Accepted)
                {
                    if (SelectionCount < OrderExecutor.MaxTargets) _things.Add(_anchor.Thing);
                    return;
                }
                var rect = CellRect.FromLimits(_anchor.Cell, _hover.Cell);
                if ((long)rect.Width * rect.Height + SelectionCount > OrderExecutor.MaxTargets)
                { _notice("Rim3D_OrdersTooLarge".Translate(OrderExecutor.MaxTargets)); return; }
                int previousCount = SelectionCount;
                foreach (var cell in rect)
                    if (OrderExecutor.VisibleCell(Map, cell) && Tool.CanDesignateCell(cell).Accepted) _cells.Add(cell);
                if (SelectionCount == previousCount)
                    _notice(Tool.CanDesignateCell(_hover.Cell).Reason ?? "Rim3D_OrdersNoTargets".Translate().ToString());
            }
        }

        void Confirm()
        {
            var cells = new IntVec3[_cells.Count]; _cells.CopyTo(cells);
            var things = new Thing[_things.Count]; _things.CopyTo(things);
            Array.Sort(cells, (a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
            Array.Sort(things, (a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            var order = new ColonyOrder { Map = Map, Tool = Tool, Cells = cells, Targets = things, Area = Area, Zone = Zone };
            if (Rim3DMultiplayer.Active)
            {
                if (Rim3DMultiplayer.SendColonyOrder == null) { _notice("Rim3D_OrdersAdapterRequired".Translate()); return; }
                Rim3DMultiplayer.SendColonyOrder(_control, order);
            }
            else _requests.Enqueue((_control.Pawn, order));
            ClearSelection();
        }

        internal void Flush()
        {
            while (_requests.Count > 0)
            {
                var request = _requests.Dequeue();
                if (Rim3DMultiplayer.Active || request.Pawn != _control.Pawn || !_control.Active) continue;
                int count = OrderExecutor.Execute(_control, request.Order);
                _notice((count > 0 ? "Rim3D_OrdersApplied" : "Rim3D_OrdersNoTargets").Translate(count));
            }
        }

        void RefreshPreview()
        {
            _preview.Clear(); _highlights.Clear();
            Map previewMap = Map ?? (_hasHover ? _hover.Map : null);
            if (previewMap == null || !Current.Game.Maps.Contains(previewMap)) { _visuals.Hide(); return; }
            using (new OrderContext(previewMap, Area, Tool is Designator_Zone, Zone))
            {
                if (!OrderExecutor.Available(Tool)) { _visuals.Hide(); return; }
                foreach (var cell in _cells) AddPreviewCell(previewMap, cell);
                foreach (var thing in _things) AddPreviewThing(previewMap, thing);
                if (_hasHover && _hover.Map == previewMap)
                {
                    var rect = CellRect.FromLimits(_dragging ? _anchor.Cell : _hover.Cell, _hover.Cell);
                    if ((long)rect.Width * rect.Height <= OrderExecutor.MaxTargets)
                    {
                        foreach (var cell in rect) AddPreviewCell(previewMap, cell);
                        if (!_dragging || _anchor.Cell == _hover.Cell) AddPreviewThing(previewMap, _hover.Thing);
                    }
                }
            }
            float offset = RimVisionSpatial.MapOffset(_sourceMap, previewMap);
            if (!RimVisionSpatial.Finite(offset)) { _visuals.Hide(); return; }
            _visuals.Show(_preview, _highlights, offset, Tool as Designator_Build,
                Tool is Designator_Place place ? _rotation(place) : Rot4.North);
        }

        void AddPreviewCell(Map map, IntVec3 cell)
        {
            if (!OrderExecutor.VisibleCell(map, cell) || !Tool.CanDesignateCell(cell).Accepted) return;
            _preview.Add(cell);
            foreach (var thing in cell.GetThingList(map)) AddPreviewThing(map, thing);
        }

        void AddPreviewThing(Map map, Thing thing)
        {
            if (OrderExecutor.VisibleThing(map, thing) && Tool.CanDesignateThing(thing).Accepted)
                _highlights.Add(thing);
        }

        public void Dispose()
        {
            Close();
            _requests.Clear();
            _visuals.Dispose();
        }
    }
}
