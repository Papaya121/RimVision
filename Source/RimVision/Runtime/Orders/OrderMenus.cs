using System;
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
            OrdersWindow _ordersWindow;

            internal void OpenOrdersMenu()
            {
                _ordersWindow?.Close(false);
                var targetMap = _view._viewTargeting.TryPickOrderTarget(out var hit) ? hit.Map : _view.map;
                Rand.PushState();
                try { _ordersWindow = new OrdersWindow(this, targetMap); }
                finally { Rand.PopState(); }
                Find.WindowStack.Add(_ordersWindow);
            }

            internal void CloseOrdersMenu()
            {
                _ordersWindow?.Close(false);
                _ordersWindow = null;
            }

            void SelectOrderTool(Designator tool, Map map, Pawn owner)
            {
                if (!_view.FirstPerson || _view.ControlledPawn != owner || !_view.controller.Active || _view._apiDisposed) return;
                if (tool is Designator_AreaAllowed)
                {
                    var options = new List<FloatMenuOption>();
                    foreach (var area in map.areaManager.AllAreas)
                        if (area is Area_Allowed)
                            options.Add(new FloatMenuOption(area.Label, () => StartOrder(tool, owner, area)));
                    options.Add(new FloatMenuOption("ManageAreas".Translate(), () => Find.WindowStack.Add(new Dialog_ManageAreas(map))));
                    Find.WindowStack.Add(new FloatMenu(options));
                    return;
                }
                if (tool is Designator_ZoneAdd)
                {
                    var options = new List<FloatMenuOption>
                    { new FloatMenuOption("Rim3D_OrdersNewZone".Translate(), () => StartOrder(tool, owner)) };
                    foreach (var zone in map.zoneManager.AllZones)
                        if (OrderExecutor.ZoneMatches(tool, zone))
                            options.Add(new FloatMenuOption(zone.label, () => StartOrder(tool, owner, zone: zone)));
                    Find.WindowStack.Add(new FloatMenu(options));
                    return;
                }
                if (tool is Designator_Build build && build.PlacingDef.MadeFromStuff)
                {
                    var options = new List<FloatMenuOption>();
                    foreach (var stuff in DefDatabase<ThingDef>.AllDefsListForReading)
                    {
                        if (!stuff.IsStuff || !stuff.stuffProps.CanMake(build.PlacingDef)) continue;
                        var material = stuff;
                        options.Add(new FloatMenuOption(material.LabelCap, () =>
                        {
                            if (StartOrder(tool, owner)) build.SetStuffDef(material);
                        }, material));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                    return;
                }
                StartOrder(tool, owner);
            }

            bool StartOrder(Designator tool, Pawn owner, Area area = null, Zone zone = null)
            {
                if (!_view.FirstPerson || _view.ControlledPawn != owner || !_view.controller.Active || _view._apiDisposed) return false;
                _view._orders.Select(tool, area, zone);
                return true;
            }

            sealed class OrdersWindow : Window
            {
                sealed class Entry
                {
                    internal string Label, Description;
                    internal Texture Icon;
                    internal Action Choose;
                }

                readonly ActionMenus _menus;
                readonly Pawn _owner;
                readonly Map _map;
                readonly List<Entry> _entries = new List<Entry>();
                string _title, _search = "";
                Pawn _colonist;
                Vector2 _scroll;
                bool _root;
                public override Vector2 InitialSize => new Vector2(500, Mathf.Min(600, UI.screenHeight - 24));

                internal OrdersWindow(ActionMenus menus, Map map)
                {
                    _menus = menus; _map = map; _owner = menus._view.ControlledPawn;
                    preventCameraMotion = absorbInputAroundWindow = true;
                    doCloseX = true; closeOnAccept = false;
                    Categories();
                }

                bool Valid => !_menus._view._apiDisposed && _menus._view.FirstPerson &&
                    _menus._view.ControlledPawn == _owner && _owner?.Spawned == true && Current.Game.Maps.Contains(_map);

                void Reset(string title, bool root = false)
                { _entries.Clear(); _title = title; _search = ""; _scroll = Vector2.zero; _root = root; _colonist = null; }

                void Add(string label, Action choose, Texture icon = null, string description = null) =>
                    _entries.Add(new Entry { Label = label, Choose = choose, Icon = icon, Description = description });

                void Categories()
                {
                    Reset("Rim3D_OrdersTitle".Translate(), true);
                    Add("Rim3D_OrdersColonists".Translate(), Colonists);
                    using (new OrderContext(_map, null))
                    {
                        var categories = new List<DesignationCategoryDef>(DefDatabase<DesignationCategoryDef>.AllDefsListForReading);
                        categories.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) :
                            string.CompareOrdinal(a.defName, b.defName));
                        foreach (var category in categories)
                        {
                            bool any = false;
                            foreach (var tool in Tools(category.ResolvedAllowedDesignators))
                                if (OrderExecutor.Supported(tool) && tool.Visible) { any = true; break; }
                            if (any) Add(category.LabelCap, () => Category(category));
                        }
                    }
                    if (_map == _menus._view.map) Add("Rim3D_OrdersZoneSettings".Translate(), Zones);
                    Add("ManageAreas".Translate(), () => { Close(false); Find.WindowStack.Add(new Dialog_ManageAreas(_map)); });
                }

                static IEnumerable<Designator> Tools(IEnumerable<Designator> tools)
                {
                    foreach (var tool in tools)
                    {
                        if (tool is Designator_Dropdown dropdown)
                        {
                            foreach (var child in Tools(dropdown.Elements)) yield return child;
                        }
                        else yield return tool;
                    }
                }

                void Category(DesignationCategoryDef category)
                {
                    Reset(category.LabelCap);
                    using (new OrderContext(_map, null))
                    {
                        foreach (var tool in Tools(category.ResolvedAllowedDesignators))
                        {
                            if (!OrderExecutor.Supported(tool) || !tool.Visible) continue;
                            var selected = tool;
                            Add(tool.LabelCap, () =>
                            {
                                if (selected.Disabled) { _menus._view.SayAction(selected.disabledReason); return; }
                                Close(false);
                                _menus.SelectOrderTool(selected, _map, _owner);
                            }, tool.icon, tool.Desc);
                        }
                    }
                }

                void Colonists()
                {
                    Reset("Rim3D_OrdersColonists".Translate());
                    foreach (var pawn in _map.mapPawns.FreeColonistsSpawned)
                    {
                        if (!ColonistCommandController.Available(pawn) || pawn == _owner) continue;
                        var selected = pawn;
                        Add(pawn.LabelShortCap, () => Colonist(selected));
                    }
                }

                void Colonist(Pawn pawn)
                {
                    Reset(pawn.LabelShortCap);
                    _colonist = pawn;
                    Add("Rim3D_OrdersChooseTarget".Translate(), () =>
                    { Close(false); _menus._view._orders.SelectColonist(pawn); });
                    Rand.PushState();
                    try
                    {
                        foreach (var gizmo in pawn.GetGizmos())
                        {
                            if (!(gizmo is Command command) || command.hotKey != KeyBindingDefOf.Command_ColonistDraft) continue;
                            Add(command.LabelCap, () =>
                            {
                                if (!ColonistCommandController.Available(pawn) || command.Disabled || !command.Visible) return;
                                command.ProcessInput(new Event { type = EventType.MouseDown, button = 0 });
                                Colonist(pawn);
                            }, command.icon, command.Desc);
                        }
                    }
                    finally { Rand.PopState(); }
                }

                void Zones()
                {
                    Reset("Rim3D_OrdersZoneSettings".Translate());
                    if (_map != _menus._view.map) return;
                    foreach (var zone in _map.zoneManager.AllZones)
                    {
                        var selected = zone;
                        Add(zone.label, () => { Close(false); _menus._view.OpenZoneSettings(selected); });
                    }
                }

                public override void WindowUpdate()
                { base.WindowUpdate(); if (!Valid) Close(false); }

                public override void DoWindowContents(Rect rect)
                {
                    if (!Valid) { Close(false); return; }
                    if (_colonist != null && (!ColonistCommandController.Available(_colonist) || _colonist.Map != _map)) Categories();
                    Widgets.Label(new Rect(0, 0, rect.width - 30, 28), _title);
                    if (!_root && Widgets.ButtonText(new Rect(0, 32, 86, 28), "Rim3D_OrdersBack".Translate())) Categories();
                    _search = Widgets.TextField(new Rect(_root ? 0 : 94, 32, rect.width - (_root ? 0 : 94), 28), _search);
                    int matches = 0;
                    foreach (var entry in _entries)
                        if (_search.Length == 0 || entry.Label.IndexOf(_search, StringComparison.CurrentCultureIgnoreCase) >= 0) matches++;
                    float listTop = 70;
                    if (_colonist?.playerSettings?.SupportsAllowedAreas == true)
                    {
                        Widgets.Label(new Rect(0, listTop, rect.width, 24), "Rim3D_OrdersAllowedArea".Translate());
                        using (new OrderContext(_map, null))
                            AreaAllowedGUI.DoAllowedAreaSelectors(new Rect(0, listTop + 26, rect.width, 28), _colonist);
                        listTop += 62;
                    }
                    var list = new Rect(0, listTop, rect.width, rect.height - listTop);
                    var content = new Rect(0, 0, list.width - 18, matches * 38);
                    Widgets.BeginScrollView(list, ref _scroll, content);
                    Entry chosen = null;
                    int index = 0;
                    foreach (var entry in _entries)
                    {
                        if (_search.Length > 0 && entry.Label.IndexOf(_search, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                        var row = new Rect(0, index++ * 38, content.width, 34);
                        if (entry.Icon != null) GUI.DrawTexture(new Rect(row.x + 3, row.y + 3, 28, 28), entry.Icon, ScaleMode.ScaleToFit);
                        if (Widgets.ButtonText(new Rect(row.x + 36, row.y, row.width - 36, row.height), entry.Label)) chosen = entry;
                        if (!entry.Description.NullOrEmpty()) TooltipHandler.TipRegion(row, entry.Description);
                    }
                    Widgets.EndScrollView();
                    chosen?.Choose();
                }

                public override void PostClose()
                {
                    if (_menus._ordersWindow == this) _menus._ordersWindow = null;
                    base.PostClose();
                }
            }
        }
    }
}
