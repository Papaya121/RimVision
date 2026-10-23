using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed class FPVBillWindow : Window
        {
            readonly MapView owner;
            readonly Building_WorkTable table;
            readonly Func<List<FloatMenuOption>> optionsMaker;
            Vector2 scroll;
            float viewHeight = 1000;
            string notice = "Rim3D_BillHelp".Translate().ToString();
            public override Vector2 InitialSize => new Vector2(540, Mathf.Min(600, UI.screenHeight - 20));

            public FPVBillWindow(MapView owner, Building_WorkTable table)
            {
                this.owner = owner;
                this.table = table;
                optionsMaker = MakeOptions;
                layer = WindowLayer.Dialog;
                forcePause = true;
                preventCameraMotion = true;
                absorbInputAroundWindow = true;
                closeOnAccept = false;
                doCloseX = true;
            }

            bool Valid => table.Spawned && table.Map == owner.map && table.Faction == Faction.OfPlayer;

            public override void WindowUpdate()
            {
                base.WindowUpdate();
                if (!Valid || !owner.FirstPerson) Close(false);
            }

            public override void DoWindowContents(Rect rect)
            {
                if (!Valid) { Close(false); return; }
                Widgets.Label(new Rect(0,0,rect.width - 32,28), "Rim3D_BillsTitle".Translate(table.LabelCap).ToString());
                Widgets.Label(new Rect(0,30,rect.width,36), notice);
                table.BillStack.DoListing(new Rect(0,66,rect.width,Mathf.Max(1,rect.height - 112)),
                    optionsMaker, ref scroll, ref viewHeight);
                if (Widgets.ButtonText(new Rect(0,rect.height - 36,rect.width * .5f - 4,32), "Rim3D_DoBills".Translate().ToString()))
                {
                    owner.StartFPVBillWork(table);
                    if (!IsOpen) return;
                    notice = owner.actionMessage;
                }
                if (Widgets.ButtonText(new Rect(rect.width * .5f + 4,rect.height - 36,rect.width * .5f - 4,32), "Rim3D_Close".Translate().ToString()))
                    Close(false);
            }

            List<FloatMenuOption> MakeOptions()
            {
                var options = new List<FloatMenuOption>();
                if (!Valid) return options;
                foreach (var recipe in table.def.AllRecipes)
                {
                    if (!recipe.AvailableNow || !recipe.AvailableOnNow(table, null)) continue;
                    AddOption(options, recipe, null);
                    if (ModsConfig.IdeologyActive && Faction.OfPlayer.ideos != null)
                        foreach (var ideo in Faction.OfPlayer.ideos.AllIdeos)
                            foreach (var precept in ideo.cachedPossibleBuildings)
                                if (precept.ThingDef == recipe.ProducedThingDef) AddOption(options, recipe, precept);
                }
                if (options.Count == 0) options.Add(new FloatMenuOption("Rim3D_NoRecipes".Translate().ToString(), null));
                return options;
            }

            void AddOption(List<FloatMenuOption> options, RecipeDef recipe, Precept_ThingStyle precept)
            {
                string label = precept == null ? recipe.LabelCap.ToString() : "Rim3D_MakeStyled".Translate(precept.LabelCap).ToString();
                options.Add(new FloatMenuOption(label, () =>
                {
                    // Float-menu callbacks may outlive this window or building.
                    if (!IsOpen || !Valid || table.BillStack.Count >= 15 ||
                        !recipe.AvailableNow || !recipe.AvailableOnNow(table, null)) return;
                    if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe)
                    {
                        bool mechanitor = false;
                        foreach (var pawn in table.Map.mapPawns.FreeColonists)
                            if (MechanitorUtility.IsMechanitor(pawn)) { mechanitor = true; break; }
                        if (!mechanitor)
                        { notice = "Rim3D_RequiresMechanitor".Translate().ToString(); owner.SayAction(notice); return; }
                    }
                    owner.AddFPVBill(table, recipe, precept, false);
                    bool skilled = false;
                    foreach (var pawn in table.Map.mapPawns.FreeColonists)
                        if (recipe.PawnSatisfiesSkillRequirements(pawn)) { skilled = true; break; }
                    if (!skilled && !Rim3DMultiplayer.Active) Bill.CreateNoPawnsWithSkillDialog(recipe);
                }));
            }
        }

        FPVBillWindow billWindow;

        void OpenFPVBills(Building_WorkTable table) => _actionMenus.OpenFPVBills(table);

        void StartFPVBillWork(Building_WorkTable table) => _constructionActions.StartFPVBillWork(table);

        void RestoreSuppliedCarry(Thing supplied, int count) => _constructionActions.RestoreSuppliedCarry(supplied, count);

        sealed class FPVBuildDesignator : Designator_Build
        {
            public FPVBuildDesignator(BuildableDef def) : base(def) { }
            public Thing ReinstallTarget { get; set; }
            public override AcceptanceReport CanDesignateCell(IntVec3 cell)
            {
                if (ReinstallTarget == null) return base.CanDesignateCell(cell);
                var target = ReinstallTarget;
                if (!CanReinstallFPV(target, Map)) return false;
                if (!(target is MinifiedThing) && cell == target.Position && placingRot == target.Rotation)
                    return new AcceptanceReport("IdenticalThingExists".Translate());
                var inner = target.GetInnerIfMinified();
                var report = GenConstruct.CanPlaceBlueprintAt(PlacingDef, cell, placingRot, Map,
                    false, target, inner);
                if (!report.Accepted) return report;
                foreach (var occupiedCell in GenAdj.OccupiedRect(cell, placingRot, inner.def.Size))
                    foreach (var existing in occupiedCell.GetThingList(Map))
                        if (existing != target && existing.def.category != ThingCategory.Filth &&
                            (GenSpawn.SpawningWipes(inner.def, existing.def) ||
                            existing is Pawn && inner.def.passability == Traversability.Impassable))
                            return new AcceptanceReport("Rim3D_BuildCellOccupied".Translate(existing.LabelCap));
                return true;
            }
            public void SetRotation(Rot4 rotation) { placingRot = rotation; }
            public AcceptanceReport CanReplaceBlueprint(Blueprint_Build blueprint) =>
                GenConstruct.CanPlaceBlueprintAt(PlacingDef, blueprint.Position, placingRot, blueprint.Map,
                    DebugSettings.godMode, blueprint, null, StuffDef);
        }

        bool constructionMenu;
        bool actionMenu;
        enum ContextAction { Interact, Bills, Cut, Harvest, Mine, Repair, Deconstruct, Equip, Build, CancelPlan, Ingest, TakeCount, Store, Drop, Melee, RoofFlat, RoofProcedural, Native, Arrest, Tend, SelfTend, ClaimBuilding, SmoothWall, Reinstall, RemoveRoof, StorageSettings, Extension }
        struct ContextEntry
        {
            public ContextAction Action;
            public string Label;
            public FloatMenuOption Option;
            public string ExtensionId;
            public IntVec3 Cell;
            public ContextEntry(ContextAction action, string label)
            {
                Action = action;
                Label = label;
                ExtensionId = null;
                Cell = IntVec3.Invalid;
                // Use RimWorld's own row renderer, including its border and hover style.
                Option = new FloatMenuOption(label, ContextOptionSelected);
            }
        }
        static void ContextOptionSelected() { }

        Thing contextTarget;
        IntVec3 _contextRoofCell;
        Pawn contextPawn;

        Rect contextPanel;
        int contextOpenedFrame;
        int contextClosedFrame = -1;

        const string BuildSearchControl = "Rim3D.BuildSearch";
        bool buildSearchFocused;
        public bool TextInputFocused => constructionMenu && buildSearchFocused;

        readonly List<BuildableDef> buildCatalog = new List<BuildableDef>();
        readonly List<BuildableDef> filteredBuildCatalog = new List<BuildableDef>();
        readonly List<DesignationCategoryDef> buildCategories = new List<DesignationCategoryDef>();
        readonly List<ThingDef> buildStuffs = new List<ThingDef>();
        readonly HashSet<Thing> aimCandidates = new HashSet<Thing>();

        readonly List<Thing> cellAimTargets = new List<Thing>(8);


        FPVBuildDesignator buildTool;

        Rot4 buildRotation = Rot4.North;
        Thing aimedThing;
        IntVec3 aimedCell = IntVec3.Invalid;
        bool aimingAtRoof;
        string roofAimFailure;

        const float RoofRemovalHoldSeconds = 1f;


        float nextAimUpdate;
        string actionMessage = "";


        sealed class FloatingItemNotice
        {
            public string Text;
            public float Created;
        }
        readonly List<FloatingItemNotice> floatingItemNotices = new List<FloatingItemNotice>(4);
        IntVec3 constructionCell = IntVec3.Invalid;
        BuildableDef constructionDef;
        GameObject placementPreview;

        sealed class DesignationIconBatch
        {
            public readonly Geometry Geometry = new Geometry();
            public GameObject Object;
            public Mesh Mesh;
        }
        readonly Dictionary<Material, DesignationIconBatch> designationIcons = new Dictionary<Material, DesignationIconBatch>();
        float nextDesignationUpdate;

        void UpdateDesignationIcons() => _worldOverlays.UpdateDesignationIcons();

        void SayAction(string text) => _hudPresentation.SayAction(text);

        void SayFloatingItem(string text) => _hudPresentation.SayFloatingItem(text);

        public void ConsumeFPVInput(Event current)
        {
            if ((current.type == EventType.MouseDown || current.type == EventType.MouseUp) && ConsumedApiKey((KeyCode)((int)KeyCode.Mouse0 + current.button))) { current.Use(); return; }
            if (_mortarTargeting.HandleInput(current, ExternalInputBlocked)) return;
            if (MortarAiming)
            {
                if (ExternalInputBlocked) return;
                if (Rim3DMultiplayer.Active && current.type == EventType.KeyDown &&
                    ViewBindings.Matches(ViewAction.Pause, current.keyCode))
                    Rim3DMultiplayer.TogglePause?.Invoke();
                if (current.isKey || current.type == EventType.ScrollWheel) current.Use();
                return;
            }
            if (current.isKey && ViewBindings.Matches(ViewAction.Compass, current.keyCode) && CompassInputAvailable)
            { current.Use(); return; }
            if (current.isKey && ViewBindings.Matches(ViewAction.Plans, current.keyCode) && !TextInputFocused && !ExternalInputBlocked)
            { current.Use(); return; }
            if (controller.InputBlocked)
            {
                // Dialogs and view navigation stay available; only pawn input is blocked.
                if (ExternalInputBlocked || TextInputFocused) return;
                if (Rim3DMultiplayer.Active && current.type == EventType.KeyDown && ViewBindings.Matches(ViewAction.Pause, current.keyCode) && !ExternalInputBlocked)
                    Rim3DMultiplayer.TogglePause?.Invoke();
                if (current.isKey && current.keyCode != KeyCode.Escape && !ViewBindings.Matches(ViewAction.NextPawn, current.keyCode) &&
                    !ViewBindings.Matches(ViewAction.Perspective, current.keyCode) && !ViewBindings.Matches(ViewAction.Toggle3D, current.keyCode) ||
                    current.isMouse || current.type == EventType.ScrollWheel) current.Use();
                return;
            }
            if (Rim3DMultiplayer.Active && FirstPerson && current.type == EventType.KeyDown &&
                ViewBindings.Matches(ViewAction.Pause, current.keyCode) && !TextInputFocused && !ExternalInputBlocked)
                Rim3DMultiplayer.TogglePause?.Invoke();
            if (FallPresentationActive)
            {
                if (current.isKey || current.isMouse || current.type == EventType.ScrollWheel) current.Use();
                return;
            }
            if (FirstPerson && !ExternalDialogInputBlocked)
            {
                if (!ExternalInputBlocked && !TextInputFocused && buildTool == null && !constructionMenu &&
                    current.isKey && ViewBindings.Matches(ViewAction.ItemActions, current.keyCode))
                { current.Use(); return; }
                // Claim character shortcuts with the cursor released as well.
                if (current.isKey && StatusTabForKey(current.keyCode) >= 0 && !TextInputFocused)
                { current.Use(); return; }
                if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape &&
                    (colonyWindow != null || constructionMenu || actionMenu || statusMenu || zoneTool != null))
                { CloseFPVMenu(); current.Use(); return; }
            }
            if (ExternalInputBlocked) return;
            if (FirstPerson && actionMenu && current.type == EventType.MouseDown &&
                Time.frameCount != contextOpenedFrame &&
                (current.button == 1 || !contextPanel.Contains(current.mousePosition)))
            { DismissActionMenu(); current.Use(); return; }
            if (!FirstPerson || !mouseCaptured || constructionMenu || actionMenu || statusMenu) return;
            if ((current.type == EventType.MouseDown || current.type == EventType.MouseUp) && (current.button == 0 || current.button == 1 || current.button == 2))
                current.Use();
            else if ((current.type == EventType.KeyDown || current.type == EventType.KeyUp) &&
                (ViewBindings.Matches(ViewAction.Combat, current.keyCode) || ViewBindings.Matches(ViewAction.Drop, current.keyCode) || ViewBindings.Matches(ViewAction.Interact, current.keyCode) ||
                 ViewBindings.Matches(ViewAction.CancelAction, current.keyCode) || ViewBindings.Matches(ViewAction.RotateOrSwap, current.keyCode) || ViewBindings.Matches(ViewAction.Store, current.keyCode) || ViewBindings.Matches(ViewAction.NextPawn, current.keyCode) || ViewBindings.Matches(ViewAction.CancelPlan, current.keyCode) ||
                 ViewBindings.Matches(ViewAction.NextTarget, current.keyCode) || ViewBindings.Matches(ViewAction.UseItem, current.keyCode) || ViewBindings.Matches(ViewAction.ItemActions, current.keyCode) || ViewBindings.Matches(ViewAction.Acceleration, current.keyCode) || ViewBindings.Matches(ViewAction.FireMode, current.keyCode) ||
                 ViewBindings.Matches(ViewAction.Sneak, current.keyCode) || ViewBindings.Matches(ViewAction.Sprint, current.keyCode) || ViewBindings.Matches(ViewAction.Character, current.keyCode) ||
                 ViewBindings.Matches(ViewAction.Construction, current.keyCode) || ViewBindings.Matches(ViewAction.Pause, current.keyCode) ||
                 ViewBindings.Matches(ViewAction.Forward, current.keyCode) || ViewBindings.Matches(ViewAction.Backward, current.keyCode) ||
                 ViewBindings.Matches(ViewAction.Left, current.keyCode) || ViewBindings.Matches(ViewAction.Right, current.keyCode) ||
                 ViewBindings.Matches(ViewAction.Cursor, current.keyCode))) current.Use();
        }

        void UpdateFPVActions()
        {
            if (FirstPerson) ClearOverviewPreview();
            if (FirstPerson && ControlledPawn?.Spawned == false && controller.HasNativeControl)
            {
                ResetCarryInput(); ResetBuildDrag(); ResetRoofRemovalInput();
                if (controller.Active && !ExternalInputBlocked && !TextInputFocused && mouseCaptured && !ApiInputBlocked(API.RimVisionInputChannels.Interaction))
                {
                    if (ViewBindings.Pressed(ViewAction.CancelAction)) controller.ReturnToManual();
                    if (ViewBindings.Pressed(ViewAction.Combat)) controller.SetCombat(!ControlledPawn.Drafted);
                    if (_actionMenus.UpdateApiInteraction()) { ResetManualFireInput(); return; }
                    if (API.RimVisionCombat.Handles(ControlledPawn, API.RimVisionAttackKind.Ranged)) UpdateManualFireInput();
                    else ResetManualFireInput();
                }
                else ResetManualFireInput();
                return;
            }
            if (ApiInputBlocked(API.RimVisionInputChannels.Interaction))
            {
                ResetCarryInput(); ResetBuildDrag(); ResetRoofRemovalInput();
                _workActions.ResetSowingInput();
                if (FirstPerson && controller.Active && !ExternalInputBlocked && !TextInputFocused && mouseCaptured &&
                    !constructionMenu && !actionMenu && !statusMenu && !controller.IsResting && !SelectingFPVTarget && buildTool == null && ControlledPawn?.Drafted == true)
                    UpdateManualFireInput();
                else ResetManualFireInput();
                return;
            }
            if (!FirstPerson || controller.InputBlocked || ExternalInputBlocked || TextInputFocused ||
                constructionMenu || actionMenu || statusMenu || !mouseCaptured || controller.IsResting ||
                SelectingFPVTarget || !SupportsBuildDrag ||
                ViewBindings.Held(ViewAction.Cursor)) ResetBuildDrag();
            if (!FirstPerson || controller.InputBlocked || ExternalInputBlocked || TextInputFocused ||
                constructionMenu || actionMenu || statusMenu || !mouseCaptured || buildTool != null ||
                SelectingFPVTarget || zoneTool != null || ControlledPawn?.Drafted != true || controller.IsResting ||
                ViewBindings.Held(ViewAction.Cursor)) ResetManualFireInput();
            if (!FirstPerson || controller.InputBlocked) CancelZoneTool();
            bool sowingWindowInput = _workMinigame.HasWindowInput;
            if (!FirstPerson || controller.InputBlocked || SelectingFPVTarget ||
                buildTool != null || zoneTool != null || controller.IsResting ||
                !sowingWindowInput && (ExternalInputBlocked || TextInputFocused || constructionMenu || actionMenu ||
                    statusMenu || !mouseCaptured || ViewBindings.Held(ViewAction.Cursor)))
                _workActions.ResetSowingInput();
            else if (sowingWindowInput && _workActions.SowingZone != null)
                _workActions.UpdateSowingInput();
            if (controller.InputBlocked)
            {
                if (!Rim3DMultiplayer.Active && !ExternalInputBlocked && ViewBindings.Pressed(ViewAction.Pause))
                    PausedByPlayer = !PausedByPlayer;
                ResetRoofRemovalInput();
                ResetCarryInput();
                ResetAimCycle();
                constructionMenu = actionMenu = statusMenu = false;
                constructionDef = null;
                buildTool = null;
                if (placementPreview != null) placementPreview.SetActive(false);
                if (Find.Targeter?.IsTargeting == true) Find.Targeter.StopTargeting();
                return;
            }
            if (!FirstPerson || ExternalInputBlocked || TextInputFocused || constructionMenu || actionMenu || statusMenu ||
                !mouseCaptured || !ViewBindings.Held(ViewAction.Interact) ||
                !ViewBindings.Held(ViewAction.Sprint) ||
                ViewBindings.Held(ViewAction.Cursor)) ResetRoofRemovalInput();
            if (!FirstPerson)
            {
                ResetCarryInput();
                ResetAimCycle();
                constructionDef = null;
                UpdateOverviewConstruction();
                return;
            }
            if (!TextInputFocused && !ExternalDialogInputBlocked && ViewBindings.Pressed(ViewAction.Character) &&
                (!ColonyTabOpen || colonyWindow != null))
            {
                ToggleStatusTab(2);
                ResetCarryInput();
                return;
            }
            if (ExternalInputBlocked) { ResetCarryInput(); return; }
            if (TextInputFocused) return;
            if (Time.frameCount == contextClosedFrame) { ResetCarryInput(); return; }
            if (buildTool == null && !constructionMenu && ViewBindings.Pressed(ViewAction.ItemActions))
            { OpenHeldItemActions(); return; }
            if (actionMenu && Time.frameCount != contextOpenedFrame && ViewBindings.MouseDown(1))
            { DismissActionMenu(); return; }
            if (ViewBindings.Pressed(ViewAction.Construction)) { CancelZoneTool(); constructionMenu = !constructionMenu; actionMenu = statusMenu = false; ReleaseMouse(); }
            if (actionMenu && (contextPawn != ControlledPawn || contextTarget != null &&
                (contextTarget.Destroyed || !contextTarget.Spawned || contextTarget.Map != map)))
            { CloseFPVMenu(); ResetCarryInput(); return; }
            if (constructionMenu || actionMenu || statusMenu || !mouseCaptured || Time.frameCount == mouseCaptureFrame)
            { ResetCarryInput(); return; }
            if (!Rim3DMultiplayer.Active && ViewBindings.Pressed(ViewAction.Pause)) PausedByPlayer = !PausedByPlayer;
            if (buildTool == null && ViewBindings.Pressed(ViewAction.NextTarget))
            {
                ResetCarryInput();
                PickFPVTarget();
                CycleFPVTarget();
                UpdateAimActionInfo();
                nextAimUpdate = Time.unscaledTime + .2f;
            }
            if (Time.unscaledTime >= nextAimUpdate)
            {
                PickFPVTarget();
                UpdateAimActionInfo();
                nextAimUpdate = Time.unscaledTime + .2f;
            }
            bool moving = ViewBindings.Held(ViewAction.Forward) || ViewBindings.Held(ViewAction.Left) || ViewBindings.Held(ViewAction.Backward) || ViewBindings.Held(ViewAction.Right);
            if (SelectingFPVTarget || targetInputFrame == Time.frameCount)
            {
                fpvTargetingWasActive |= SelectingFPVTarget;
                ResetCarryInput();
                return;
            }
            if (UpdateZoneInput()) return;
            if (_actionMenus.UpdateApiInteraction()) { ResetCarryInput(); ResetManualFireInput(); return; }
            if (moving || ViewBindings.Pressed(ViewAction.CancelAction) || ViewBindings.Pressed(ViewAction.Combat) ||
                ViewBindings.Pressed(ViewAction.Store) || ViewBindings.Pressed(ViewAction.RotateOrSwap) ||
                ViewBindings.Pressed(ViewAction.UseItem) || ViewBindings.Pressed(ViewAction.Drop) ||
                ViewBindings.MouseDown(1) || ViewBindings.MouseDown(2))
                _workActions.ResetSowingInput();
            else if (!ViewBindings.Held(ViewAction.Cursor) && !controller.IsResting && buildTool == null &&
                _workActions.UpdateSowingInput()) return;
            if (moving) constructionDef = null;
            if (ViewBindings.Pressed(ViewAction.CancelAction))
            {
                ResetManualFireInput();
                ResetCarryInput();
                constructionDef = null; buildTool = null;
                controller.ReturnToManual(); SayAction("Rim3D_ActionCancelled".Translate().ToString());
            }
            if (ViewBindings.Pressed(ViewAction.CancelPlan)) { PickFPVTarget(); CancelFPVPlan(); }
            bool cancelledPlacement = buildTool != null && ViewBindings.MouseDown(1);
            if (cancelledPlacement)
            { buildTool = null; SayAction("Rim3D_PlacementClosed".TranslateBindings().ToString()); }
            if (ViewBindings.Pressed(ViewAction.Combat))
            {
                constructionDef = null; buildTool = null;
                bool wasDrafted = ControlledPawn.Drafted;
                var heldWeapon = ControlledPawn.carryTracker?.CarriedThing as ThingWithComps;
                if (!controller.IsResting && heldWeapon?.def.IsWeapon == true &&
                    heldWeapon.TryGetComp<CompEquippable>() != null)
                { ResetCarryInput(); SwapHandsAndEquipment(); }
                controller.SetCombat(!wasDrafted);
                if (!Rim3DMultiplayer.LocalInput && ControlledPawn.Drafted != wasDrafted)
                    (ControlledPawn.Drafted ? SoundDefOf.DraftOn : SoundDefOf.DraftOff).PlayOneShotOnCamera();
                if (controller.Status.Length > 0 || !Rim3DMultiplayer.LocalInput && !ControlledPawn.Drafted)
                    SayAction(controller.Status.Length > 0 ? controller.Status :
                        "Rim3D_PeacefulMode".Translate().ToString());
            }
            if (buildTool?.PlacingDef is ThingDef rotateDef && rotateDef.rotatable && ViewBindings.Pressed(ViewAction.RotateOrSwap))
            {
                buildRotation = new Rot4((buildRotation.AsInt + 1) % 4);
                buildTool.SetRotation(buildRotation);
            }
            if (!controller.IsResting)
            {
                if (ViewBindings.Pressed(ViewAction.Store)) { ResetCarryInput(); constructionDef = null; StoreFPVCarry(); }
                if (buildTool == null && ViewBindings.Pressed(ViewAction.RotateOrSwap))
                { ResetCarryInput(); constructionDef = null; SwapHandsAndEquipment(); }
            }
            if (!ViewBindings.Held(ViewAction.Cursor))
            {
                if (UpdateRoofRemovalInput(moving)) return;
                if (ViewBindings.MouseDown(1) && !cancelledPlacement && !CanAimWeapon)
                {
                    OpenActionMenu();
                    return;
                }
                if (ViewBindings.Pressed(ViewAction.Interact))
                {
                    PickRecreationTarget();
                    if (aimingAtRoof && !controller.IsResting)
                    {
                        ResetCarryInput();
                        OpenActionMenu();
                        return;
                    }
                    if (aimedThing is Building_Bed || MortarTargeting.IsMortar(aimedThing)) { InteractFPV(); return; }
                    if (RecreationGiver(aimedThing) != null) { ResetCarryInput(); InteractFPV(); return; }
                }
                if (controller.IsResting) return;
                if (buildTool == null && ViewBindings.Pressed(ViewAction.UseItem)) { UseHeldItem(); return; }
                UpdateCarryInput();
                if (ViewBindings.Pressed(ViewAction.Drop)) DropFPVCarry();
                if (buildTool == null && !ApiInputBlocked(API.RimVisionInputChannels.Combat) && ViewBindings.MouseDown(2))
                {
                    PickFPVTarget();
                    MeleeFPV();
                }
                if (SupportsBuildDrag) UpdateBuildDragInput();
                else if (buildTool != null && ViewBindings.MouseDown(0))
                {
                    PickFPVTarget();
                    PlaceFPVBlueprint();
                }
                if (buildTool == null && ControlledPawn.Drafted && !ViewBindings.Pressed(ViewAction.CancelAction)) UpdateManualFireInput();
            }
            else ResetCarryInput();
            ContinueConstruction();
            UpdatePlacementPreview();
        }

        void PickRecreationTarget() => _viewTargeting.PickRecreationTarget();

        void PickFPVTarget(bool precise = false, bool distantRecreation = false) => _viewTargeting.PickFPVTarget(precise, distantRecreation);

        string RoofPlacementFailure(IntVec3 cell) => _constructionActions.RoofPlacementFailure(cell);

        Room RoofBuildingRoom(IntVec3 cell) => _constructionActions.RoofBuildingRoom(cell);

        void BuildFPVRoof(IntVec3 cell, bool procedural = false) => _constructionActions.BuildFPVRoof(cell, procedural);

        void CollectRoofRoomCells(Room room) => _constructionActions.CollectRoofRoomCells(room);

        void AddRoofBuildCell(IntVec3 cell) => _constructionActions.AddRoofBuildCell(cell);

        void CheckRoofSupportAfterEdit() => _constructionActions.CheckRoofSupportAfterEdit();

        void ResetRoofRemovalInput() => _constructionActions.ResetRoofRemovalInput();

        string RoofRemovalFailure(IntVec3 cell) => _constructionActions.RoofRemovalFailure(cell);

        bool UpdateRoofRemovalInput(bool moving) => _constructionActions.UpdateRoofRemovalInput(moving);

        void RemoveFPVRoof(IntVec3 cell) => _constructionActions.RemoveFPVRoof(cell);

        bool CanCycleAimTarget(Thing thing) => _viewTargeting.CanCycleAimTarget(thing);

        void ResetAimCycle() => _viewTargeting.ResetAimCycle();

        void RefreshAimCycle() => _viewTargeting.RefreshAimCycle();

        void CycleFPVTarget() => _viewTargeting.CycleFPVTarget();

        void InteractFPV() => _workActions.InteractFPV();

        public bool CloseFPVMenu() => _actionMenus.CloseFPVMenu();

        bool CanClaimFPVBuilding(Thing target) => _workActions.CanClaimFPVBuilding(target);

        void ClaimFPVBuilding() => _workActions.ClaimFPVBuilding();

        void DesignateAndWork(Designator designator, string kind) => _constructionActions.DesignateAndWork(designator, kind);

        Job WorkJob(Thing target, string kind) => _constructionActions.WorkJob(target, kind);

        bool WorkOnTarget(string kind) => _workActions.WorkOnTarget(kind);

        void EquipFPVWeapon() => _carryActions.EquipFPVWeapon();

        void BeginConstruction(Thing target) => _constructionActions.BeginConstruction(target);

        void ContinueConstruction() => _constructionActions.ContinueConstruction();

        void MeleeFPV() => _combatActions.MeleeFPV();

