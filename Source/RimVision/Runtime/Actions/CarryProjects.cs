using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        const float CarryHoldSeconds = .4f;


        FPVCarryWindow carryWindow;

        static readonly FieldInfo actionToilsField = typeof(JobDriver).GetField("toils", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        sealed class FPVCarryWindow : Window
        {
            readonly MapView owner;
            readonly Pawn pawn;
            readonly Thing target;
            int count;
            public override Vector2 InitialSize => new Vector2(430, 190);
            bool Valid => owner.FirstPerson && owner.ControlledPawn == pawn && owner.CanTouchCarryTarget(target);

            public FPVCarryWindow(MapView owner, Thing target)
            {
                this.owner = owner;
                this.target = target;
                pawn = owner.ControlledPawn;
                count = target.stackCount;
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
                count = Mathf.Clamp(count, 1, target.stackCount);
                Widgets.Label(new Rect(0, 0, rect.width - 30, 28), target.LabelCap);
                Widgets.Label(new Rect(0, 34, rect.width, 26), "Rim3D_TakeCount".Translate(count, target.stackCount).ToString());
                count = Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(0, 68, rect.width, 26), count, 1, target.stackCount));
                if (Widgets.ButtonText(new Rect(0, 112, rect.width * .5f - 4, 32), "Rim3D_Take".Translate().ToString()))
                { owner.TakeFPVCarry(target, count); Close(false); }
                if (Widgets.ButtonText(new Rect(rect.width * .5f + 4, 112, rect.width * .5f - 4, 32), "Rim3D_Cancel".Translate().ToString())) Close(false);
            }
        }

        void ResetCarryInput() => _carryActions.ResetCarryInput();

        void UpdateCarryInput() => _carryActions.UpdateCarryInput();

        bool CanTouchCarryTarget(Thing target) => _carryActions.CanTouchCarryTarget(target);

        void TakeFPVCarry(Thing target, int count) => _carryActions.TakeFPVCarry(target, count);

        IntVec3 FPVDropCell() => _carryActions.FPVDropCell();

        void DropFPVCarry(IntVec3? requestedCell = null) => _carryActions.DropFPVCarry(requestedCell);

        bool StoreFPVCarry() => _carryActions.StoreFPVCarry();

        void TakeInventoryItem(Thing thing) => _carryActions.TakeInventoryItem(thing);

        void MergeFPVInventory() => _carryActions.MergeFPVInventory();

        void DropInventoryItem(Thing thing) => _carryActions.DropInventoryItem(thing);

        void RemoveFPVApparel(Thing thing) => _carryActions.RemoveFPVApparel(thing);

        void DropEquippedFPVWeapon() => _carryActions.DropEquippedFPVWeapon();

        void SwapHandsAndEquipment() => _carryActions.SwapHandsAndEquipment();

        static bool ProjectMaterialsReady(IConstructible project)
        {
            foreach (var cost in project.TotalMaterialCost())
                if (project.ThingCountNeeded(cost.thingDef) > 0) return false;
            return true;
        }

        void DepositFPVResource(Thing target) => _carryActions.DepositFPVResource(target);

        void DrawProjectOverlay(Rect viewport) => _hudPresentation.DrawProjectOverlay(viewport);

        void DrawControlledActionProgress(Rect viewport) => _hudPresentation.DrawControlledActionProgress(viewport);

        sealed partial class HudPresentation
        {
            readonly HashSet<Thing> _nearbyProjects = new HashSet<Thing>();

            internal void DrawProjectOverlay(Rect viewport)
            {
                if (_view._constructionPlansHidden) return;
                foreach (var target in _nearbyProjects)
                {
                    if (target is Blueprint_Install) continue;
                    if (!target.Spawned || target.Destroyed || !(target is IConstructible project)) continue;
                    // A row of wall plans should not cover the scene with identical material bars.
                    if (_view.FirstPerson && IsConstructionWall(target.def.entityDefToBuild) && target != _view.aimedThing &&
                        target != _view.ControlledPawn?.CurJob?.targetA.Thing) continue;
                    var position = target.TrueCenter();
                    position.y = _view.ConstructionHeight(target.def.entityDefToBuild) + .25f;
                    if (!_view.OverlayPoint(position, viewport, out var point, 7f)) continue;
                    var costs = project.TotalMaterialCost();
                    float top = point.y - costs.Count * 20 - 42;
                    _view.Badge(new Rect(point.x - 140, top, 280, 20), target.def.entityDefToBuild.LabelCap.ToString(), _view.activityStyle, _view.nearbyAlpha);
                    int total = 0, delivered = 0;
                    for (int i = 0; i < costs.Count; i++)
                    {
                        var cost = costs[i];
                        int added = Mathf.Clamp(cost.count - project.ThingCountNeeded(cost.thingDef), 0, cost.count);
                        total += cost.count; delivered += added;
                        _view.Badge(new Rect(point.x - 140, top + 20 + i * 20, 280, 20),
                            cost.thingDef.LabelCap + " " + added + "/" + cost.count, _view.activityStyle, _view.nearbyAlpha);
                    }
                    // Material delivery and actual construction are distinct phases.
                    float progress = target is Frame frame ? Mathf.Clamp01(frame.PercentComplete) : 0;
                    bool ready = delivered >= total;
                    var bar = new Rect(point.x - 140, point.y - 20, 280, 20);
                    _view.ProgressBar(bar, ready ? progress : (total == 0 ? 1 : (float)delivered / total), _view.nearbyAlpha);
                    _view.Badge(new Rect(point.x - 140, point.y + 2, 280, 20), ready ?
                        "Rim3D_BuildProgress".TranslateBindings(Mathf.RoundToInt(progress * 100)).ToString() :
                        "Rim3D_MaterialsProgress".Translate(delivered, total).ToString(), _view.activityStyle, _view.nearbyAlpha);
                }
            }

            internal void DrawControlledActionProgress(Rect viewport)
            {
                if (!_view.FirstPerson) return;
                var pawn = _view.ControlledPawn;
                if (pawn?.stances?.curStance is Stance_Warmup warmup && warmup.ticksLeft > 0)
                {
                    int elapsed = Mathf.Max(0, Find.TickManager.TicksGame - warmup.startedTick);
                    var warmupBar = new Rect(viewport.center.x - 160, viewport.yMax - 116, 320, 22);
                    _view.ProgressBar(warmupBar, (float)elapsed / Mathf.Max(1, elapsed + warmup.ticksLeft));
                    string label = warmup.verb?.EquipmentSource?.LabelCap ?? warmup.verb?.ReportLabel;
                    _view.Badge(new Rect(warmupBar.x, warmupBar.y - 24, warmupBar.width, 22),
                        "Rim3D_EquipmentWarmup".Translate(label ?? pawn.LabelShort,
                            (warmup.ticksLeft / 60f).ToString("0.0")).ToString(), _view.activityStyle);
                    return;
                }
                if (!_view.controller.IsActing) return;
                var job = pawn.CurJob;
                float progress = -1;
                // Work motes often link to the project rather than the worker.
                foreach (var mote in _view.progressMotes)
                {
                    var linked = mote.link1.Target.Thing;
                    if (mote.Alpha > 0 && linked != null &&
                        (linked == pawn || linked == job.targetA.Thing || linked == job.targetB.Thing))
                    { progress = mote.progress; break; }
                }
                if (job.targetA.Thing is Frame frame && ProjectMaterialsReady(frame)) progress = frame.PercentComplete;
                if (progress < 0 && pawn.stances?.curStance is Stance_Busy busy && busy.ticksLeft > 0)
                {
                    int elapsed = Mathf.Max(0, Find.TickManager.TicksGame - busy.startedTick);
                    progress = (float)elapsed / Mathf.Max(1, elapsed + busy.ticksLeft);
                }
                if (progress < 0 && pawn.jobs.curDriver is JobDriver driver &&
                    actionToilsField?.GetValue(driver) is List<Toil> toils && driver.CurToilIndex >= 0 && driver.CurToilIndex < toils.Count)
                {
                    var toil = toils[driver.CurToilIndex];
                    if (toil.defaultCompleteMode == ToilCompleteMode.Delay && toil.defaultDuration > 0)
                        progress = 1f - (float)driver.ticksLeftThisToil / toil.defaultDuration;
                }
                if (progress < 0) return;
                var bar = new Rect(viewport.center.x - 160, viewport.yMax - 116, 320, 22);
                _view.ProgressBar(bar, Mathf.Clamp01(progress));
                _view.Badge(new Rect(bar.x, bar.y - 24, bar.width, 22), _view.ActionReport(pawn), _view.activityStyle);
            }

        }

        readonly CarryActions _carryActions;

        sealed partial class CarryActions
        {
            readonly MapView _view;

            internal CarryActions(MapView view)
            {
                _view = view;
            }

            Thing _carryPressTarget;

            float _carryPressedAt;

            internal void ResetCarryInput() { _carryPressTarget = null; _carryPressedAt = 0; }

            internal void UpdateCarryInput()
            {
                if (ViewBindings.Pressed(ViewAction.Interact))
                {
                    _view.ResetCarryInput();
                    _view.PickRecreationTarget();
                    if (_view.aimedThing?.def.category == ThingCategory.Item)
                    {
                        if (!_view.CanTouchCarryTarget(_view.aimedThing)) { _view.SayAction("Rim3D_MoveCloserToItem".Translate().ToString()); return; }
                        _carryPressTarget = _view.aimedThing;
                        _carryPressedAt = Time.unscaledTime;
                    }
                    else _view.InteractFPV();
                }
                if (_carryPressTarget == null) return;
                if (!_view.CanTouchCarryTarget(_carryPressTarget)) { _view.ResetCarryInput(); return; }
                if ((ViewBindings.Held(ViewAction.Interact) || ViewBindings.Released(ViewAction.Interact)) && Time.unscaledTime - _carryPressedAt >= CarryHoldSeconds)
                {
                    var target = _carryPressTarget;
                    _view.ResetCarryInput();
                    _view.constructionMenu = _view.actionMenu = _view.statusMenu = false;
                    _view.carryWindow?.Close(false);
                    _view.ReleaseMouse();
                    _view.carryWindow = new FPVCarryWindow(_view, target);
                    Find.WindowStack.Add(_view.carryWindow);
                }
                else if (ViewBindings.Released(ViewAction.Interact))
                {
                    var target = _carryPressTarget;
                    _view.ResetCarryInput();
                    _view.TakeFPVCarry(target, target.stackCount);
                }
                else if (!ViewBindings.Held(ViewAction.Interact)) _view.ResetCarryInput();
            }

            internal bool CanTouchCarryTarget(Thing target) => target != null && !target.Destroyed && target.Spawned &&
                target.Map == _view.map && PawnControl.Eligible(_view.ControlledPawn) && !_view.controller.IsResting && !_view.map.fogGrid.IsFogged(target.Position) &&
                ReachabilityImmediate.CanReachImmediate(_view.ControlledPawn, target, PathEndMode.Touch);

            internal static SlotGroup StorageSlots(Thing target)
            {
                if (!(target is Building building)) return null;
                if (target is ISlotGroupParent storage) return storage.GetSlotGroup();
                foreach (var comp in building.AllComps)
                    if (comp is ISlotGroupParent parent) return parent.GetSlotGroup();
                return null;
            }

            internal bool CanOpenStorage(Thing target) => target?.Faction == Faction.OfPlayer &&
                CanTouchCarryTarget(target) && StorageSlots(target) != null;

            bool CanTakeStoredItem(Thing target)
            {
                if (target == null || !target.Spawned || target.Map != _view.map || target.def.category != ThingCategory.Item ||
                    _view.map.fogGrid.IsFogged(target.Position)) return false;
                var slots = target.GetSlotGroup();
                var storage = slots?.parent as Thing ?? (slots?.parent as ThingComp)?.parent;
                return CanOpenStorage(storage) && StorageSlots(storage) == slots;
            }

            internal void TakeFPVCarry(Thing target, int count)
            {
                TakeItem(target, count, false);
            }

            internal bool CanTakePaste(Building_NutrientPasteDispenser dispenser)
            {
                var pawn = _view.ControlledPawn;
                return CanTouchCarryTarget(dispenser) && pawn.carryTracker != null &&
                    pawn.carryTracker.CarriedThing == null && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
                    !dispenser.IsForbidden(pawn) && !dispenser.IsBurning() && pawn.CanReserve(dispenser) && dispenser.CanDispenseNow;
            }

            internal void TakePaste(Building_NutrientPasteDispenser dispenser)
            {
                var pawn = _view.ControlledPawn;
                if (pawn.carryTracker?.CarriedThing != null)
                { _view.SayAction("Rim3D_HandsOccupiedHint".TranslateBindings().ToString()); return; }
                if (!CanTakePaste(dispenser))
                { _view.SayAction("Rim3D_PasteUnavailable".Translate().ToString()); return; }
                _view.constructionDef = null;
                if (pawn.Drafted) _view.controller.SetCombat(false);
                _view.controller.ReturnToManual();
                var meal = dispenser.TryDispenseFood();
                if (meal == null)
                { _view.SayAction("Rim3D_PasteUnavailable".Translate().ToString()); return; }
                if (!pawn.carryTracker.TryStartCarry(meal))
                {
                    GenPlace.TryPlaceThing(meal, pawn.Position, _view.map, ThingPlaceMode.Near);
                    _view.SayAction("Rim3D_TakeFailed".Translate().ToString());
                }
                _view.map.resourceCounter.UpdateResourceCounts();
                _view.MarkNeighbors(dispenser.Position);
            }

            internal void TakeStoredItem(Thing target, int count)
            {
                TakeItem(target, count, true);
            }

            void TakeItem(Thing target, int count, bool fromStorage)
            {
                if (_view.SyncAction(Rim3DAction.Take, target, count, fromStorage ? "Storage" : null)) return;
                if (!(fromStorage ? CanTakeStoredItem(target) : CanTouchCarryTarget(target)) ||
                    target.def.category != ThingCategory.Item || count <= 0) return;
                var pawn = _view.ControlledPawn;
                var carried = pawn.carryTracker?.CarriedThing;
                if (pawn.carryTracker == null || (carried != null && !carried.CanStackWith(target)))
                { _view.SayAction("Rim3D_HandsOccupiedHint".TranslateBindings().ToString()); return; }
                count = Mathf.Min(count, target.stackCount);
                if (carried != null)
                {
                    count = Mathf.Min(count, carried.def.stackLimit - carried.stackCount);
                    if (count <= 0) return;
                }
                if (!FPVRespectReservationsPatch.ReservePickup(pawn, target))
                { _view.SayAction("Rim3D_ItemReserved".Translate().ToString()); return; }
                _view.constructionDef = null;
                if (pawn.Drafted) _view.controller.SetCombat(false);
                _view.controller.ReturnToManual();
                var cell = target.Position;
                // The one-argument API accepts the actual stack, without the hauling
                // job's capacity clamp. The tracker remains the single saved owner.
                var portion = target.SplitOff(count);
                bool taken = carried != null
                    ? carried.TryAbsorbStack(portion, respectStackLimit: true)
                    : pawn.carryTracker.TryStartCarry(portion);
                if (!taken)
                {
                    GenPlace.TryPlaceThing(portion, cell, _view.map, ThingPlaceMode.Near);
                    _view.SayAction("Rim3D_TakeFailed".Translate().ToString());
                }
                else
                {
                    _view.map.resourceCounter.UpdateResourceCounts();
                }
                _view.MarkNeighbors(cell);
                _view.aimedThing = null;
            }

            internal IntVec3 FPVDropCell()
            {
                var pawn = _view.ControlledPawn;
                var origin = pawn.Position.ToVector3Shifted();
                _view.PickFPVTarget();
                var ray = new Ray(_view.CameraMapPosition, _view.camera.transform.forward);
                Vector3 target;
                if (_view.aimedCell.IsValid) target = _view.aimedCell.ToVector3Shifted();
                else if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance))
                    target = ray.GetPoint(distance);
                else target = origin + new Vector3(ray.direction.x, 0, ray.direction.z).normalized * 2f;
                var offset = target - origin;
                offset.y = 0;
                offset = Vector3.ClampMagnitude(offset, 2f);
                var result = pawn.Position;
                // A short bounded scan keeps the drop in front of walls and on-map.
                for (int i = 1; i <= 8; i++)
                {
                    var cell = (origin + offset * (i / 8f)).ToIntVec3();
                    if (cell == result) continue;
                    if (!cell.InBounds(_view.map) || !cell.Standable(_view.map) || _view.map.fogGrid.IsFogged(cell) ||
                        !GenSight.LineOfSight(pawn.Position, cell, _view.map)) break;
                    if ((cell.ToVector3Shifted() - origin).sqrMagnitude <= 4.01f) result = cell;
                }
                return result;
            }

            internal void DropFPVCarry(IntVec3? requestedCell = null)
            {
                var pawn = _view.ControlledPawn;
                var carried = pawn?.carryTracker?.CarriedThing;
                if (carried == null) return;
                var cell = requestedCell ?? _view.FPVDropCell();
                if (Rim3DMultiplayer.LocalInput)
                {
                    Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand { Action = Rim3DAction.Drop, Target = carried, Cell = cell });
                    return;
                }
                if (!cell.InBounds(_view.map) || (cell.ToVector3Shifted() - pawn.Position.ToVector3Shifted()).sqrMagnitude > 4.01f ||
                    !cell.Standable(_view.map) || !GenSight.LineOfSight(pawn.Position, cell, _view.map)) cell = pawn.Position;
                _view.ResetCarryInput();
                _view.constructionDef = null;
                string label = carried.LabelCap.ToString();
                // Ending a stock job can drop its cargo during cleanup. Drop the
                // captured item first, before replacing that job with manual control.
                var container = pawn.carryTracker.innerContainer;
                if ((!GenSpawn.WouldWipeAnythingWith(cell, carried.def.defaultPlacingRot, carried.def, _view.map, thing => true) &&
                        container.TryDrop(carried, cell, _view.map, ThingPlaceMode.Direct, out var dropped)) ||
                    (cell != pawn.Position && container.Contains(carried) &&
                        !GenSpawn.WouldWipeAnythingWith(pawn.Position, carried.def.defaultPlacingRot, carried.def, _view.map, thing => true) &&
                        container.TryDrop(carried, pawn.Position, _view.map, ThingPlaceMode.Direct, out dropped)))
                {
                    _view.controller.ReturnToManual();
                    _view.MarkNeighbors(dropped?.Position ?? pawn.Position);
                    _view.SayAction("Rim3D_DropFromHands".Translate(label).ToString());
                }
                else _view.SayAction("Rim3D_NoDropSpace".Translate().ToString());
            }

            internal bool StoreFPVCarry()
            {
                if (_view.SyncAction(Rim3DAction.Store)) return true;
                var pawn = _view.ControlledPawn;
                var carried = pawn?.carryTracker?.CarriedThing;
                if (carried == null) return true;
                if (pawn.inventory == null || _view.controller.IsResting) return false;
                if (!PawnControl.CanStoreCargo(pawn, carried))
                { _view.SayAction("Rim3D_InventoryOverweight".Translate().ToString()); return false; }
                _view.controller.ReturnToManual();
                int count = carried.stackCount;
                string label = carried.LabelCap.ToString();
                int moved = PawnControl.TransferCargo(pawn.carryTracker.innerContainer,
                    pawn.inventory.innerContainer, carried, count);
                _view.SayAction(moved == count ? "Rim3D_StoredItem".Translate(label).ToString() : "Rim3D_StoreIncomplete".Translate().ToString());
                return moved == count;
            }

            internal void TakeInventoryItem(Thing thing)
            {
                if (_view.SyncAction(Rim3DAction.TakeInventory, thing)) return;
                var pawn = _view.ControlledPawn;
                if (_view.controller.IsResting || pawn.inventory?.innerContainer.Contains(thing) != true) return;
                var carried = pawn.carryTracker?.CarriedThing;
                if (pawn.carryTracker == null) return;
                if (carried != null && !carried.CanStackWith(thing))
                { _view.SayAction("Rim3D_HandsOccupied".Translate().ToString()); return; }
                int count = Mathf.Min(thing.stackCount, thing.def.stackLimit - (carried?.stackCount ?? 0));
                if (count <= 0) return;
                _view.controller.ReturnToManual();
                int moved = PawnControl.TransferCargo(pawn.inventory.innerContainer,
                    pawn.carryTracker.innerContainer, thing, count);
                if (moved <= 0) _view.SayAction("Rim3D_TakeFailed".Translate().ToString());
            }

            internal void MergeFPVInventory()
            {
                if (_view.SyncAction(Rim3DAction.MergeInventory)) return;
                var inventory = _view.ControlledPawn?.inventory?.innerContainer;
                if (inventory == null || _view.controller.IsResting) return;
                _view.controller.ReturnToManual();
                // Explicit action only: no inventory scans or mutations during GUI drawing.
                for (int i = inventory.Count - 1; i > 0; i--)
                {
                    var source = inventory[i];
                    if (source.def.stackLimit <= 1) continue;
                    for (int j = 0; j < i && !source.Destroyed; j++)
                    {
                        var target = inventory[j];
                        if (target.def == source.def && target.stackCount < target.def.stackLimit && target.CanStackWith(source))
                            target.TryAbsorbStack(source, respectStackLimit: true);
                    }
                }
            }

            internal void DropInventoryItem(Thing thing)
            {
                if (_view.SyncAction(Rim3DAction.DropInventory, thing)) return;
                var pawn = _view.ControlledPawn;
                if (_view.controller.IsResting || pawn.inventory?.innerContainer.Contains(thing) != true) return;
                string label = thing.LabelCap.ToString();
                _view.controller.ReturnToManual();
                if (pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, _view.map, ThingPlaceMode.Near, out var dropped))
                { _view.MarkNeighbors(dropped.Position); _view.SayAction("Rim3D_DropFromInventory".Translate(label).ToString()); }
                else _view.SayAction("Rim3D_NoDropSpace".Translate().ToString());
            }

            internal void RemoveFPVApparel(Thing thing)
            {
                if (_view.SyncAction(Rim3DAction.RemoveApparel, thing)) return;
                var pawn = _view.ControlledPawn;
                if (!(thing is Apparel apparel) || pawn?.apparel == null || _view.controller.IsResting ||
                    !pawn.apparel.WornApparel.Contains(apparel) || pawn.apparel.IsLocked(apparel)) return;
                _view.controller.ReturnToManual();
                if (pawn.apparel.TryDrop(apparel, out var dropped, pawn.Position, forbid: false))
                {
                    _view.nextGearSummary = 0;
                    _view.MarkNeighbors(dropped.Position);
                    _view.SayAction("Rim3D_RemovedApparel".Translate(dropped.LabelCap).ToString());
                }
                else _view.SayAction("Rim3D_NoDropSpace".Translate().ToString());
            }

            internal void DropEquippedFPVWeapon()
            {
                if (_view.SyncAction(Rim3DAction.DropWeapon)) return;
                var pawn = _view.ControlledPawn;
                var weapon = pawn?.equipment?.Primary;
                if (weapon == null || _view.controller.IsResting) return;
                _view.controller.ReturnToManual();
                if (pawn.equipment.TryDropEquipment(weapon, out var dropped, pawn.Position, forbid: false))
                { _view.MarkNeighbors(dropped.Position); _view.SayAction("Rim3D_DropFromEquipment".Translate(dropped.LabelCap).ToString()); }
                else _view.SayAction("Rim3D_NoWeaponDropSpace".Translate().ToString());
            }

            internal void SwapHandsAndEquipment()
            {
                if (_view.SyncAction(Rim3DAction.Swap)) return;
                var pawn = _view.ControlledPawn;
                if (_view.controller.IsResting || pawn?.equipment == null || pawn.carryTracker == null)
                { _view.SayAction("Rim3D_CannotChangeEquipment".Translate().ToString()); return; }
                var held = pawn.carryTracker.CarriedThing;
                var previous = pawn.equipment.Primary;
                if (held == null && previous == null)
                { _view.SayAction("Rim3D_SlotsEmpty".Translate().ToString()); return; }
                if (held != null && (!(held is ThingWithComps) || !held.def.IsWeapon ||
                    held.TryGetComp<CompEquippable>() == null))
                { _view.SayAction("Rim3D_CannotEquip".Translate(held.LabelCap).ToString()); return; }
                float previousMass = previous?.GetStatValue(StatDefOf.Mass) ?? 0;
                float incomingMass = held == null ? 0 : held.stackCount * held.GetStatValue(StatDefOf.Mass);
                if (held != null && MassUtility.GearAndInventoryMass(pawn) - previousMass + incomingMass > MassUtility.Capacity(pawn))
                { _view.SayAction("Rim3D_SwapOverweight".Translate().ToString()); return; }
                if (held != null && held.stackCount > 1 && pawn.inventory == null)
                { _view.SayAction("Rim3D_NoWeaponInventory".Translate().ToString()); return; }
                _view.constructionDef = null;
                if (pawn.Drafted) _view.controller.SetCombat(false);
                _view.controller.ReturnToManual();
                var taken = held == null ? null : pawn.carryTracker.innerContainer.Take(held, held.stackCount) as ThingWithComps;
                if (held != null && taken == null) { _view.SayAction("Rim3D_TakeWeaponFailed".Translate().ToString()); return; }
                var incoming = taken != null && taken.stackCount > 1 ? taken.SplitOff(1) as ThingWithComps : taken;
                var remainder = taken != incoming ? taken : null;
                if (previous != null) pawn.equipment.Remove(previous);
                // Keep the remainder's identity until the swap commits, so rollback
                // can reunite it with the incoming weapon without touching other stacks.
                if (remainder != null && !pawn.inventory.innerContainer.TryAdd(remainder, canMergeWithExistingStacks: false))
                {
                    if (previous != null) pawn.equipment.AddEquipment(previous);
                    remainder.TryAbsorbStack(incoming, respectStackLimit: false);
                    pawn.carryTracker.TryStartCarry(remainder);
                    _view.SayAction("Rim3D_StoreRemainderFailed".Translate().ToString()); return;
                }
                if (incoming != null) pawn.equipment.AddEquipment(incoming);
                if (previous != null && !pawn.carryTracker.TryStartCarry(previous))
                {
                    if (incoming != null) pawn.equipment.Remove(incoming);
                    pawn.equipment.AddEquipment(previous);
                    if (remainder != null)
                    {
                        pawn.inventory.innerContainer.Remove(remainder);
                        remainder.TryAbsorbStack(incoming, respectStackLimit: false);
                        pawn.carryTracker.TryStartCarry(remainder);
                    }
                    else if (incoming != null) pawn.carryTracker.TryStartCarry(incoming);
                    _view.SayAction("Rim3D_SwapFailed".Translate().ToString()); return;
                }
                _view.SayAction("Rim3D_SwapResult".Translate(previous?.LabelCap.ToString() ?? "Rim3D_Empty".Translate().ToString(), incoming?.LabelCap.ToString() ?? "Rim3D_Empty".Translate().ToString()).ToString());
                if (remainder != null)
                {
                    string label = remainder.LabelCap.ToString();
                    var inventory = pawn.inventory.innerContainer;
                    for (int i = 0; i < inventory.Count && !remainder.Destroyed; i++)
                    {
                        var target = inventory[i];
                        if (target != remainder && target.def == remainder.def &&
                            target.stackCount < target.def.stackLimit && target.CanStackWith(remainder))
                            target.TryAbsorbStack(remainder, respectStackLimit: true);
                    }
                    _view.SayFloatingItem("Rim3D_StoredRemainder".Translate(label).ToString());
                }
            }

            internal void DepositFPVResource(Thing target)
            {
                if (_view.SyncAction(Rim3DAction.Deposit, target)) return;
                var pawn = _view.ControlledPawn;
                var carried = pawn?.carryTracker?.CarriedThing;
                if (carried == null || !(target is IConstructible project) || !_view.CanTouchCarryTarget(target) ||
                    target.Faction != pawn.Faction || project.ThingCountNeeded(carried.def) <= 0 || !pawn.CanReserve(target)) return;
                // Convert through the game's API to retain stuff, style, faction and
                // reservations. Blocked blueprints are left intact.
                if (target is Blueprint_Build blueprint)
                {
                    if (_view.NeedsFloorRemoval(target)) { _view.BeginConstruction(target); return; }
                    if (GenConstruct.FirstBlockingThing(target, pawn) != null ||
                        !blueprint.TryReplaceWithSolidThing(pawn, out var solid, out _)) return;
                    target = solid;
                    _view.aimedThing = solid;
                    _view.MarkNeighbors(solid.Position);
                }
                if (!(target is Frame frame)) return;
                int needed = Mathf.Max(0, frame.ThingCountNeeded(carried.def));
                int amount = Mathf.Min(needed, carried.stackCount);
                if (amount <= 0) return;
                _view.constructionDef = null;
                _view.controller.ReturnToManual();
                int transferred = pawn.carryTracker.innerContainer.TryTransferToContainer(carried, frame.resourceContainer, amount);
                if (transferred <= 0) return;
                _view.map.resourceCounter.UpdateResourceCounts();
                _view.MarkNeighbors(frame.Position);
                _view.SayAction("Rim3D_DeliveredResources".Translate(carried.def.LabelCap, transferred).ToString() +
                    (ProjectMaterialsReady(frame) ? "Rim3D_StartBuildingHint".TranslateBindings().ToString() : ""));
            }

        }
    }
}

