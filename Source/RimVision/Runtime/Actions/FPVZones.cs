using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        string zoneTool;
        IntVec3 zoneStart = IntVec3.Invalid;

        float nextZoneOverlay;

        static readonly AccessTools.FieldRef<CompPowerPlantWind, List<IntVec3>> _windBlockedCells =
            AccessTools.FieldRefAccess<CompPowerPlantWind, List<IntVec3>>("windPathBlockedCells");
        static readonly AccessTools.FieldRef<CompPowerPlantWind, List<Thing>> _windBlockedThings =
            AccessTools.FieldRefAccess<CompPowerPlantWind, List<Thing>>("windPathBlockedByThings");

        static bool IsHiddenPowerConduit(Thing thing)
        {
            return IsConduit(thing.def) && (thing.def.defName == "HiddenConduit" ||
                thing.def.graphicData == null || thing.def.graphicData.color.a <= 0);
        }

        bool PowerHoverVisible(Thing thing, bool includeHidden = false) => _worldOverlays.PowerHoverVisible(thing, includeHidden);

        void UpdatePowerHoverOverlay() => _worldOverlays.UpdatePowerHoverOverlay();

        static void ClearHoverGeometry(Geometry geometry)
        {
            geometry.Vertices.Clear(); geometry.Normals.Clear(); geometry.Uvs.Clear();
            geometry.CardCenters.Clear(); geometry.Indices.Clear();
        }

        bool WindHoverBoundary(IntVec3 cell, bool blocked) => _worldOverlays.WindHoverBoundary(cell, blocked);

        void AppendWindBlockerDot(Vector3 center) => _worldOverlays.AppendWindBlockerDot(center);

        int PowerHoverSignature(CompPower power) => _worldOverlays.PowerHoverSignature(power);

        void AppendPowerHover(CompPower power) => _worldOverlays.AppendPowerHover(power);

        void AppendPowerHoverLine(Vector3 start, Vector3 end, bool dashed, float width = .06f) => _worldOverlays.AppendPowerHoverLine(start, end, dashed, width);

        IntVec3 ZoneAimCell() => _zoneActions.ZoneAimCell();

        void OpenZoneMenu() => _actionMenus.OpenZoneMenu();

        void OpenZoneSettings(Zone zone) => _actionMenus.OpenZoneSettings(zone);

        public void NotifyGrowingZoneCreated(Zone_Growing zone) => _zoneActions.NotifyGrowingZoneCreated(zone);

        void CancelZoneTool() => _zoneActions.CancelZoneTool();

        bool UpdateZoneInput() => _zoneActions.UpdateZoneInput();

        void ApplyZoneRectangle(string kind, IntVec3 start, IntVec3 end, bool remove) => _zoneActions.ApplyZoneRectangle(kind, start, end, remove);

        bool ZoneCellAllowed(IntVec3 cell, bool growing) => _zoneActions.ZoneCellAllowed(cell, growing);

        void ReplaceZoneMesh(ref GameObject obj, Material material, Geometry geometry, string label) => _zoneActions.ReplaceZoneMesh(ref obj, material, geometry, label);

        void UpdateZonePreview(IntVec3 cell) => _zoneActions.UpdateZonePreview(cell);

        void UpdateZoneVisibility() => _zoneActions.UpdateZoneVisibility();

        void RefreshZoneOverlay() => _zoneActions.RefreshZoneOverlay();

        bool ZoneBoundary(IntVec3 cell, Zone zone, bool home) => _zoneActions.ZoneBoundary(cell, zone, home);

        static void AddZoneStrip(Geometry geometry, float left, float right, float bottom, float top, float height = .047f)
        {
            geometry.FloorCard(new Vector3((left + right) * .5f, height, (bottom + top) * .5f),
                right - left, top - bottom);
        }

        void DrawZoneHud(Rect viewport) => _actionMenus.DrawZoneHud(viewport);

        sealed class FPVStorageTab : ITab_Storage
        {
            public Vector2 Size => size;
            public void Draw() => FillTab();
        }

        sealed class FPVStorageWindow : Window
        {
            readonly Map _map;
            readonly Thing _target;
            readonly FPVStorageTab _storage;
            public override Vector2 InitialSize => new Vector2(_storage.Size.x + 36, _storage.Size.y + 92);

            internal FPVStorageWindow(Map map, Thing target)
            {
                _map = map; _target = target;
                forcePause = true; doCloseX = true; closeOnCancel = true; absorbInputAroundWindow = true;
                Find.Selector.ClearSelection(); Find.Selector.Select(target, false, false);
                _storage = new FPVStorageTab();
                _storage.OnOpen();
            }

            bool Valid => Find.CurrentMap == _map && _target.Spawned && _target.Map == _map &&
                _target.Faction == Faction.OfPlayer && Find.Selector.SingleSelectedThing == _target && _storage.IsVisible;

            public override void WindowUpdate()
            {
                base.WindowUpdate();
                if (!Valid) Close();
            }

            public override void DoWindowContents(Rect rect)
            {
                if (!Valid) { Close(); return; }
                Widgets.Label(new Rect(0, 0, rect.width - 36, 28), _target.LabelCap);
                GUI.BeginGroup(new Rect(0, 44, _storage.Size.x, _storage.Size.y));
                try { _storage.Draw(); } finally { GUI.EndGroup(); }
            }

            public override void PostClose()
            {
                if (Find.Selector.SingleSelectedThing == _target) Find.Selector.ClearSelection();
                base.PostClose();
            }
        }

        sealed class FPVStorageInventoryWindow : Window
        {
            readonly MapView _owner;
            readonly Pawn _pawn;
            readonly Thing _target;
            readonly List<Thing> _items = new List<Thing>();
            Vector2 _scroll;
            Thing _selected;
            int _count;

            public override Vector2 InitialSize => new Vector2(540, Mathf.Min(600, UI.screenHeight - 20));
            bool Valid => _owner.FirstPerson && Find.CurrentMap == _owner.map && _owner.ControlledPawn == _pawn &&
                _owner._carryActions.CanOpenStorage(_target);

            internal FPVStorageInventoryWindow(MapView owner, Thing target)
            {
                _owner = owner; _pawn = owner.ControlledPawn; _target = target;
                layer = WindowLayer.Dialog;
                forcePause = preventCameraMotion = absorbInputAroundWindow = true;
                closeOnAccept = false;
                doCloseX = true;
            }

            public override void WindowUpdate()
            {
                base.WindowUpdate();
                if (!Valid) Close(false);
            }

            public override void DoWindowContents(Rect rect)
            {
                if (!Valid) { Close(false); return; }
                _items.Clear();
                var slots = CarryActions.StorageSlots(_target);
                foreach (var item in slots.HeldThings)
                    if (item.Spawned && item.Map == _owner.map && item.def.category == ThingCategory.Item &&
                        item.GetSlotGroup() == slots && !_owner.map.fogGrid.IsFogged(item.Position)) _items.Add(item);
                if (!_items.Contains(_selected)) { _selected = null; _count = 0; }
                Widgets.Label(new Rect(0, 0, rect.width - 32, 28), "Rim3D_StorageInventory".Translate(_target.LabelCap));
                var list = new Rect(0, 38, rect.width, Mathf.Max(1, rect.height - 174));
                var content = new Rect(0, 0, list.width - 18, Mathf.Max(list.height, _items.Count * 48));
                Widgets.BeginScrollView(list, ref _scroll, content);
                if (_items.Count == 0) Widgets.Label(new Rect(8, 0, content.width - 16, 28), "Rim3D_EmptyInventory".Translate());
                for (int i = 0; i < _items.Count; i++)
                {
                    float y = i * 48;
                    if (y + 48 < _scroll.y || y > _scroll.y + list.height) continue;
                    var item = _items[i];
                    var row = new Rect(0, y, content.width, 44);
                    if (item == _selected) Widgets.DrawBoxSolid(row, new Color(.2f, .35f, .45f));
                    Widgets.ThingIcon(new Rect(4, y + 4, 36, 36), item);
                    Widgets.Label(new Rect(48, y + 8, content.width - 52, 32), item.LabelCap);
                    if (Widgets.ButtonInvisible(row)) { _selected = item; _count = item.stackCount; }
                }
                Widgets.EndScrollView();
                if (_selected == null) return;
                _count = Mathf.Clamp(_count, 1, _selected.stackCount);
                float bottom = rect.height - 126;
                Widgets.Label(new Rect(0, bottom, rect.width, 26), "Rim3D_TakeCount".Translate(_count, _selected.stackCount));
                _count = Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(0, bottom + 30, rect.width, 26), _count, 1, _selected.stackCount));
                if (Widgets.ButtonText(new Rect(0, bottom + 62, rect.width, 32), "Rim3D_Take".Translate()))
                    _owner._carryActions.TakeStoredItem(_selected, _count);
                Widgets.Label(new Rect(0, bottom + 98, rect.width, 28), _owner.actionMessage);
            }
        }

        sealed class FPVZoneWindow : Window
        {
            readonly Map map;
            readonly Zone zone;
            readonly FPVStorageTab storage;
            public override Vector2 InitialSize => storage == null ? new Vector2(500, 500) : new Vector2(storage.Size.x + 36, storage.Size.y + 92);
            public FPVZoneWindow(Map map, Zone zone)
            {
                this.map = map; this.zone = zone;
                forcePause = true; doCloseX = true; closeOnCancel = true; absorbInputAroundWindow = true;
                Find.Selector.ClearSelection(); Find.Selector.Select(zone, false, false);
                if (zone is Zone_Stockpile) { storage = new FPVStorageTab(); storage.OnOpen(); }
            }
            public override void WindowUpdate()
            {
                base.WindowUpdate();
                if (Find.CurrentMap != map || !map.zoneManager.AllZones.Contains(zone)) Close();
            }
            public override void DoWindowContents(Rect rect)
            {
                if (Find.Selector.SelectedZone != zone) { Close(); return; }
                Widgets.Label(new Rect(0, 0, rect.width - 36, 28), zone.label + " · " + zone.CellCount);
                if (storage != null)
                {
                    GUI.BeginGroup(new Rect(0, 44, storage.Size.x, storage.Size.y));
                    try { storage.Draw(); } finally { GUI.EndGroup(); }
                    return;
                }
                float y = 44;
                foreach (var gizmo in zone.GetGizmos())
                {
                    if (!(gizmo is Command command) || command is Designator || !command.Visible || command.Disabled) continue;
                    string label = command.LabelCap;
                    if (command is Command_Toggle toggle && toggle.isActive()) label = "✓ " + label;
                    if (Widgets.ButtonText(new Rect(0, y, rect.width, 32), label)) command.ProcessInput(Event.current);
                    y += 38;
                    if (y + 32 > rect.height) break;
                }
            }
            public override void PostClose()
            {
                if (Find.Selector.SelectedZone == zone) Find.Selector.ClearSelection();
                base.PostClose();
            }
        }

        sealed partial class ActionMenus
        {
            internal void OpenZoneMenu()
            {
                var options = new List<FloatMenuOption>();
                foreach (string kind in new[] { "Stockpile", "Dumping", "Growing", "HomeAdd", "HomeRemove", "Erase" })
                {
                    string selected = kind;
                    options.Add(new FloatMenuOption(("Rim3D_Zone" + kind).Translate(), () =>
                    {
                        _view.CancelZoneTool();
                        _view.buildTool = null; _view.constructionDef = null;
                        if (_view.placementPreview != null) _view.placementPreview.SetActive(false);
                        _view.zoneTool = selected;
                        _view.nextZoneOverlay = 0;
                    }));
                }
                var cell = _view.ZoneAimCell();
                var zone = cell.IsValid ? _view.map.zoneManager.ZoneAt(cell) : null;
                if (zone != null)
                    options.Add(new FloatMenuOption("Rim3D_ZoneSettings".Translate(zone.label), () => _view.OpenZoneSettings(zone)));
                _view.constructionMenu = _view.actionMenu = _view.statusMenu = false;
                _view.buildSearchFocused = false;
                GUI.FocusControl(null);
                _view.ReleaseMouse();
                Find.WindowStack.Add(new FloatMenu(options));
            }

            internal void OpenZoneSettings(Zone zone)
            {
                _view.CancelZoneTool();
                _view.ReleaseMouse();
                Find.WindowStack.Add(new FPVZoneWindow(_view.map, zone));
            }

            internal void OpenStorageSettings(Thing target)
            {
                if (target == null || !target.Spawned || target.Map != _view.map ||
                    target.Faction != Faction.OfPlayer || CarryActions.StorageSlots(target) == null) return;
                _view.CancelZoneTool();
                _view.ReleaseMouse();
                Find.WindowStack.Add(new FPVStorageWindow(_view.map, target));
            }

            internal void OpenStorageInventory(Thing target)
            {
                if (Rim3DMultiplayer.Simulating || !_view._carryActions.CanOpenStorage(target)) return;
                _view.ResetCarryInput();
                _view.constructionMenu = _view.actionMenu = _view.statusMenu = false;
                _view.ReleaseMouse();
                Find.WindowStack.Add(new FPVStorageInventoryWindow(_view, target));
            }

            internal void DrawZoneHud(Rect viewport)
            {
                if (_view.zoneTool == null) return;
                var cell = _view.ZoneAimCell();
                string size = "";
                if (_view.zoneStart.IsValid && cell.IsValid)
                { var rect = CellRect.FromLimits(_view.zoneStart, cell); size = " · " + rect.Width + " × " + rect.Height; }
                _view.Badge(new Rect(viewport.center.x - 350, viewport.yMax - 100, 700, 80),
                    ("Rim3D_Zone" + _view.zoneTool).Translate() + size + "\n" +
                    (_view.zoneStart.IsValid ? "Rim3D_ZoneSecondCorner" : "Rim3D_ZoneFirstCorner").Translate() + "\n" +
                    "Rim3D_ZoneControls".TranslateBindings(), _view.activityStyle);
            }

        }

        readonly ZoneActions _zoneActions;

        sealed partial class ZoneActions
        {
            readonly MapView _view;

            internal ZoneActions(MapView view)
            {
                _view = view;
            }

            IntVec3 _zonePreviewStart = IntVec3.Invalid, _zonePreviewEnd = IntVec3.Invalid;

            GameObject _zonePreview, _zoneOverlay;

            Material _zonePreviewMaterial, _zoneOverlayMaterial;

            int _zoneOverlaySignature;

            internal bool ShowZoneFills;
            readonly Dictionary<Color, ZoneFill> _zoneFills = new Dictionary<Color, ZoneFill>();

            sealed class ZoneFill
            {
                internal readonly Geometry Geometry = new Geometry();
                internal Material Material;
                internal GameObject Root;
            }

            Zone_Growing _createdGrowingZone;

            internal void NotifyGrowingZoneCreated(Zone_Growing zone)
            {
                if (!_view.simulationOnly) _createdGrowingZone = zone;
            }

            internal IntVec3 ZoneAimCell()
            {
                var ray = new Ray(_view.CameraMapPosition, _view.camera.transform.forward);
                if (ray.direction.y >= -.015f || !new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance) || distance > 40)
                    return IntVec3.Invalid;
                var cell = ray.GetPoint(distance).ToIntVec3();
                return cell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(cell) ? cell : IntVec3.Invalid;
            }

            internal void CancelZoneTool()
            {
                if (_view.zoneTool != null) _view.nextZoneOverlay = 0;
                _view.zoneTool = null;
                _view.zoneStart = IntVec3.Invalid;
                if (_zonePreview != null) _zonePreview.SetActive(false);
            }

            internal bool UpdateZoneInput()
            {
                if (_view.zoneTool == null) return false;
                _view.ResetCarryInput(); _view.ResetRoofRemovalInput();
                if (ViewBindings.Pressed(ViewAction.CancelAction) || ViewBindings.MouseDown(1))
                { _view.CancelZoneTool(); return true; }
                if (ViewBindings.Held(ViewAction.Cursor)) return true;
                var cell = _view.ZoneAimCell();
                _view.UpdateZonePreview(cell);
                if (ViewBindings.MouseDown(0))
                {
                    if (!cell.IsValid) _view.SayAction("Rim3D_ZoneAimGround".Translate());
                    else if (!_view.zoneStart.IsValid) _view.zoneStart = cell;
                    else
                    {
                        bool remove = ViewBindings.Held(ViewAction.Sprint);
                        _view.ApplyZoneRectangle(_view.zoneTool, _view.zoneStart, cell, remove);
                        _view.zoneStart = IntVec3.Invalid;
                        _view.nextZoneOverlay = 0;
                    }
                }
                if (ViewBindings.Pressed(ViewAction.Interact) && cell.IsValid)
                {
                    var zone = _view.map.zoneManager.ZoneAt(cell);
                    if (zone != null) _view.OpenZoneSettings(zone);
                }
                return true;
            }

            internal void ApplyZoneRectangle(string kind, IntVec3 start, IntVec3 end, bool remove)
            {
                if (!start.InBounds(_view.map) || !end.InBounds(_view.map)) return;
                var rect = CellRect.FromLimits(start, end);
                if (rect.Width > 64 || rect.Height > 64) { _view.SayAction("Rim3D_ZoneTooLarge".Translate()); return; }
                if (kind != "Stockpile" && kind != "Dumping" && kind != "Growing" && kind != "HomeAdd" && kind != "HomeRemove" && kind != "Erase") return;
                if (Rim3DMultiplayer.LocalInput)
                {
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                    { Action = Rim3DAction.Zone, Kind = kind, Cell = start, Origin = end.ToVector3(), Count = remove ? 1 : 0 });
                    return;
                }
                Zone target = _view.map.zoneManager.ZoneAt(start);
                bool growing = kind == "Growing";
                bool area = kind == "HomeAdd" || kind == "HomeRemove";
                bool erase = kind == "Erase" || remove;
                bool created = false;
                if (!area && kind != "Erase" && target != null &&
                    (growing ? !(target is Zone_Growing) : !(target is Zone_Stockpile)))
                { _view.SayAction("Rim3D_ZoneWrongType".Translate()); return; }
                var touchedSet = new HashSet<Zone>();
                var touched = new List<Zone>();
                int changed = 0;
                foreach (var cell in rect)
                {
                    if (_view.map.fogGrid.IsFogged(cell)) continue;
                    if (area)
                    {
                        bool value = kind == "HomeAdd" && !remove;
                        if (_view.map.areaManager.Home[cell] != value) { _view.map.areaManager.Home[cell] = value; changed++; }
                        continue;
                    }
                    var existing = _view.map.zoneManager.ZoneAt(cell);
                    if (erase)
                    {
                        // Starting within a zone limits shrinking to that zone.
                        if (existing == null || target != null && existing != target) continue;
                        if (kind != "Erase" && (target == null || existing != target)) continue;
                        existing.RemoveCell(cell);
                        if (touchedSet.Add(existing)) touched.Add(existing);
                        changed++;
                        continue;
                    }
                    if (existing != null || !_view.ZoneCellAllowed(cell, growing)) continue;
                    if (target == null)
                    {
                        target = growing ? (Zone)new Zone_Growing(_view.map.zoneManager) :
                            new Zone_Stockpile(kind == "Dumping" ? StorageSettingsPreset.DumpingStockpile : StorageSettingsPreset.DefaultStockpile, _view.map.zoneManager);
                        _view.map.zoneManager.RegisterZone(target);
                        created = true;
                    }
                    target.AddCell(cell);
                    if (touchedSet.Add(target)) touched.Add(target);
                    changed++;
                }
                foreach (var zone in touched)
                {
                    if (zone.CellCount == 0) zone.Delete();
                    else zone.CheckContiguous();
                    if (zone is Zone_Stockpile stockpile) stockpile.slotGroup.RemoveHaulDesignationOnStoredThings();
                }
                _view.SayAction(changed == 0 ? "Rim3D_ZoneNoCells".Translate().ToString() : "Rim3D_ZoneChanged".Translate(changed).ToString());
                if (created && target is Zone_Growing growingZone)
                {
                    if (Rim3DMultiplayer.Simulating)
                        Rim3DMultiplayer.GrowingZoneCreated?.Invoke(_view.ControlledPawn, growingZone);
                    else NotifyGrowingZoneCreated(growingZone);
                }
            }

            internal bool ZoneCellAllowed(IntVec3 cell, bool growing)
            {
                if (!Designator_ZoneAdd.IsZoneableCell(cell, _view.map).Accepted) return false;
                if (growing)
                {
                    float fertility = ModsConfig.BiotechActive ? .5f : ThingDefOf.Plant_Potato.plant.fertilityMin;
                    if (ModsConfig.IdeologyActive && BuildCopyCommandUtility.FindAllowedDesignator(TerrainDefOf.FungalGravel, true) != null)
                        fertility = Mathf.Min(fertility, ThingDefOf.Plant_Nutrifungus.plant.fertilityMin);
                    return cell.GetFertility(_view.map) >= fertility;
                }
                if (cell.GetTerrain(_view.map).passability == Traversability.Impassable) return false;
                foreach (var thing in cell.GetThingList(_view.map)) if (!thing.def.CanOverlapZones) return false;
                return true;
            }

            internal void ReplaceZoneMesh(ref GameObject obj, Material material, Geometry geometry, string label)
            {
                if (obj != null)
                {
                    var mesh = obj.GetComponent<MeshFilter>().sharedMesh;
                    mesh.Clear();
                    mesh.SetVertices(geometry.Vertices); mesh.SetNormals(geometry.Normals);
                    mesh.SetUVs(0, geometry.Uvs); mesh.SetUVs(1, geometry.CardCenters);
                    mesh.SetTriangles(geometry.Indices, 0); mesh.RecalculateBounds();
                    obj.SetActive(true);
                    return;
                }
                var previous = _view.geometryParent;
                _view.geometryParent = null;
                try { obj = _view.CreateMesh(material, geometry, label); }
                finally { _view.geometryParent = previous; }
            }

            internal void UpdateZonePreview(IntVec3 cell)
            {
                if (!cell.IsValid) { if (_zonePreview != null) _zonePreview.SetActive(false); return; }
                var start = _view.zoneStart.IsValid ? _view.zoneStart : cell;
                if (start != _zonePreviewStart || cell != _zonePreviewEnd || _zonePreview == null)
                {
                    var rect = CellRect.FromLimits(start, cell);
                    var geometry = new Geometry();
                    const float line = .035f;
                    AddZoneStrip(geometry, rect.minX, rect.maxX + 1, rect.minZ, rect.minZ + line, .06f);
                    AddZoneStrip(geometry, rect.minX, rect.maxX + 1, rect.maxZ + 1 - line, rect.maxZ + 1, .06f);
                    AddZoneStrip(geometry, rect.minX, rect.minX + line, rect.minZ + line, rect.maxZ + 1 - line, .06f);
                    AddZoneStrip(geometry, rect.maxX + 1 - line, rect.maxX + 1, rect.minZ + line, rect.maxZ + 1 - line, .06f);
                    if (_zonePreviewMaterial == null) _zonePreviewMaterial = _view.PlanMaterial(Color.green);
                    _zonePreviewMaterial.color = rect.Width <= 64 && rect.Height <= 64 ? new Color(.3f, 1, .65f) : Color.red;
                    _view.ReplaceZoneMesh(ref _zonePreview, _zonePreviewMaterial, geometry, "FPV zone rectangle");
                    _zonePreviewStart = start; _zonePreviewEnd = cell;
                }
                _zonePreview.SetActive(true);
            }

            internal void UpdateZoneVisibility()
            {
                if (_createdGrowingZone != null && !Rim3DMultiplayer.Simulating)
                {
                    var zone = _createdGrowingZone;
                    _createdGrowingZone = null;
                    if (_view.FirstPerson && Find.CurrentMap == _view.map && _view.map.zoneManager.AllZones.Contains(zone))
                        _view.OpenZoneSettings(zone);
                }
                if (!ShowZoneFills)
                    foreach (var fill in _zoneFills.Values)
                        if (fill.Root != null) fill.Root.SetActive(false);
                if (!Rim3DSettings.Current.ZoneOutlines && !ShowZoneFills)
                {
                    if (_zoneOverlay != null && _zoneOverlay.activeSelf) _zoneOverlay.SetActive(false);
                    _view.nextZoneOverlay = 0;
                    return;
                }
                if (Time.unscaledTime < _view.nextZoneOverlay) return;
                _view.nextZoneOverlay = Time.unscaledTime + .5f;
                _view.RefreshZoneOverlay();
            }

            internal void RefreshZoneOverlay()
            {
                var position = _view.CameraMapPosition;
                var center = new IntVec3(Mathf.FloorToInt(position.x), 0, Mathf.FloorToInt(position.z));
                bool home = _view.zoneTool == "HomeAdd" || _view.zoneTool == "HomeRemove";
                bool passive = _view.zoneTool == null;
                int signature = home ? 2 : passive ? 0 : 1;
                unchecked
                {
                    signature = signature * 397 ^ ShowZoneFills.GetHashCode();
                    signature = signature * 397 ^ Rim3DSettings.Current.ZoneOutlines.GetHashCode();
                    signature = signature * 397 ^ center.GetHashCode();
                    for (int z = Mathf.Max(0, center.z - 40); z <= Mathf.Min(_view.map.Size.z - 1, center.z + 40); z++)
                        for (int x = Mathf.Max(0, center.x - 40); x <= Mathf.Min(_view.map.Size.x - 1, center.x + 40); x++)
                        {
                            var cell = new IntVec3(x, 0, z);
                            var zone = _view.map.zoneManager.ZoneAt(cell);
                            int value = _view.map.fogGrid.IsFogged(cell) ? 0 : home ? (_view.map.areaManager.Home[cell] ? 1 : 0) :
                                passive && !(zone is Zone_Stockpile) && !(zone is Zone_Growing) ? 0 : zone?.GetHashCode() ?? 0;
                            signature = signature * 31 + value;
                            if (ShowZoneFills && value != 0)
                                signature = signature * 31 + (home ? _view.map.areaManager.Home.Color : zone.color).GetHashCode();
                        }
                }
                if (_zoneOverlay != null && signature == _zoneOverlaySignature)
                {
                    _zoneOverlay.SetActive(Rim3DSettings.Current.ZoneOutlines);
                    if (ShowZoneFills)
                        foreach (var fill in _zoneFills.Values)
                            if (fill.Root != null) fill.Root.SetActive(fill.Geometry.Vertices.Count > 0);
                    return;
                }
                _zoneOverlaySignature = signature;
                foreach (var fill in _zoneFills.Values)
                {
                    ClearHoverGeometry(fill.Geometry);
                    if (fill.Root != null) fill.Root.SetActive(false);
                }
                var geometry = new Geometry();
                float lineWidth = passive ? .025f : .035f;
                for (int z = Mathf.Max(0, center.z - 40); z <= Mathf.Min(_view.map.Size.z - 1, center.z + 40); z++)
                    for (int x = Mathf.Max(0, center.x - 40); x <= Mathf.Min(_view.map.Size.x - 1, center.x + 40); x++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        if (_view.map.fogGrid.IsFogged(cell)) continue;
                        var zone = _view.map.zoneManager.ZoneAt(cell);
                        if (home ? !_view.map.areaManager.Home[cell] : zone == null) continue;
                        if (passive && !(zone is Zone_Stockpile) && !(zone is Zone_Growing)) continue;
                        if (ShowZoneFills)
                        {
                            var color = home ? _view.map.areaManager.Home.Color : zone.color;
                            if (!_zoneFills.TryGetValue(color, out var fill))
                                _zoneFills.Add(color, fill = new ZoneFill());
                            AddZoneStrip(fill.Geometry, x, x + 1, z, z + 1, .043f);
                        }
                        if (!Rim3DSettings.Current.ZoneOutlines) continue;
                        bool south = _view.ZoneBoundary(cell + IntVec3.South, zone, home);
                        bool north = _view.ZoneBoundary(cell + IntVec3.North, zone, home);
                        bool west = _view.ZoneBoundary(cell + IntVec3.West, zone, home);
                        bool east = _view.ZoneBoundary(cell + IntVec3.East, zone, home);
                        // Each strip stays inside its cell; trim vertical strips at
                        // corners so transparent faces never overlap or draw twice.
                        const float inset = .002f;
                        float left = x + inset, right = x + 1 - inset;
                        float bottom = z + inset, top = z + 1 - inset;
                        if (south) AddZoneStrip(geometry, left, right, bottom, bottom + lineWidth);
                        if (north) AddZoneStrip(geometry, left, right, top - lineWidth, top);
                        if (west) AddZoneStrip(geometry, left, left + lineWidth,
                            south ? bottom + lineWidth : bottom, north ? top - lineWidth : top);
                        if (east) AddZoneStrip(geometry, right - lineWidth, right,
                            south ? bottom + lineWidth : bottom, north ? top - lineWidth : top);
                    }
                if (_zoneOverlayMaterial == null)
                {
                    _zoneOverlayMaterial = _view.PlanMaterial(new Color(.3f, .7f, 1));
                    _zoneOverlayMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _zoneOverlayMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _zoneOverlayMaterial.SetFloat("_ZWrite", 0);
                    _zoneOverlayMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
                    _zoneOverlayMaterial.SetOverrideTag("RenderType", "Transparent");
                    _zoneOverlayMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                }
                // A muted, thin floor outline stays visible during normal play;
                // the stronger editing color is reserved for the zone tool.
                _zoneOverlayMaterial.color = passive ? new Color(.28f, .40f, .46f) : new Color(.3f, .7f, 1);
                _zoneOverlayMaterial.SetFloat("_SurfaceOpacity", passive ? .3f : .65f);
                _view.ReplaceZoneMesh(ref _zoneOverlay, _zoneOverlayMaterial, geometry, "FPV zone boundaries");
                _zoneOverlay.SetActive(Rim3DSettings.Current.ZoneOutlines);
                foreach (var entry in _zoneFills)
                {
                    var fill = entry.Value;
                    if (fill.Geometry.Vertices.Count == 0) continue;
                    if (fill.Material == null)
                    {
                        fill.Material = _view.PlanMaterial(entry.Key);
                        fill.Material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        fill.Material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        fill.Material.SetFloat("_ZWrite", 0);
                        fill.Material.SetFloat("_SurfaceOpacity", .25f);
                        fill.Material.SetOverrideTag("RenderType", "Transparent");
                        fill.Material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    }
                    _view.ReplaceZoneMesh(ref fill.Root, fill.Material, fill.Geometry, "FPV zone fill");
                }
            }

            internal bool ZoneBoundary(IntVec3 cell, Zone zone, bool home)
            {
                return !cell.InBounds(_view.map) || (home ? !_view.map.areaManager.Home[cell] : _view.map.zoneManager.ZoneAt(cell) != zone);
            }

        }

        sealed partial class WorldOverlays
        {
            GameObject _powerHoverOverlay;

            Material _powerHoverMaterial;

            Thing _powerHoverTarget;

            PowerNet _powerHoverNet;

            float _nextPowerHoverUpdate;

            int _powerHoverSignature;

            bool _powerOverlayWasActive;
            IntVec3 _powerOverlayCenter = IntVec3.Invalid;

            readonly Geometry _powerHoverGeometry = new Geometry();

            readonly HashSet<IntVec3> _windHoverCells = new HashSet<IntVec3>();

            readonly HashSet<IntVec3> _windBlockedHoverCells = new HashSet<IntVec3>();

            readonly HashSet<Thing> _windHoverBlockers = new HashSet<Thing>();

            readonly Geometry _windFreeHoverGeometry = new Geometry();

            readonly Geometry _windBlockedHoverGeometry = new Geometry();

            GameObject _windFreeHoverOverlay, _windBlockedHoverOverlay;

            Material _windFreeHoverMaterial, _windBlockedHoverMaterial;

            bool PowerOverlayActive => InspectionActive && (ShowPower ||
                _view.buildTool?.PlacingDef is ThingDef def && def.HasComp<CompPower>());

            internal bool PowerHoverVisible(Thing thing, bool includeHidden = false)
            {
                return thing != null && thing.Spawned && thing.Map == _view.map &&
                    !_view.map.fogGrid.IsFogged(thing.Position) && (includeHidden || !IsHiddenPowerConduit(thing));
            }

            internal void UpdatePowerHoverOverlay()
            {
                bool persistent = PowerOverlayActive;
                var target = _view.FirstPerson && _view.ControlledPawn != null && !_view.ControlledPawn.Drafted && !_view.controller.InputBlocked && !_view.ExternalInputBlocked &&
                    !_view.TextInputFocused && _view.mouseCaptured && !_view.constructionMenu && !_view.actionMenu && !_view.statusMenu &&
                    _view.buildTool == null && _view.zoneTool == null && _view.PowerHoverVisible(_view.aimedThing) ? _view.aimedThing as ThingWithComps : null;
                var power = target?.GetComp<CompPower>();
                var wind = target?.GetComp<CompPowerPlantWind>();
                var net = power?.PowerNet;
                if (!persistent && wind == null && !(power is CompPowerPlant) && !(power is CompPowerBattery) &&
                    (power == null || !IsConduit(target.def)))
                {
                    if (_powerHoverOverlay != null) _powerHoverOverlay.SetActive(false);
                    if (_windFreeHoverOverlay != null) _windFreeHoverOverlay.SetActive(false);
                    if (_windBlockedHoverOverlay != null) _windBlockedHoverOverlay.SetActive(false);
                    _powerHoverTarget = null;
                    _powerHoverNet = null;
                    _powerOverlayWasActive = false;
                    return;
                }
                var center = persistent ? _view.ControlledPawn.Position : IntVec3.Invalid;
                bool changed = target != _powerHoverTarget || net != _powerHoverNet ||
                    persistent != _powerOverlayWasActive || center != _powerOverlayCenter;
                if (!changed && Time.unscaledTime < _nextPowerHoverUpdate) return;
                _nextPowerHoverUpdate = Time.unscaledTime + .5f;
                _powerHoverTarget = target;
                _powerHoverNet = net;
                _powerOverlayWasActive = persistent;
                _powerOverlayCenter = center;
                _windHoverCells.Clear();
                _windBlockedHoverCells.Clear();
                _windHoverBlockers.Clear();
                if (wind != null)
                {
                    foreach (var cell in WindTurbineUtility.CalculateWindCells(target.Position, target.Rotation, target.def.size))
                        if (cell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(cell)) _windHoverCells.Add(cell);
                    var blockedCells = _windBlockedCells(wind);
                    var blockedThings = _windBlockedThings(wind);
                    for (int i = 0; i < blockedCells.Count; i++)
                    {
                        if (!_windHoverCells.Contains(blockedCells[i])) continue;
                        _windBlockedHoverCells.Add(blockedCells[i]);
                        var blocker = blockedThings[i];
                        if (_view.PowerHoverVisible(blocker, true)) _windHoverBlockers.Add(blocker);
                    }
                }
                int signature = _view.PowerHoverSignature(power);
                unchecked
                {
                    foreach (var cell in _windHoverCells) signature = signature * 31 + cell.GetHashCode();
                    foreach (var cell in _windBlockedHoverCells) signature = signature * 31 + cell.GetHashCode();
                    foreach (var blocker in _windHoverBlockers)
                        signature = (signature * 31 + blocker.GetHashCode()) ^ blocker.Position.GetHashCode();
                    if (persistent)
                    {
                        _nearbyPower.Clear();
                        int count = GenRadial.NumCellsInRadius(InspectionRadius);
                        for (int i = 0; i < count; i++)
                        {
                            var cell = center + GenRadial.RadialPattern[i];
                            if (!NearbyInspectionCell(cell)) continue;
                            foreach (var thing in _view.map.thingGrid.ThingsListAt(cell))
                            {
                                var comp = (thing as ThingWithComps)?.GetComp<CompPower>();
                                if (comp != null && NearbyInspectionCell(thing.Position) && _nearbyPower.Add(comp))
                                    signature = signature * 31 + _view.PowerHoverSignature(comp);
                            }
                        }
                    }
                    else if (net != null)
                    {
                        foreach (var comp in net.transmitters) signature = signature * 31 + _view.PowerHoverSignature(comp);
                        foreach (var comp in net.connectors) signature = signature * 31 + _view.PowerHoverSignature(comp);
                    }
                }
                if (!changed && signature == _powerHoverSignature && _powerHoverOverlay != null)
                {
                    _powerHoverOverlay.SetActive(true);
                    if (_windFreeHoverOverlay != null) _windFreeHoverOverlay.SetActive(wind != null);
                    if (_windBlockedHoverOverlay != null) _windBlockedHoverOverlay.SetActive(wind != null);
                    return;
                }
                _powerHoverSignature = signature;
                var geometry = _powerHoverGeometry;
                ClearHoverGeometry(geometry);
                ClearHoverGeometry(_windFreeHoverGeometry);
                ClearHoverGeometry(_windBlockedHoverGeometry);
                foreach (var cell in _windHoverCells)
                {
                    bool blocked = _windBlockedHoverCells.Contains(cell);
                    var windGeometry = blocked ? _windBlockedHoverGeometry : _windFreeHoverGeometry;
                    if (_view.WindHoverBoundary(cell + IntVec3.South, blocked)) AddZoneStrip(windGeometry, cell.x, cell.x + 1, cell.z, cell.z + .04f, .065f);
                    if (_view.WindHoverBoundary(cell + IntVec3.North, blocked)) AddZoneStrip(windGeometry, cell.x, cell.x + 1, cell.z + .96f, cell.z + 1, .065f);
                    if (_view.WindHoverBoundary(cell + IntVec3.West, blocked)) AddZoneStrip(windGeometry, cell.x, cell.x + .04f, cell.z, cell.z + 1, .065f);
                    if (_view.WindHoverBoundary(cell + IntVec3.East, blocked)) AddZoneStrip(windGeometry, cell.x + .96f, cell.x + 1, cell.z, cell.z + 1, .065f);
                    if (blocked && _view.map.roofGrid.Roofed(cell)) _view.AppendWindBlockerDot(cell.ToVector3Shifted() + Vector3.up * 1.85f);
                }
                foreach (var blocker in _windHoverBlockers)
                {
                    var position = blocker.TrueCenter();
                    float height = blocker is Plant plant ? Mathf.Clamp((plant.Graphic?.drawSize.y ?? 1) *
                        (plant.def.plant.IsTree ? 3.5f : .4f), .2f, 9) : _view.ConstructionHeight(blocker.def);
                    position.y = height + .12f;
                    _view.AppendWindBlockerDot(position);
                }
                if (persistent)
                {
                    foreach (var comp in _nearbyPower) _view.AppendPowerHover(comp);
                }
                else if (net != null)
                {
                    foreach (var comp in net.transmitters) _view.AppendPowerHover(comp);
                    foreach (var comp in net.connectors) _view.AppendPowerHover(comp);
                }
                else if (power != null) _view.AppendPowerHover(power);
                if (_powerHoverMaterial == null)
                {
                    _powerHoverMaterial = _view.PlanMaterial(new Color(.3f, .8f, 1));
                    _powerHoverMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    _powerHoverMaterial.SetFloat("_ZWrite", 0);
                    _powerHoverMaterial.SetInt("_StencilWriteMask", 0);
                    _powerHoverMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
                }
                _view.ReplaceZoneMesh(ref _powerHoverOverlay, _powerHoverMaterial, geometry, "FPV power network");
                if (wind != null)
                {
                    if (_windFreeHoverMaterial == null) _windFreeHoverMaterial = _view.PlanMaterial(new Color(.2f, 1, .3f));
                    if (_windBlockedHoverMaterial == null) _windBlockedHoverMaterial = _view.PlanMaterial(new Color(1, .15f, .1f));
                    _view.ReplaceZoneMesh(ref _windFreeHoverOverlay, _windFreeHoverMaterial, _windFreeHoverGeometry, "FPV clear wind path");
                    _view.ReplaceZoneMesh(ref _windBlockedHoverOverlay, _windBlockedHoverMaterial, _windBlockedHoverGeometry, "FPV blocked wind path");
                }
                else
                {
                    if (_windFreeHoverOverlay != null) _windFreeHoverOverlay.SetActive(false);
                    if (_windBlockedHoverOverlay != null) _windBlockedHoverOverlay.SetActive(false);
                }
            }

            internal bool WindHoverBoundary(IntVec3 cell, bool blocked)
            {
                return !_windHoverCells.Contains(cell) || _windBlockedHoverCells.Contains(cell) != blocked;
            }

            internal void AppendWindBlockerDot(Vector3 center)
            {
                const float radius = .12f;
                var uv = new Rect(0, 0, 1, 1);
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * Mathf.PI * .25f, next = (i + 1) * Mathf.PI * .25f;
                    var a = center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                    var b = center + new Vector3(Mathf.Cos(next), 0, Mathf.Sin(next)) * radius;
                    var top = center + Vector3.up * radius;
                    var bottom = center - Vector3.up * radius;
                    _windBlockedHoverGeometry.Quad(a, top, b, b, (a + top + b - center * 3).normalized,
                        uv.min, uv.max, uv.max, uv.max);
                    _windBlockedHoverGeometry.Quad(a, b, bottom, bottom, (a + b + bottom - center * 3).normalized,
                        uv.min, uv.max, uv.max, uv.max);
                }
            }

            internal int PowerHoverSignature(CompPower power)
            {
                var thing = power?.parent;
                if (!_view.PowerHoverVisible(thing, true)) return 0;
                unchecked
                {
                    return ((thing.GetHashCode() * 397 ^ thing.Position.GetHashCode()) * 397 ^ thing.Rotation.GetHashCode()) * 397 ^
                        (power.connectParent?.parent.GetHashCode() ?? 0) ^ (IsHiddenPowerConduit(thing) ? 1 : 0);
                }
            }

            internal void AppendPowerHover(CompPower power)
            {
                var thing = power.parent;
                if (!_view.PowerHoverVisible(thing, true)) return;
                var center = thing.TrueCenter();
                center.y = .065f;
                if (IsConduit(thing.def))
                {
                    bool hidden = IsHiddenPowerConduit(thing);
                    _powerHoverGeometry.FloorCard(center, hidden ? .08f : .12f, hidden ? .08f : .12f);
                    for (int d = 0; d < 4; d++)
                    {
                        var cell = thing.Position + GenAdj.CardinalDirections[d];
                        if (PowerOverlayActive && !NearbyInspectionCell(cell)) continue;
                        if (power.PowerNet == null || !cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell) ||
                            _view.map.powerNetGrid.TransmittedPowerNetAt(cell) != power.PowerNet) continue;
                        var end = cell.ToVector3Shifted();
                        end.y = center.y;
                        _view.AppendPowerHoverLine(center, (center + end) * .5f, hidden);
                    }
                }
                else
                {
                    var size = thing.RotatedSize;
                    float height = _view.ConstructionHeight(thing.def) + .04f;
                    for (int x = -1; x <= 1; x += 2) for (int z = -1; z <= 1; z += 2)
                    {
                        var corner = center + new Vector3(x * (size.x + .04f) * .5f, 0, z * (size.z + .04f) * .5f);
                        _powerHoverGeometry.OutlineLine(corner, corner + Vector3.up * height, .025f);
                        if (x == -1)
                        {
                            _powerHoverGeometry.OutlineLine(corner, corner + Vector3.right * (size.x + .04f), .025f);
                            _powerHoverGeometry.OutlineLine(corner + Vector3.up * height,
                                corner + Vector3.up * height + Vector3.right * (size.x + .04f), .025f);
                        }
                        if (z == -1)
                        {
                            _powerHoverGeometry.OutlineLine(corner, corner + Vector3.forward * (size.z + .04f), .025f);
                            _powerHoverGeometry.OutlineLine(corner + Vector3.up * height,
                                corner + Vector3.up * height + Vector3.forward * (size.z + .04f), .025f);
                        }
                    }
                }
                var parent = power.connectParent?.parent;
                if (_view.PowerHoverVisible(parent, true) &&
                    (!PowerOverlayActive || NearbyInspectionCell(parent.Position)))
                {
                    var end = parent.TrueCenter();
                    end.y = center.y;
                    _view.AppendPowerHoverLine(center, end, false, .025f);
                }
            }

            internal void AppendPowerHoverLine(Vector3 start, Vector3 end, bool dashed, float width = .06f)
            {
                if (!dashed) { _powerHoverGeometry.OutlineLine(start, end, width); return; }
                var direction = end - start;
                float length = direction.magnitude;
                if (length <= .001f) return;
                direction /= length;
                for (float distance = 0; distance < length; distance += .24f)
                    _powerHoverGeometry.OutlineLine(start + direction * distance,
                        start + direction * Mathf.Min(distance + .12f, length), .045f);
            }

        }
    }
}
