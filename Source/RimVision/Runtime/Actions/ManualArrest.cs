using RimWorld;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        string ArrestLabel(Pawn target) => _arrestActions.ArrestLabel(target);

        bool CanArrestFPV(Pawn target) => _arrestActions.CanArrestFPV(target);

        void ArrestFPV(Pawn target) => _arrestActions.ArrestFPV(target);

        readonly ArrestActions _arrestActions;

        sealed partial class ArrestActions
        {
            readonly MapView _view;

            internal ArrestActions(MapView view)
            {
                _view = view;
            }

            internal string ArrestLabel(Pawn target) => "TryToArrest".Translate(target.LabelCap, target,
                target.GetAcceptArrestChance(_view.ControlledPawn).ToStringPercent()).ToString();

            internal bool CanArrestFPV(Pawn target)
            {
                var pawn = _view.ControlledPawn;
                return target != null && target != pawn && !target.Dead && _view.CanTouchCarryTarget(target) &&
                    target.guest != null && GenAI.CanBeArrestedBy(target, pawn) &&
                    !(target.Downed && target.guilt?.IsGuilty == true) &&
                    pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
                    pawn.carryTracker?.CarriedThing == null && pawn.CanReserve(target) &&
                    !QuestUtility.InSameExtraFaction(pawn, target, ExtraFactionType.HomeFaction, null) &&
                    !QuestUtility.InSameExtraFaction(pawn, target, ExtraFactionType.MiniFaction, null);
            }

            internal void ArrestFPV(Pawn target)
            {
                if (!_view.CanArrestFPV(target)) { _view.SayAction("Rim3D_ActionUnavailable".Translate().ToString()); return; }
                if (_view.SyncAction(Rim3DAction.Arrest, target)) return;
                var pawn = _view.ControlledPawn;
                var bed = RestUtility.FindBedFor(target, pawn, false, false, GuestStatus.Prisoner) ??
                    RestUtility.FindBedFor(target, pawn, false, true, GuestStatus.Prisoner);
                if (bed == null)
                {
                    _view.SayAction("CannotArrest".Translate().ToString() + ": " + "NoPrisonerBed".Translate().ToString());
                    return;
                }
                _view.ResetCarryInput();
                _view.constructionDef = null;
                var job = JobMaker.MakeJob(JobDefOf.Arrest, target, bed);
                job.count = 1;
                if (!_view.controller.OrderAction(job)) { _view.SayAction(_view.controller.Status); return; }
                // The native driver handles resistance, faction relations and prisoner status.
                if (!Rim3DMultiplayer.Active && target.Faction != null &&
                    (target.Faction != Faction.OfPlayer && !target.Faction.Hidden || QuestUtility.IsQuestLodger(target)))
                    TutorUtility.DoModalDialogIfNotKnown(ConceptDefOf.ArrestingCreatesEnemies,
                        target.GetAcceptArrestChance(pawn).ToStringPercent());
            }

        }
    }
}
