using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    // Manual treatment uses only the medicine already in the hands. Native
    // finalization preserves tend quality, self-tend penalties, XP and effects.
    public sealed class JobDriver_Rim3DTend : JobDriver
    {
        Pawn Patient => job.targetA.Pawn;
        public override string GetReport() => (Patient == pawn ? "Rim3D_SelfTendReport" : "Rim3D_TendReport")
            .Translate(Patient?.LabelShort ?? "").ToString();

        internal static bool CanTreat(Pawn doctor, Pawn patient)
        {
            if (doctor == null || patient == null || patient.Dead || !patient.Spawned ||
                patient.Map != doctor.Map || patient.RaceProps.IsMechanoid ||
                !patient.health.HasHediffsNeedingTend() ||
                !doctor.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) ||
                doctor.skills?.GetSkill(SkillDefOf.Medicine).TotallyDisabled == true ||
                doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)) return false;
            if (patient != doctor && patient.HostileTo(doctor) && !patient.Downed && !patient.IsPrisoner) return false;
            var held = doctor.carryTracker?.CarriedThing;
            return held == null || held is Medicine;
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed) => Patient == pawn ||
            pawn.Reserve(Patient, job, errorOnFailed: errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            SetFinalizerJob(condition => PawnControl.Eligible(pawn) && pawn.jobs.jobQueue.Count > 0 &&
                pawn.jobs.jobQueue[0].job.def.defName == "Recovery_Rim3DControl"
                ? pawn.jobs.jobQueue.Dequeue().job : null);
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Patient.Dead || Patient.Map != pawn.Map ||
                !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) ||
                Patient != pawn && (Patient.IsForbidden(pawn) ||
                    !ReachabilityImmediate.CanReachImmediate(pawn, Patient, PathEndMode.Touch)));

            var wait = ToilMaker.MakeToil("Rim3DTend");
            wait.defaultCompleteMode = ToilCompleteMode.Delay;
            wait.activeSkill = () => SkillDefOf.Medicine;
            wait.handlingFacing = true;
            wait.initAction = () =>
            {
                pawn.pather.StopDead();
                if (!Patient.health.HasHediffsNeedingTend())
                { EndJobWith(JobCondition.Succeeded); return; }
                var medicine = pawn.carryTracker?.CarriedThing as Medicine;
                if (medicine != null && Patient.playerSettings != null &&
                    !Patient.playerSettings.medCare.AllowsMedicine(medicine.def)) medicine = null;
                job.SetTarget(TargetIndex.B, medicine != null ? new LocalTargetInfo(medicine) : LocalTargetInfo.Invalid);
                wait.defaultDuration = ticksLeftThisToil = Mathf.Max(1, Mathf.CeilToInt(600f /
                    Mathf.Max(.01f, pawn.GetStatValue(StatDefOf.MedicalTendSpeed))));
            };
            wait.tickAction = () =>
            {
                if (Patient != pawn) pawn.rotationTracker.FaceTarget(Patient);
                if (!Patient.health.HasHediffsNeedingTend()) EndJobWith(JobCondition.Succeeded);
            };
            wait.WithProgressBarToilDelay(TargetIndex.A);
            yield return wait;
            yield return Toils_Tend.FinalizeTend(Patient);
            yield return Toils_Jump.JumpIf(wait, () => Patient.health.HasHediffsNeedingTend());
        }
    }

    public sealed partial class MapView
    {
        bool CanTendFPV(Pawn patient) => _medicalActions.CanTendFPV(patient);

        string TendLabel(Pawn patient) => _medicalActions.TendLabel(patient);

        void TendFPV(Pawn patient) => _medicalActions.TendFPV(patient);

        readonly MedicalActions _medicalActions;

        sealed partial class MedicalActions
        {
            readonly MapView _view;

            internal MedicalActions(MapView view)
            {
                _view = view;
            }

            internal bool CanTendFPV(Pawn patient) => PawnControl.Eligible(_view.ControlledPawn) && !_view.controller.IsResting &&
                JobDriver_Rim3DTend.CanTreat(_view.ControlledPawn, patient) &&
                (patient == _view.ControlledPawn || _view.CanTouchCarryTarget(patient) && !patient.IsForbidden(_view.ControlledPawn) &&
                    _view.ControlledPawn.CanReserve(patient));

            internal string TendLabel(Pawn patient)
            {
                bool medicine = _view.ControlledPawn?.carryTracker?.CarriedThing is Medicine held &&
                    (patient.playerSettings == null || patient.playerSettings.medCare.AllowsMedicine(held.def));
                return (patient == _view.ControlledPawn
                    ? medicine ? "Rim3D_SelfTendMedicine" : "Rim3D_SelfTendHands"
                    : medicine ? "Rim3D_TendMedicine" : "Rim3D_TendHands").Translate().ToString();
            }

            internal void TendFPV(Pawn patient)
            {
                if (!_view.CanTendFPV(patient)) { _view.SayAction("Rim3D_ActionUnavailable".Translate().ToString()); return; }
                if (_view.SyncAction(Rim3DAction.Tend, patient)) return;
                _view.ResetCarryInput();
                _view.constructionDef = null;
                if (_view.ControlledPawn.Drafted) _view.controller.SetCombat(false);
                var job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("Recovery_Rim3DTend"), patient);
                if (!_view.controller.OrderAction(job)) _view.SayAction(_view.controller.Status);
                else _view.statusMenu = _view.actionMenu = false;
            }

        }
    }
}
