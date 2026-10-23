using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        const int HeldEat = -1, HeldWear = -2, HeldEquip = -3, HeldRead = -4;

        bool CanActOnHeldItem(Thing thing) => _carryActions.CanActOnHeldItem(thing);

        string HeldItemActionLabel(Thing thing, int action) => _carryActions.HeldItemActionLabel(thing, action);

        int PrimaryHeldItemAction(Thing thing) => _carryActions.PrimaryHeldItemAction(thing);

        void UseHeldItem() => _carryActions.UseHeldItem();

        void OpenHeldItemActions() => _actionMenus.OpenHeldItemActions();

        bool HasEquipmentActions(ThingWithComps item) => _actionMenus.HasEquipmentActions(item);

        void AddUnavailableEquipmentOption(List<FloatMenuOption> options, Pawn pawn) => _actionMenus.AddUnavailableEquipmentOption(options, pawn);

        void OpenEquipmentItemActions(Pawn pawn, ThingWithComps item) => _actionMenus.OpenEquipmentItemActions(pawn, item);

        bool CanActOnEquipment(Pawn pawn, ThingWithComps item) => _carryActions.CanActOnEquipment(pawn, item);

        IEnumerable<Gizmo> EquipmentItemGizmos(Pawn pawn, ThingWithComps item) => _actionMenus.EquipmentItemGizmos(pawn, item);

        IEnumerable<Gizmo> EquipmentAbilityGizmos(Pawn pawn, Ability ability) => _actionMenus.EquipmentAbilityGizmos(pawn, ability);

        void AddEquipmentActions(List<FloatMenuOption> options) => _actionMenus.AddEquipmentActions(options);

        void AddEquipmentItemActions(List<FloatMenuOption> options, Pawn pawn, ThingWithComps item) => _actionMenus.AddEquipmentItemActions(options, pawn, item);

        bool IsGrenadeAttack(ThingWithComps item, Command command) => _actionMenus.IsGrenadeAttack(item, command);

        void RunEquipmentAction(Pawn pawn, ThingWithComps item, int index, Command original) => _carryActions.RunEquipmentAction(pawn, item, index, original);

        void RequestHeldItemAction(Thing thing, int action) => _carryActions.RequestHeldItemAction(thing, action);

        void ExecuteHeldItemAction(Thing thing, int action) => _carryActions.ExecuteHeldItemAction(thing, action);

        sealed partial class ActionMenus
        {
            internal void OpenHeldItemActions()
            {
                var thing = _view.ControlledPawn?.carryTracker?.CarriedThing;
                bool selfTend = _view.CanTendFPV(_view.ControlledPawn);
                if (!PawnControl.Eligible(_view.ControlledPawn)) return;
                if (_view.controller.IsResting) { _view.SayAction("Rim3D_StandUpHint".Translate()); return; }
                var options = new List<FloatMenuOption>();
                if (selfTend) options.Add(new FloatMenuOption(_view.TendLabel(_view.ControlledPawn), () => _view.TendFPV(_view.ControlledPawn)));
                int count = (thing as ThingWithComps)?.AllComps.Count ?? 0;
                for (int i = HeldRead; i < count; i++)
                {
                    int action = i;
                    string label = _view.HeldItemActionLabel(thing, action);
                    if (label != null) options.Add(new FloatMenuOption(label, () => _view.RequestHeldItemAction(thing, action)));
                }
                if (PawnControl.CanStoreCargo(_view.ControlledPawn, thing))
                    options.Add(new FloatMenuOption("Rim3D_StoreShortcut".TranslateBindings(), () =>
                    { if (_view.CanActOnHeldItem(thing)) _view.StoreFPVCarry(); }));
                if (thing != null) options.Add(new FloatMenuOption("Rim3D_Drop".Translate(), () =>
                { if (_view.CanActOnHeldItem(thing)) _view.DropFPVCarry(); }));
                _view.AddEquipmentActions(options);
                if (options.Count == 0) _view.AddUnavailableEquipmentOption(options, _view.ControlledPawn);
                _view.actionMenu = _view.constructionMenu = false;
                _view.ResetCarryInput();
                _view.ReleaseMouse();
                Find.WindowStack.Add(new FloatMenu(options));
            }

            internal bool HasEquipmentActions(ThingWithComps item) =>
                item.def.Verbs?.Count > 0 || item.def.apparel?.abilities?.Count > 0 ||
                item.TryGetComp<CompEquippable>() != null;

            internal void AddUnavailableEquipmentOption(List<FloatMenuOption> options, Pawn pawn)
            {
                options.Add(new FloatMenuOption((pawn.Drafted ? "Rim3D_NoEquipmentActions" :
                    "Rim3D_EquipmentCombatHint").TranslateBindings(), null));
            }

            internal void OpenEquipmentItemActions(Pawn pawn, ThingWithComps item)
            {
                if (!_view.CanActOnEquipment(pawn, item))
                {
                    if (_view.controller.IsResting) _view.SayAction("Rim3D_StandUpHint".Translate());
                    return;
                }
                var options = new List<FloatMenuOption>();
                _view.AddEquipmentItemActions(options, pawn, item);
                if (options.Count == 0) _view.AddUnavailableEquipmentOption(options, pawn);
                _view.ResetCarryInput();
                _view.ReleaseMouse();
                Find.WindowStack.Add(new FloatMenu(options));
            }

            internal IEnumerable<Gizmo> EquipmentItemGizmos(Pawn pawn, ThingWithComps item)
            {
                if (!_view.CanActOnEquipment(pawn, item)) yield break;
                if (item is Apparel apparel)
                {
                    foreach (var gizmo in apparel.GetWornGizmos()) yield return gizmo;
                    foreach (var ability in apparel.AllAbilitiesForReading)
                        foreach (var gizmo in _view.EquipmentAbilityGizmos(pawn, ability)) yield return gizmo;
                }
                else
                {
                    var equippable = item.TryGetComp<CompEquippable>();
                    if (equippable != null)
                    {
                        foreach (var gizmo in equippable.CompGetEquippedGizmosExtra()) yield return gizmo;
                        foreach (var command in equippable.GetVerbsCommands()) yield return command;
                    }
                    var ability = item.TryGetComp<CompEquippableAbility>()?.AbilityForReading;
                    if (ability != null)
                        foreach (var gizmo in _view.EquipmentAbilityGizmos(pawn, ability)) yield return gizmo;
                }
            }

            internal IEnumerable<Gizmo> EquipmentAbilityGizmos(Pawn pawn, Ability ability)
            {
                if ((pawn.Drafted || ability.def.displayGizmoWhileUndrafted) && ability.GizmosVisible())
                    foreach (var command in ability.GetGizmos()) yield return command;
                foreach (var gizmo in ability.GetGizmosExtra()) yield return gizmo;
            }

            internal void AddEquipmentActions(List<FloatMenuOption> options)
            {
                var pawn = _view.ControlledPawn;
                if (pawn?.equipment != null)
                    foreach (var item in pawn.equipment.AllEquipmentListForReading)
                        _view.AddEquipmentItemActions(options, pawn, item);
                if (pawn?.apparel != null)
                    foreach (var item in pawn.apparel.WornApparel)
                        _view.AddEquipmentItemActions(options, pawn, item);
            }

            internal void AddEquipmentItemActions(List<FloatMenuOption> options, Pawn pawn, ThingWithComps item)
            {
                int index = 0;
                foreach (var gizmo in _view.EquipmentItemGizmos(pawn, item))
                {
                    int commandIndex = index++;
                    if (!(gizmo is Command command) || !command.Visible) continue;
                    if (_view.IsGrenadeAttack(item, command)) continue;
                    string actionLabel = command.LabelCap.NullOrEmpty() ? "Rim3D_UseEquipment".Translate().ToString() : command.LabelCap;
                    string label = string.Equals(item.LabelCap, actionLabel, StringComparison.OrdinalIgnoreCase)
                        ? actionLabel : item.LabelCap + ": " + actionLabel;
                    if (command is Command_Toggle toggle && toggle.isActive?.Invoke() == true) label = "✓ " + label;
                    if (command.Disabled && !command.disabledReason.NullOrEmpty()) label += " · " + command.disabledReason;
                    options.Add(new FloatMenuOption(label, command.Disabled ? (Action)null :
                        () => _view.RunEquipmentAction(pawn, item, commandIndex, command)) { tooltip = command.Desc });
                }
            }

            internal bool IsGrenadeAttack(ThingWithComps item, Command command)
            {
                if (!(command is Command_VerbTarget target) || !(target.verb is Verb_LaunchProjectile) ||
                    item.def.thingCategories == null) return false;
                foreach (var category in item.def.thingCategories)
                    for (var parent = category; parent != null; parent = parent.parent)
                        if (parent.defName == "Grenades") return true;
                return false;
            }

        }

        sealed partial class CarryActions
        {
            internal bool CanActOnHeldItem(Thing thing) => thing != null && !thing.Destroyed &&
                _view.ControlledPawn != null && PawnControl.Eligible(_view.ControlledPawn) && !_view.controller.IsResting &&
                _view.ControlledPawn.carryTracker?.CarriedThing == thing;

            internal string HeldItemActionLabel(Thing thing, int action)
            {
                if (!_view.CanActOnHeldItem(thing)) return null;
                var pawn = _view.ControlledPawn;
                if (action == HeldEat) return _view.CanIngestFPV(thing) ? IngestLabel(thing) : null;
                if (action == HeldWear)
                    return thing is Apparel apparel && pawn.apparel != null &&
                        ApparelUtility.HasPartsToWear(pawn, thing.def) && !pawn.apparel.WouldReplaceLockedApparel(apparel) &&
                        EquipmentUtility.CanEquip(thing, pawn, out _) ? "Rim3D_WearHeld".Translate().ToString() : null;
                if (action == HeldEquip)
                    return thing.def.IsWeapon && pawn.equipment != null && thing.TryGetComp<CompEquippable>() != null &&
                        EquipmentUtility.CanEquip(thing, pawn, out _) ? "Rim3D_EquipHeld".Translate().ToString() : null;
                if (action == HeldRead)
                    return thing is Book book && BookUtility.CanReadBook(book, pawn, out _) ?
                        "Rim3D_ReadHeld".Translate().ToString() : null;
                if (thing is ThingWithComps item && action >= 0 && action < item.AllComps.Count &&
                    item.AllComps[action] is CompUsable usable && usable.Props.useJob != null &&
                    usable.CanBeUsedBy(pawn, false, true))
                    return usable.Props.useLabel.NullOrEmpty() ? "Rim3D_UseHeld".Translate().ToString() :
                        usable.Props.useLabel.Formatted(thing);
                return null;
            }

            internal int PrimaryHeldItemAction(Thing thing)
            {
                for (int action = HeldEat; action >= HeldRead; action--)
                    if (_view.HeldItemActionLabel(thing, action) != null) return action;
                if (thing is ThingWithComps item)
                    for (int i = 0; i < item.AllComps.Count; i++)
                        if (_view.HeldItemActionLabel(thing, i) != null) return i;
                return int.MinValue;
            }

            internal void UseHeldItem()
            {
                var thing = _view.ControlledPawn?.carryTracker?.CarriedThing;
                if ((thing == null || thing is Medicine) && _view.CanTendFPV(_view.ControlledPawn))
                { _view.TendFPV(_view.ControlledPawn); return; }
                int action = _view.PrimaryHeldItemAction(thing);
                if (action != int.MinValue) _view.RequestHeldItemAction(thing, action);
            }

            internal bool CanActOnEquipment(Pawn pawn, ThingWithComps item) =>
                _view.FirstPerson && pawn == _view.ControlledPawn && _view.controller.Active && !_view.controller.IsResting &&
                item != null && !item.Destroyed && (item is Apparel apparel
                    ? pawn.apparel?.WornApparel.Contains(apparel) == true
                    : pawn.equipment?.AllEquipmentListForReading.Contains(item) == true);

            internal void RunEquipmentAction(Pawn pawn, ThingWithComps item, int index, Command original)
            {
                if (!_view.CanActOnEquipment(pawn, item)) return;
                foreach (var gizmo in _view.EquipmentItemGizmos(pawn, item))
                {
                    if (index-- != 0) continue;
                    if (!(gizmo is Command command) || command.GetType() != original.GetType() ||
                        command.LabelCap != original.LabelCap || !command.Visible || command.Disabled ||
                        command is Command_VerbTarget verb && original is Command_VerbTarget oldVerb && verb.verb != oldVerb.verb ||
                        command is Command_Ability ability && original is Command_Ability oldAbility && ability.Ability != oldAbility.Ability)
                        return;
                    _view.statusMenu = _view.actionMenu = _view.constructionMenu = false;
                    _view.ResetCarryInput();
                    _view.ResetManualFireInput();
                    _view.CancelSimulationConstruction();
                    if (Rim3DMultiplayer.LocalInput)
                        Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand { Action = Rim3DAction.CancelConstruction });
                    command.ProcessInput(new Event { type = EventType.MouseDown, button = 0 });
                    return;
                }
            }

            internal void RequestHeldItemAction(Thing thing, int action)
            {
                if (_view.HeldItemActionLabel(thing, action) == null) return;
                // Preserve the item's native warnings before sending a synchronized command.
                if (action >= 0 && thing is ThingWithComps item)
                {
                    var confirmation = new StringBuilder();
                    foreach (var effect in item.GetComps<CompUseEffect>())
                    {
                        var message = effect.ConfirmMessage(_view.ControlledPawn);
                        if (message.NullOrEmpty()) continue;
                        if (confirmation.Length > 0) confirmation.AppendLine();
                        confirmation.Append(message.ToString());
                    }
                    if (confirmation.Length > 0)
                    {
                        _view.ReleaseMouse();
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(confirmation.ToString(),
                            () => _view.ExecuteHeldItemAction(thing, action)));
                        return;
                    }
                }
                _view.ExecuteHeldItemAction(thing, action);
            }

            internal void ExecuteHeldItemAction(Thing thing, int action)
            {
                if (_view.HeldItemActionLabel(thing, action) == null) return;
                if (_view.SyncAction(Rim3DAction.UseHeldItem, thing, action)) return;
                _view.ResetCarryInput();
                _view.constructionDef = null;
                if (action == HeldEat) { _view.IngestFPV(thing); return; }
                if (action == HeldEquip) { _view.SwapHandsAndEquipment(); return; }
                var pawn = _view.ControlledPawn;
                JobDef jobDef = action == HeldWear ? JobDefOf.Wear : action == HeldRead ? JobDefOf.Reading :
                    ((CompUsable)((ThingWithComps)thing).AllComps[action]).Props.useJob;
                if (pawn.Drafted) _view.controller.SetCombat(false);
                _view.controller.ReturnToManual();
                // Stock wear/use jobs require a spawned target. Place it at the pawn's
                // feet and let the native job handle duration, replacement and effects.
                if (!pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out var dropped))
                { _view.SayAction("Rim3D_NoDropSpace".Translate().ToString()); return; }
                _view.MarkNeighbors(dropped.Position);
                dropped.SetForbidden(false, false);
                var job = JobMaker.MakeJob(jobDef, dropped);
                job.count = 1;
                if (_view.controller.OrderAction(job)) { _view.statusMenu = _view.actionMenu = false; return; }
                // A failed reservation must leave the item available in the hands.
                if (dropped.Spawned && pawn.carryTracker.CarriedThing == null) pawn.carryTracker.TryStartCarry(dropped);
                _view.SayAction(_view.controller.Status);
            }

        }
    }
}