#if UNITY_EDITOR
        bool editorBuildCatalogUnlocked;
#endif
        void EnsureBuildCatalog() => _actionMenus.EnsureBuildCatalog();

        void FilterBuildCatalog() => _actionMenus.FilterBuildCatalog();

        void DrawBuildCategory(Rect rect, DesignationCategoryDef category) => _actionMenus.DrawBuildCategory(rect, category);

        static bool CanReinstallFPV(Thing target, Map targetMap)
        {
            if (target == null || target.Destroyed || !target.Spawned || target.Map != targetMap ||
                targetMap.fogGrid.IsFogged(target.Position)) return false;
            var inner = target.GetInnerIfMinified();
            return inner is Building && inner.def.Minifiable && inner.def.installBlueprintDef != null &&
                (target is MinifiedThing ? (target.Faction == null || target.Faction == Faction.OfPlayer) &&
                    (inner.Faction == null || inner.Faction == Faction.OfPlayer) : target.Faction == Faction.OfPlayer);
        }

        void SelectFPVReinstall(Thing target) => _actionMenus.SelectFPVReinstall(target);

        void PlaceFPVReinstall(Thing target, IntVec3 cell, Rot4 rotation) => _constructionActions.PlaceFPVReinstall(target, cell, rotation);

        void StartFPVInstall(Blueprint_Install blueprint) => _constructionActions.StartFPVInstall(blueprint);

        void SelectFPVBuild(BuildableDef def, ThingDef selectedStuff = null) => _actionMenus.SelectFPVBuild(def, selectedStuff);

        void OpenFPVBuildMaterialMenu(BuildableDef def, bool beginPlacement) => _actionMenus.OpenFPVBuildMaterialMenu(def, beginPlacement);

        void PlaceFPVBlueprint() => _constructionActions.PlaceFPVBlueprint();

        bool SupportsBuildDrag => buildTool?.ReinstallTarget == null &&
            (buildTool?.PlacingDef.defName == "Wall" || buildTool?.PlacingDef is TerrainDef ||
            IsConduit(buildTool?.PlacingDef));

        void ResetBuildDrag() => _constructionActions.ResetBuildDrag();

        void UpdateBuildDragInput() => _constructionActions.UpdateBuildDragInput();

        void PlaceFPVBlueprint(IntVec3 cell) => _constructionActions.PlaceFPVBlueprint(cell);

        static bool IsWallLamp(BuildableDef def) => def != null && def.defName == "WallLamp";

        IntVec3 FPVPlacementCell() => _viewTargeting.FPVPlacementCell();

        void CancelFPVPlan() => _constructionActions.CancelFPVPlan();

        float ConstructionHeight(BuildableDef def) => _constructionActions.ConstructionHeight(def);

        static void OutlineBox(Geometry geometry, Vector3 center, Vector3 size, float line = .025f)
        {
            for (int x = -1; x <= 1; x += 2) for (int z = -1; z <= 1; z += 2)
                geometry.Box(center + new Vector3(x * size.x * .5f, 0, z * size.z * .5f), new Vector3(line, size.y + line, line), new Rect(0, 0, 1, 1));
            for (int y = 0; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z += 2)
                    geometry.Box(center + new Vector3(0, y * size.y, z * size.z * .5f), new Vector3(Mathf.Max(.001f, size.x - line), line, line), new Rect(0, 0, 1, 1));
                for (int x = -1; x <= 1; x += 2)
                    geometry.Box(center + new Vector3(x * size.x * .5f, y * size.y, 0), new Vector3(line, line, Mathf.Max(.001f, size.z - line)), new Rect(0, 0, 1, 1));
            }
        }

        Material PlanMaterial(Color color) => _constructionActions.PlanMaterial(color);


        Geometry ConstructionDoorOutline(ThingDef def) => _constructionActions.ConstructionDoorOutline(def);

        static bool IsConstructionWall(BuildableDef def) => def is ThingDef building &&
            building.Fillage == FillCategory.Full && !building.IsDoor;

        int ConstructionWallFaces(IntVec3 cell) => _constructionActions.ConstructionWallFaces(cell);

        Graphic ConstructionGraphic(BuildableDef def, ThingDef stuff, ThingStyleDef style = null) => _constructionActions.ConstructionGraphic(def, stuff, style);

        Material ConstructionMaterial(Material source, bool wall = false) => _constructionActions.ConstructionMaterial(source, wall);

        void DrawConstructionWorkSpot(Geometry geometry, BuildableDef def, IntVec3 cell, Rot4 rotation, Vector3 origin) => _constructionActions.DrawConstructionWorkSpot(geometry, def, cell, rotation, origin);

        void ConstructionVisual(BuildableDef def, Graphic graphic, Rot4 rotation, Vector3 center,
            OptionalModelLibrary.Model model, int links, int wallFaces, ThingDef stuff, Func<Material, Geometry> geometryFor) => _constructionActions.ConstructionVisual(def, graphic, rotation, center, model, links, wallFaces, stuff, geometryFor);

        private bool _constructionPlansHidden;
        private readonly HashSet<Material> _constructionPlanMaterials = new HashSet<Material>();

        private Geometry ConstructionPlanBatch(Material material, Vector3 center) => _constructionActions.ConstructionPlanBatch(material, center);

        private void ToggleConstructionPlans() => _constructionActions.ToggleConstructionPlans();

        void DrawConstructionPlan(Thing thing) => _constructionActions.DrawConstructionPlan(thing);

        void UpdatePlacementPreview() => _constructionActions.UpdatePlacementPreview();


        void AddControlHint(string hint) => _hudPresentation.AddControlHint(hint);

        public float BottomControlsHeight(float width) => _hudPresentation.BottomControlsHeight(width);

        public void DrawBottomControls(Rect rect) => _hudPresentation.DrawBottomControls(rect);

        internal void ToggleControlsHelp() => _hudPresentation.ToggleControlsHelp();

        public void DrawFPVControls(Rect viewport) => _hudPresentation.DrawFPVControls(viewport);

        void DrawBuildMenu(Rect viewport) => _actionMenus.DrawBuildMenu(viewport);

        void AddContextAction(ContextAction action, string label) => _actionMenus.AddContextAction(action, label);

        void AddNativeContextActions() => _actionMenus.AddNativeContextActions();

        void RunNativeContextAction(Action callback) => _workActions.RunNativeContextAction(callback);

        void AddObjectContextActions() => _actionMenus.AddObjectContextActions();

        void RunContextBuild(Thing target, Designator_Build command) => _constructionActions.RunContextBuild(target, command);

        void ReplaceFPVBlueprint(Blueprint_Build blueprint, BuildableDef def, ThingDef stuff, Rot4 rotation) => _constructionActions.ReplaceFPVBlueprint(blueprint, def, stuff, rotation);

        bool CanContextWork(string kind) => _actionMenus.CanContextWork(kind);

        bool CanContextAction(ContextAction action) => _actionMenus.CanContextAction(action);

        void OpenActionMenu() => _actionMenus.OpenActionMenu();

        void DismissActionMenu() => _actionMenus.DismissActionMenu();

        void RunContextAction(ContextAction action) => _workActions.RunContextAction(action);

        void DrawActionMenu(Rect viewport) => _actionMenus.DrawActionMenu(viewport);

        void DisposeFPV()
        {
            _actionMenus.CloseOrdersMenu();
            _orders?.Dispose();
            _orders = null;
            _workActions.ResetSowingInput();
            _mortarTargeting.Close();
            if (fpvTargetingWasActive && Find.Targeter?.IsTargeting == true) Find.Targeter.StopTargeting();
            fpvTargetingWasActive = false;
            floatingItemNotices.Clear();
            carryWindow?.Close(false);
            carryWindow = null;
            ResetCarryInput();
            ResetAimCycle();
            billWindow?.Close(false);
            billWindow = null;
            DisposeImmersion();
            designationIcons.Clear();
            buildCatalog.Clear(); filteredBuildCatalog.Clear(); buildCategories.Clear(); buildStuffs.Clear(); aimCandidates.Clear();
            buildTool = null; aimedThing = null; constructionDef = null;
            ResetBuildDrag();
        }

        sealed partial class HudPresentation
        {
            float _messageUntil;

            GUIStyle _floatingItemStyle;

            internal void SayAction(string text)
            {
                if (_view.simulationOnly) { Rim3DMultiplayer.Notice?.Invoke(_view.ControlledPawn, text, false); return; }
                _view.actionMessage = text;
                _messageUntil = Time.unscaledTime + 5;
            }

            internal void SayFloatingItem(string text)
            {
                if (_view.simulationOnly) { Rim3DMultiplayer.Notice?.Invoke(_view.ControlledPawn, text, true); return; }
                if (text.NullOrEmpty()) return;
                float now = Time.unscaledTime;
                if (_view.floatingItemNotices.Count > 0 && _view.floatingItemNotices[_view.floatingItemNotices.Count - 1].Text == text &&
                    now - _view.floatingItemNotices[_view.floatingItemNotices.Count - 1].Created < .5f) return;
                if (_view.floatingItemNotices.Count == 4) _view.floatingItemNotices.RemoveAt(0);
                _view.floatingItemNotices.Add(new FloatingItemNotice { Text = text, Created = now });
            }

            readonly StringBuilder _controlHints = new StringBuilder(768);
            readonly List<string> _activeControlHints = new List<string>();

            string _bottomControlsText = "";



            float _nextControlHintsUpdate;


            LoadedLanguage _bottomControlsLanguage;

            int _bottomControlsState = -1;
            int _bottomControlsBindingsVersion = -1;

            internal void AddControlHint(string hint)
            {
                if (_controlHints.Length > 0) _controlHints.Append("   ·   ");
                _controlHints.Append(hint);
                if (!_activeControlHints.Contains(hint)) _activeControlHints.Add(hint);
            }

            internal float BottomControlsHeight(float width)
            {
                bool blocked = _view.ExternalInputBlocked;
                bool alt = ViewBindings.Held(ViewAction.Cursor);
                int state = (_view.FirstPerson ? 1 : 0) | (blocked ? 2 : 0) | (_view.TextInputFocused ? 4 : 0) |
                    (_view.constructionMenu ? 8 : 0) | (_view.actionMenu ? 16 : 0) | (_view.statusMenu ? 32 : 0) |
                    (_view.mouseCaptured ? 64 : 0) | (alt ? 128 : 0) | (_view.zoneTool != null ? 256 : 0);
                bool languageChanged = _bottomControlsLanguage != LanguageDatabase.activeLanguage;
                bool bindingsChanged = _bottomControlsBindingsVersion != Rim3DSettings.Current.BindingsVersion;
                bool refresh = bindingsChanged || languageChanged || state != _bottomControlsState || Time.unscaledTime >= _nextControlHintsUpdate;
                if (refresh)
                {
                    _bottomControlsState = state;
                    _nextControlHintsUpdate = Time.unscaledTime + .15f;
                    _controlHints.Clear();
                    _activeControlHints.Clear();
                    if (_view.MortarAiming && !blocked)
                        _view.AddControlHint("Rim3D_MortarHelp".TranslateBindings().ToString());
                    else if (_view.TextInputFocused)
                        _view.AddControlHint("Rim3D_HintSearch".Translate().ToString());
                    else if (blocked)
                    {
                        if (_view.colonyWindow != null && !_view.ExternalDialogInputBlocked)
                            _view.AddControlHint("Rim3D_HintCloseInventory".TranslateBindings().ToString());
                        else _view.AddControlHint("Rim3D_HintWindowCursor".Translate().ToString());
                    }
                    else if (_view._orders?.Active == true)
                        _view.AddControlHint((_view._orders.Personal ? "Rim3D_OrdersColonistHint" : "Rim3D_OrdersControls").TranslateBindings());
                    else if (_view.zoneTool != null && !_view.constructionMenu && !_view.actionMenu && !_view.statusMenu)
                    {
                        _view.AddControlHint("Rim3D_HintWalk".TranslateBindings().ToString());
                        _view.AddControlHint("Rim3D_ZoneControls".TranslateBindings().ToString());
                    }
                    else
                    {
                        if (_view.CompassInputAvailable) _view.AddControlHint("Rim3D_CompassHint".TranslateBindings().ToString());
                        if (!_view.FirstPerson)
                        {
                            _view.AddControlHint("Rim3D_HintCameraMove".TranslateBindings().ToString());
                            _view.AddControlHint("Rim3D_HintCameraFast".TranslateBindings().ToString());
                            _view.AddControlHint("Rim3D_HintCameraRotate".Translate().ToString());
                            _view.AddControlHint("Rim3D_HintCameraZoom".Translate().ToString());
                            _view.AddControlHint("Rim3D_HintCameraReset".TranslateBindings().ToString());
                        }
                        else
                        {
                            _view.AddControlHint(_view.constructionMenu ? "Rim3D_HintCloseConstruction".TranslateBindings().ToString() : "Rim3D_HintOpenConstruction".TranslateBindings().ToString());
                            _view.AddControlHint(_view.actionMenu ? "Rim3D_HintCloseActions".Translate().ToString() : "Rim3D_HintTargetActions".Translate().ToString());
                            _view.AddControlHint(_view.statusMenu ? "Rim3D_HintStatusTabs".TranslateBindings().ToString() : "Rim3D_HintCharacterMenus".TranslateBindings().ToString());
                            if (_view.constructionMenu || _view.actionMenu || _view.statusMenu)
                                _view.AddControlHint("Rim3D_HintCloseMenu".Translate().ToString());
                            else
                            {
                                var pawn = _view.ControlledPawn;
                                var carried = pawn?.carryTracker?.CarriedThing;
                                bool resting = _view.controller.IsResting;
                                _view.AddControlHint(resting ? "Rim3D_HintStandUp".TranslateBindings().ToString() : "Rim3D_HintWalk".TranslateBindings().ToString());
                                if (!resting)
                                {
                                    _view.AddControlHint("Rim3D_HintRun".TranslateBindings().ToString());
                                    _view.AddControlHint("Rim3D_HintSneak".TranslateBindings().ToString());
                                }
                                _view.AddControlHint("Rim3D_HintCursor".TranslateBindings().ToString());
                                if (_view.mouseCaptured)
                                {
                                    _view.AddControlHint(_view.PausedByPlayer ? "Rim3D_HintResume".TranslateBindings().ToString() : "Rim3D_HintPause".TranslateBindings().ToString());
                                    _view.AddControlHint(pawn.Drafted ? "Rim3D_HintLeaveCombat".TranslateBindings().ToString() : "Rim3D_HintEnterCombat".TranslateBindings().ToString());
                                    if (_view.controller.IsActing || _view.buildTool != null || _view.constructionDef != null || resting)
                                        _view.AddControlHint("Rim3D_HintCancelAction".TranslateBindings().ToString());
                                    if (_view.buildTool != null)
                                    {
                                        _view.AddControlHint("Rim3D_HintRotateBuilding".TranslateBindings().ToString());
                                        if (!alt) _view.AddControlHint("Rim3D_HintPutToolAwayMouse".Translate().ToString());
                                        _view.AddControlHint("Rim3D_HintPutToolAwayEscape".Translate().ToString());
                                        if (!resting && !alt && _view.FPVPlacementCell().IsValid &&
                                            _view.buildTool.CanDesignateCell(_view.FPVPlacementCell()).Accepted)
                                            _view.AddControlHint("Rim3D_HintPlaceBlueprint".Translate().ToString());
                                    }
                                    else
                                    {
                                        if (_view.cellAimTargets.Count > 1) _view.AddControlHint("Rim3D_HintNextTarget".TranslateBindings().ToString());
                                        if (!resting && pawn.equipment != null && pawn.carryTracker != null &&
                                            (carried == null ? pawn.equipment.Primary != null :
                                            carried.def.IsWeapon && carried.TryGetComp<CompEquippable>() != null))
                                            _view.AddControlHint("Rim3D_HintSwapHands".TranslateBindings().ToString());
                                    }
                                    if (_view.aimedThing != null && (_view.aimedThing is Blueprint || _view.aimedThing is Frame) &&
                                        _view.aimedThing.Faction == pawn.Faction)
                                        _view.AddControlHint("Rim3D_HintCancelBlueprint".TranslateBindings().ToString());
                                    if (!resting && carried != null)
                                    {
                                        if (pawn.inventory != null && PawnControl.CanStoreCargo(pawn, carried))
                                            _view.AddControlHint("Rim3D_HintStoreItem".TranslateBindings().ToString());
                                        if (!alt && _view.buildTool == null)
                                        {
                                            int heldAction = _view.PrimaryHeldItemAction(carried);
                                            if (carried is Medicine && _view.CanTendFPV(pawn))
                                                _view.AddControlHint("Rim3D_HintUseHeld".TranslateBindings(_view.TendLabel(pawn)).ToString());
                                            else if (heldAction != int.MinValue)
                                                _view.AddControlHint("Rim3D_HintUseHeld".TranslateBindings(_view.HeldItemActionLabel(carried, heldAction)).ToString());
                                            _view.AddControlHint("Rim3D_HintHeldActions".TranslateBindings().ToString());
                                        }
                                        if (!alt) _view.AddControlHint("Rim3D_HintDropItem".TranslateBindings().ToString());
                                    }
                                    if (!resting && !alt && _view.buildTool == null && carried == null && _view.CanTendFPV(pawn))
                                    {
                                        _view.AddControlHint("Rim3D_HintUseHeld".TranslateBindings(_view.TendLabel(pawn)).ToString());
                                        _view.AddControlHint("Rim3D_HintHeldActions".TranslateBindings().ToString());
                                    }
                                    else if (!resting && !alt && _view.buildTool == null && carried == null &&
                                        (pawn.equipment?.Primary != null || pawn.apparel?.WornApparel.Count > 0))
                                        _view.AddControlHint("Rim3D_HintEquipmentActions".TranslateBindings().ToString());
                                    if (!resting && !alt && RecreationGiver(_view.aimedThing) != null)
                                        _view.AddControlHint("Rim3D_HintRecreation".TranslateBindings().ToString());
                                    if (!alt && _view.CanTouchCarryTarget(_view.aimedThing))
                                    {
                                        if (_view.HasCarryPlacement(_view.aimedThing))
                                            _view.AddControlHint(_view.CarryPlacementFailure(_view.aimedThing) ?? _view.CarryPlacementLabel(_view.aimedThing, true));
                                        else if (_view.aimedThing is Building_Bed bed &&
                                            PawnControl.CanSleepInBed(pawn, bed))
                                            _view.AddControlHint("Rim3D_HintSleep".TranslateBindings().ToString());
                                        else if (!resting && MortarTargeting.IsMortar(_view.aimedThing))
                                            _view.AddControlHint("Rim3D_MortarHint".TranslateBindings().ToString());
                                        else if (_view._carryActions.CanOpenStorage(_view.aimedThing))
                                            _view.AddControlHint("Rim3D_HintStorageInventory".TranslateBindings().ToString());
                                        else if (_view.aimedThing is Building_NutrientPasteDispenser dispenser)
                                            _view.AddControlHint(_view._carryActions.CanTakePaste(dispenser) ?
                                                "Rim3D_HintTakePaste".TranslateBindings().ToString() : "Rim3D_PasteUnavailable".Translate().ToString());
                                        else if (!resting)
                                        {
                                            if (_view.aimedThing.def.category == ThingCategory.Item)
                                            {
                                                if (carried == null && _view.CanTouchCarryTarget(_view.aimedThing))
                                                    _view.AddControlHint((_view.aimedThing.stackCount > 1 ? "Rim3D_HintTakeStack" : "Rim3D_HintTakeItem").TranslateBindings().ToString());
                                            }
                                            else if (_view.aimedThing is IConstructible project)
                                            {
                                                if (_view.aimFailureText.Length == 0)
                                                    _view.AddControlHint("Rim3D_HintBuild".TranslateBindings().ToString());
                                                if (carried != null && _view.CanTouchCarryTarget(_view.aimedThing) &&
                                                    _view.aimedThing.Faction == pawn.Faction && project.ThingCountNeeded(carried.def) > 0)
                                                    _view.AddControlHint("Rim3D_HintDeliver".TranslateBindings().ToString());
                                            }
                                            else if ((_view.aimFailureText.Length == 0 ||
                                                _view.aimedThing is Building_WorkTable table && table.BillStack.Count == 0) &&
                                                PrimaryWork(_view.aimedThing) != null)
                                                _view.AddControlHint(_view.aimedThing is Building_WorkTable ? "Rim3D_HintWork".TranslateBindings().ToString() : "Rim3D_HintTargetAction".TranslateBindings().ToString());
                                            if (!Find.TickManager.Paused && (pawn.Drafted || _view.aimedThing.HostileTo(pawn)) &&
                                                _view.buildTool == null && !_view.controller.CombatRecovering && !pawn.stances.FullBodyBusy)
                                                _view.AddControlHint("Rim3D_HintMelee".Translate().ToString());
                                        }
                                    }
                                    if (!resting && !alt && _view.aimingAtRoof && _view.roofAimFailure == null)
                                        _view.AddControlHint("Rim3D_HintRoof".TranslateBindings().ToString());
                                    if (!resting && !alt && _view.buildTool == null && pawn.Drafted && !Find.TickManager.Paused &&
                                        !_view.controller.IsActing && !_view.controller.CombatRecovering && !pawn.stances.FullBodyBusy)
                                    {
                                        var verb = pawn.equipment?.Primary?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                                        if (verb != null && Find.TickManager.TicksGame >= _view.nextManualShotTick && verb.Available())
                                            _view.AddControlHint("Rim3D_HintShoot".Translate().ToString());
                                        else if (verb == null && _view.aimedThing != null)
                                            _view.AddControlHint("Rim3D_HintMeleeAttack".Translate().ToString());
                                    }
                                }
                            }
                        }
                        if (!Rim3DMultiplayer.PawnLocked || !_view.FirstPerson)
                            _view.AddControlHint(_view.FirstPerson ? "Rim3D_HintOverview".TranslateBindings().ToString() : "Rim3D_HintFirstPerson".TranslateBindings().ToString());
                        if (!Rim3DMultiplayer.PawnLocked)
                            _view.AddControlHint("Rim3D_HintNextColonist".TranslateBindings().ToString());
                        _view.AddControlHint("Rim3D_HintTogglePlans".TranslateBindings().ToString());
                        if (!Rim3DMultiplayer.PawnLocked)
                            _view.AddControlHint(_view.constructionMenu || _view.actionMenu || _view.statusMenu || _view.buildTool != null ?
                                "Rim3D_HintReturn2D".TranslateBindings().ToString() : "Rim3D_HintReturn2DEscape".TranslateBindings().ToString());
                    }
                    _bottomControlsText = _controlHints.ToString();
                }
                _bottomControlsBindingsVersion = Rim3DSettings.Current.BindingsVersion;
                _bottomControlsLanguage = LanguageDatabase.activeLanguage;
                return 0;
            }

            internal void DrawBottomControls(Rect rect)
            {
                BottomControlsHeight(rect.width);
                if (Widgets.ButtonText(new Rect(rect.x + 12, rect.yMax - 44, 230, 32),
                    "Rim3D_ControlsHelpButton".Translate())) ToggleControlsHelp();
            }

            internal void ToggleControlsHelp()
            {
                var existing = Find.WindowStack.WindowOfType<ControlsHelpWindow>();
                if (existing != null) { existing.Close(); return; }
                _nextControlHintsUpdate = 0;
                BottomControlsHeight(UI.screenWidth);
                Find.WindowStack.Add(new ControlsHelpWindow(_view, _activeControlHints));
            }

            sealed class ControlsHelpWindow : Window
            {
                readonly MapView _owner;
                readonly List<string> _active;
                readonly List<string> _other = new List<string>();
                Vector2 _scroll;
                static readonly string[] _hintKeys =
                {
                    "Rim3D_HintBuild",
                    "Rim3D_HintCameraFast",
                    "Rim3D_HintCameraMove",
                    "Rim3D_HintCameraReset",
                    "Rim3D_HintCameraRotate",
                    "Rim3D_HintCameraZoom",
                    "Rim3D_HintCancelAction",
                    "Rim3D_HintCancelBlueprint",
                    "Rim3D_HintCharacterMenus",
                    "Rim3D_HintCloseActions",
                    "Rim3D_HintCloseConstruction",
                    "Rim3D_HintCloseInventory",
                    "Rim3D_HintCloseMenu",
                    "Rim3D_HintCursor",
                    "Rim3D_HintDeliver",
                    "Rim3D_HintDropItem",
                    "Rim3D_HintEnterCombat",
                    "Rim3D_HintFirstPerson",
                    "Rim3D_HintLeaveCombat",
                    "Rim3D_HintMelee",
                    "Rim3D_HintMeleeAttack",
                    "Rim3D_HintNextColonist",
                    "Rim3D_HintTogglePlans",
                    "Rim3D_HintNextTarget",
                    "Rim3D_HintOpenConstruction",
                    "Rim3D_HintOverview",
                    "Rim3D_HintPause",
                    "Rim3D_HintPlaceBlueprint",
                    "Rim3D_HintPutToolAwayEscape",
                    "Rim3D_HintPutToolAwayMouse",
                    "Rim3D_HintRecipes",
                    "Rim3D_HintResume",
                    "Rim3D_HintReturn2D",
                    "Rim3D_HintReturn2DEscape",
                    "Rim3D_HintRoof",
                    "Rim3D_HintRotateBuilding",
                    "Rim3D_HintRun",
                    "Rim3D_HintSearch",
                    "Rim3D_HintShoot",
                    "Rim3D_HintSleep",
                    "Rim3D_HintSneak",
                    "Rim3D_HintStandUp",
                    "Rim3D_HintStatusTabs",
                    "Rim3D_HintStoreItem",
                    "Rim3D_HintSwapHands",
                    "Rim3D_HintTakeStack",
                    "Rim3D_HintTakeItem",
                    "Rim3D_HintTargetAction",
                    "Rim3D_HintTargetActions",
                    "Rim3D_HintWalk",
                    "Rim3D_HintWindowCursor",
                    "Rim3D_HintWork",
                    "Rim3D_HintEquipmentActions",
                    "Rim3D_HintHeldActions",
                    "Rim3D_HintStorageInventory",
                    "Rim3D_HintBuryCarried",
                    "Rim3D_HintPutCarriedInBed",
                    "Rim3D_HintRecreation",
                    "Rim3D_HintTakePaste",
                    "Rim3D_ZoneControls",
                    "Rim3D_CompassHint",
                    "Rim3D_MortarHint",
                    "Rim3D_MortarHelp",
                };

                public override Vector2 InitialSize => new Vector2(Mathf.Min(820, UI.screenWidth - 20), Mathf.Min(760, UI.screenHeight - 20));

                internal ControlsHelpWindow(MapView owner, List<string> active)
                {
                    _owner = owner;
                    _active = new List<string>(active);
                    layer = WindowLayer.Dialog;
                    preventCameraMotion = absorbInputAroundWindow = true;
                    closeOnAccept = false;
                    doCloseX = true;
                    foreach (var key in _hintKeys)
                    {
                        string hint = key.TranslateBindings();
                        if (!_active.Contains(hint) && !_other.Contains(hint)) _other.Add(hint);
                    }
                    foreach (var action in ViewBindings.Actions)
                    {
                        if (action == ViewAction.Orders) continue;
                        string hint = ViewBindings.Label(action) + " –  " + ("Rim3D_Binding" + action).Translate();
                        _other.Add(hint);
                    }
                    foreach (var entry in API.RimVisionInput.Bindings.Entries)
                        if (entry.Active) _other.Add(API.RimVisionInput.BindingLabel(entry.Id) + " –  " + entry.Value.Label);
                    _other.Add("F1 –  " + "Rim3D_ControlsHelpTitle".Translate());
                    _other.Add("F11 –  " + "Rim3D_ControlsHelpInterface".Translate());
                }

                public override void WindowUpdate()
                {
                    base.WindowUpdate();
                    if (Rim3DPreview.Instance?.ActiveWindow?.View != _owner) Close(false);
                }

                public override void DoWindowContents(Rect rect)
                {
                    Text.Font = GameFont.Medium;
                    Widgets.Label(new Rect(0, 0, rect.width - 30, 34), "Rim3D_ControlsHelpTitle".Translate());
                    Text.Font = GameFont.Small;
                    Widgets.Label(new Rect(0, 40, rect.width, 54), "Rim3D_ControlsHelpLegend".Translate());
                    var outer = new Rect(0, 100, rect.width, Mathf.Max(1, rect.height - 100));
                    float width = outer.width - 20;
                    float height = 0;
                    foreach (var hint in _active) height += Mathf.Max(32, Text.CalcHeight(hint, width - 24) + 12) + 4;
                    foreach (var hint in _other) height += Mathf.Max(32, Text.CalcHeight(hint, width - 24) + 12) + 4;
                    Widgets.BeginScrollView(outer, ref _scroll, new Rect(0, 0, width, height));
                    float y = 0;
                    DrawHints(_active, width, ref y, true);
                    DrawHints(_other, width, ref y, false);
                    Widgets.EndScrollView();
                }

                static void DrawHints(List<string> hints, float width, ref float y, bool active)
                {
                    foreach (var hint in hints)
                    {
                        float height = Mathf.Max(32, Text.CalcHeight(hint, width - 24) + 12);
                        var row = new Rect(0, y, width, height);
                        Widgets.DrawBoxSolid(row, active ? new Color(.12f, .27f, .23f) : new Color(.12f, .13f, .15f));
                        if (active) Widgets.DrawBoxSolid(new Rect(0, y, 3, height), new Color(.4f, .9f, .68f));
                        Widgets.Label(new Rect(12, y + 6, width - 24, height - 6), hint);
                        y += height + 4;
                    }
                }
            }

            internal void DrawFPVControls(Rect viewport)
            {
                if (!_view.FirstPerson)
                {
                    if (_view.constructionMenu) _view.DrawBuildMenu(viewport);
                    return;
                }
                if (_view.controller.InputBlocked) return;
                if (_view._orders?.Active == true)
                {
                    _view.Badge(new Rect(viewport.center.x - 350, viewport.yMax - 130, 700, 100),
                        _view._orders.Label + " · " + _view._orders.SelectionCount + "\n" +
                        (_view._orders.Personal ? "Rim3D_OrdersColonistHint" : "Rim3D_OrdersControls").TranslateBindings() +
                        "\n" + _view.actionMessage, _view.activityStyle);
                    return;
                }
                var center = viewport.center;
                _view.DrawFPVTargetSelection(viewport);
                _view.DrawZoneHud(viewport);
                DrawInspectionHud(viewport);
                _view.DrawManualFireHud(viewport);
                if (_view.zoneTool == null && _view.aimingAtRoof && !_view.SelectingFPVTarget && !_view.actionMenu && !_view.statusMenu && !_view.constructionMenu)
                {
                    string roofHint = _view.roofAimFailure ?? "Rim3D_HintRoof".TranslateBindings().ToString();
                    if (!roofHint.NullOrEmpty())
                        _view.Badge(new Rect(center.x - 300, center.y + 24, 600, 24), roofHint, _view.activityStyle);
                }
                if (_view.zoneTool == null && !_view.SelectingFPVTarget && !_view.actionMenu && !_view.statusMenu && !_view.constructionMenu &&
                    (_view.CanTouchCarryTarget(_view.aimedThing) || !_view.controller.IsResting && RecreationGiver(_view.aimedThing) != null))
                {
                    _view.Badge(new Rect(center.x - 300, center.y + 24, 600, 24), _view.aimedThing.LabelCap +
                        (_view.HasCarryPlacement(_view.aimedThing) ? " · " + (_view.CarryPlacementFailure(_view.aimedThing) ?? _view.CarryPlacementLabel(_view.aimedThing, true)) :
                        _view._carryActions.CanOpenStorage(_view.aimedThing) ? " · " + "Rim3D_HintStorageInventory".TranslateBindings().ToString() :
                        RecreationGiver(_view.aimedThing) != null ? "Rim3D_HintRecreation".TranslateBindings().ToString() :
                        MortarTargeting.IsMortar(_view.aimedThing) ? " · " + "Rim3D_MortarHint".TranslateBindings().ToString() :
                        _view.aimedThing is Building_Bed ? "Rim3D_AimBedHint".TranslateBindings().ToString() :
                        _view.aimedThing is Plant ? "Rim3D_AimPlantHint".TranslateBindings().ToString() :
                        _view.aimedThing is Building_WorkTable ? "Rim3D_AimWorkTableHint".TranslateBindings().ToString() :
                        _view.aimedThing is Blueprint || _view.aimedThing is Frame ? "Rim3D_AimBlueprintHint".TranslateBindings().ToString() :
                        _view.aimedThing.def.category == ThingCategory.Item ? (_view.aimedThing.stackCount > 1 ? "Rim3D_AimItemHint" : "Rim3D_AimSingleItemHint").TranslateBindings().ToString() : "Rim3D_AimActionHint".TranslateBindings().ToString()) +
                        (_view.cellAimTargets.Count > 1 ? "Rim3D_AimTargetIndex".TranslateBindings(_view.cellAimTargets.IndexOf(_view.aimedThing) + 1, _view.cellAimTargets.Count).ToString() : ""), _view.activityStyle);
                    float infoY = center.y + 50;
                    if (_view.aimedThing is Building_Bed aimedBed)
                    {
                        var owners = aimedBed.OwnersForReading;
                        string names = "";
                        foreach (var owner in owners)
                            if (owner != null) names += (names.Length == 0 ? "" : ", ") + owner.LabelShortCap;
                        _view.Badge(new Rect(center.x - 360, infoY, 720, 24), names.Length == 0 ?
                            "Rim3D_BedUnowned".Translate().ToString() : "Rim3D_BedOwners".Translate(names).ToString(), _view.activityStyle);
                        infoY += 26;
                    }
                    if (_view.aimPlantText.Length > 0)
                    {
                        _view.Badge(new Rect(center.x - 360, infoY, 720, 24), _view.aimPlantText, _view.activityStyle);
                        infoY += 26;
                    }
                    if (_view.aimSkillText.Length > 0)
                    {
                        _view.Badge(new Rect(center.x - 300, infoY, 600, 24), _view.aimSkillText, _view.activityStyle);
                        infoY += 26;
                    }
                    if (_view.aimFailureText.Length > 0 && !_view.controller.IsActing)
                        _view.Badge(new Rect(center.x - 360, infoY, 720, 24), _view.aimFailureText, _view.activityStyle);
                }
                var carried = _view.ControlledPawn.carryTracker?.CarriedThing;
                if (carried != null)
                    _view.Badge(new Rect(center.x - 260, viewport.yMax - 76, 520, 24), "Rim3D_HintDropItem".TranslateBindings().ToString(), _view.activityStyle);
                if (_view.buildTool != null)
                    _view.Badge(new Rect(center.x - 240, viewport.yMax - 44, 480, 26), _view.buildTool.PlacingDef.LabelCap.ToString() +
                        (_view.buildTool.StuffDef != null ? " · " + _view.buildTool.StuffDef.LabelCap.ToString() : "") + "Rim3D_PlacementHudHint".TranslateBindings().ToString(), _view.activityStyle);
                if (_view.constructionMenu) _view.DrawBuildMenu(viewport);
                if (_view.actionMenu) _view.DrawActionMenu(viewport);
                if (_view.statusMenu) _view.DrawStatusMenu(viewport);
                if (_floatingItemStyle == null) _floatingItemStyle = new GUIStyle(_view.activityStyle)
                { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                for (int i = _view.floatingItemNotices.Count - 1, row = 0; i >= 0; i--)
                {
                    float age = Time.unscaledTime - _view.floatingItemNotices[i].Created;
                    if (age >= 2.5f) { _view.floatingItemNotices.RemoveAt(i); continue; }
                    float width = Mathf.Min(620, viewport.width - 32);
                    float alpha = Mathf.Clamp01((2.5f - age) / .7f);
                    var textRect = new Rect(viewport.center.x - width * .5f,
                        viewport.center.y + 110 - age * 48 - row++ * 38, width, 42);
                    _view.Badge(textRect, _view.floatingItemNotices[i].Text, _floatingItemStyle, alpha);
                }
            }

        }

        readonly ActionMenus _actionMenus;

        sealed partial class ActionMenus
        {
            readonly MapView _view;

            internal ActionMenus(MapView view)
            {
                _view = view;
            }

            internal void OpenFPVBills(Building_WorkTable table)
            {
                if (table.Faction != Faction.OfPlayer) { _view.SayAction("Rim3D_BillsOwnBuildings".Translate().ToString()); return; }
                _view.billWindow?.Close(false);
                _view.constructionMenu = _view.actionMenu = _view.statusMenu = false;
                _view.buildSearchFocused = false;
                _view.ReleaseMouse();
                _view.billWindow = new FPVBillWindow(_view, table);
                Find.WindowStack.Add(_view.billWindow);
            }

            readonly List<ContextEntry> _contextEntries = new List<ContextEntry>(12);

            float _contextMenuWidth;

            float _contextMenuHeight;

            Vector2 _actionScroll;

            string _contextTitle;

            string _buildSearch = "";

            DesignationCategoryDef _buildCategory;

            DesignationCategoryDef _filteredBuildCategory;

            string _filteredBuildSearch;

            Vector2 _buildScroll;

            internal bool CloseFPVMenu()
            {
                if (_view.zoneTool != null) { _view.CancelZoneTool(); return true; }
                if (_view.colonyWindow != null) { _view.CloseColonyTab(); return true; }
                if (_view.constructionMenu || _view.actionMenu || _view.statusMenu)
                {
                    if (_view.constructionMenu) { _view.buildSearchFocused = false; GUI.FocusControl(null); }
                    _view.constructionMenu = _view.actionMenu = _view.statusMenu = false;
                    return true;
                }
                if (_view.buildTool != null) { _view.buildTool = null; return true; }
                return false;
            }

            internal void EnsureBuildCatalog()
            {
    #if UNITY_EDITOR
                if (_view.editorBuildCatalogUnlocked != EditorSandbox.UnlockAll)
                {
                    _view.editorBuildCatalogUnlocked = EditorSandbox.UnlockAll;
                    _view.buildCatalog.Clear();
                    _view.buildCategories.Clear();
                    _buildCategory = null;
                    _filteredBuildSearch = null;
                    if (_view.buildTool != null && !_view.buildTool.Visible) _view.buildTool = null;
                }
    #endif
                if (_view.buildCatalog.Count > 0) return;
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                    if (def.BuildableByPlayer && def.category == ThingCategory.Building && new FPVBuildDesignator(def).Visible)
                        _view.buildCatalog.Add(def);
                foreach (var def in DefDatabase<TerrainDef>.AllDefsListForReading)
                    if (def.BuildableByPlayer && new FPVBuildDesignator(def).Visible) _view.buildCatalog.Add(def);
                _view.buildCatalog.Sort((a, b) =>
                {
                    int order = b.designationCategory.order.CompareTo(a.designationCategory.order);
                    if (order == 0) order = a.uiOrder.CompareTo(b.uiOrder);
                    return order != 0 ? order : string.Compare(a.label, b.label, StringComparison.CurrentCultureIgnoreCase);
                });
                foreach (var def in _view.buildCatalog)
                    if (!_view.buildCategories.Contains(def.designationCategory)) _view.buildCategories.Add(def.designationCategory);
                _view.buildCategories.Sort((a, b) => b.order.CompareTo(a.order));
                _filteredBuildSearch = null;
            }

            internal void FilterBuildCatalog()
            {
                if (_filteredBuildSearch == _buildSearch && _filteredBuildCategory == _buildCategory) return;
                _filteredBuildSearch = _buildSearch;
                _filteredBuildCategory = _buildCategory;
                _buildScroll = Vector2.zero;
                _view.filteredBuildCatalog.Clear();
                foreach (var def in _view.buildCatalog)
                    if ((_buildCategory == null || def.designationCategory == _buildCategory) &&
                        (_buildSearch.NullOrEmpty() || def.label.IndexOf(_buildSearch, StringComparison.CurrentCultureIgnoreCase) >= 0))
                        _view.filteredBuildCatalog.Add(def);
            }

            internal void DrawBuildCategory(Rect rect, DesignationCategoryDef category)
            {
                Widgets.DrawBoxSolid(rect, _buildCategory == category ? new Color(.25f, .35f, .43f) : new Color(.14f, .18f, .22f));
                if (Widgets.ButtonText(rect, category?.LabelCap.ToString() ?? "Rim3D_AllCategories".Translate().ToString(), false)) _buildCategory = category;
            }

            internal void SelectFPVReinstall(Thing target)
            {
                if (!CanReinstallFPV(target, _view.map)) return;
                var inner = target.GetInnerIfMinified();
                _view.SelectFPVBuild(inner.def, inner.Stuff);
                _view.buildRotation = inner.def.rotatable ? inner.Rotation : Rot4.North;
                _view.buildTool.SetRotation(_view.buildRotation);
                _view.buildTool.ReinstallTarget = target;
                _view.buildStuffs.Clear();
                _view.SayAction("Rim3D_MoveFurnitureHint".TranslateBindings().ToString());
            }

            internal void SelectFPVBuild(BuildableDef def, ThingDef selectedStuff = null)
            {
                if (_view.ControlledPawn?.Drafted == true) _view.controller.SetCombat(false);
                _view.ResetBuildDrag();
                _view.CancelZoneTool();
                if (!(def is ThingDef rotatedDef) || !rotatedDef.rotatable) _view.buildRotation = Rot4.North;
                _view.buildTool = new FPVBuildDesignator(def);
                _view.buildTool.SetRotation(_view.buildRotation);
                _view.buildStuffs.Clear();
                if (def.MadeFromStuff)
                    foreach (var stuff in DefDatabase<ThingDef>.AllDefsListForReading)
                        if (stuff.IsStuff && stuff.stuffProps.CanMake(def)) _view.buildStuffs.Add(stuff);
                if (def.MadeFromStuff) _view.buildTool.SetStuffDef(selectedStuff ?? GenStuff.DefaultStuffFor(def));
                _view.constructionMenu = false;
                _view.buildSearchFocused = false;
                GUI.FocusControl(null);
                _view.SayAction(IsWallTorch(def) ? "Rim3D_WallTorchPlacementHint".TranslateBindings().ToString() :
                    IsWallLamp(def) ? "Rim3D_WallLampPlacementHint".TranslateBindings().ToString() :
                    def.defName == "Wall" ? "Rim3D_WallPlacementHint".TranslateBindings().ToString() :
                    def is TerrainDef ? "Rim3D_FloorPlacementHint".TranslateBindings().ToString() :
                    IsConduit(def) ? "Rim3D_ConduitPlacementHint".TranslateBindings().ToString() :
                    "Rim3D_PlacementHint".TranslateBindings().ToString());
            }

            internal void OpenFPVBuildMaterialMenu(BuildableDef def, bool beginPlacement)
            {
                if (_view.buildTool?.ReinstallTarget != null && !beginPlacement) return;
                if (!def.MadeFromStuff) return;
                var currentTool = _view.buildTool;
                var options = new List<FloatMenuOption>();
                // Resolve compatible materials only when the player opens the picker.
                foreach (var stuff in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (!stuff.IsStuff || !stuff.stuffProps.CanMake(def)) continue;
                    var selectedStuff = stuff;
                    options.Add(new FloatMenuOption(stuff.LabelCap.ToString(), () =>
                    {
                        if (beginPlacement) _view.SelectFPVBuild(def, selectedStuff);
                        else if (_view.buildTool == currentTool) currentTool.SetStuffDef(selectedStuff);
                    }));
                }
                if (options.Count == 0) return;
                options.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
                _view.buildSearchFocused = false;
                GUI.FocusControl(null);
                _view.ReleaseMouse();
                Find.WindowStack.Add(new FloatMenu(options));
            }

            internal void DrawBuildMenu(Rect viewport)
            {
                _view.EnsureBuildCatalog();
                var panel = new Rect(viewport.xMax - 380, viewport.y + 12, 368, Mathf.Min(560, viewport.height - 100));
                Widgets.DrawBoxSolid(panel, new Color(.07f, .09f, .12f, .96f));
                Widgets.Label(new Rect(panel.x + 12, panel.y + 8, panel.width - 60, 26), "Rim3D_BuildMenu".TranslateBindings().ToString());
                if (Widgets.ButtonText(new Rect(panel.xMax - 36, panel.y + 6, 24, 26), "×"))
                { _view.constructionMenu = false; _view.buildSearchFocused = false; GUI.FocusControl(null); return; }
                var searchRect = new Rect(panel.x + 12, panel.y + 38, panel.width - 24, 28);
                if (Event.current.type == EventType.MouseDown && !searchRect.Contains(Event.current.mousePosition))
                { GUI.FocusControl(null); _view.buildSearchFocused = false; }
                GUI.SetNextControlName(BuildSearchControl);
                _buildSearch = Widgets.TextField(searchRect, _buildSearch);
                _view.buildSearchFocused = GUI.GetNameOfFocusedControl() == BuildSearchControl;
                if (_view.FirstPerson && Widgets.ButtonText(new Rect(panel.x + 12, panel.y + 72, panel.width - 24, 28), "Rim3D_ZonesMenu".Translate()))
                { _view.OpenZoneMenu(); return; }
                float listY = panel.y + 106;
                if (_view.buildTool != null && _view.buildStuffs.Count > 0 && Widgets.ButtonText(new Rect(panel.x + 12, listY, panel.width - 24, 28),
                    "Rim3D_BuildMaterial".Translate(_view.buildTool.StuffDef.LabelCap).ToString()))
                    _view.OpenFPVBuildMaterialMenu(_view.buildTool.PlacingDef, false);
                if (_view.buildTool != null && _view.buildStuffs.Count > 0) listY += 34;
                _view.DrawBuildCategory(new Rect(panel.x + 12, listY, panel.width - 24, 24), null);
                listY += 28;
                float categoryWidth = (panel.width - 28) * .5f;
                for (int i = 0; i < _view.buildCategories.Count; i += 2)
                {
                    var left = _view.buildCategories[i];
                    var right = i + 1 < _view.buildCategories.Count ? _view.buildCategories[i + 1] : null;
                    if ((left.preferredColumn == 1 || right?.preferredColumn == 0) &&
                        left.preferredColumn != 0 && right?.preferredColumn != 1)
                    { var swap = left; left = right; right = swap; }
                    if (left != null) _view.DrawBuildCategory(new Rect(panel.x + 12, listY, categoryWidth, 24), left);
                    if (right != null) _view.DrawBuildCategory(new Rect(panel.x + 16 + categoryWidth, listY, categoryWidth, 24), right);
                    listY += 28;
                }
                _view.FilterBuildCatalog();
                var list = new Rect(panel.x + 12, listY + 4, panel.width - 24, Mathf.Max(1, panel.yMax - listY - 16));
                int count = _view.filteredBuildCatalog.Count;
                const int columns = 3;
                const float cardHeight = 104;
                var content = new Rect(0, 0, list.width - 18, Mathf.Ceil(count / (float)columns) * cardHeight);
                bool materialClick = !_view.ExternalInputBlocked && Event.current.type == EventType.MouseDown &&
                    Event.current.button == 1 && list.Contains(Event.current.mousePosition);
                BuildableDef materialMenuDef = null;
                Widgets.BeginScrollView(list, ref _buildScroll, content);
                int first = Mathf.Max(0, Mathf.FloorToInt(_buildScroll.y / cardHeight) * columns);
                int end = Mathf.Min(count, Mathf.CeilToInt((_buildScroll.y + list.height) / cardHeight) * columns);
                for (int row = first; row < end; row++)
                {
                    var def = _view.filteredBuildCatalog[row];
                    var card = new Rect((row % columns) * (content.width / columns), (row / columns) * cardHeight,
                        content.width / columns - 4, cardHeight - 4);
                    // Draw only the visible rows; large mod catalogs need no offscreen icons.
                    Widgets.DrawBoxSolid(card, new Color(.14f, .18f, .22f));
                    Widgets.DefIcon(new Rect(card.x + 14, card.y + 4, card.width - 28, 52), def);
                    var oldFont = Text.Font; Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(card.x + 4, card.y + 58, card.width - 8, 40), def.LabelCap);
                    Text.Font = oldFont;
                    TooltipHandler.TipRegion(card, def.MadeFromStuff ?
                        (def.description ?? def.label) + "\n\n" + "Rim3D_BuildMaterialRightClick".Translate().ToString() :
                        def.description ?? def.label);
                    if (def.MadeFromStuff)
                    {
                        var e = Event.current;
                        if (materialClick && e.type == EventType.MouseDown &&
                            card.Contains(e.mousePosition))
                        {
                            e.Use();
                            materialMenuDef = def;
                        }
                    }
                    if (Widgets.ButtonInvisible(card)) _view.SelectFPVBuild(def);
                }
                Widgets.EndScrollView();
                if (materialMenuDef != null) _view.OpenFPVBuildMaterialMenu(materialMenuDef, true);
                if (count == 0) Widgets.Label(list, "Rim3D_NoSearchResults".Translate().ToString());
                DrawInspectionButtons(new Rect(panel.x, panel.yMax + 4, panel.width, 72));
            }

            internal void AddContextAction(ContextAction action, string label)
            {
                if (!_view.CanContextAction(action)) return;
                var entry = new ContextEntry(action, label);
                _contextEntries.Add(entry);
            }

            internal void AddNativeContextActions()
            {
                if (!PawnControl.Eligible(_view.contextPawn) || _view.controller.IsResting || Find.CurrentMap != _view.map) return;
                var cell = _view.contextTarget?.Position ?? _view.aimedCell;
                if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell) || _view.aimingAtRoof) return;
                var previousTarget = FPVFloatMenuTargetPatch.Target;
                var previousWorkPawn = FPVManualWorkAssignmentPatch.Pawn;
                var previousPawn = FloatMenuMakerMap.makingFor;
                try
                {
                    // The 3D ray has already selected the object. Do not pick again using
                    // the vanilla sprite bounds and the 2D mouse projection.
                    FPVFloatMenuTargetPatch.Target = _view.contextTarget;
                    FPVManualWorkAssignmentPatch.Pawn = _view.contextPawn;
                    var options = FloatMenuMakerMap.GetOptions(new List<Pawn> { _view.contextPawn },
                        cell.ToVector3Shifted(), out var context);
                    if (options == null) return;
                    foreach (var option in options)
                    {
                        if (option == null) continue;
                        // Use the nearby-only manual arrest command in either combat mode.
                        if (_view.contextTarget is Pawn arrestTarget && option.Label == _view.ArrestLabel(arrestTarget)) continue;
                        var callback = option.action;
                        if (callback != null)
                            option.action = () => _view.RunNativeContextAction(callback);
                        _contextEntries.Add(new ContextEntry
                        {
                            Action = ContextAction.Native, Label = option.Label, Option = option
                        });
                    }
                }
                finally
                {
                    FPVFloatMenuTargetPatch.Target = previousTarget;
                    FPVManualWorkAssignmentPatch.Pawn = previousWorkPawn;
                    FloatMenuMakerMap.makingFor = previousPawn;
                }
            }

            internal void AddObjectContextActions()
            {
                var target = _view.contextTarget;
                if (target == null || target.Destroyed || !target.Spawned || target.Map != _view.map ||
                    _view.map.fogGrid.IsFogged(target.Position) || _view.aimingAtRoof) return;

                // Get the same commands as the selected object's 2D gizmo bar, once per
                // menu opening. Keep the original command callbacks and DLC/mod rules.
                foreach (var gizmo in target.GetGizmos())
                {
                    if (gizmo is Designator_Install && CanReinstallFPV(target, _view.map)) continue;
                    if (!(gizmo is Command command) || !command.Visible || command.LabelCap.NullOrEmpty()) continue;
                    string label = command.LabelCap;
                    if (command is Command_Toggle toggle && toggle.isActive != null)
                    {
                        bool active = toggle.isActive();
                        if (toggle.icon == TexCommand.ForbidOff)
                            label = (active ? "CommandForbid" : "CommandAllow").Translate().CapitalizeFirst();
                        else if (active)
                            label = "✓ " + label;
                    }
                    if (command.Disabled && !command.disabledReason.NullOrEmpty())
                        label += " · " + command.disabledReason;
                    var option = new FloatMenuOption(label, command.Disabled ? (Action)null :
                        () => _view.RunNativeContextAction(() =>
                        {
                            if (!command.Disabled && command.Visible)
                            {
                                if (command is Designator_Build build)
                                    _view.RunContextBuild(target, build);
                                else
                                {
                                    var previousDesignator = Find.DesignatorManager.SelectedDesignator;
                                    command.ProcessInput(new Event { type = EventType.MouseDown, button = 0 });
                                    if (Find.DesignatorManager.SelectedDesignator is Designator_Build selectedBuild &&
                                        selectedBuild != previousDesignator)
                                    {
                                        _view.SelectFPVBuild(selectedBuild.PlacingDef, selectedBuild.StuffDef);
                                        Find.DesignatorManager.Deselect();
                                    }
                                }
                            }
                        }));
                    option.tooltip = command.Desc;
                    _contextEntries.Add(new ContextEntry
                    {
                        Action = ContextAction.Native, Label = label, Option = option
                    });
                }
            }

            internal bool CanContextWork(string kind)
            {
                if (_view.ManualActionFailure(_view.contextTarget, kind) != null) return false;
                foreach (var def in _view.ManualWorkers(kind == "Build" && _view.NeedsFloorRemoval(_view.contextTarget) ? "RemoveFloor" : kind))
                    if (def.Worker is WorkGiver_Scanner worker &&
                        (def.workType == null || !_view.contextPawn.WorkTypeIsDisabled(def.workType)) &&
                        worker.MissingRequiredCapacity(_view.contextPawn) == null)
                    {
                        // UI previews must not create jobs or alter ingredient-search
                        // caches on only one MP client. Validate the real bill job
                        // when the synchronized work command executes.
                        if (kind != "Bill" || Rim3DMultiplayer.LocalInput) return true;
                        // Ingredient searches happen only on opening/choosing an action.
                        var job = _view.ManualWorkJob(_view.contextTarget, kind);
                        if (job == null) return false;
                        JobMaker.ReturnToPool(job);
                        return true;
                    }
                return false;
            }

            internal bool CanContextAction(ContextAction action)
            {
                var pawn = _view.contextPawn;
                var target = _view.contextTarget;
                if (pawn != _view.ControlledPawn || !PawnControl.Eligible(pawn) || _view.controller.IsResting) return false;
                var carried = pawn.carryTracker?.CarriedThing;
                if (action == ContextAction.Store) return carried != null && pawn.inventory != null && PawnControl.CanStoreCargo(pawn, carried);
                if (action == ContextAction.Drop) return carried != null;
                if (action == ContextAction.SelfTend) return _view.CanTendFPV(pawn);
                if (action == ContextAction.RoofFlat || action == ContextAction.RoofProcedural)
                    return _view.aimingAtRoof && _view.RoofPlacementFailure(_view.aimedCell) == null;
                if (action == ContextAction.RemoveRoof)
                    return _view.aimingAtRoof && _view.RoofRemovalFailure(_view.aimedCell) == null;
                if (target == null || target.Destroyed || !target.Spawned || target.Map != _view.map || _view.map.fogGrid.IsFogged(target.Position)) return false;
                switch (action)
                {
                    case ContextAction.Reinstall: return CanReinstallFPV(target, _view.map);
                    case ContextAction.Bills: return target is Building_WorkTable && target.Faction == Faction.OfPlayer;
                    case ContextAction.StorageSettings: return target.Faction == Faction.OfPlayer && CarryActions.StorageSlots(target) != null;
                    case ContextAction.ClaimBuilding: return _view.CanClaimFPVBuilding(target);
                    case ContextAction.CancelPlan: return (target is Blueprint || target is Frame) && target.Faction == pawn.Faction;
                    case ContextAction.Ingest: return _view.CanIngestFPV(target);
                    case ContextAction.Arrest: return target is Pawn arrestTarget && _view.CanArrestFPV(arrestTarget);
                    case ContextAction.Tend: return target is Pawn patient && patient != pawn && _view.CanTendFPV(patient);
                    case ContextAction.Interact:
                        if (target is Building_NutrientPasteDispenser dispenser) return _view._carryActions.CanTakePaste(dispenser);
                        if (MortarTargeting.IsMortar(target)) return MortarTargeting.CanMan(pawn, _view.map, target);
                        if (_view.HasCarryPlacement(target)) return _view.CarryPlacementFailure(target) == null;
                        if (CarryActions.StorageSlots(target) != null) return _view._carryActions.CanOpenStorage(target);
                        if (target is IConstructible project)
                            return carried != null && _view.CanTouchCarryTarget(target) && target.Faction == pawn.Faction &&
                                project.ThingCountNeeded(carried.def) > 0 && pawn.CanReserve(target) &&
                                !target.IsForbidden(pawn) && !target.IsBurning() && GenConstruct.FirstBlockingThing(target, pawn) == null;
                        if (target is Building_WorkTable) return _view.CanContextWork("Bill");
                        if (target is Building_ResearchBench) return _view.CanContextWork("Research");
                        if (target is Building_Bed bed) return PawnControl.CanSleepInBed(pawn, bed) &&
                            pawn.CanReach(bed, PathEndMode.OnCell, Danger.Deadly) &&
                            pawn.CanReserve(bed, bed.SleepingSlotsCount, 0);
                        return target.def.category == ThingCategory.Item && carried == null && pawn.carryTracker != null &&
                            _view.CanTouchCarryTarget(target) && pawn.CanReserve(target) && !target.IsForbidden(pawn) && !target.IsBurning();
                    case ContextAction.TakeCount: return target.stackCount > 1 && _view.CanContextAction(ContextAction.Interact);
                    case ContextAction.Equip:
                        return target.def.IsWeapon && target.TryGetComp<CompEquippable>() != null && pawn.equipment != null &&
                            pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && !target.IsForbidden(pawn) &&
                            !target.IsBurning() && _view.CanTouchCarryTarget(target) && pawn.CanReserve(target) &&
                            EquipmentUtility.CanEquip(target, pawn) &&
                            MassUtility.FreeSpace(pawn) + (pawn.equipment.Primary?.GetStatValue(StatDefOf.Mass) ?? 0) >= target.GetStatValue(StatDefOf.Mass);
                    case ContextAction.Cut: return _view.CanContextWork("Cut");
                    case ContextAction.Harvest: return _view.CanContextWork("Harvest");
                    case ContextAction.Mine: return _view.CanContextWork("Mine");
                    case ContextAction.SmoothWall: return _view.CanContextWork("SmoothWall");
                    case ContextAction.Repair: return _view.CanContextWork("Repair");
                    case ContextAction.Build: return _view.CanTouchCarryTarget(target) &&
                        (target is Blueprint_Install ? target.Faction == pawn.Faction : _view.CanContextWork("Build"));
                    case ContextAction.Deconstruct:
                        return _view.CanContextWork("Deconstruct") && (new Designator_Deconstruct().CanDesignateThing(target).Accepted ||
                            _view.map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) != null);
                    case ContextAction.Melee:
                        return target != pawn && (!(target is Pawn victim) || !victim.Dead) &&
                            PawnControl.CanUseCombatSkill(pawn, SkillDefOf.Melee) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
                            (pawn.Drafted || target.HostileTo(pawn)) && !Find.TickManager.Paused && !_view.controller.CombatRecovering &&
                            !pawn.stances.FullBodyBusy && pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly);
                }
                return false;
            }

            internal void OpenActionMenu()
            {
                if (_view.actionMenu) { _view.DismissActionMenu(); return; }
                _view.ResetCarryInput();
                _view.PickFPVTarget();
                // Match the nearby interaction hints instead of the longer aiming range.
                if (_view.aimedThing != null ? !_view.CanTouchCarryTarget(_view.aimedThing) :
                    !PawnControl.Eligible(_view.ControlledPawn) || _view.controller.IsResting ||
                    !_view.aimedCell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(_view.aimedCell) ||
                    !ReachabilityImmediate.CanReachImmediate(_view.ControlledPawn, _view.aimedCell, PathEndMode.Touch)) return;
                _view.contextTarget = _view.aimedThing;
                _view._contextRoofCell = _view.aimingAtRoof ? _view.aimedCell : IntVec3.Invalid;
                _view.contextPawn = _view.ControlledPawn;
                _contextTitle = _view.aimedThing?.LabelCap.ToString() ?? (_view.aimingAtRoof ? "Rim3D_Roof".Translate().ToString() : "Rim3D_PawnActions".Translate().ToString());
                _contextEntries.Clear();
                _view.UpdateAimActionInfo();
                _actionScroll = Vector2.zero;
                var target = _view.aimedThing;
                if (_view.aimingAtRoof)
                {
                    if (!_view.map.roofGrid.Roofed(_view.aimedCell))
                        _contextTitle = "Rim3D_ChooseRoof".Translate().ToString();
                    _view.AddContextAction(ContextAction.RemoveRoof, "Rim3D_RemoveRoof".Translate().ToString());
                    _view.AddContextAction(ContextAction.RoofFlat, "Rim3D_RoofFlat".Translate().ToString());
                    _view.AddContextAction(ContextAction.RoofProcedural, "Rim3D_RoofProcedural".Translate().ToString());
                }
                else
                {
                    _view.AddNativeContextActions();
                    _view.AddObjectContextActions();
                    if (!_view.controller.IsResting && target != null)
                    {
                        _view.AddContextAction(ContextAction.Reinstall, "Rim3D_MoveFurniture".Translate().ToString());
                        _view.AddContextAction(ContextAction.ClaimBuilding, "Rim3D_ClaimBuilding".Translate().ToString());
                        _view.AddContextAction(ContextAction.StorageSettings, "Rim3D_ZoneSettings".Translate(target.LabelCap).ToString());
                        if (target.def.IsSmoothable) _view.AddContextAction(ContextAction.SmoothWall, "Rim3D_SmoothWall".Translate().ToString());
                        if (target is Blueprint || target is Frame)
                        {
                            _view.AddContextAction(ContextAction.Build, "Rim3D_Build".Translate().ToString());
                            if (!(target is Blueprint_Install))
                            {
                                _view.AddContextAction(ContextAction.Interact, "Rim3D_DeliverResources".Translate().ToString());
                            }
                            if (target.Faction == _view.contextPawn.Faction) _view.AddContextAction(ContextAction.CancelPlan, "Rim3D_CancelPlan".Translate().ToString());
                        }
                        else if (target is Building_WorkTable)
                        {
                            _view.AddContextAction(ContextAction.Interact, "Rim3D_WorkAtTable".Translate().ToString());
                            if (target.Faction == Faction.OfPlayer) _view.AddContextAction(ContextAction.Bills, "Rim3D_Bills".Translate().ToString());
                        }
                        else if (MortarTargeting.IsMortar(target)) _view.AddContextAction(ContextAction.Interact, "Rim3D_MortarUse".Translate().ToString());
                        else if (target is Building_ResearchBench) _view.AddContextAction(ContextAction.Interact, "Rim3D_Research".Translate().ToString());
                        else if (_view.HasCarryPlacement(target)) _view.AddContextAction(ContextAction.Interact, _view.CarryPlacementLabel(target));
                        else if (CarryActions.StorageSlots(target) != null) _view.AddContextAction(ContextAction.Interact, "Rim3D_OpenStorageInventory".Translate().ToString());
                        else if (target is Building_Bed) _view.AddContextAction(ContextAction.Interact, "Rim3D_Sleep".Translate().ToString());
                        else if (target is Building_NutrientPasteDispenser) _view.AddContextAction(ContextAction.Interact, "Rim3D_TakePaste".Translate().ToString());
                        else if (target is Plant plant)
                        {
                            _view.AddContextAction(ContextAction.Cut, plant.def.plant.IsTree ? "Rim3D_CutTree".Translate().ToString() : "Rim3D_CutPlant".Translate().ToString());
                            if (plant.HarvestableNow) _view.AddContextAction(ContextAction.Harvest, "Rim3D_Harvest".Translate().ToString());
                        }
                        else if (target.def.mineable) _view.AddContextAction(ContextAction.Mine, "Rim3D_Mine".Translate().ToString());
                        else if (target.def.category == ThingCategory.Item)
                        {
                            _view.AddContextAction(ContextAction.Interact, "Rim3D_TakeStack".Translate(target.stackCount).ToString());
                            if (target.stackCount > 1) _view.AddContextAction(ContextAction.TakeCount, "Rim3D_ChooseCount".Translate().ToString());
                            if (target.def.IsWeapon) _view.AddContextAction(ContextAction.Equip, "Rim3D_EquipWeapon".Translate().ToString());
                        }
                        if (target.def.category == ThingCategory.Building && !target.def.mineable &&
                            !(target is Blueprint) && !(target is Frame))
                        {
                            if (target.Faction == _view.contextPawn.Faction && target.def.useHitPoints && target.HitPoints < target.MaxHitPoints)
                                _view.AddContextAction(ContextAction.Repair, "Rim3D_Repair".Translate().ToString());
                            if (target.Faction == _view.contextPawn.Faction || target.def.building?.alwaysDeconstructible == true)
                                _view.AddContextAction(ContextAction.Deconstruct, "Rim3D_Deconstruct".Translate().ToString());
                        }
                        if (target.def.ingestible != null) _view.AddContextAction(ContextAction.Ingest, IngestLabel(target));
                        if (target is Pawn arrestTarget) _view.AddContextAction(ContextAction.Arrest, _view.ArrestLabel(arrestTarget));
                        if (target is Pawn patient) _view.AddContextAction(ContextAction.Tend, _view.TendLabel(patient));
                        if (target is Pawn || target.HostileTo(_view.contextPawn) || _view.contextPawn.Drafted)
                            _view.AddContextAction(ContextAction.Melee, "Rim3D_Melee".Translate().ToString());
                    }
                    _view.AddContextAction(ContextAction.SelfTend, _view.TendLabel(_view.contextPawn));
                    if (!_view.controller.IsResting && _view.contextPawn.carryTracker?.CarriedThing != null)
                    {
                        _view.AddContextAction(ContextAction.Store, "Rim3D_StoreHeld".Translate().ToString());
                        _view.AddContextAction(ContextAction.Drop, "Rim3D_DropHeld".Translate().ToString());
                    }
                }
                AddExtensionActions();
                if (_contextEntries.Count == 0) return;
                var oldFont = Text.Font;
                Text.Font = GameFont.Small;
                _contextMenuWidth = Mathf.Max(160, Text.CalcSize(_contextTitle).x + 12);
                _contextMenuHeight = 0;
                foreach (var entry in _contextEntries)
                {
                    _contextMenuWidth = Mathf.Max(_contextMenuWidth, Text.CalcSize(entry.Label).x + 24);
                    _contextMenuHeight += entry.Option.RequiredHeight - 1;
                }
                Text.Font = oldFont;
                _contextMenuHeight += 1;
                _view.constructionMenu = _view.statusMenu = false;
                _view.actionMenu = true;
                _view.contextOpenedFrame = Time.frameCount;
                _view.ReleaseMouse();
            }

            internal void DismissActionMenu()
            {
                _view.CloseFPVMenu();
                _view.ResetCarryInput();
                _view.contextClosedFrame = Time.frameCount;
            }

            internal void DrawActionMenu(Rect viewport)
            {
                const float titleHeight = 24;
                float width = Mathf.Min(_contextMenuWidth, viewport.width - 16);
                float listHeight = Mathf.Min(_contextMenuHeight, Mathf.Max(1, viewport.height - titleHeight - 16));
                bool scrolling = _contextMenuHeight > listHeight;
                if (scrolling) width = Mathf.Min(width + 18, viewport.width - 16);
                float height = listHeight + titleHeight;
                _view.contextPanel = new Rect(
                    Mathf.Clamp(viewport.center.x + 18, viewport.x + 8, viewport.xMax - width - 8),
                    Mathf.Clamp(viewport.center.y - titleHeight, viewport.y + 8, viewport.yMax - height - 8), width, height);
                var current = Event.current;
                if (current.type == EventType.MouseDown && Time.frameCount != _view.contextOpenedFrame &&
                    (current.button == 1 || !_view.contextPanel.Contains(current.mousePosition)))
                {
                    _view.DismissActionMenu();
                    current.Use();
                    return;
                }
                var oldColor = GUI.color;
                var oldFont = Text.Font;
                var oldAnchor = Text.Anchor;
                bool oldWrap = Text.WordWrap;
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;
                Text.WordWrap = false;
                GUI.color = new Color(.75f, .75f, .75f);
                var title = new Rect(_view.contextPanel.x + 4, _view.contextPanel.y, width - 8, titleHeight);
                Widgets.Label(title, _contextTitle);
                TooltipHandler.TipRegion(title, _contextTitle);
                GUI.color = Color.white;
                var list = new Rect(_view.contextPanel.x, _view.contextPanel.y + titleHeight, width, listHeight);
                var content = new Rect(0, 0, width - (scrolling ? 18 : 0), _contextMenuHeight);
                if (scrolling) Widgets.BeginScrollView(list, ref _actionScroll, content);
                else GUI.BeginGroup(list);
                int selected = -1;
                float y = 0;
                for (int i = 0; i < _contextEntries.Count; i++)
                {
                    var option = _contextEntries[i].Option;
                    var row = new Rect(0, y, content.width, option.RequiredHeight);
                    y += option.RequiredHeight - 1;
                    if (row.yMax < _actionScroll.y || row.y > _actionScroll.y + listHeight) continue;
                    if (option.DoGUI(row, false, null)) selected = i;
                }
                if (scrolling) Widgets.EndScrollView();
                else GUI.EndGroup();
                GUI.color = oldColor;
                Text.Font = oldFont;
                Text.Anchor = oldAnchor;
                Text.WordWrap = oldWrap;
                if (selected >= 0 && _contextEntries[selected].Action == ContextAction.Extension)
                    RunExtensionAction(_contextEntries[selected]);
                else if (selected >= 0 && _contextEntries[selected].Action != ContextAction.Native)
                    _view.RunContextAction(_contextEntries[selected].Action);
            }

        }

        readonly ViewTargeting _viewTargeting;

        sealed partial class ViewTargeting
        {
            readonly MapView _view;

            internal ViewTargeting(MapView view)
            {
                _view = view;
            }

            readonly HashSet<IntVec3> _aimCells = new HashSet<IntVec3>();

            IntVec3 _cycleAimCell = IntVec3.Invalid;

            Thing _cycleAimTarget;

            internal void PickRecreationTarget()
            {
                _view.PickFPVTarget();
                if (_view.aimedThing != null || _view.aimingAtRoof || _view.buildTool != null) return;
                var cell = _view.aimedCell;
                _view.PickFPVTarget(distantRecreation: true);
                if (RecreationGiver(_view.aimedThing) == null)
                { _view.aimedThing = null; _view.aimedCell = cell; _view.aimingAtRoof = false; _view.roofAimFailure = null; _view.ResetAimCycle(); }
            }

            internal void PickFPVTarget(bool precise = false, bool distantRecreation = false)
            {
                precise |= _view.buildTool?.PlacingDef is TerrainDef || IsConduit(_view.buildTool?.PlacingDef);
                _view.aimedThing = null;
                _view.aimedCell = IntVec3.Invalid;
                _view.aimingAtRoof = false;
                _view.roofAimFailure = null;
                var ray = new Ray(_view.CameraMapPosition, _view.camera.transform.forward);
                float limit = distantRecreation ? _view.map.Size.LengthHorizontal : _view.ControlledPawn.Drafted ? 55 : 12;
                float roofDistance = float.PositiveInfinity;
                IntVec3 roofCell = IntVec3.Invalid;
                // Intersect the rendered ceiling, even when this tile has no roof yet.
                // Objects closer to the camera still take precedence.
                if (!precise && _view.buildTool == null && ray.direction.y > .01f && ray.origin.y < 2f)
                {
                    roofDistance = (2f - ray.origin.y) / ray.direction.y;
                    if (roofDistance <= limit)
                    {
                        var point = ray.GetPoint(roofDistance);
                        roofCell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.z));
                        if (roofCell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(roofCell) && _view.DrawSpatialRoof(roofCell)) limit = roofDistance;
                        else roofCell = IntVec3.Invalid;
                    }
                }
                if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var groundDistance) && groundDistance <= limit &&
                    (_view._spatialProvider == null || ray.GetPoint(groundDistance).ToIntVec3().InBounds(_view.map) &&
                        _view.DrawSpatialFloor(ray.GetPoint(groundDistance).ToIntVec3())))
                {
                    _view.aimedCell = ray.GetPoint(groundDistance).ToIntVec3();
                    limit = groundDistance;
                    if (_view.buildTool?.PlacingDef is TerrainDef || IsConduit(_view.buildTool?.PlacingDef))
                    {
                        if (!_view.aimedCell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(_view.aimedCell))
                            _view.aimedCell = IntVec3.Invalid;
                        return;
                    }
                }
                _view.aimCandidates.Clear();
                _aimCells.Clear();
                // Traverse only cells near the sight line. No Physics colliders or
                // full-map scan is needed for the independently rendered preview.
                for (float distanceAlongRay = 0; distanceAlongRay <= limit; distanceAlongRay += .4f)
                {
                    var cell = ray.GetPoint(distanceAlongRay).ToIntVec3();
                    for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
                    {
                        var nearby = cell + new IntVec3(x, 0, z);
                        if (!nearby.InBounds(_view.map) || !_aimCells.Add(nearby) || _view.map.fogGrid.IsFogged(nearby)) continue;
                        foreach (var thing in nearby.GetThingList(_view.map)) _view.aimCandidates.Add(thing);
                    }
                }
                float closest = limit;
                foreach (var thing in _view.aimCandidates)
                {
                    if (!thing.Spawned || thing == _view.ControlledPawn ||
                        _view._viewCamera.HasFrame && _view._viewCamera.Frame.AimFromActor && API.RimVisionControl.TryGetContext(_view.ControlledPawn, out var native) && thing == native.Carrier || thing.def.category == ThingCategory.Mote || !_view.PickSpatialThing(thing)) continue;
                    if (IsConduit(thing.def) && !_view.PowerHoverVisible(thing)) continue;
                    if (!(thing is Pawn) && !(thing is Blueprint) && !(thing is Frame) &&
                        thing.def.category != ThingCategory.Item && thing.def.category != ThingCategory.Plant &&
                        thing.def.category != ThingCategory.Building) continue;
                    if (thing is Pawn pawnTarget)
                    {
                        var pawnHit = _view.RaycastPawnSprite(pawnTarget, ray, false);
                        if (pawnHit.HasValue && pawnHit.Value <= closest)
                        { closest = pawnHit.Value; _view.aimedThing = thing; _view.aimedCell = precise ? ray.GetPoint(closest).ToIntVec3() : thing.Position; }
                        continue;
                    }
                    if (TryRaycastThing(thing, ray, out var hit) && hit < closest)
                    {
                        closest = hit; _view.aimedThing = thing; _view.aimedCell = precise ? ray.GetPoint(hit + .001f).ToIntVec3() : thing.Position;
                        if (!precise && (thing is Building_Storage || thing is Blueprint || thing is Frame))
                        {
                            var hitCell = ray.GetPoint(hit + .001f).ToIntVec3();
                            var occupiedRect = thing.OccupiedRect();
                            if ((thing is Blueprint || thing is Frame) && groundDistance > 0 && groundDistance <= limit)
                            {
                                var groundCell = ray.GetPoint(groundDistance).ToIntVec3();
                                if (occupiedRect.Contains(groundCell)) hitCell = groundCell;
                            }
                            if (occupiedRect.Contains(hitCell)) _view.aimedCell = hitCell;
                        }
                    }
                }
                // Vegetation must not hide other interaction targets in its cell.
                // Explicit C selection is applied afterwards by RefreshAimCycle.
                if (!precise && _view.aimedThing is Plant plant)
                {
                    Thing preferredTarget = null;
                    int preferredPriority = 0;
                    foreach (var thing in plant.Position.GetThingList(_view.map))
                    {
                        if (!_view.CanCycleAimTarget(thing) || thing.def.category == ThingCategory.Plant) continue;
                        int priority = thing is Pawn ? 4 :
                            thing.def.category == ThingCategory.Building || thing is Blueprint || thing is Frame ? 3 :
                            thing.def.IsWeapon ? 2 : 1;
                        if (priority <= preferredPriority) continue;
                        preferredTarget = thing;
                        preferredPriority = priority;
                    }
                    if (preferredTarget != null) { _view.aimedThing = preferredTarget; _view.aimedCell = preferredTarget.Position; }
                }
                if (!_view.aimedCell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(_view.aimedCell)) _view.aimedCell = IntVec3.Invalid;
                // Looking up at a wall targets the roof above the actual hit tile.
                // A wall face otherwise hides the ceiling plane behind its bounds.
                if (!precise && _view.buildTool == null && ray.direction.y > .01f && _view.aimedThing is Building roofHolder &&
                    roofHolder.def.holdsRoof && !roofHolder.def.mineable && roofHolder.Faction == Faction.OfPlayer)
                {
                    var point = ray.GetPoint(closest + .001f);
                    roofCell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.z));
                    if (!roofCell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(roofCell)) roofCell = IntVec3.Invalid;
                    roofDistance = closest;
                }
                if (roofCell.IsValid && closest >= roofDistance)
                {
                    _view.aimedThing = null;
                    _view.aimedCell = roofCell;
                    _view.aimingAtRoof = true;
                    _view.roofAimFailure = _view.RoofPlacementFailure(roofCell);
                    _view.ResetAimCycle();
                    return;
                }
                if (!precise) _view.RefreshAimCycle();
            }

            internal bool CanCycleAimTarget(Thing thing) => thing != null && thing.Spawned && !thing.Destroyed &&
                thing.Map == _view.map && thing != _view.ControlledPawn &&
                (thing is Pawn || thing is Blueprint || thing is Frame ||
                 thing.def.category == ThingCategory.Item || thing.def.category == ThingCategory.Plant ||
                 thing.def.category == ThingCategory.Building) && !(thing is Building_Door door && door.Open);

            internal void ResetAimCycle()
            {
                _cycleAimCell = IntVec3.Invalid;
                _cycleAimTarget = null;
                _view.cellAimTargets.Clear();
            }

            internal void RefreshAimCycle()
            {
                if (!_view.aimedCell.IsValid || _view.buildTool != null) { _view.ResetAimCycle(); return; }
                if (_cycleAimCell != _view.aimedCell)
                {
                    _cycleAimCell = _view.aimedCell;
                    _cycleAimTarget = null;
                }
                _view.cellAimTargets.Clear();
                var targetRect = _view.aimedThing is Building || _view.aimedThing is Blueprint || _view.aimedThing is Frame
                    ? _view.aimedThing.OccupiedRect() : GenAdj.OccupiedRect(_cycleAimCell, Rot4.North, new IntVec2(1, 1));
                foreach (var cell in targetRect)
                {
                    if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell)) continue;
                    foreach (var thing in cell.GetThingList(_view.map))
                        if (_view.CanCycleAimTarget(thing) && !_view.cellAimTargets.Contains(thing))
                            _view.cellAimTargets.Add(thing);
                }
                // Keep the chosen object through periodic and action-triggered picks.
                // Returning to another cell restores the normal nearest-hit target.
                if (_cycleAimTarget != null && _view.cellAimTargets.Contains(_cycleAimTarget))
                {
                    _view.aimedThing = _cycleAimTarget;
                    if (!_cycleAimTarget.OccupiedRect().Contains(_view.aimedCell))
                        _view.aimedCell = _cycleAimTarget.Position;
                }
                else _cycleAimTarget = null;
            }

            internal void CycleFPVTarget()
            {
                if (_view.cellAimTargets.Count < 2) return;
                int index = _view.cellAimTargets.IndexOf(_view.aimedThing);
                _cycleAimTarget = _view.cellAimTargets[(index + 1) % _view.cellAimTargets.Count];
                _view.aimedThing = _cycleAimTarget;
                if (!_cycleAimTarget.OccupiedRect().Contains(_view.aimedCell))
                    _view.aimedCell = _cycleAimTarget.Position;
            }

            internal IntVec3 FPVPlacementCell()
            {
                if (_view.buildTool == null || _view.aimedThing == null ||
                    (!IsWallTorch(_view.buildTool.PlacingDef) && !IsWallLamp(_view.buildTool.PlacingDef))) return _view.aimedCell;
                var wallDef = (_view.aimedThing.def.entityDefToBuild ?? _view.aimedThing.def) as ThingDef;
                if (wallDef == null || wallDef.Fillage != FillCategory.Full ||
                    wallDef.building == null || !wallDef.building.supportsWallAttachments) return _view.aimedCell;
                var center = _view.aimedThing.TrueCenter();
                center.y = 1;
                var size = _view.aimedThing.RotatedSize;
                var bounds = new Bounds(center, new Vector3(size.x, 2, size.z));
                var ray = new Ray(_view.CameraMapPosition, _view.camera.transform.forward);
                if (!bounds.IntersectRay(ray, out var distance) || distance > 12) return IntVec3.Invalid;
                var hit = ray.GetPoint(distance);
                float west = Mathf.Abs(hit.x - bounds.min.x), east = Mathf.Abs(hit.x - bounds.max.x);
                float south = Mathf.Abs(hit.z - bounds.min.z), north = Mathf.Abs(hit.z - bounds.max.z);
                float nearest = Mathf.Min(Mathf.Min(west, east), Mathf.Min(south, north));
                if (nearest > .02f) return IntVec3.Invalid;
                var outward = nearest == west ? IntVec3.West : nearest == east ? IntVec3.East :
                    nearest == south ? IntVec3.South : IntVec3.North;
                var normal = new Vector3(outward.x, 0, outward.z);
                var cell = (hit - normal * .02f).ToIntVec3() + outward;
                if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell)) return IntVec3.Invalid;
                _view.buildRotation = outward == IntVec3.West ? Rot4.East : outward == IntVec3.East ? Rot4.West :
                    outward == IntVec3.South ? Rot4.North : Rot4.South;
                _view.buildTool.SetRotation(_view.buildRotation);
                return cell;
            }

        }

        readonly ConstructionActions _constructionActions;

        sealed partial class ConstructionActions
        {
            readonly MapView _view;

            internal ConstructionActions(MapView view)
            {
                _view = view;
            }

            internal void StartFPVBillWork(Building_WorkTable table)
            {
                if (table == null || !table.Spawned || table.Map != _view.map) return;
                if (table.BillStack.Count == 0)
                {
                    if (!Rim3DMultiplayer.Simulating) _view.ChooseManualRecipe(table);
                    else _view.SayAction("Rim3D_AddRecipeHint".Translate().ToString());
                    return;
                }
                if (_view.SyncAction(Rim3DAction.BillWork, table)) return;
                if (_view.ControlledPawn.Drafted) _view.controller.SetCombat(false);
                string failure = _view.ManualActionFailure(table, "Bill");
                if (failure != null) { _view.SayAction(failure); return; }
                var carried = _view.ControlledPawn.carryTracker?.CarriedThing;
                Thing supplied = null;
                int suppliedCount = carried?.stackCount ?? 0;
                if (carried != null)
                {
                    bool ingredient = false;
                    foreach (var bill in table.BillStack)
                        if (_view.RecipeFailure(bill.recipe) == null && bill.IsFixedOrAllowedIngredient(carried))
                        { ingredient = true; break; }
                    if (ingredient)
                    {
                        // Native bill searches only spawned ingredients. Put the
                        // supplied stack within reach before asking for a stock job.
                        _view.controller.ReturnToManual();
                        if (!_view.ControlledPawn.carryTracker.TryDropCarriedThing(_view.ControlledPawn.Position,
                            ThingPlaceMode.Near, out supplied))
                        { _view.SayAction("Rim3D_NoIngredientSpace".Translate().ToString()); return; }
                        if (supplied.TryGetComp<CompForbiddable>() != null) supplied.SetForbidden(false);
                        _view.MarkNeighbors(supplied.Position);
                    }
                    else if (!_view.StoreFPVCarry()) return;
                }
                var job = _view.WorkJob(table, "Bill");
                if (job == null)
                { _view.RestoreSuppliedCarry(supplied, suppliedCount); _view.SayAction(_view.workFailure); return; }
                if (!_view.controller.OrderAction(job))
                { _view.RestoreSuppliedCarry(supplied, suppliedCount); _view.SayAction(_view.controller.Status); return; }
                _view.constructionDef = null;
                _view.billWindow?.Close(false);
                _view.SayAction("Rim3D_WorkingHint".TranslateBindings().ToString());
            }

            internal void RestoreSuppliedCarry(Thing supplied, int count)
            {
                if (supplied == null || supplied.Destroyed || !supplied.Spawned ||
                    _view.ControlledPawn.carryTracker.CarriedThing != null) return;
                var cell = supplied.Position;
                var portion = supplied.SplitOff(Mathf.Min(count, supplied.stackCount));
                if (!_view.ControlledPawn.carryTracker.TryStartCarry(portion))
                {
                    GenPlace.TryPlaceThing(portion, cell, _view.map, ThingPlaceMode.Near);
                    _view.SayFloatingItem("Rim3D_IngredientDropped".Translate(portion.LabelCap).ToString());
                }
                _view.MarkNeighbors(cell);
                _view.map.resourceCounter.UpdateResourceCounts();
            }

            IntVec3 _buildDragStart = IntVec3.Invalid;

            IntVec3 _buildDragEnd = IntVec3.Invalid;

            IntVec3 _buildPreviewStart = IntVec3.Invalid;

            IntVec3 _buildPreviewEnd = IntVec3.Invalid;

            readonly List<IntVec3> _buildDragCells = new List<IntVec3>();

            bool _roofRemovalHeld;

            IntVec3 _roofRemovalCell = IntVec3.Invalid;

            float _roofRemovalStarted;

            readonly List<IntVec3> _roofBuildCells = new List<IntVec3>();

            readonly HashSet<IntVec3> _roofBuildCellSet = new HashSet<IntVec3>();

            Material _placementMaterial;

            Material _blueprintOutline;

            Material _frameOutline;

            BuildableDef _placementPreviewDef;

            OptionalModelLibrary.Model _placementPreviewModel;

            int _placementPreviewLinks = -1;

            internal string RoofPlacementFailure(IntVec3 cell)
            {
                if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell)) return "Rim3D_CellUnavailable".Translate().ToString();
                if (_view.controller.IsResting || !PawnControl.Eligible(_view.ControlledPawn)) return "Rim3D_CannotBuildNow".Translate().ToString();
                if (_view.map.roofGrid.Roofed(cell)) return "";
                if (!ReachabilityImmediate.CanReachImmediate(_view.ControlledPawn, cell, PathEndMode.Touch))
                    return "";
                // Enclosure is independent of roof coverage: an unfinished room is
                // valid, but open ground beside a wall or column is not.
                if (_view.RoofBuildingRoom(cell) == null) return "";
                if (RoofUtility.FirstBlockingThing(cell, _view.map) != null) return "Rim3D_RoofPlantBlocked".Translate().ToString();
                return null;
            }

            internal Room RoofBuildingRoom(IntVec3 cell)
            {
                var room = cell.GetRoom(_view.map);
                if (room != null && room.ProperRoom) return room;
                // Walls have no normal room region. Include the wall's own tile
                // when it borders an enclosed room, including diagonal corners.
                var wall = cell.GetEdifice(_view.map);
                if (wall == null || !wall.def.holdsRoof || wall.def.mineable || wall.Faction != Faction.OfPlayer) return null;
                foreach (var offset in GenAdj.AdjacentCells)
                {
                    var neighbor = cell + offset;
                    if (!neighbor.InBounds(_view.map)) continue;
                    room = neighbor.GetRoom(_view.map);
                    if (room != null && room.ProperRoom) return room;
                }
                return null;
            }

            internal void BuildFPVRoof(IntVec3 cell, bool procedural = false)
            {
                if (Rim3DMultiplayer.LocalInput)
                {
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                    { Action = Rim3DAction.BuildRoof, Cell = cell, Count = procedural ? 1 : 0 });
                    return;
                }
                string failure = _view.RoofPlacementFailure(cell);
                if (failure != null) { if (failure.Length > 0) _view.SayAction(failure); return; }
                var room = _view.RoofBuildingRoom(cell);
                if (room == null) return;
                _view.CollectRoofRoomCells(room);
                int built = 0;
                bool progress;
                do
                {
                    progress = false;
                    for (int i = 0; i < _roofBuildCells.Count; i++)
                    {
                        var tile = _roofBuildCells[i];
                        if (_view.map.roofGrid.Roofed(tile) || RoofUtility.FirstBlockingThing(tile, _view.map) != null ||
                            !RoofCollapseUtility.WithinRangeOfRoofHolder(tile, _view.map, false)) continue;
                        _view.map.areaManager.NoRoof[tile] = false;
                        _view.map.areaManager.BuildRoof[tile] = false;
                        _view.map.roofGrid.SetRoof(tile, RoofDefOf.RoofConstructed);
                        if (!_view.simulationOnly) _view.MarkNeighbors(tile);
                        built++;
                        progress = true;
                    }
                } while (progress);
                if (built > 0) _view.map.GetComponent<RoofStyles>().Set(_roofBuildCells, procedural);
                _view.CheckRoofSupportAfterEdit();
                _roofBuildCells.Clear();
                _roofBuildCellSet.Clear();
                if (built > 0) _view.SayAction("Rim3D_RoofBuilt".Translate().ToString());
            }

            internal void CollectRoofRoomCells(Room room)
            {
                _roofBuildCells.Clear();
                _roofBuildCellSet.Clear();
                foreach (var inside in room.Cells)
                {
                    _view.AddRoofBuildCell(inside);
                    foreach (var offset in GenAdj.AdjacentCells)
                    {
                        var border = inside + offset;
                        if (!border.InBounds(_view.map)) continue;
                        var wall = border.GetEdifice(_view.map);
                        if (wall != null && wall.def.holdsRoof && !wall.def.mineable && wall.Faction == Faction.OfPlayer)
                            _view.AddRoofBuildCell(border);
                    }
                }
                // Stable order for synchronized commands. Build from supported
                // edges inward; retry only cells whose support is not ready yet.
                _roofBuildCells.Sort((a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
            }

            internal void AddRoofBuildCell(IntVec3 cell)
            {
                if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell) || !_roofBuildCellSet.Add(cell)) return;
                _roofBuildCells.Add(cell);
            }

            internal void CheckRoofSupportAfterEdit()
            {
                // SetRoof updates visuals/rooms, but does not trigger collapse.
                // Check only the edited area and its edge, using the game's buffer
                // so debris, damage and collapse timing remain native.
                int editedCount = _roofBuildCells.Count;
                for (int i = 0; i < editedCount; i++)
                    foreach (var offset in GenAdj.AdjacentCells) _view.AddRoofBuildCell(_roofBuildCells[i] + offset);
                RoofCollapseCellsFinder.CheckCollapseFlyingRoofs(_roofBuildCells, _view.map, false, false);
                bool marked = false;
                foreach (var tile in _roofBuildCells)
                {
                    var roof = _view.map.roofGrid.RoofAt(tile);
                    if (roof != null && roof.canCollapse && !_view.map.roofCollapseBuffer.IsMarkedToCollapse(tile) &&
                        !RoofCollapseUtility.WithinRangeOfRoofHolder(tile, _view.map, false))
                    {
                        _view.map.roofCollapseBuffer.MarkToCollapse(tile);
                        marked = true;
                    }
                }
                if (marked) RoofCollapseCellsFinder.CheckCollapseFlyingRoofs(_roofBuildCells, _view.map, false, false);
            }

            internal void ResetRoofRemovalInput()
            {
                _roofRemovalHeld = false;
                _roofRemovalCell = IntVec3.Invalid;
            }

            internal string RoofRemovalFailure(IntVec3 cell)
            {
                if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell) || _view.controller.IsResting ||
                    !PawnControl.Eligible(_view.ControlledPawn) ||
                    !ReachabilityImmediate.CanReachImmediate(_view.ControlledPawn, cell, PathEndMode.Touch)) return "";
                var roof = _view.map.roofGrid.RoofAt(cell);
                if (roof == null) return "";
                return roof.isThickRoof ? "Rim3D_ThickRoof".Translate().ToString() : null;
            }

            internal bool UpdateRoofRemovalInput(bool moving)
            {
                if (!ViewBindings.Held(ViewAction.Interact) || !ViewBindings.Held(ViewAction.Sprint))
                { _view.ResetRoofRemovalInput(); return false; }
                if (ViewBindings.Pressed(ViewAction.Interact))
                {
                    _view.PickFPVTarget();
                    if (!_view.aimingAtRoof) return false;
                    _view.ResetCarryInput();
                    _roofRemovalHeld = true;
                    _roofRemovalCell = _view.aimedCell;
                    _roofRemovalStarted = Time.unscaledTime;
                }
                if (!_roofRemovalHeld) return false;
                // Releasing either key, moving, or changing the aimed tile cancels.
                // After completion another E press is required for the next tile.
                if (!_roofRemovalCell.IsValid) return true;
                _view.PickFPVTarget();
                if (moving || ViewBindings.Pressed(ViewAction.CancelAction) || !_view.aimingAtRoof || _view.aimedCell != _roofRemovalCell ||
                    _view.RoofRemovalFailure(_roofRemovalCell) != null)
                { _roofRemovalCell = IntVec3.Invalid; return true; }
                if (Time.unscaledTime - _roofRemovalStarted >= RoofRemovalHoldSeconds)
                {
                    _view.RemoveFPVRoof(_roofRemovalCell);
                    _roofRemovalCell = IntVec3.Invalid;
                    _view.PickFPVTarget();
                    _view.UpdateAimActionInfo();
                }
                return true;
            }

            internal void RemoveFPVRoof(IntVec3 cell)
            {
                if (Rim3DMultiplayer.LocalInput)
                {
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                    { Action = Rim3DAction.RemoveRoof, Cell = cell });
                    return;
                }
                string failure = _view.RoofRemovalFailure(cell);
                if (failure != null) { if (failure.Length > 0) _view.SayAction(failure); return; }
                var room = _view.RoofBuildingRoom(cell);
                if (room != null) _view.CollectRoofRoomCells(room);
                else
                {
                    // If the walls are already gone, remove only the connected
                    // removable roof, rather than traversing the outdoor room.
                    _roofBuildCells.Clear();
                    _roofBuildCellSet.Clear();
                    _view.AddRoofBuildCell(cell);
                    for (int i = 0; i < _roofBuildCells.Count; i++)
                        foreach (var offset in GenAdj.CardinalDirections)
                        {
                            var neighbor = _roofBuildCells[i] + offset;
                            if (!neighbor.InBounds(_view.map)) continue;
                            var roof = _view.map.roofGrid.RoofAt(neighbor);
                            if (roof != null && !roof.isThickRoof) _view.AddRoofBuildCell(neighbor);
                        }
                    _roofBuildCells.Sort((a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
                }
                foreach (var tile in _roofBuildCells)
                {
                    var roof = _view.map.roofGrid.RoofAt(tile);
                    if (roof != null && roof.isThickRoof) continue;
                    // Clear orders across the room too, so missing tiles are not
                    // rebuilt after the batch removal.
                    _view.map.areaManager.BuildRoof[tile] = false;
                    _view.map.areaManager.NoRoof[tile] = true;
                    if (roof == null) continue;
                    _view.map.roofGrid.SetRoof(tile, null);
                    if (!_view.simulationOnly) _view.MarkNeighbors(tile);
                }
                _view.CheckRoofSupportAfterEdit();
                _roofBuildCells.Clear();
                _roofBuildCellSet.Clear();
            }

            internal void DesignateAndWork(Designator designator, string kind)
            {
                if (_view.SyncAction(Rim3DAction.Designate, kind: kind)) return;
                if (_view.aimedThing == null) return;
                if (kind == "Cut" || kind == "Harvest") { _view.WorkOnTarget(kind); return; }
                // Match the game's Orders category: cutting mature trees requires
                // an explicit order rather than the automatic clearing designator.
                designator.isOrder = true;
                var report = designator.CanDesignateThing(_view.aimedThing);
                if (report.Accepted) designator.DesignateThing(_view.aimedThing);
                _view.nextDesignationUpdate = 0;
                // An existing designation may make CanDesignateThing reject a duplicate;
                // the work giver still decides whether the actual job is available.
                if (!_view.WorkOnTarget(kind) && !report.Accepted && !report.Reason.NullOrEmpty()) _view.SayAction(report.Reason);
            }

            internal Job WorkJob(Thing target, string kind) => _view.ManualWorkJob(target, kind);

            internal void BeginConstruction(Thing target)
            {
                if (_view.SyncAction(Rim3DAction.Build, target)) return;
                if (target is Blueprint_Install install)
                {
                    _view.StartFPVInstall(install);
                    return;
                }
                string failure = _view.ManualActionFailure(target, "Build");
                if (failure != null) { _view.SayAction(failure); return; }
                if (!_view.CanTouchCarryTarget(target)) { _view.SayAction("Rim3D_MoveCloserToProject".Translate().ToString()); return; }
                if (_view.NeedsFloorRemoval(target))
                {
                    _view.constructionDef = null;
                    if (_view.ControlledPawn.Drafted) _view.controller.SetCombat(false);
                    var removalJob = _view.WorkJob(target, "Build");
                    _view.SayAction(removalJob == null ? _view.workFailure : _view.controller.OrderAction(removalJob)
                        ? "Rim3D_ActionStartedHint".TranslateBindings().ToString() : _view.controller.Status);
                    _view.actionMenu = false;
                    return;
                }
                if (target is Blueprint_Build blueprint && blueprint.TotalMaterialCost().Count == 0 &&
                    GenConstruct.FirstBlockingThing(target, _view.ControlledPawn) == null &&
                    blueprint.TryReplaceWithSolidThing(_view.ControlledPawn, out var solid, out _))
                { target = solid; _view.aimedThing = solid; _view.MarkNeighbors(solid.Position); }
                if (!(target is Frame frame) || !ProjectMaterialsReady(frame))
                { _view.SayAction("Rim3D_DeliverFirstHint".TranslateBindings().ToString()); return; }
                if (_view.ControlledPawn.Drafted) _view.controller.SetCombat(false);
                _view.constructionCell = target.Position;
                _view.constructionDef = target.def.entityDefToBuild;
                var job = _view.WorkJob(target, "Build");
                if (job == null || !_view.controller.OrderAction(job))
                {
                    _view.constructionDef = null;
                    _view.SayAction(job == null ? _view.workFailure : _view.controller.Status);
                }
                else _view.SayAction("Rim3D_BuildingHint".TranslateBindings().ToString());
                _view.actionMenu = false;
            }

            internal void ContinueConstruction()
            {
                if (Rim3DMultiplayer.LocalInput) return;
                if (_view.constructionDef == null || _view.controller.IsActing || !_view.constructionCell.InBounds(_view.map)) return;
                foreach (var thing in _view.constructionCell.GetThingList(_view.map))
                {
                    if ((thing is Blueprint || thing is Frame) && thing.def.entityDefToBuild == _view.constructionDef)
                    {
                        var job = _view.WorkJob(thing, "Build");
                        if (job != null && _view.controller.OrderAction(job)) return;
                        _view.constructionDef = null;
                        _view.SayAction(job == null ? _view.workFailure : _view.controller.Status);
                        return;
                    }
                }
                _view.constructionDef = null;
                _view.SayAction("Rim3D_BuildFinished".Translate().ToString());
            }

            internal void PlaceFPVReinstall(Thing target, IntVec3 cell, Rot4 rotation)
            {
                if (!CanReinstallFPV(target, _view.map) || Find.CurrentMap != _view.map ||
                    !PawnControl.Eligible(_view.ControlledPawn) || _view.controller.IsResting) return;
                var inner = target.GetInnerIfMinified();
                if (!inner.def.rotatable) rotation = Rot4.North;
                var tool = new FPVBuildDesignator(inner.def) { ReinstallTarget = target };
                tool.SetRotation(rotation);
                var report = tool.CanDesignateCell(cell);
                if (!report.Accepted) { _view.SayAction(report.Reason ?? "Rim3D_CannotBuildHere".Translate().ToString()); return; }
                var oldCell = target.Position;
                var oldRotation = target.Rotation;
                var oldInnerRotation = inner.Rotation;
                var minified = target as MinifiedThing;
                bool moved = false;
                try
                {
                    target.DeSpawn();
                    if (minified != null) minified.InnerThing = null;
                    GenSpawn.Spawn(inner, cell, _view.map, rotation);
                    moved = inner.Spawned && inner.Map == _view.map && inner.Position == cell;
                }
                catch (Exception exception)
                {
                    Log.Error("[RimVision] Furniture placement failed: " + exception);
                }
                finally
                {
                    if (!moved && !inner.Destroyed)
                    {
                        if (inner.Spawned) inner.DeSpawn();
                        inner.Rotation = oldInnerRotation;
                        if (minified != null) minified.InnerThing = inner;
                        if (!target.Spawned) GenSpawn.Spawn(target, oldCell, _view.map, oldRotation);
                    }
                    _view.MarkNeighbors(oldCell);
                    _view.MarkNeighbors(cell);
                }
                if (moved)
                {
                    InstallBlueprintUtility.CancelBlueprintsFor(inner);
                    if (minified != null) minified.Destroy();
                    _view.constructionDef = null;
                    _view.map.resourceCounter.UpdateResourceCounts();
                }
                _view.SayAction((moved ? "Rim3D_FurnitureMoved" : "Rim3D_CannotBuildHere").Translate().ToString());
            }

            internal void StartFPVInstall(Blueprint_Install blueprint)
            {
                if (blueprint == null || blueprint.Destroyed || !blueprint.Spawned || blueprint.Map != _view.map ||
                    blueprint.Faction != _view.ControlledPawn.Faction || !PawnControl.Eligible(_view.ControlledPawn) ||
                    _view.controller.IsResting) return;
                _view.constructionDef = null;
                if (_view.ControlledPawn.Drafted) _view.controller.SetCombat(false);
                foreach (var def in _view.ManualWorkers("RemoveFloor"))
                {
                    if (!(def.Worker is WorkGiver_Scanner worker) ||
                        def.workType != null && _view.ControlledPawn.WorkTypeIsDisabled(def.workType) ||
                        worker.MissingRequiredCapacity(_view.ControlledPawn) != null ||
                        !worker.HasJobOnThing(_view.ControlledPawn, blueprint, true)) continue;
                    var job = worker.JobOnThing(_view.ControlledPawn, blueprint, true);
                    if (job == null) continue;
                    job.workGiverDef = def;
                    _view.SayAction(_view.controller.OrderAction(job) ? "Rim3D_WorkingHint".TranslateBindings().ToString() : _view.controller.Status);
                    return;
                }
                _view.SayAction("Rim3D_BlueprintPlaced".TranslateBindings().ToString());
            }

            internal void PlaceFPVBlueprint()
            {
                var cell = _view.FPVPlacementCell();
                if (!cell.IsValid) { _view.SayAction("Rim3D_AimBuildRange".Translate().ToString()); return; }
                _view.PlaceFPVBlueprint(cell);
            }

            internal void ResetBuildDrag()
            {
                if (_buildDragStart.IsValid && _view.placementPreview != null) _view.placementPreview.SetActive(false);
                _buildDragStart = _buildDragEnd = IntVec3.Invalid;
                _buildDragCells.Clear();
            }

            internal void UpdateBuildDragInput()
            {
                if (!ViewBindings.MouseHeld(0) && !ViewBindings.MouseUp(0))
                { _view.ResetBuildDrag(); return; }
                _view.PickFPVTarget();
                var cell = _view.FPVPlacementCell();
                if (ViewBindings.MouseDown(0))
                {
                    if (!cell.IsValid) { _view.SayAction("Rim3D_AimBuildRange".Translate().ToString()); return; }
                    _buildDragStart = cell;
                }
                if (!_buildDragStart.IsValid) return;
                if (cell.IsValid && IsConduit(_view.buildTool.PlacingDef))
                {
                    if (Mathf.Abs(cell.x - _buildDragStart.x) >= Mathf.Abs(cell.z - _buildDragStart.z))
                        cell.z = _buildDragStart.z;
                    else cell.x = _buildDragStart.x;
                }
                if (cell.IsValid && cell != _buildDragEnd)
                {
                    _buildDragEnd = cell;
                    _buildDragCells.Clear();
                    var rect = CellRect.FromLimits(_buildDragStart, cell);
                    if (_view.buildTool.PlacingDef is TerrainDef || IsConduit(_view.buildTool.PlacingDef))
                    {
                        for (int z = rect.minZ; z <= rect.maxZ; z++)
                            for (int x = rect.minX; x <= rect.maxX; x++)
                                _buildDragCells.Add(new IntVec3(x, 0, z));
                    }
                    else
                    {
                        for (int x = rect.minX; x <= rect.maxX; x++)
                        {
                            _buildDragCells.Add(new IntVec3(x, 0, rect.minZ));
                            if (rect.maxZ != rect.minZ) _buildDragCells.Add(new IntVec3(x, 0, rect.maxZ));
                        }
                        for (int z = rect.minZ + 1; z < rect.maxZ; z++)
                        {
                            _buildDragCells.Add(new IntVec3(rect.minX, 0, z));
                            if (rect.maxX != rect.minX) _buildDragCells.Add(new IntVec3(rect.maxX, 0, z));
                        }
                    }
                }
                if (!ViewBindings.MouseUp(0)) return;
                if (cell.IsValid)
                {
                    bool placed = false;
                    foreach (var dragCell in _buildDragCells)
                        if (dragCell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(dragCell) && _view.buildTool.CanDesignateCell(dragCell).Accepted)
                        {
                            _view.PlaceFPVBlueprint(dragCell);
                            placed = true;
                        }
                    if (!placed) _view.SayAction(_view.buildTool.CanDesignateCell(cell).Reason ?? "Rim3D_CannotBuildHere".Translate().ToString());
                }
                _view.ResetBuildDrag();
            }

            internal void PlaceFPVBlueprint(IntVec3 cell)
            {
                if (_view.buildTool.ReinstallTarget != null)
                {
                    var reinstallReport = _view.buildTool.CanDesignateCell(cell);
                    if (!reinstallReport.Accepted) { _view.SayAction(reinstallReport.Reason ?? "Rim3D_CannotBuildHere".Translate().ToString()); return; }
                    var target = _view.buildTool.ReinstallTarget;
                    if (Rim3DMultiplayer.LocalInput)
                        Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                        { Action = Rim3DAction.Reinstall, Target = target, Cell = cell, Rotation = _view.buildRotation });
                    else _view.PlaceFPVReinstall(target, cell, _view.buildRotation);
                    _view.buildTool = null;
                    return;
                }
                if (Rim3DMultiplayer.LocalInput)
                {
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                    {
                        Action = Rim3DAction.Place, Cell = cell, BuildDef = _view.buildTool.PlacingDef,
                        Stuff = _view.buildTool.StuffDef, Rotation = _view.buildRotation
                    });
                    return;
                }
                var report = _view.buildTool.CanDesignateCell(cell);
                if (!report.Accepted) { _view.SayAction(report.Reason ?? "Rim3D_CannotBuildHere".Translate().ToString()); return; }
                _view.buildTool.DesignateSingleCell(cell);
                _view.MarkNeighbors(cell);
                _view.SayAction("Rim3D_BlueprintPlaced".TranslateBindings().ToString());
            }

            internal void CancelFPVPlan()
            {
                if (_view.SyncAction(Rim3DAction.CancelPlan)) return;
                Thing target = _view.aimedThing;
                if (!(target is Blueprint) && !(target is Frame) && _view.aimedCell.InBounds(_view.map))
                    foreach (var thing in _view.aimedCell.GetThingList(_view.map))
                        if (thing is Blueprint || thing is Frame) { target = thing; break; }
                if (target == null || !target.Spawned || target.Destroyed || (!(target is Blueprint) && !(target is Frame)))
                { _view.SayAction("Rim3D_AimBlueprint".Translate().ToString()); return; }
                if (target.Faction != _view.ControlledPawn.Faction)
                { _view.SayAction("Rim3D_CancelOwnBlueprints".Translate().ToString()); return; }
                var cell = target.Position;
                _view.constructionDef = null;
                if (_view.controller.IsActing) _view.controller.ReturnToManual();
                target.Destroy(DestroyMode.Cancel);
                _view.MarkNeighbors(cell);
                _view.aimedThing = null; _view.actionMenu = false;
                _view.SayAction("Rim3D_BuildCancelled".Translate().ToString());
            }

            internal float ConstructionHeight(BuildableDef def)
            {
                if (def is TerrainDef) return .06f;
                if (IsConduit(def)) return 0;
                if (def != null && def.defName == "FenceGate") return .6f;
                if (IsWallTorch(def)) return 1.6f;
                var model = _view.ConstructionModel(def);
                if (model != null) return Mathf.Max(.06f, model.Height);
                var thing = def as ThingDef;
                return thing != null && (thing.Fillage == FillCategory.Full || thing.mineable) ? 2 :
                    thing != null && thing.IsDoor ? 1.7f : .65f;
            }

            internal Material PlanMaterial(Color color)
            {
                var material = _view.NewMaterial(Texture2D.whiteTexture, color, true);
                material.SetFloat("_Unlit", 1);
                material.SetFloat("_FlatItem", 1);
                return material;
            }

            readonly Dictionary<ThingDef, Geometry> _constructionDoorOutlines = new Dictionary<ThingDef, Geometry>();

            internal Geometry ConstructionDoorOutline(ThingDef def)
            {
                if (_constructionDoorOutlines.TryGetValue(def, out var geometry)) return geometry;
                geometry = new Geometry();
                OutlineBox(geometry, Vector3.zero, new Vector3(def.Size.x, 1.7f, .22f), .018f);
                _constructionDoorOutlines.Add(def, geometry);
                return geometry;
            }

            Rot4 _placementPreviewRotation = Rot4.Invalid;

            ThingDef _placementPreviewStuff;

            readonly List<GameObject> _placementParts = new List<GameObject>();

            readonly Dictionary<Material, Material> _constructionMaterials = new Dictionary<Material, Material>();

            readonly Dictionary<Material, Material> _constructionWallMaterials = new Dictionary<Material, Material>();

            int _placementPreviewWallFaces = -1;

            internal int ConstructionWallFaces(IntVec3 cell)
            {
                int faces = 15;
                for (int i = 0; i < 4; i++)
                {
                    var offset = i == 0 ? IntVec3.South : i == 1 ? IntVec3.North : i == 2 ? IntVec3.West : IntVec3.East;
                    var neighbor = cell + offset;
                    if (!neighbor.InBounds(_view.map)) continue;
                    foreach (var thing in _view.map.thingGrid.ThingsListAt(neighbor))
                        if (IsConstructionWall(thing.def.entityDefToBuild ?? thing.def))
                        { faces &= ~(1 << i); break; }
                }
                return faces;
            }

            internal Graphic ConstructionGraphic(BuildableDef def, ThingDef stuff, ThingStyleDef style = null)
            {
                var graphic = style?.graphicData?.Graphic ?? (def as ThingDef)?.graphicData?.Graphic;
                if (graphic == null) return null;
                var color = def.MadeFromStuff && stuff != null ? stuff.stuffProps.color : graphic.color;
                return graphic.GetColoredVersion(graphic.Shader, color, graphic.colorTwo);
            }

            internal Material ConstructionMaterial(Material source, bool wall = false)
            {
                var cache = wall ? _constructionWallMaterials : _constructionMaterials;
                if (cache.TryGetValue(source, out var material)) return material;
                // Derived building materials already contain surface UV and texture settings.
                material = new Material(source.shader == _view.shader ? source : _view.ConvertMaterial(source, false)) { hideFlags = HideFlags.DontSave, renderQueue = 3000 };
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0);
                material.SetFloat("_Ghost", wall ? .14f : .35f);
                if (wall) material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
                material.SetInt("_StencilWriteMask", 0);
                material.SetFloat("_Unlit", .65f);
                _view.materials.Add(material);
                cache.Add(source, material);
                return material;
            }

            internal void DrawConstructionWorkSpot(Geometry geometry, BuildableDef def, IntVec3 cell, Rot4 rotation, Vector3 origin)
            {
                if (!(def is ThingDef building) || !building.hasInteractionCell) return;
                var workCell = ThingUtility.InteractionCellWhenAt(building, cell, rotation, _view.map);
                var point = workCell.ToVector3Shifted() - origin; point.y = .065f;
                const int segments = 16;
                const float radius = .17f;
                for (int i = 0; i < segments; i++)
                {
                    float a = i * Mathf.PI * 2 / segments, b = (i + 1) * Mathf.PI * 2 / segments;
                    geometry.Quad(point, point + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius,
                        point + new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * radius, point, Vector3.up,
                        Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                }
            }

            internal void ConstructionVisual(BuildableDef def, Graphic graphic, Rot4 rotation, Vector3 center,
                OptionalModelLibrary.Model model, int links, int wallFaces, ThingDef stuff, Func<Material, Geometry> geometryFor)
            {
                if (def is TerrainDef terrain)
                {
                    geometryFor(_view.ConstructionMaterial(terrain.DrawMatSingle)).FloorCard(center + Vector3.up * .008f, def.Size.x, def.Size.z);
                    return;
                }
                if (IsWallTorch(def) && model == null)
                {
                    if (_view.wallTorchWood == null)
                    {
                        _view.wallTorchWood = _view.NewMaterial(Texture2D.whiteTexture, new Color(.36f, .20f, .085f), true);
                        _view.wallTorchIron = _view.NewMaterial(Texture2D.whiteTexture, new Color(.18f, .20f, .22f), true);
                        _view.wallTorchWrap = _view.NewMaterial(Texture2D.whiteTexture, new Color(.55f, .39f, .20f), true);
                    }
                    var rotationTorch = Quaternion.Euler(0, rotation.AsAngle, 0);
                    geometryFor(_view.ConstructionMaterial(_view.wallTorchWood)).Append(WallTorchHandle, center, rotationTorch);
                    geometryFor(_view.ConstructionMaterial(_view.wallTorchIron)).Append(WallTorchMount, center, rotationTorch);
                    geometryFor(_view.ConstructionMaterial(_view.wallTorchWrap)).Append(WallTorchHead, center, rotationTorch);
                    return;
                }
                var image = graphic is Graphic_Linked linked ? linked.SubGraphic : graphic;
                var source = image?.MatAt(rotation);
                if (source == null || source.mainTexture == null || source.mainTexture == BaseContent.BadTex) return;
                if (IsConduit(def))
                {
                    if (graphic is Graphic_Linked conduitGraphic)
                        source = MaterialAtlasPool.SubMaterialFromAtlas(conduitGraphic.SubGraphic.MatSingle, (LinkDirections)links);
                    geometryFor(_view.ConstructionMaterial(source)).FloorCard(center + Vector3.up * .008f, 1, 1);
                    return;
                }
                var turn = Quaternion.Euler(0, rotation.AsAngle, 0);
                if (def is ThingDef doorDef && doorDef.IsDoor)
                {
                    float width = def.Size.x;
                    if (!_view.doorPanels.TryGetValue(source, out var panelMaterial))
                    {
                        panelMaterial = new Material(_view.BuildingSideMaterial(source)) { hideFlags = HideFlags.DontSave };
                        if (stuff != null && stuff.defName.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0)
                            _view.ApplyWoodSurface(panelMaterial);
                        _view.materials.Add(panelMaterial); _view.doorPanels.Add(source, panelMaterial);
                    }
                    var trim = new Geometry();
                    trim.Box(new Vector3(-width * .5f + .035f, 0, 0), new Vector3(.07f, 1.7f, .22f), new Rect(0, 0, 1, 1));
                    trim.Box(new Vector3(width * .5f - .035f, 0, 0), new Vector3(.07f, 1.7f, .22f), new Rect(0, 0, 1, 1));
                    trim.Box(new Vector3(0, 1.63f, 0), new Vector3(width, .07f, .22f), new Rect(0, 0, 1, 1));
                    geometryFor(_view.ConstructionMaterial(_view.BuildingSideMaterial(source))).Append(trim, center, turn);
                    var panel = new Geometry();
                    panel.Box(Vector3.zero, new Vector3(width - .14f, 1.63f, .12f), new Rect(0, 0, width, 1.63f));
                    geometryFor(_view.ConstructionMaterial(panelMaterial)).Append(panel, center, turn);
                    // Doors are vertical panels. Their native top-down sprite is never a ceiling cap.
                    return;
                }
                if (IsConstructionWall(def) && graphic is Graphic_Linked wallGraphic)
                {
                    var size = GenAdj.OccupiedRect(IntVec3.Zero, rotation, def.Size);
                    var surface = _view.ConstructionMaterial(_view.LinkedBuildingMaterial(wallGraphic.SubGraphic.MatSingle), true);
                    // One cap and only exposed sides; adjacent plans must not stack transparent faces.
                    geometryFor(surface).Box(center, new Vector3(size.Width - .012f, _view.ConstructionHeight(def), size.Height - .012f),
                        new Rect(0, 0, 1, 1), new Rect(0, 0, 1, 1), wallFaces, false);
                    return;
                }
                if (model != null)
                {
                    var origin = new Vector3(def.Size.x * .5f, -model.HeightOffset, def.Size.z * .5f);
                    foreach (var face in model.Faces)
                    {
                        if ((links & face.RequiredLinks) != face.RequiredLinks || (links & face.ForbiddenLinks) != 0) continue;
                        Material material;
                        var texture = face.Texture == 4 ? source : image.MatAt(new Rot4(face.Texture));
                        if (face.Solid)
                        {
                            var color = face.TintWithThing ? face.Color * graphic.color : face.Color; color.a = 1;
                            if (!_view.optionalSolidMaterials.TryGetValue(color, out var solid))
                            {
                                solid = _view.NewMaterial(Texture2D.whiteTexture, color, true);
                                solid.SetFloat("_Cull", 2);
                                _view.optionalSolidMaterials.Add(color, solid);
                            }
                            material = _view.ConstructionMaterial(solid);
                        }
                        else material = _view.ConstructionMaterial(texture);
                        var geometry = geometryFor(material);
                        int start = geometry.Vertices.Count;
                        for (int v = 0; v < face.Indices.Length; v++)
                        {
                            geometry.Vertices.Add(center + turn * (model.Vertices[face.Indices[v]] - origin));
                            geometry.Normals.Add(turn * face.Normal);
                            geometry.CardCenters.Add(Vector3.zero);
                            var uv = face.Uvs[v];
                            geometry.Uvs.Add(face.PixelUvs ? new Vector2(
                                Mathf.Clamp01(uv.x / Mathf.Max(1, texture.mainTexture.width * Mathf.Abs(texture.mainTextureScale.x))),
                                Mathf.Clamp01(1 - uv.y / Mathf.Max(1, texture.mainTexture.height * Mathf.Abs(texture.mainTextureScale.y)))) : uv);
                        }
                        for (int v = 1; v < face.Indices.Length - 1; v++)
                        { geometry.Indices.Add(start); geometry.Indices.Add(start + v); geometry.Indices.Add(start + v + 1); }
                    }
                    return;
                }
                float height = def is TerrainDef || def is ThingDef spot && IsGroundSpot(spot) ? 0 : _view.ConstructionHeight(def);
                if (height > 0)
                {
                    var size = GenAdj.OccupiedRect(IntVec3.Zero, rotation, def.Size);
                    geometryFor(_view.ConstructionMaterial(_view.BuildingSideMaterial(source))).Box(center,
                        new Vector3(size.Width, height, size.Height), new Rect(0, 0, 1, 1), new Rect(0, 0, 1, 1), 15, false, false);
                }
                // var top = geometryFor(ConstructionMaterial(source));
                // if (def is ThingDef groundSpot && IsGroundSpot(groundSpot))
                //     AppendGroundSpotSurface(top, graphic, groundSpot, rotation, center, height + .008f);
                // else AppendGraphicSurface(top, graphic, rotation, center, height + .008f);
                _view.AppendGraphicSurface(
                    geometryFor(_view.ConstructionMaterial(source)),
                    graphic,
                    rotation,
                    center,
                    height + .008f
                );
            }

            internal Geometry ConstructionPlanBatch(Material material, Vector3 center)
            {
                _view._constructionPlanMaterials.Add(material);
                return _view.Batch(material, center);
            }

            internal void ToggleConstructionPlans()
            {
                _view._constructionPlansHidden = !_view._constructionPlansHidden;
                foreach (var chunk in _view.chunkRoots.Values)
                    for (int i = 0; i < chunk.transform.childCount; i++)
                    {
                        var child = chunk.transform.GetChild(i);
                        var renderer = child.GetComponent<MeshRenderer>();
                        if (renderer != null && _view._constructionPlanMaterials.Contains(renderer.sharedMaterial))
                            child.gameObject.SetActive(!_view._constructionPlansHidden);
                    }
            }

            internal void DrawConstructionPlan(Thing thing)
            {
                if (!thing.Spawned || _view.map.fogGrid.IsFogged(thing.Position)) return;
                var def = thing.def.entityDefToBuild;
                if (def == null) return;
                if (_blueprintOutline == null) _blueprintOutline = _view.PlanMaterial(new Color(.25f, .75f, 1));
                if (_frameOutline == null) _frameOutline = _view.PlanMaterial(new Color(1, .75f, .25f));
                var center = thing.TrueCenter(); center.y = .035f;
                var graphic = _view.ConstructionGraphic(def, thing.Stuff, thing.StyleDef);
                var model = _view.ConstructionModel(def, graphic?.path);
                int links = model == null ? 0 : _view.ConstructionLinks(model, (ThingDef)def, thing.Position, thing.Rotation);
                if (IsConduit(def)) links = _view.ConduitLinks(thing.Position);
                var outline = _view.ConstructionPlanBatch(thing is Frame ? _frameOutline : _blueprintOutline, center);
                if (def is ThingDef doorPlan && doorPlan.IsDoor)
                    outline.Append(_view.ConstructionDoorOutline(doorPlan), center, Quaternion.Euler(0, thing.Rotation.AsAngle, 0));
                else if (model != null) outline.Append(ConstructionOutline(model, (ThingDef)def, links), center, Quaternion.Euler(0, thing.Rotation.AsAngle, 0));
                else if (IsWallTorch(def)) outline.Append(WallTorchConstructionOutline(), center, Quaternion.Euler(0, thing.Rotation.AsAngle, 0));
                else OutlineBox(outline, center, new Vector3(thing.RotatedSize.x - .025f, _view.ConstructionHeight(def), thing.RotatedSize.z - .025f),
                    IsConstructionWall(def) ? .012f : .025f);
                _view.DrawConstructionWorkSpot(outline, def, thing.Position, thing.Rotation, Vector3.zero);
                _view.ConstructionVisual(def, graphic, thing.Rotation, center, model, links, IsConstructionWall(def) ? _view.ConstructionWallFaces(thing.Position) : 15, thing.Stuff, material => _view.ConstructionPlanBatch(material, center));
            }

            internal void UpdatePlacementPreview()
            {
                if (!_view.SupportsBuildDrag) _view.ResetBuildDrag();
                var cell = _view.FPVPlacementCell();
                bool buildDrag = _buildDragStart.IsValid && _view.SupportsBuildDrag;
                if (buildDrag) cell = _buildDragEnd;
                if (_view.buildTool == null || !cell.IsValid)
                { if (_view.placementPreview != null) _view.placementPreview.SetActive(false); return; }
                var def = _view.buildTool.PlacingDef;
                if (!buildDrag && cell.InBounds(_view.map))
                    foreach (var existing in _view.map.thingGrid.ThingsListAt(cell))
                        if ((existing is Blueprint || existing is Frame) && existing.def.entityDefToBuild == def)
                        { if (_view.placementPreview != null) _view.placementPreview.SetActive(false); return; }
                var stuff = _view.buildTool.StuffDef;
                bool wallTorch = IsWallTorch(def);
                var graphic = _view.ConstructionGraphic(def, stuff, _view.buildTool.ReinstallTarget?.GetInnerIfMinified().StyleDef);
                var model = wallTorch ? null : _view.ConstructionModel(def, graphic?.path);
                int links = model == null || !(def is ThingDef modelDef) ? 0 : _view.ConstructionLinks(model, modelDef, cell, _view.buildRotation);
                if (IsConduit(def)) links = _view.ConduitLinks(cell);
                int wallFaces = IsConstructionWall(def) ? _view.ConstructionWallFaces(cell) : 15;
                var occupied = GenAdj.OccupiedRect(cell, _view.buildRotation, def.Size);
                var center = new Vector3((occupied.minX + occupied.maxX + 1) * .5f, .035f,
                    (occupied.minZ + occupied.maxZ + 1) * .5f);
                if (_view.placementPreview == null || _placementPreviewDef != def || _placementPreviewModel != model ||
                    _placementPreviewLinks != links || _placementPreviewWallFaces != wallFaces || _placementPreviewRotation != _view.buildRotation || _placementPreviewStuff != stuff ||
                    _buildPreviewStart != (buildDrag ? _buildDragStart : IntVec3.Invalid) ||
                    _buildPreviewEnd != (buildDrag ? _buildDragEnd : IntVec3.Invalid))
                {
                    if (_placementMaterial == null) _placementMaterial = _view.PlanMaterial(Color.green);
                    foreach (var part in _placementParts)
                    {
                        var mesh = part.GetComponent<MeshFilter>().sharedMesh;
                        _view.meshes.Remove(mesh); UnityEngine.Object.Destroy(mesh);
                    }
                    _placementParts.Clear();
                    if (_view.placementPreview != null)
                    {
                        _view.RemoveMovingShadows(_view.placementPreview.transform);
                        _view.placementPreview.SetActive(false);
                        UnityEngine.Object.Destroy(_view.placementPreview);
                    }
                    _view.placementPreview = new GameObject("FPV placement preview") { layer = Layer, hideFlags = HideFlags.DontSave };
                    _view.placementPreview.transform.SetParent(_view.root.transform, false);
                    var geometry = new Geometry();
                    var turn = Quaternion.Euler(0, _view.buildRotation.AsAngle, 0);
                    if (buildDrag)
                    {
                        var size = new Vector3(.975f, _view.ConstructionHeight(def), .975f);
                        foreach (var dragCell in _buildDragCells)
                        {
                            var offset = dragCell.ToVector3Shifted() - center;
                            offset.y = 0;
                            OutlineBox(geometry, offset, size, .012f);
                        }
                    }
                    else if (def is ThingDef doorPreview && doorPreview.IsDoor) geometry.Append(_view.ConstructionDoorOutline(doorPreview), Vector3.zero, turn);
                    else if (model != null) geometry.Append(ConstructionOutline(model, (ThingDef)def, links), Vector3.zero, turn);
                    else if (wallTorch) geometry.Append(WallTorchConstructionOutline(), Vector3.zero, turn);
                    else OutlineBox(geometry, Vector3.zero, new Vector3(occupied.Width - .025f, _view.ConstructionHeight(def), occupied.Height - .025f),
                        IsConstructionWall(def) ? .012f : .025f);
                    if (!buildDrag) _view.DrawConstructionWorkSpot(geometry, def, cell, _view.buildRotation, center);
                    var previewBatches = new Dictionary<Material, Geometry> { [_placementMaterial] = geometry };
                    if (!buildDrag) _view.ConstructionVisual(def, graphic, _view.buildRotation, Vector3.zero, model, links, wallFaces, stuff, material =>
                    {
                        if (!previewBatches.TryGetValue(material, out var batch)) previewBatches.Add(material, batch = new Geometry());
                        return batch;
                    });
                    var previousParent = _view.geometryParent; _view.geometryParent = _view.placementPreview.transform;
                    try { foreach (var batch in previewBatches) _placementParts.Add(_view.CreateMesh(batch.Key, batch.Value, "Construction preview")); }
                    finally { _view.geometryParent = previousParent; }
                    _placementPreviewDef = def; _placementPreviewModel = model; _placementPreviewLinks = links; _placementPreviewWallFaces = wallFaces;
                    _placementPreviewRotation = _view.buildRotation; _placementPreviewStuff = stuff;
                    _buildPreviewStart = buildDrag ? _buildDragStart : IntVec3.Invalid;
                    _buildPreviewEnd = buildDrag ? _buildDragEnd : IntVec3.Invalid;
                }
                _view.placementPreview.SetActive(true);
                _view.placementPreview.transform.localPosition = center;
                bool throughWalls = IsConduit(def) || wallTorch || IsWallLamp(def) ||
                    def is ThingDef placedDef && placedDef.building?.canPlaceOverWall == true;
                if (!throughWalls && def is ThingDef)
                    foreach (var occupiedCell in occupied)
                    {
                        if (!occupiedCell.InBounds(_view.map)) continue;
                        var wall = occupiedCell.GetEdifice(_view.map);
                        if (wall?.def.Fillage != FillCategory.Full ||
                            !GenConstruct.CanPlaceBlueprintOver(def, wall.def, stuff, wall.Stuff)) continue;
                        throughWalls = true;
                        break;
                    }
                _placementMaterial.SetInt("_ZTest", (int)(throughWalls ?
                    UnityEngine.Rendering.CompareFunction.Always : UnityEngine.Rendering.CompareFunction.LessEqual));
                _placementMaterial.SetFloat("_ZWrite", throughWalls ? 0 : 1);
                _placementMaterial.SetInt("_StencilWriteMask", throughWalls ? 0 : 64);
                _placementMaterial.renderQueue = throughWalls ? (int)UnityEngine.Rendering.RenderQueue.Overlay : -1;
                _placementMaterial.color = _view.buildTool.CanDesignateCell(cell).Accepted ? new Color(.35f, 1, .5f) : new Color(1, .3f, .25f);
            }

            internal void RunContextBuild(Thing target, Designator_Build command)
            {
                var def = command.PlacingDef;
                if (!(target is Blueprint_Build blueprint) || target.Faction != _view.ControlledPawn.Faction)
                {
                    _view.SelectFPVBuild(def);
                    return;
                }
                var stuff = def.MadeFromStuff ? blueprint.EntityToBuildStuff() : null;
                if (def.MadeFromStuff && (stuff == null || !stuff.stuffProps.CanMake(def)))
                    stuff = command.StuffDef?.stuffProps?.CanMake(def) == true
                        ? command.StuffDef : GenStuff.DefaultStuffFor(def);
                var rotation = def is ThingDef thingDef && thingDef.rotatable ? blueprint.Rotation : Rot4.North;
                if (Rim3DMultiplayer.LocalInput)
                {
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                    {
                        Action = Rim3DAction.ReplacePlan, Target = blueprint, BuildDef = def,
                        Stuff = stuff, Rotation = rotation
                    });
                    return;
                }
                _view.ReplaceFPVBlueprint(blueprint, def, stuff, rotation);
            }

            internal void ReplaceFPVBlueprint(Blueprint_Build blueprint, BuildableDef def, ThingDef stuff, Rot4 rotation)
            {
                if (blueprint == null || blueprint.Destroyed || !blueprint.Spawned || blueprint.Map != _view.map ||
                    blueprint.Faction != _view.ControlledPawn.Faction || Find.CurrentMap != _view.map ||
                    def == null || !def.BuildableByPlayer || _view.map.fogGrid.IsFogged(blueprint.Position)) return;
                if (def.MadeFromStuff && (stuff?.IsStuff != true || !stuff.stuffProps.CanMake(def))) return;
                var tool = new FPVBuildDesignator(def) { isOrder = true };
                if (!tool.Visible || tool.Disabled) return;
                tool.SetRotation(rotation);
                if (def.MadeFromStuff) tool.SetStuffDef(stuff);
                var report = tool.CanReplaceBlueprint(blueprint);
                if (!report.Accepted)
                {
                    _view.SayAction(report.Reason ?? "Rim3D_CannotBuildHere".Translate().ToString());
                    return;
                }
                var cell = blueprint.Position;
                var oldRotation = blueprint.Rotation;
                bool placed = false;
                // Keep the old plan recoverable until placement completes successfully.
                blueprint.DeSpawn();
                try
                {
                    tool.DesignateSingleCell(cell);
                    foreach (var thing in cell.GetThingList(_view.map))
                        if (thing.def == def || thing.def.entityDefToBuild == def)
                        { placed = true; break; }
                    if (def is TerrainDef terrain)
                        placed = _view.map.terrainGrid.TerrainAt(cell) == terrain ||
                            _view.map.terrainGrid.FoundationAt(cell) == terrain;
                }
                finally
                {
                    if (placed) blueprint.Destroy(DestroyMode.Cancel);
                    else GenSpawn.Spawn(blueprint, cell, _view.map, oldRotation);
                    _view.MarkNeighbors(cell);
                }
                _view.SayAction((placed ? "Rim3D_BlueprintPlaced" : "Rim3D_CannotBuildHere").TranslateBindings().ToString());
            }

        }

        sealed partial class CarryActions
        {
            internal void EquipFPVWeapon()
            {
                if (_view.SyncAction(Rim3DAction.Equip)) return;
                var pawn = _view.ControlledPawn;
                var target = _view.aimedThing as ThingWithComps;
                if (target == null || !target.def.IsWeapon || target.TryGetComp<CompEquippable>() == null ||
                    pawn?.equipment == null || _view.controller.IsResting) return;
                if (!_view.CanTouchCarryTarget(target))
                { _view.SayAction("Rim3D_MoveCloserToItem".Translate().ToString()); return; }
                if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) ||
                    target.IsForbidden(pawn) || target.IsBurning())
                { _view.SayAction("Rim3D_ActionUnavailable".Translate().ToString()); return; }
                if (!pawn.CanReserve(target))
                { _view.SayAction("Rim3D_ItemReserved".Translate().ToString()); return; }
                if (!EquipmentUtility.CanEquip(target, pawn, out var reason))
                { _view.SayAction(reason); return; }
                var previous = pawn.equipment.Primary;
                float previousMass = previous?.GetStatValue(StatDefOf.Mass) ?? 0;
                if (MassUtility.FreeSpace(pawn) + previousMass < target.GetStatValue(StatDefOf.Mass))
                { _view.SayAction("Rim3D_EquipOverweight".Translate().ToString()); return; }
                _view.constructionDef = null;
                if (pawn.Drafted) _view.controller.SetCombat(false);
                _view.controller.ReturnToManual();
                string previousLabel = previous?.LabelCap.ToString();
                if (previous != null && !pawn.equipment.TryDropEquipment(previous, out _, pawn.Position, forbid: false))
                { _view.SayAction("Rim3D_NoWeaponDropSpace".Translate().ToString()); return; }
                var cell = target.Position;
                // Manual pickup is immediate, like taking an item into the hands.
                // Avoid a goto toil that waits for the intercepted vanilla pather.
                var weapon = (ThingWithComps)target.SplitOff(1);
                if (weapon.Spawned) weapon.DeSpawn();
                pawn.equipment.AddEquipment(weapon);
                pawn.mindState.droppedWeapon = null;
                weapon.def.soundInteract?.PlayOneShot(new TargetInfo(pawn.Position, _view.map));
                _view.nextGearSummary = 0;
                _view.MarkNeighbors(cell);
                if (previous != null && previous.Spawned)
                {
                    _view.MarkNeighbors(previous.Position);
                    _view.SayFloatingItem("Rim3D_PreviousWeaponDropped".Translate(previousLabel).ToString());
                }
                _view.SayAction("Rim3D_EquipHint".TranslateBindings().ToString());
                _view.actionMenu = false;
            }

        }

        readonly WorkActions _workActions;

        sealed partial class WorkActions
        {
            readonly MapView _view;

            internal WorkActions(MapView view)
            {
                _view = view;
            }

            internal void InteractFPV()
            {
                if (!_view.controller.IsResting && !_view.HasCarryPlacement(_view.aimedThing) &&
                    CarryActions.StorageSlots(_view.aimedThing) != null)
                {
                    _view._actionMenus.OpenStorageInventory(_view.aimedThing);
                    return;
                }
                // Recipe selection is local UI; synchronize the chosen recipe or
                // bill work afterwards, so an empty table can open its menu in MP.
                if (!Rim3DMultiplayer.Simulating && !_view.controller.IsResting && _view.aimedThing is Building_WorkTable workTable)
                { _view.StartFPVBillWork(workTable); return; }
                if (_view.SyncAction(Rim3DAction.Interact)) return;
                if (_view.controller.IsResting) { _view.SayAction("Rim3D_StandUpHint".Translate().ToString()); return; }
                if (_view.aimedThing == null) { _view.SayAction("Rim3D_AimInteractionHint".Translate().ToString()); return; }
                if (_view.HasCarryPlacement(_view.aimedThing)) { _view.PlaceCarriedFPV(_view.aimedThing); return; }
                if (_view.aimedThing is Building_NutrientPasteDispenser dispenser)
                { _view._carryActions.TakePaste(dispenser); return; }
                if (MortarTargeting.IsMortar(_view.aimedThing))
                {
                    if (!MortarTargeting.CanMan(_view.ControlledPawn, _view.map, _view.aimedThing))
                    { _view.SayAction("Rim3D_ActionUnavailable".Translate().ToString()); return; }
                    _view.constructionDef = null; _view.buildTool = null;
                    var job = JobMaker.MakeJob(JobDefOf.ManTurret, _view.aimedThing);
                    job.expiryInterval = -1;
                    if (!_view.controller.OrderAction(job)) _view.SayAction(_view.controller.Status);
                    return;
                }
                if (_view.aimedThing is Building_Bed bed)
                {
                    var pawn = _view.ControlledPawn;
                    if (!PawnControl.CanSleepInBed(pawn, bed))
                    { _view.SayAction("Rim3D_BedSleepUnavailable".Translate().ToString()); return; }
                    _view.constructionDef = null; _view.buildTool = null;
                    _view.SayAction(_view.controller.OrderSleep(bed) ? "Rim3D_SleepOrdered".Translate().ToString() : _view.controller.Status);
                }
                else if (_view.aimedThing is Blueprint_Install) _view.BeginConstruction(_view.aimedThing);
                else if (_view.aimedThing is IConstructible project)
                {
                    if (ProjectMaterialsReady(project)) _view.BeginConstruction(_view.aimedThing);
                    else _view.DepositFPVResource(_view.aimedThing);
                }
                else if (RecreationGiver(_view.aimedThing) != null) _view.StartRecreation(_view.aimedThing);
                else if (_view.aimedThing is Building_WorkTable table) _view.StartFPVBillWork(table);
                else if (_view.aimedThing is Building_ResearchBench) _view.WorkOnTarget("Research");
                else if (_view.aimedThing is Plant) _view.WorkOnTarget("Harvest");
                else if (_view.aimedThing.def.mineable) _view.DesignateAndWork(new Designator_Mine(), "Mine");
                else if (_view.aimedThing.def.category == ThingCategory.Item) _view.TakeFPVCarry(_view.aimedThing, _view.aimedThing.stackCount);
                else if (_view.aimedThing is Pawn && _view.ControlledPawn.Drafted) _view.MeleeFPV();
                else if (_view.aimedThing.def.category == ThingCategory.Building) _view.WorkOnTarget("Repair");
                else _view.SayAction("Rim3D_NoQuickAction".Translate().ToString());
            }

            internal bool CanClaimFPVBuilding(Thing target) => target is Building && !target.Destroyed &&
                target.Spawned && target.Map == _view.map && !_view.map.fogGrid.IsFogged(target.Position) &&
                target.Faction != Faction.OfPlayer && PawnControl.Eligible(_view.ControlledPawn) &&
                !_view.controller.IsResting && _view.CanTouchCarryTarget(target) && target.ClaimableBy(Faction.OfPlayer).Accepted;

            internal void ClaimFPVBuilding()
            {
                if (_view.SyncAction(Rim3DAction.ClaimBuilding)) return;
                if (!_view.CanClaimFPVBuilding(_view.aimedThing))
                { _view.SayAction("Rim3D_ActionUnavailable".Translate().ToString()); return; }
                new Designator_Claim().DesignateThing(_view.aimedThing);
                _view.nextDesignationUpdate = 0;
                _view.SayAction("Rim3D_BuildingClaimed".Translate().ToString());
            }

            internal bool WorkOnTarget(string kind)
            {
                if (_view.SyncAction(Rim3DAction.Work, kind: kind)) return true;
                if (_view.ControlledPawn.Drafted) _view.controller.SetCombat(false);
                _view.constructionDef = null;
                var job = _view.WorkJob(_view.aimedThing, kind);
                if (job == null) { _view.SayAction(_view.workFailure); return false; }
                bool ordered = _view.controller.OrderAction(job);
                _view.SayAction(ordered ? "Rim3D_ActionStartedHint".TranslateBindings().ToString() : _view.controller.Status);
                _view.actionMenu = false;
                return ordered;
            }

            internal void RunNativeContextAction(Action callback)
            {
                if (_view.contextPawn != _view.ControlledPawn || !PawnControl.Eligible(_view.contextPawn) ||
                    _view.contextTarget != null && (_view.contextTarget.Destroyed || !_view.contextTarget.Spawned || _view.contextTarget.Map != _view.map))
                { _view.DismissActionMenu(); return; }
                _view.actionMenu = false;
                _view.ResetCarryInput();
                _view.CancelSimulationConstruction();
                // Queue cancellation before the native callback queues its own MP command.
                // The authoritative MapView is separate from this local UI view.
                if (Rim3DMultiplayer.LocalInput)
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand { Action = Rim3DAction.CancelConstruction });
                callback();
            }

            internal void RunContextAction(ContextAction action)
            {
                // Keep the original target while the cursor is over the menu.
                if (_view.contextPawn != _view.ControlledPawn || _view.contextTarget != null &&
                    (_view.contextTarget.Destroyed || !_view.contextTarget.Spawned || _view.contextTarget.Map != _view.map))
                { _view.CloseFPVMenu(); return; }
                _view.aimedThing = _view.contextTarget;
                if (action == ContextAction.RoofFlat || action == ContextAction.RoofProcedural || action == ContextAction.RemoveRoof)
                {
                    _view.aimedCell = _view._contextRoofCell;
                    _view.aimingAtRoof = _view._contextRoofCell.IsValid;
                }
                _view.actionMenu = false;
                _view.ResetCarryInput();
                if (!_view.CanContextAction(action)) { _view.SayAction("Rim3D_ActionUnavailable".Translate().ToString()); return; }
                switch (action)
                {
                    case ContextAction.Reinstall: _view.SelectFPVReinstall(_view.aimedThing); break;
                    case ContextAction.Interact: _view.InteractFPV(); break;
                    case ContextAction.ClaimBuilding: _view.ClaimFPVBuilding(); break;
                    case ContextAction.Bills: _view.OpenFPVBills((Building_WorkTable)_view.aimedThing); break;
                    case ContextAction.StorageSettings: _view._actionMenus.OpenStorageSettings(_view.aimedThing); break;
                    case ContextAction.Cut: _view.DesignateAndWork(new Designator_PlantsCut(), "Cut"); break;
                    case ContextAction.Harvest: _view.DesignateAndWork(new Designator_PlantsHarvest(), "Harvest"); break;
                    case ContextAction.Mine: _view.DesignateAndWork(new Designator_Mine(), "Mine"); break;
                    case ContextAction.SmoothWall: _view.DesignateAndWork(new Designator_SmoothWalls(), "SmoothWall"); break;
                    case ContextAction.Repair: _view.WorkOnTarget("Repair"); break;
                    case ContextAction.Deconstruct: _view.DesignateAndWork(new Designator_Deconstruct(), "Deconstruct"); break;
                    case ContextAction.Equip: _view.EquipFPVWeapon(); break;
                    case ContextAction.Build: _view.BeginConstruction(_view.aimedThing); break;
                    case ContextAction.CancelPlan: _view.CancelFPVPlan(); break;
                    case ContextAction.Ingest: _view.IngestFPV(_view.aimedThing); break;
                    case ContextAction.TakeCount:
                        if (!_view.CanTouchCarryTarget(_view.aimedThing)) { _view.SayAction("Rim3D_MoveCloserToItem".Translate().ToString()); break; }
                        _view.carryWindow?.Close(false);
                        _view.carryWindow = new FPVCarryWindow(_view, _view.aimedThing);
                        Find.WindowStack.Add(_view.carryWindow);
                        break;
                    case ContextAction.Store: _view.StoreFPVCarry(); break;
                    case ContextAction.Drop: _view.DropFPVCarry(); break;
                    case ContextAction.Melee: _view.MeleeFPV(); break;
                    case ContextAction.Arrest: _view.ArrestFPV(_view.aimedThing as Pawn); break;
                    case ContextAction.Tend: _view.TendFPV(_view.aimedThing as Pawn); break;
                    case ContextAction.SelfTend: _view.TendFPV(_view.ControlledPawn); break;
                    case ContextAction.RoofFlat: _view.BuildFPVRoof(_view.aimedCell); break;
                    case ContextAction.RoofProcedural: _view.BuildFPVRoof(_view.aimedCell, true); break;
                    case ContextAction.RemoveRoof: _view.RemoveFPVRoof(_view.aimedCell); break;
                }
            }

        }

        readonly CombatActions _combatActions;

        sealed partial class CombatActions
        {
            readonly MapView _view;

            internal CombatActions(MapView view)
            {
                _view = view;
            }

            internal void MeleeFPV()
            {
                if (!PawnControl.CanUseCombatSkill(_view.ControlledPawn, SkillDefOf.Melee))
                { _view.SayAction("Rim3D_MeleeDisabled".Translate().ToString()); return; }
                if (_view.SyncAction(Rim3DAction.Melee)) return;
                var pawn = _view.ControlledPawn;
                if (pawn == null || Find.TickManager.Paused) return;
                if (_view.aimedThing == null) { _view.SayAction("Rim3D_AimMeleeHint".Translate().ToString()); return; }
                if (!_view.aimedThing.Spawned || _view.aimedThing.Destroyed || _view.aimedThing == pawn ||
                    (_view.aimedThing is Pawn deadPawn && deadPawn.Dead)) return;
                if (!pawn.Drafted && !_view.aimedThing.HostileTo(pawn))
                { _view.SayAction("Rim3D_FriendlyAttackHint".TranslateBindings().ToString()); return; }
                if (API.RimVisionCombat.Handles(pawn, API.RimVisionAttackKind.Melee))
                {
                    if (!_view.controller.Active || _view.aimedThing.Map != pawn.Map) return;
                    var context = new API.RimVisionCombatContext(pawn, API.RimVisionAttackKind.Melee,
                        _view.aimedThing, _view.aimedThing.Position, _view.controller.Position, Vector3.zero, 0, true);
                    API.RimVisionCombat.TryExecute(context, out _, out var reason);
                    if (!string.IsNullOrEmpty(reason)) _view.SayAction(reason);
                    return;
                }
                if (_view.controller.CombatRecovering || pawn.stances.FullBodyBusy ||
                    (_view.controller.IsActing && pawn.CurJob.def == JobDefOf.AttackMelee)) return;
                _view.constructionDef = null;
                if (!_view.ControlledPawn.Drafted) _view.controller.SetCombat(true);
                if (ReachabilityImmediate.CanReachImmediate(pawn, _view.aimedThing, PathEndMode.Touch))
                {
                    // A click in reach strikes now instead of waiting for the chase
                    // toil and its path destination to catch up with manual movement.
                    _view.controller.ReturnToManual();
                    if (pawn.meleeVerbs.TryMeleeAttack(_view.aimedThing)) _view.controller.RememberCombatRecovery();
                    return;
                }
                var job = JobMaker.MakeJob(JobDefOf.AttackMelee, _view.aimedThing);
                job.maxNumMeleeAttacks = 1;
                job.expiryInterval = 600;
                _view.controller.OrderAction(job);
            }

        }

        readonly WorldOverlays _worldOverlays;

        sealed partial class WorldOverlays
        {
            readonly MapView _view;

            internal WorldOverlays(MapView view)
            {
                _view = view;
            }

            internal void UpdateDesignationIcons()
            {
                if (Time.unscaledTime < _view.nextDesignationUpdate) return;
                _view.nextDesignationUpdate = Time.unscaledTime + .1f;
                foreach (var batch in _view.designationIcons.Values)
                {
                    var geometry = batch.Geometry;
                    geometry.Vertices.Clear(); geometry.Normals.Clear(); geometry.Uvs.Clear();
                    geometry.CardCenters.Clear(); geometry.Indices.Clear();
                }
                // Read only assigned orders, rather than visiting every map cell.
                foreach (var entry in _view.map.designationManager.designationsByDef)
                {
                    foreach (var designation in entry.Value)
                    {
                        var target = designation.target;
                        if (target.HasThing && (!target.Thing.Spawned || target.Thing.Map != _view.map)) continue;
                        var cell = target.Cell;
                        if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell)) continue;
                        var source = designation.IconMat;
                        if (source == null || source.mainTexture == null) continue;
                        var position = target.HasThing ? target.Thing.DrawPos : cell.ToVector3Shifted();
                        position.y = .3f;
                        if (target.HasThing)
                        {
                            var thing = target.Thing;
                            position.y = thing is Pawn pawn ? _view.PawnSpriteTop(pawn) + .25f :
                                thing is Plant ? .65f : thing.def.category == ThingCategory.Building
                                    ? _view.ConstructionHeight(thing.def) + .2f : .35f;
                        }
                        // Bring trunk markers just in front of the target's surface.
                        var towardCamera = _view.CameraMapPosition - position;
                        towardCamera.y = 0;
                        position += towardCamera.normalized * (target.HasThing && target.Thing is Plant ? .65f : .15f);
                        if (!_view.designationIcons.TryGetValue(source, out var batch))
                        {
                            batch = new DesignationIconBatch();
                            _view.designationIcons.Add(source, batch);
                        }
                        batch.Geometry.Upright(position, .55f, .55f, false);
                    }
                }
                Material needsPower = null;
                var buildings = _view.map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
                foreach (var thing in buildings)
                {
                    if (!thing.Spawned || thing.Map != _view.map || _view.map.fogGrid.IsFogged(thing.Position)) continue;
                    var power = thing.TryGetComp<CompPowerTrader>();
                    if (power == null || power.PowerOn || !power.Props.showPowerNeededIfOff ||
                        thing.IsBrokenDown() || !FlickUtility.WantsToBeOn(thing)) continue;
                    if (needsPower == null)
                        needsPower = MaterialPool.MatFrom("UI/Overlays/NeedsPower", ShaderDatabase.MetaOverlay);
                    if (!_view.designationIcons.TryGetValue(needsPower, out var batch))
                    {
                        batch = new DesignationIconBatch();
                        _view.designationIcons.Add(needsPower, batch);
                    }
                    var position = thing.DrawPos;
                    position.y = _view.ConstructionHeight(thing.def) + .2f;
                    var towardCamera = _view.CameraMapPosition - position;
                    towardCamera.y = 0;
                    position += towardCamera.normalized * .15f;
                    batch.Geometry.Upright(position, .55f, .55f, false);
                }
                foreach (var entry in _view.designationIcons)
                {
                    var batch = entry.Value;
                    var geometry = batch.Geometry;
                    if (geometry.Vertices.Count == 0)
                    {
                        if (batch.Object != null) batch.Object.SetActive(false);
                        continue;
                    }
                    if (batch.Object == null)
                    {
                        var source = entry.Key;
                        var material = _view.NewMaterial(source.mainTexture, source.color, false);
                        material.mainTextureScale = source.mainTextureScale;
                        material.mainTextureOffset = source.mainTextureOffset;
                        material.SetFloat("_Billboard", 1);
                        material.SetFloat("_Unlit", 1);
                        material.SetFloat("_FlatItem", 1);
                        _view.geometryParent = null;
                        batch.Object = _view.CreateMesh(material, geometry, "Designation icons");
                        batch.Mesh = batch.Object.GetComponent<MeshFilter>().sharedMesh;
                        batch.Mesh.MarkDynamic();
                    }
                    else
                    {
                        batch.Object.SetActive(true);
                        var mesh = batch.Mesh;
                        mesh.Clear();
                        mesh.SetVertices(geometry.Vertices); mesh.SetNormals(geometry.Normals);
                        mesh.SetUVs(0, geometry.Uvs); mesh.SetUVs(1, geometry.CardCenters);
                        mesh.SetTriangles(geometry.Indices, 0);
                        mesh.RecalculateBounds();
                        geometry.ApplyPlantWind(mesh);
                        // The shader turns each card toward the view. Include every yaw.
                        var bounds = mesh.bounds;
                        bounds.Expand(new Vector3(.55f, 0, .55f));
                        mesh.bounds = bounds;
                    }
                }
            }

        }
    }
}

