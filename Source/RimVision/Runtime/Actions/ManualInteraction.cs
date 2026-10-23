using System;
using System.Collections.Generic;
using RimWorld;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {

        string workFailure;
        string aimSkillText = "";
        string aimFailureText = "";
        string aimPlantText = "";

        public void NotifySowingResult(IntVec3 cell, int jobId, bool started) => _workActions.SowingResult(cell, jobId, started);

        static readonly System.Reflection.MethodInfo _canUseRecreation = AccessTools.Method(typeof(JoyGiver_InteractBuilding), "CanInteractWith");
        static readonly System.Reflection.MethodInfo _makeRecreationJob = AccessTools.Method(typeof(JoyGiver_InteractBuilding), "TryGivePlayJob");

        static JoyGiverDef RecreationGiver(Thing target)
        {
            if (!(target is Building)) return null;
            foreach (var def in DefDatabase<JoyGiverDef>.AllDefsListForReading)
                if (def.thingDefs?.Contains(target.def) == true &&
                    typeof(JoyGiver_InteractBuilding).IsAssignableFrom(def.giverClass)) return def;
            return null;
        }

        void StartRecreation(Thing target) => _workActions.StartRecreation(target);

        void ChooseManualRecipe(Building_WorkTable table) => _actionMenus.ChooseManualRecipe(table);

        string RecipeFailure(RecipeDef recipe) => _workActions.RecipeFailure(recipe);

        static string PrimaryWork(Thing target)
        {
            if (target is Blueprint_Install) return null;
            if (target is Blueprint || target is Frame) return "Build";
            if (target is Building_ResearchBench) return "Research";
            if (target is Building_WorkTable) return "Bill";
            if (target is Plant) return "Harvest";
            if (target?.def.mineable == true) return "Mine";
            if (target?.def.category == ThingCategory.Building) return "Repair";
            return null;
        }

        SkillDef ActionSkill(Thing target, string kind) => _workActions.ActionSkill(target, kind);

        string SkillFailure(SkillDef skill, int required) => _workActions.SkillFailure(skill, required);

        // Cheap shared checks. Ingredient searches run only when an action is requested.
        string ManualActionFailure(Thing target, string kind) => _workActions.ManualActionFailure(target, kind);

        bool NeedsFloorRemoval(Thing target) => _workActions.NeedsFloorRemoval(target);

        List<WorkGiverDef> ManualWorkers(string kind) => _workActions.ManualWorkers(kind);

        Job ManualWorkJob(Thing target, string kind) => _workActions.ManualWorkJob(target, kind);

        Job ManualBillJob(WorkGiver_Scanner worker, Building_WorkTable table) => _workActions.ManualBillJob(worker, table);

        void UpdateAimActionInfo() => _viewTargeting.UpdateAimActionInfo();

        bool HasCarryPlacement(Thing target) => _workActions.HasCarryPlacement(target);

        string CarryPlacementLabel(Thing target, bool hint = false) => _workActions.CarryPlacementLabel(target, hint);

        string CarryPlacementFailure(Thing target) => _workActions.CarryPlacementFailure(target);

        void PlaceCarriedFPV(Thing target) => _workActions.PlaceCarriedFPV(target);

        string PlantHarvestInfo(Plant plant) => _viewTargeting.PlantHarvestInfo(plant);

        sealed partial class ActionMenus
        {
            internal void ChooseManualRecipe(Building_WorkTable table)
            {
                if (!table.Spawned || table.Map != _view.map || table.Faction != _view.ControlledPawn.Faction)
                { _view.SayAction("Rim3D_WorkObjectUnavailable".Translate().ToString()); return; }
                var options = new List<FloatMenuOption>();
                foreach (var recipe in table.def.AllRecipes)
                {
                    if (!recipe.AvailableNow || !recipe.AvailableOnNow(table, null)) continue;
                    string failure = _view.RecipeFailure(recipe);
                    string label = recipe.LabelCap.ToString();
                    if (recipe.workSkill != null)
                        label += " · " + recipe.workSkill.LabelCap + " " + (_view.ControlledPawn.skills?.GetSkill(recipe.workSkill).Level ?? 0);
                    if (failure != null) options.Add(new FloatMenuOption(label + " · " + failure, null));
                    else options.Add(new FloatMenuOption(label, () =>
                    {
                        if (!_view.FirstPerson || !table.Spawned || table.Map != _view.map || table.Faction != _view.ControlledPawn.Faction)
                        { _view.SayAction("Rim3D_WorkObjectGone".Translate().ToString()); return; }
                        string currentFailure = _view.RecipeFailure(recipe);
                        if (currentFailure != null) { _view.SayAction(currentFailure); return; }
                        if (!recipe.AvailableNow || !recipe.AvailableOnNow(table, null))
                        { _view.SayAction("Rim3D_RecipeGone".Translate().ToString()); return; }
                        if (table.BillStack.Count >= 15) { _view.SayAction("Rim3D_BillStackFull".Translate().ToString()); return; }
                        _view.AddFPVBill(table, recipe, null, true);
                    }));
                }
                if (options.Count == 0) { _view.SayAction("Rim3D_NoUnlockedRecipes".Translate().ToString()); return; }
                _view.actionMenu = _view.constructionMenu = _view.statusMenu = false;
                _view.ReleaseMouse();
                Find.WindowStack.Add(new FloatMenu(options));
            }

        }

        sealed partial class ViewTargeting
        {
            internal void UpdateAimActionInfo()
            {
                if (!_view.CanTouchCarryTarget(_view.aimedThing))
                {
                    _view.aimSkillText = _view.aimFailureText = _view.aimPlantText = "";
                    return;
                }
                if (_view.HasCarryPlacement(_view.aimedThing))
                {
                    _view.aimSkillText = _view.aimPlantText = "";
                    _view.aimFailureText = _view.CarryPlacementFailure(_view.aimedThing) ?? "";
                    return;
                }
                if (_view._carryActions.CanOpenStorage(_view.aimedThing))
                {
                    _view.aimSkillText = _view.aimFailureText = _view.aimPlantText = "";
                    return;
                }
                if (_view.aimedThing is Building_NutrientPasteDispenser)
                {
                    _view.aimSkillText = _view.aimFailureText = _view.aimPlantText = "";
                    return;
                }
                bool mortar = MortarTargeting.IsMortar(_view.aimedThing);
                var kind = mortar ? null : PrimaryWork(_view.aimedThing);
                var skill = mortar || _view.aimedThing.def.IsRangedWeapon ? SkillDefOf.Shooting :
                    _view.aimedThing.def.IsMeleeWeapon ? SkillDefOf.Melee : _view.ActionSkill(_view.aimedThing, kind);
                _view.aimSkillText = skill == null ? "" : "Rim3D_AimSkill".Translate(skill.LabelCap, _view.ControlledPawn.skills?.GetSkill(skill).Level ?? 0).ToString();
                _view.aimFailureText = mortar ? (MortarTargeting.CanMan(_view.ControlledPawn, _view.map, _view.aimedThing) ? "" :
                    "Rim3D_ActionUnavailable".Translate().ToString()) : kind == null ? "" : _view.ManualActionFailure(_view.aimedThing, kind) ?? "";
                _view.aimPlantText = _view.aimedThing is Plant plant ? _view.PlantHarvestInfo(plant) : "";
            }

            internal string PlantHarvestInfo(Plant plant)
            {
                var properties = plant.def.plant;
                var product = properties.harvestedThingDef;
                string state = !properties.Harvestable || product == null ? "Rim3D_PlantNoHarvest" :
                    plant.Blighted ? "Rim3D_PlantBlighted" : plant.HarvestableNow ? "Rim3D_PlantReady" : "Rim3D_PlantUnripe";
                return "Rim3D_PlantSummary".Translate(Mathf.RoundToInt(plant.Growth * 100f), state.Translate()).ToString();
            }

        }

        sealed partial class WorkActions
        {
            Zone_Growing _sowingZone;
            Pawn _sowingPawn;
            IntVec3 _sowingCell = IntVec3.Invalid;
            int _sowRequestJobId = -1;
            internal Zone_Growing SowingZone => _sowingPawn == _view.ControlledPawn ? _sowingZone : null;

            void ClearSowingInput()
            {
                _sowingZone = null;
                _sowingPawn = null;
                _sowingCell = IntVec3.Invalid;
                _sowRequestJobId = -1;
            }

            internal void SowingResult(IntVec3 cell, int jobId, bool started)
            {
                if (!started && _sowingCell == cell && _sowRequestJobId == jobId) ClearSowingInput();
            }

            internal void ResetSowingInput()
            {
                if (_sowingZone == null) return;
                bool cancel = _sowingPawn == _view.ControlledPawn;
                ClearSowingInput();
                if (!cancel) return;
                if (Rim3DMultiplayer.LocalInput)
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand { Action = Rim3DAction.Work, Kind = "StopSow" });
                else Sow(IntVec3.Invalid, -1, true);
            }

            internal bool UpdateSowingInput()
            {
                var pawn = _view.ControlledPawn;
                if (_sowingZone == null && ViewBindings.Pressed(ViewAction.Interact))
                    _view.PickRecreationTarget();
                if (_sowingZone == null && ViewBindings.Pressed(ViewAction.Interact) &&
                    _view.aimedCell.InBounds(_view.map) && !_view.aimingAtRoof &&
                    (_view.aimedThing == null || _view.aimedThing is Plant))
                {
                    _sowingZone = _view.aimedCell.GetZone(_view.map) as Zone_Growing;
                    if (_sowingZone != null)
                    {
                        _sowingPawn = pawn;
                        _sowingCell = _view.aimedCell;
                    }
                }
                if (_sowingZone == null) return false;
                if (pawn != _sowingPawn || _sowingZone.Map != _view.map || _sowingZone.CellCount == 0 ||
                    _sowingCell.GetZone(_view.map) != _sowingZone ||
                    !_sowingZone.allowSow)
                { ResetSowingInput(); return false; }
                if (_view.controller.IsActing)
                {
                    if (pawn.CurJob.def == JobDefOf.Sow) return true;
                    ClearSowingInput();
                    return false;
                }
                int jobId = pawn.CurJob?.loadID ?? -1;
                if (_sowRequestJobId == jobId) return true;
                _sowRequestJobId = jobId;
                if (Rim3DMultiplayer.LocalInput)
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                    { Action = Rim3DAction.Work, Kind = "Sow", Cell = _sowingCell, Count = jobId });
                else Sow(_sowingCell, jobId, false);
                return true;
            }

            internal void Sow(IntVec3 cell, int expectedJobId, bool stop)
            {
                if (Rim3DMultiplayer.LocalInput) return;
                var pawn = _view.ControlledPawn;
                if (stop)
                {
                    if (_view.controller.IsActing && pawn.CurJob.def == JobDefOf.Sow)
                        _view.controller.ReturnToManual();
                    return;
                }
                bool started = TrySow(cell, expectedJobId);
                if (Rim3DMultiplayer.Active) Rim3DMultiplayer.SowingResult?.Invoke(pawn, cell, expectedJobId, started);
                else SowingResult(cell, expectedJobId, started);
            }

            bool TrySow(IntVec3 cell, int expectedJobId)
            {
                var pawn = _view.ControlledPawn;
                if (!_view.controller.Active || _view.controller.IsResting || _view.controller.IsActing ||
                    (pawn.CurJob?.loadID ?? -1) != expectedJobId || !cell.InBounds(_view.map) ||
                    _view.map.fogGrid.IsFogged(cell) || !(cell.GetZone(_view.map) is Zone_Growing zone) ||
                    zone.CellCount == 0 || !zone.allowSow || !zone.CanAcceptSowNow()) return false;
                foreach (var def in _view.ManualWorkers("Sow"))
                {
                    if (!(def.Worker is WorkGiver_GrowerSow worker) ||
                        def.workType != null && pawn.WorkTypeIsDisabled(def.workType) ||
                        worker.MissingRequiredCapacity(pawn) != null) continue;
                    Job best = null;
                    int bestDistance = int.MaxValue;
                    foreach (var candidate in worker.PotentialWorkCellsGlobal(pawn))
                    {
                        if (candidate.GetZone(_view.map) != zone || _view.map.fogGrid.IsFogged(candidate)) continue;
                        int distance = (candidate - pawn.Position).LengthHorizontalSquared;
                        if (distance >= bestDistance || !pawn.CanReserve(candidate) ||
                            !pawn.CanReach(candidate, PathEndMode.OnCell, Danger.Deadly)) continue;
                        var job = worker.JobOnCell(pawn, candidate, true);
                        if (job == null) continue;
                        if (job.def != JobDefOf.Sow) { JobMaker.ReturnToPool(job); continue; }
                        if (best != null) JobMaker.ReturnToPool(best);
                        best = job;
                        bestDistance = distance;
                    }
                    if (best == null) continue;
                    best.workGiverDef = def;
                    _view.constructionDef = null;
                    if (pawn.Drafted) _view.controller.SetCombat(false);
                    bool started = _view.controller.OrderAction(best);
                    if (!started) _view.SayAction(_view.controller.Status);
                    return started;
                }
                _view.SayAction("Rim3D_NoWorkForAction".Translate().ToString());
                return false;
            }

            readonly Dictionary<string, List<WorkGiverDef>> _manualWorkers = new Dictionary<string, List<WorkGiverDef>>();

            internal void StartRecreation(Thing target)
            {
                var failure = _view.ManualActionFailure(target, "Joy");
                if (failure != null) { _view.SayAction(failure); return; }
                var giver = RecreationGiver(target)?.Worker as JoyGiver_InteractBuilding;
                var pawn = _view.ControlledPawn;
                if (pawn.needs?.joy == null || giver == null || !giver.CanBeGivenTo(pawn) ||
                    !(bool)_canUseRecreation.Invoke(giver, new object[] { pawn, target, false }))
                { _view.SayAction("Rim3D_RecreationUnavailable".Translate().ToString()); return; }
                if (pawn.Drafted) _view.controller.SetCombat(false);
                var job = (Job)_makeRecreationJob.Invoke(giver, new object[] { pawn, target });
                if (job == null) { _view.SayAction("Rim3D_RecreationUnavailable".Translate().ToString()); return; }
                _view.constructionDef = null; _view.buildTool = null;
                _view.SayAction(_view.controller.OrderAction(job) ? "Rim3D_ActionStartedHint".TranslateBindings().ToString() : _view.controller.Status);
                _view.actionMenu = false;
            }

            internal string RecipeFailure(RecipeDef recipe)
            {
                if (recipe.workSkill != null && _view.ControlledPawn.skills?.GetSkill(recipe.workSkill).TotallyDisabled == true)
                    return "Rim3D_WorkSkillDisabled".Translate(recipe.workSkill.LabelCap).ToString();
                if (recipe.workSpeedStat?.Worker.IsDisabledFor(_view.ControlledPawn) == true ||
                    recipe.efficiencyStat?.Worker.IsDisabledFor(_view.ControlledPawn) == true)
                    return "Rim3D_CannotDoRecipe".Translate().ToString();
                if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe && !MechanitorUtility.IsMechanitor(_view.ControlledPawn))
                    return "Rim3D_RequiresMechanitor".Translate().ToString();
                var requirement = recipe.FirstSkillRequirementPawnDoesntSatisfy(_view.ControlledPawn);
                return requirement == null ? null : _view.SkillFailure(requirement.skill, requirement.minLevel);
            }

            internal SkillDef ActionSkill(Thing target, string kind)
            {
                if (kind == "Build" || kind == "Repair" || kind == "Deconstruct" || kind == "SmoothWall") return SkillDefOf.Construction;
                if (kind == "Cut" || kind == "Harvest") return SkillDefOf.Plants;
                if (kind == "Mine") return SkillDefOf.Mining;
                if (kind == "Research") return SkillDefOf.Intellectual;
                if (target is Building_WorkTable table)
                {
                    foreach (var bill in table.BillStack) if (bill.recipe.workSkill != null) return bill.recipe.workSkill;
                    foreach (var recipe in table.def.AllRecipes) if (recipe.workSkill != null) return recipe.workSkill;
                }
                return null;
            }

            internal string SkillFailure(SkillDef skill, int required)
            {
                if (required > 0 && _view.ControlledPawn.skills?.GetSkill(skill).TotallyDisabled == true)
                    return "Rim3D_SkillUnavailable".Translate(skill.LabelCap).ToString();
                int level = _view.ControlledPawn.skills?.GetSkill(skill).Level ?? 0;
                return level >= required ? null : "Rim3D_SkillTooLow".Translate(skill.LabelCap, level, required).ToString();
            }

            internal string ManualActionFailure(Thing target, string kind)
            {
                var pawn = _view.ControlledPawn;
                if (pawn == null || !pawn.Spawned) return "Rim3D_NoControlledPawn".Translate().ToString();
                if (pawn.Dead) return "Rim3D_PawnDead".Translate().ToString();
                if (pawn.Downed) return "Rim3D_PawnCannotStand".Translate().ToString();
                if (pawn.InMentalState) return "Rim3D_PawnMentalBreak".Translate().ToString();
                if (!PawnControl.Eligible(pawn)) return "Rim3D_PawnControlLost".Translate().ToString();
                if (_view.controller.IsResting) return "Rim3D_PawnSleepingHint".TranslateBindings().ToString();
                var skill = kind == "Bill" || kind == "Joy" ? null : _view.ActionSkill(target, kind);
                if (skill != null && pawn.skills?.GetSkill(skill).TotallyDisabled == true)
                    return "Rim3D_WorkSkillDisabled".Translate(skill.LabelCap).ToString();
                if (target == null || target.Destroyed || !target.Spawned || target.Map != _view.map) return "Rim3D_TargetGone".Translate().ToString();
                if (_view.map.fogGrid.IsFogged(target.Position)) return "Rim3D_TargetFogged".Translate().ToString();
                if (target.IsBurning()) return "Rim3D_TargetBurning".Translate().ToString();
                if (target.IsForbidden(pawn)) return "Rim3D_TargetForbidden".Translate().ToString();
                if ((kind == "Build" || kind == "Bill" || kind == "Research" || kind == "Repair" ||
                    kind == "Deconstruct" && target.def.building?.alwaysDeconstructible != true) && target.Faction != pawn.Faction)
                    return "Rim3D_TargetNotOwned".Translate().ToString();
                var end = kind == "Bill" || kind == "Research" ? PathEndMode.InteractionCell : PathEndMode.Touch;
                if (kind != "Joy" && !pawn.CanReach(target, end, Danger.Deadly)) return "Rim3D_TargetUnreachable".Translate().ToString();
                if (kind != "Joy" && !FPVRespectReservationsPatch.CanReservePlant(pawn, target)) return "Rim3D_TargetReserved".Translate().ToString();
                if (kind == "SmoothWall")
                {
                    if (!target.def.IsSmoothable || target.Position.GetEdifice(_view.map) != target)
                        return "Rim3D_NoWorkForAction".Translate().ToString();
                    if (!pawn.CanReserve(target.Position)) return "Rim3D_TargetReserved".Translate().ToString();
                    if (_view.map.designationManager.DesignationAt(target.Position, DesignationDefOf.SmoothWall) == null)
                    {
                        var report = new Designator_SmoothWalls().CanDesignateThing(target);
                        if (!report.Accepted) return report.Reason ?? "Rim3D_ActionUnavailable".Translate().ToString();
                    }
                }
                if (kind == "Research")
                {
                    if (!(target is Building_ResearchBench bench)) return "Rim3D_NoWorkForAction".Translate().ToString();
                    var project = Find.ResearchManager.GetProject(null);
                    if (project == null) return "Rim3D_NoResearchProject".Translate().ToString();
                    if (!project.CanBeResearchedAt(bench, false)) return "Rim3D_ResearchBenchUnavailable".Translate().ToString();
                    if (bench.def.hasInteractionCell && !pawn.CanReserveSittableOrSpot(bench.InteractionCell, bench))
                        return "Rim3D_WorkSpotReserved".Translate().ToString();
                }
                if (kind == "Build")
                {
                    var failure = _view.SkillFailure(SkillDefOf.Construction, target.def.constructionSkillPrerequisite);
                    if (failure != null) return failure;
                    failure = _view.SkillFailure(SkillDefOf.Artistic, target.def.artisticSkillPrerequisite);
                    if (failure != null) return failure;
                    if (target is IConstructible project && !_view.NeedsFloorRemoval(target))
                    {
                        string missing = "";
                        foreach (var cost in project.TotalMaterialCost())
                        {
                            int count = project.ThingCountNeeded(cost.thingDef);
                            if (count > 0) missing += (missing.Length == 0 ? "" : ", ") + cost.thingDef.LabelCap + " ×" + count;
                        }
                        if (missing.Length > 0) return "Rim3D_MissingResources".TranslateBindings(missing).ToString();
                    }
                    var blocking = GenConstruct.FirstBlockingThing(target, pawn);
                    if (blocking != null) return "Rim3D_BuildCellOccupied".Translate(blocking.LabelCap).ToString();
                }
                if (kind == "Harvest" && target is Plant plant)
                {
                    if (!plant.def.plant.Harvestable || plant.def.plant.harvestedThingDef == null)
                        return "Rim3D_PlantNoHarvestHint".Translate().ToString();
                    if (plant.Blighted) return "Rim3D_PlantBlightedHint".Translate().ToString();
                    if (!plant.HarvestableNow) return "Rim3D_PlantNotReady".Translate().ToString();
                }
                if (kind == "Repair")
                {
                    if (!RepairUtility.PawnCanRepairEver(pawn, target)) return "Rim3D_CannotRepair".Translate().ToString();
                    if (target.HitPoints >= target.MaxHitPoints) return "Rim3D_RepairNotNeeded".Translate().ToString();
                    if (_view.map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) != null) return "Rim3D_RepairDeconstructConflict".Translate().ToString();
                }
                if (kind == "Bill" && target is Building_WorkTable table)
                {
                    if (table.TryGetComp<CompBreakdownable>()?.BrokenDown == true) return "Rim3D_WorkTableBroken".Translate().ToString();
                    if (!table.CanWorkWithoutPower && table.TryGetComp<CompPowerTrader>()?.PowerOn != true) return "Rim3D_WorkTableNoPower".Translate().ToString();
                    if (!table.CanWorkWithoutFuel && table.TryGetComp<CompRefuelable>()?.HasFuel != true) return "Rim3D_WorkTableNoFuel".Translate().ToString();
                    if (!pawn.CanReserveSittableOrSpot(table.InteractionCell, table)) return "Rim3D_WorkSpotReserved".Translate().ToString();
                    if (table.BillStack.Count == 0) return "Rim3D_HintRecipes".Translate().ToString();
                    string recipeFailure = null;
                    bool possibleRecipe = false;
                    foreach (var bill in table.BillStack)
                    {
                        var failure = _view.RecipeFailure(bill.recipe);
                        if (failure == null) { possibleRecipe = true; break; }
                        if (recipeFailure == null) recipeFailure = bill.LabelCap + ": " + failure;
                    }
                    if (!possibleRecipe) return recipeFailure;
                }
                return null;
            }

            internal bool NeedsFloorRemoval(Thing target) => target is Blueprint_Build &&
                target.def.entityDefToBuild is TerrainDef && _view.map.terrainGrid.CanRemoveTopLayerAt(target.Position);

            internal List<WorkGiverDef> ManualWorkers(string kind)
            {
                if (_manualWorkers.TryGetValue(kind, out var workers)) return workers;
                workers = new List<WorkGiverDef>();
                foreach (var def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                {
                    string name = def.giverClass?.Name;
                    bool match = kind == "Build" ? name == "WorkGiver_ConstructFinishFrames" :
                        kind == "RemoveFloor" ? name == "WorkGiver_ConstructDeliverResourcesToBlueprints" :
                        kind == "Cut" || kind == "Harvest" ? name == "WorkGiver_PlantsCut" :
                        kind == "Sow" ? name == "WorkGiver_GrowerSow" :
                        kind == "Mine" ? name == "WorkGiver_Miner" : kind == "Haul" ? name == "WorkGiver_HaulGeneral" :
                        kind == "Repair" ? name == "WorkGiver_Repair" : kind == "Bill" ? name == "WorkGiver_DoBill" :
                        kind == "SmoothWall" ? name == "WorkGiver_ConstructSmoothWall" :
                        kind == "Research" ? name == "WorkGiver_Researcher" :
                        kind == "Deconstruct" && name == "WorkGiver_Deconstruct";
                    if (match) workers.Add(def);
                }
                _manualWorkers.Add(kind, workers);
                return workers;
            }

            internal Job ManualWorkJob(Thing target, string kind)
            {
                if (Rim3DMultiplayer.LocalInput) return null;
                _view.workFailure = _view.ManualActionFailure(target, kind);
                if (_view.workFailure != null) return null;
                bool removeFloor = kind == "Build" && _view.NeedsFloorRemoval(target);
                string nativeFailure = null;
                var previousPawn = FloatMenuMakerMap.makingFor;
                string previousReason = JobFailReason.Reason;
                string previousJobString = JobFailReason.CustomJobString;
                bool previousSilent = JobFailReason.Silent;
                try
                {
                    // Ask for the same detailed ingredient diagnostics as the game's context menu.
                    FloatMenuMakerMap.makingFor = _view.ControlledPawn;
                    foreach (var def in _view.ManualWorkers(removeFloor ? "RemoveFloor" : kind))
                    {
                        if (!(def.Worker is WorkGiver_Scanner worker)) continue;
                        if (kind == "Bill" && worker is WorkGiver_DoBill billWorker && !billWorker.ThingIsUsableBillGiver(target)) continue;
                        if (def.workType != null && _view.ControlledPawn.WorkTypeIsDisabled(def.workType))
                        { nativeFailure = "Rim3D_WorkSkillDisabled".Translate(def.workType.LabelCap).ToString(); continue; }
                        JobFailReason.Clear();
                        var missing = worker.MissingRequiredCapacity(_view.ControlledPawn);
                        if (missing != null) { nativeFailure = "Rim3D_MissingCapacity".Translate(missing.LabelCap).ToString(); continue; }
                        Job job;
                        if (kind == "Bill") job = _view.ManualBillJob(worker, (Building_WorkTable)target);
                        else if (kind == "SmoothWall") job = worker.HasJobOnCell(_view.ControlledPawn, target.Position, true)
                            ? worker.JobOnCell(_view.ControlledPawn, target.Position, true) : null;
                        else if (kind == "Repair") job = JobMaker.MakeJob(JobDefOf.Repair, target);
                        else if (kind == "Cut" || kind == "Harvest")
                            job = JobMaker.MakeJob(kind == "Harvest" ? JobDefOf.Harvest : JobDefOf.CutPlant, target);
                        else if ((kind == "Research" || kind == "Deconstruct" || (kind == "Build" && def.giverClass.Name != "WorkGiver_ConstructFinishFrames")) &&
                            !worker.HasJobOnThing(_view.ControlledPawn, target, true)) job = null;
                        else job = worker.JobOnThing(_view.ControlledPawn, target, true);
                        if (removeFloor && job != null && job.def != JobDefOf.RemoveFloor)
                        {
                            JobMaker.ReturnToPool(job);
                            job = null;
                        }
                        if (job != null)
                        {
                            if (kind == "Research" && job.def == JobDefOf.Research)
                            {
                                var manualResearch = DefDatabase<JobDef>.GetNamedSilentFail("Recovery_Rim3DResearch");
                                if (manualResearch == null)
                                {
                                    JobMaker.ReturnToPool(job);
                                    nativeFailure = "Rim3D_ReloadResearchJob".Translate().ToString();
                                    continue;
                                }
                                job.def = manualResearch;
                            }
                            if (kind == "Build" && job.def != JobDefOf.RemoveFloor)
                            {
                                var manualBuild = DefDatabase<JobDef>.GetNamedSilentFail("Recovery_Rim3DBuild");
                                if (manualBuild == null)
                                {
                                    JobMaker.ReturnToPool(job);
                                    nativeFailure = "Rim3D_ReloadBuildJob".Translate().ToString();
                                    continue;
                                }
                                job.def = manualBuild;
                            }
                            job.workGiverDef = def;
                            return job;
                        }
                        if (JobFailReason.HaveReason) nativeFailure = JobFailReason.CustomJobString.NullOrEmpty()
                            ? JobFailReason.Reason : JobFailReason.CustomJobString + ": " + JobFailReason.Reason;
                    }
                }
                finally
                {
                    FloatMenuMakerMap.makingFor = previousPawn;
                    JobFailReason.Clear();
                    if (previousReason != null) JobFailReason.Is(previousReason, previousJobString);
                    if (previousSilent) JobFailReason.IsSilent();
                }
                _view.workFailure = nativeFailure ?? (kind == "Haul" ? "Rim3D_NoStorage".Translate().ToString() :
                    kind == "Bill" ? "Rim3D_NoSuitableBill".Translate().ToString() :
                    kind == "Build" ? "Rim3D_ConstructionBlocked".Translate().ToString() :
                    kind == "Mine" ? "Rim3D_CannotMineNow".Translate().ToString() :
                    kind == "Deconstruct" ? "Rim3D_CannotDeconstructNow".Translate().ToString() :
                    "Rim3D_NoWorkForAction".Translate().ToString());
                return null;
            }

            internal Job ManualBillJob(WorkGiver_Scanner worker, Building_WorkTable table)
            {
                // Only relax automatic assignments during synchronous job creation.
                // The original bill is retained for ingredients, unfinished products and completion accounting.
                var states = new List<Action>();
                try
                {
                    foreach (var bill in table.BillStack)
                    {
                        if (_view.RecipeFailure(bill.recipe) != null)
                        {
                            bool suspended = bill.suspended;
                            states.Add(() => bill.suspended = suspended);
                            bill.suspended = true;
                            continue;
                        }
                        var restriction = bill.PawnRestriction;
                        bool slaves = bill.SlavesOnly, mechs = bill.MechsOnly, nonMechs = bill.NonMechsOnly;
                        var range = bill.allowedSkillRange;
                        int searchTick = bill.nextTickToSearchForIngredients;
                        states.Add(() =>
                        {
                            if (restriction != null) bill.SetPawnRestriction(restriction);
                            else if (slaves) bill.SetAnySlaveRestriction();
                            else if (mechs) bill.SetAnyMechRestriction();
                            else if (nonMechs) bill.SetAnyNonMechRestriction();
                            else bill.SetAnyPawnRestriction();
                            bill.allowedSkillRange = range;
                            bill.nextTickToSearchForIngredients = searchTick;
                        });
                        bill.SetAnyPawnRestriction();
                        bill.allowedSkillRange = new IntRange(0, 20);
                        bill.nextTickToSearchForIngredients = -1;
                        if (bill is Bill_Production production)
                        {
                            bool suspended = bill.suspended, paused = production.paused;
                            var mode = production.repeatMode;
                            states.Add(() => { bill.suspended = suspended; production.paused = paused; production.repeatMode = mode; });
                            bill.suspended = production.paused = false;
                            production.repeatMode = BillRepeatModeDefOf.Forever;
                        }
                    }
                    return worker.JobOnThing(_view.ControlledPawn, table, true);
                }
                finally { for (int i = states.Count - 1; i >= 0; i--) states[i](); }
            }

            internal bool HasCarryPlacement(Thing target) => target is Building_Bed && _view.ControlledPawn?.carryTracker?.CarriedThing is Pawn ||
                target is Building_Grave && _view.ControlledPawn?.carryTracker?.CarriedThing is Corpse;

            internal string CarryPlacementLabel(Thing target, bool hint = false) =>
                (target is Building_Grave ? hint ? "Rim3D_HintBuryCarried" : "Rim3D_BuryCarried" :
                    hint ? "Rim3D_HintPutCarriedInBed" : "Rim3D_PutCarriedInBed").TranslateBindings().ToString();

            internal string CarryPlacementFailure(Thing target)
            {
                var pawn = _view.ControlledPawn;
                if (!_view.HasCarryPlacement(target) || !_view.CanTouchCarryTarget(target) || target.Faction != pawn.Faction ||
                    target.IsForbidden(pawn) || target.IsBurning() ||
                    !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                    return "Rim3D_ActionUnavailable".Translate().ToString();
                if (target is Building_Grave grave)
                    return grave.Accepts(pawn.carryTracker.CarriedThing) && pawn.CanReserve(grave) ? null :
                        "Rim3D_GraveUnavailable".Translate().ToString();
                var bed = (Building_Bed)target;
                var takee = (Pawn)pawn.carryTracker.CarriedThing;
                if (takee.Dead || !takee.Downed || takee.ownership == null ||
                    !RestUtility.CanUseBedEver(takee, bed.def) || bed.ForPrisoners != takee.IsPrisoner ||
                    bed.ForSlaves != takee.IsSlave || bed.CompAssignableToPawn.IdeoligionForbids(takee) ||
                    !bed.CompAssignableToPawn.CanAssignTo(takee).Accepted ||
                    !pawn.CanReach(bed, PathEndMode.OnCell, Danger.Some) ||
                    !pawn.CanReserve(bed, bed.SleepingSlotsCount, 0) ||
                    bed.ForPrisoners && !bed.Position.IsInPrisonCell(_view.map) ||
                    takee.HarmedByVacuum && bed.Position.GetVacuum(_view.map) >= .5f)
                    return "Rim3D_BedUnavailable".Translate().ToString();
                if (bed.Medical)
                    return RestUtility.IsValidBedFor(bed, takee, pawn, checkSocialProperness: false) ? null :
                        "Rim3D_BedUnavailable".Translate().ToString();
                if (ModsConfig.AnomalyActive && takee.IsMutant && takee.needs?.rest == null && takee.mutant.Def.entitledToMedicalCare)
                    return "Rim3D_BedUnavailable".Translate().ToString();
                int slot = bed.OwnersForReading.IndexOf(takee);
                if (slot < 0)
                {
                    slot = 0;
                    int remainingOwners = Mathf.Min(bed.OwnersForReading.Count, bed.SleepingSlotsCount - 1);
                    for (int i = 0; i < bed.SleepingSlotsCount; i++)
                        if (bed.GetCurOccupant(i) != null) return "Rim3D_BedUnavailable".Translate().ToString();
                    for (int i = 0; i < remainingOwners; i++)
                        if (bed.OwnersForReading[i].thingIDNumber < takee.thingIDNumber) slot++;
                }
                return slot >= 0 && bed.GetCurOccupant(slot) == null && pawn.CanReserve(bed.GetSleepingSlotPos(slot)) ? null :
                    "Rim3D_BedUnavailable".Translate().ToString();
            }

            internal void PlaceCarriedFPV(Thing target)
            {
                string failure = _view.CarryPlacementFailure(target);
                if (failure != null) { _view.SayAction(failure); return; }
                var pawn = _view.ControlledPawn;
                var carried = pawn.carryTracker.CarriedThing;
                _view.ResetCarryInput();
                _view.constructionDef = null;
                if (pawn.Drafted) _view.controller.SetCombat(false);
                var job = JobMaker.MakeJob(target is Building_Grave ? JobDefOf.HaulToContainer : JobDefOf.Rescue, carried, target);
                job.count = 1;
                if (!_view.controller.OrderAction(job)) { _view.SayAction(_view.controller.Status); return; }
                if (carried is Pawn takee && takee.Faction != Faction.OfPlayer && takee.HostFaction != Faction.OfPlayer &&
                    takee.guest != null && !takee.IsWildMan() && takee.DevelopmentalStage != DevelopmentalStage.Baby && !takee.IsPrisoner)
                {
                    takee.guest.SetGuestStatus(Faction.OfPlayer);
                    QuestUtility.SendQuestTargetSignals(takee.questTags, "Rescued", takee.Named("SUBJECT"));
                }
            }

        }
    }
}

