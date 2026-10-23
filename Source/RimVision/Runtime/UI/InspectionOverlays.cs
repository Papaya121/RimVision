using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed partial class ActionMenus
        {
            void DrawInspectionButtons(Rect panel)
            {
                Widgets.DrawBoxSolid(panel, new Color(.07f, .09f, .12f, .96f));
                var overlays = _view._worldOverlays;
                float available = panel.width - 16;
                float fertilityWidth = available * .42f, powerWidth = available * .34f;
                DrawInspectionButton(new Rect(panel.x + 4, panel.y + 4, fertilityWidth, 30),
                    "Rim3D_OverlayFertility", ref overlays.ShowFertility);
                DrawInspectionButton(new Rect(panel.x + 8 + fertilityWidth, panel.y + 4, powerWidth, 30),
                    "Rim3D_OverlayPower", ref overlays.ShowPower);
                DrawInspectionButton(new Rect(panel.x + 12 + fertilityWidth + powerWidth, panel.y + 4,
                    available - fertilityWidth - powerWidth, 30),
                    "Rim3D_OverlayBeauty", ref overlays.ShowBeauty);
                bool showZones = _view._zoneActions.ShowZoneFills;
                DrawInspectionButton(new Rect(panel.x + 4, panel.y + 38, panel.width - 8, 30),
                    "Rim3D_OverlayZones", ref _view._zoneActions.ShowZoneFills);
                if (showZones != _view._zoneActions.ShowZoneFills) _view.nextZoneOverlay = 0;
            }

            static void DrawInspectionButton(Rect rect, string key, ref bool active)
            {
                if (active) Widgets.DrawBoxSolid(rect, new Color(.2f, .4f, .3f));
                if (Widgets.ButtonText(rect, (active ? "✓ " : "") + key.Translate(), false)) active = !active;
            }
        }

        sealed partial class WorldOverlays
        {
            internal bool ShowFertility, ShowPower, ShowBeauty;

            const int InspectionRadius = 12;
            readonly Geometry[] _fertilityGeometry = { new Geometry(), new Geometry(), new Geometry(), new Geometry() };
            readonly GameObject[] _fertilityOverlays = new GameObject[4];
            readonly Material[] _fertilityMaterials = new Material[4];
            readonly List<BeautyCell> _beautyCells = new List<BeautyCell>();
            readonly HashSet<Thing> _beautyCountedThings = new HashSet<Thing>();
            readonly HashSet<CompPower> _nearbyPower = new HashSet<CompPower>();
            float _nextInspectionUpdate;
            bool _fertilityWasVisible, _beautyWasVisible;
            IntVec3 _inspectionCenter = IntVec3.Invalid;

            struct BeautyCell
            {
                internal IntVec3 Cell;
                internal string Label;
                internal Color Color;
            }

            GUIStyle _inspectionLabelStyle;

            internal void DrawBeautyLabels(Rect viewport)
            {
                if (_inspectionLabelStyle == null)
                    _inspectionLabelStyle = new GUIStyle(_view.activityStyle) { alignment = TextAnchor.MiddleCenter };
                foreach (var entry in _beautyCells)
                {
                    if (!NearbyInspectionCell(entry.Cell) ||
                        !_view.OverlayPoint(entry.Cell.ToVector3Shifted() + Vector3.up * .12f,
                            viewport, out var point, InspectionRadius + 2)) continue;
                    _inspectionLabelStyle.normal.textColor = entry.Color;
                    GUI.Label(new Rect(point.x - 22, point.y - 12, 44, 24), entry.Label, _inspectionLabelStyle);
                }
            }

            internal bool InspectionActive => _view.FirstPerson && _view.ControlledPawn != null &&
                _view.ControlledPawn.Spawned && _view.ControlledPawn.Map == _view.map;

            internal bool FertilityOverlayActive => InspectionActive && (ShowFertility || _view.zoneTool == "Growing");

            internal bool NearbyInspectionCell(IntVec3 cell)
            {
                var pawn = _view.ControlledPawn;
                return InspectionActive && cell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(cell) &&
                    (cell - pawn.Position).LengthHorizontalSquared <= InspectionRadius * InspectionRadius;
            }

            internal void UpdateInspectionOverlays()
            {
                bool fertility = FertilityOverlayActive;
                bool beauty = InspectionActive && ShowBeauty;
                foreach (var obj in _fertilityOverlays)
                    if (obj != null) obj.SetActive(fertility);
                if (!fertility && !beauty)
                {
                    _fertilityWasVisible = _beautyWasVisible = false;
                    _beautyCells.Clear();
                    return;
                }
                var center = _view.ControlledPawn.Position;
                if (_fertilityWasVisible == fertility && _beautyWasVisible == beauty &&
                    center == _inspectionCenter && Time.unscaledTime < _nextInspectionUpdate) return;
                _fertilityWasVisible = fertility;
                _beautyWasVisible = beauty;
                _inspectionCenter = center;
                _nextInspectionUpdate = Time.unscaledTime + .5f;
                _beautyCells.Clear();
                _beautyCountedThings.Clear();
                if (fertility)
                    foreach (var geometry in _fertilityGeometry) ClearHoverGeometry(geometry);
                int count = fertility ? GenRadial.NumCellsInRadius(InspectionRadius) : 0;
                for (int i = 0; i < count; i++)
                {
                    var cell = center + GenRadial.RadialPattern[i];
                    if (!NearbyInspectionCell(cell)) continue;
                    if (!cell.Filled(_view.map))
                    {
                        float value = cell.GetFertility(_view.map);
                        if (ModsConfig.BiotechActive ? value >= .5f : value > .69f)
                        {
                            int band = ModsConfig.BiotechActive && value <= .5f ? 0 : value <= .95f ? 1 : value <= 1.1f ? 2 : 3;
                            var position = cell.ToVector3Shifted();
                            position.y = .055f;
                            _fertilityGeometry[band].FloorCard(position, 1, 1);
                        }
                    }
                }
                if (beauty)
                {
                    BeautyUtility.FillBeautyRelevantCells(center, _view.map);
                    foreach (var cell in BeautyUtility.beautyRelevantCells)
                    {
                        if (!NearbyInspectionCell(cell)) continue;
                        float value = BeautyUtility.CellBeauty(cell, _view.map, _beautyCountedThings);
                        _beautyCells.Add(new BeautyCell
                        {
                            Cell = cell,
                            Label = Mathf.RoundToInt(value).ToString("+0;-0;0"),
                            Color = BeautyDrawer.BeautyColor(value, 8)
                        });
                    }
                    _beautyCountedThings.Clear();
                }
                if (!fertility) return;
                for (int i = 0; i < _fertilityGeometry.Length; i++)
                {
                    if (_fertilityMaterials[i] == null)
                    {
                        var color = i == 0 ? Color.red : i == 1 ? Color.yellow :
                            i == 2 ? new Color(.59f, .98f, .59f) : Color.green;
                        var material = _view.PlanMaterial(color);
                        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        material.SetFloat("_ZWrite", 0);
                        material.SetFloat("_SurfaceOpacity", .33f);
                        material.SetOverrideTag("RenderType", "Transparent");
                        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                        _fertilityMaterials[i] = material;
                    }
                    _view.ReplaceZoneMesh(ref _fertilityOverlays[i], _fertilityMaterials[i],
                        _fertilityGeometry[i], "FPV fertility");
                }
            }
        }

        sealed partial class HudPresentation
        {
            void DrawInspectionHud(Rect viewport)
            {
                if (Event.current.type != EventType.Repaint) return;
                var overlays = _view._worldOverlays;
                if (!overlays.InspectionActive) return;
                var aim = _view.aimedCell;
                if (overlays.FertilityOverlayActive && overlays.NearbyInspectionCell(aim) && !_view.aimingAtRoof)
                    _view.Badge(new Rect(viewport.center.x - 180, viewport.center.y + 82, 360, 24),
                        "Rim3D_OverlayFertilityValue".Translate(aim.GetFertility(_view.map).ToStringPercent()), _view.activityStyle);
                if (!overlays.ShowBeauty) return;
                overlays.DrawBeautyLabels(viewport);
                var room = _view.ControlledPawn.Position.GetRoom(_view.map);
                if (room == null || !room.ProperRoom) return;
                var stats = DefDatabase<RoomStatDef>.AllDefsListForReading;
                int rows = 0;
                foreach (var stat in stats) if (!stat.isHidden) rows++;
                float left = viewport.x + 394;
                float width = Mathf.Max(1, Mathf.Min(420, viewport.xMax - left - 12));
                float height = 42 + rows * 23;
                var panel = new Rect(left, viewport.yMax - height - 10, width, height);
                Widgets.DrawBoxSolid(panel, new Color(.07f, .09f, .12f, .94f));
                Widgets.Label(new Rect(panel.x + 10, panel.y + 6, width - 20, 28), room.GetRoomRoleLabel().CapitalizeFirst());
                var oldFont = Text.Font;
                Text.Font = GameFont.Tiny;
                try
                {
                    float y = panel.y + 36;
                    foreach (var stat in stats)
                    {
                        if (stat.isHidden) continue;
                        float value = room.GetStat(stat);
                        if (((int)((y - panel.y - 36) / 23) & 1) == 1)
                            Widgets.DrawBoxSolid(new Rect(panel.x + 8, y, width - 16, 23), new Color(1, 1, 1, .04f));
                        Widgets.Label(new Rect(panel.x + 10, y, width * .4f, 23),
                            (room.Role.IsStatRelated(stat) ? "* " : "") + stat.LabelCap);
                        Widgets.Label(new Rect(panel.x + width * .43f, y, width * .18f, 23), stat.ScoreToString(value));
                        Widgets.Label(new Rect(panel.x + width * .62f, y, width * .36f, 23),
                            stat.GetScoreStage(value)?.label.CapitalizeFirst() ?? "");
                        y += 23;
                    }
                }
                finally { Text.Font = oldFont; }
            }
        }
    }
}
