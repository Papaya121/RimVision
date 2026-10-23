using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public enum PawnControlState { Manual, Action, GoingToBed, Sleeping, LyingAwake }

    public sealed class JobDriver_Rim3DResearch : JobDriver_Research
    {
        protected override IEnumerable<Toil> MakeNewToils()
        {
            foreach (var toil in base.MakeNewToils())
            {
                if (toil.defaultCompleteMode == ToilCompleteMode.Delay && toil.activeSkill != null)
                    toil.defaultCompleteMode = ToilCompleteMode.Never;
                yield return toil;
            }
        }
    }

    public sealed class JobDriver_Rim3DBuild : JobDriver_ConstructFinishFrame
    {
        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Finalizers retain cargo through native cleanup and bypass the
            // one-tick Wait_MaintainPosture job that otherwise drops it.
            SetFinalizerJob(condition => PawnControl.Eligible(pawn) && pawn.jobs.jobQueue.Count > 0 &&
                pawn.jobs.jobQueue[0].job.def.defName == "Recovery_Rim3DControl"
                ? pawn.jobs.jobQueue.Dequeue().job : null);
            foreach (var toil in base.MakeNewToils())
            {
                if (toil.defaultCompleteMode == ToilCompleteMode.Delay && toil.activeSkill != null)
                    toil.defaultCompleteMode = ToilCompleteMode.Never;
                yield return toil;
            }
        }
    }

    // Retain native bed reservations, rest gain and wake conditions, but leave
    // the pawn in bed after waking until the player requests movement.
    public sealed class JobDriver_Rim3DSleep : JobDriver_LayDown
    {
        bool wokeUp;
        public override bool LookForOtherJobs => false;
        public override bool CanSleep => !wokeUp;
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            var bed = job.targetA.Thing as Building_Bed;
            if (bed == null) return base.TryMakePreToilReservations(errorOnFailed);
            if (!job.targetB.IsValid) job.targetB = PawnControl.SleepingSlot(pawn, bed);
            return job.targetB.IsValid && base.TryMakePreToilReservations(errorOnFailed) &&
                pawn.Reserve(job.targetB, job, errorOnFailed: errorOnFailed);
        }
        protected override IEnumerable<Toil> MakeNewToils()
        {
            foreach (var toil in base.MakeNewToils())
            {
                // Manual rest is temporary; do not reassign the bed's owners.
                if (toil.debugName == "ClaimBedIfNonMedical") toil.initAction = () => { };
                if (toil.debugName == "GotoBed")
                {
                    var initialize = toil.initAction;
                    var tickInterval = toil.tickIntervalAction;
                    toil.initAction = () =>
                    {
                        if (PawnControl.ForPawn(pawn) == null) { initialize(); return; }
                        // Manual movement approaches the bed's edge. The lay-down
                        // step then places the pawn in its native sleeping slot.
                        pawn.pather.StartPath(job.targetA, PathEndMode.Touch);
                    };
                    toil.tickIntervalAction = delta =>
                    {
                        // Vanilla reads the path follower's destination here;
                        // manual movement tracks its destination separately.
                        if (PawnControl.ForPawn(pawn) == null) tickInterval?.Invoke(delta);
                    };
                }
                yield return toil;
            }
        }
        public override Toil LayDownToil(bool hasBed)
        {
            var toil = base.LayDownToil(hasBed);
            var initialize = toil.initAction;
            toil.initAction = () =>
            {
                if (hasBed && PawnControl.ForPawn(pawn) != null)
                {
                    var bed = job.targetA.Thing as Building_Bed;
                    if (bed == null || !bed.Spawned || bed.Map != pawn.Map ||
                        !ReachabilityImmediate.CanReachImmediate(pawn, bed, PathEndMode.Touch))
                    { EndJobWith(JobCondition.Incompletable); return; }
                    var slot = job.targetB.Cell;
                    if (!slot.IsValid || !bed.OccupiedRect().Contains(slot))
                    { EndJobWith(JobCondition.Incompletable); return; }
                    var occupant = bed.GetCurOccupantAt(slot);
                    if (!slot.IsValid || occupant != null && occupant != pawn)
                    { EndJobWith(JobCondition.Incompletable); return; }
                    pawn.pather.StopDead();
                    pawn.Position = slot;
                }
                initialize();
                wokeUp = !job.forceSleep;
                asleep = job.forceSleep && !wokeUp;
                // Force only the initial sleep, never suppress native waking.
                job.forceSleep = false;
            };
            var tick = toil.tickAction;
            var awakeToil = Toils_LayDown.LayDown(TargetIndex.A, hasBed, false, canSleep: false);
            toil.AddFinishAction(() => awakeToil.ReturnToPool());
            toil.tickAction = () =>
            {
                if (PawnControl.ForPawn(pawn) == null &&
                    (wokeUp || pawn.needs?.rest == null || pawn.needs.rest.CurLevelPercentage >= 1f))
                {
                    EndJobWith(JobCondition.Succeeded);
                    return;
                }
                if (wokeUp)
                {
                    awakeToil.actor = pawn;
                    awakeToil.tickAction();
                    return;
                }
                bool wasAsleep = asleep;
                tick();
                if (wasAsleep && !asleep) wokeUp = true;
            };
            return toil;
        }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref wokeUp, "rim3DWokeUp");
        }
    }

    public sealed class JobDriver_Rim3DControl : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;
        protected override IEnumerable<Toil> MakeNewToils()
        {
            var toil = ToilMaker.MakeToil("Rim3DControl");
            toil.defaultCompleteMode = ToilCompleteMode.Never;
            toil.handlingFacing = true;
            toil.initAction = () => pawn.pather.StopDead();
            toil.tickIntervalAction = delta =>
            {
                if (Rim3DMultiplayer.Active && Rim3DMultiplayer.GetController?.Invoke(pawn)?.Pawn != pawn)
                {
                    EndJobWith(JobCondition.InterruptForced);
                    return;
                }
                pawn.GainComfortFromCellIfPossible(delta, chairsOnly: true);
            };
            toil.AddFinishAction(() => pawn.pather.StopDead());
            yield return toil;
        }
        // A blocked direction stops movement, not the whole control session.
        public override void Notify_PatherFailed() => pawn.pather.StopDead();
    }

    public sealed partial class PawnControl : IDisposable, IExposable
    {
        public Pawn Pawn { get; private set; }
        Job controlJob;
        JobDef controlDef;
        Job actionJob;
        int controlJobId = -1;
        int actionJobId = -1;
        JobQueue suspendedJobs;
        bool followingAction;
        LocalTargetInfo manualDestination = LocalTargetInfo.Invalid;
        PathEndMode manualPathEndMode;
        int manualPathJobId = -1;
        public bool WaitingForManualApproach => manualDestination.IsValid && Pawn?.CurJob?.loadID == manualPathJobId;

        internal static PawnControl ForPawn(Pawn pawn)
        {
            if (pawn == null) return null;
            var control = Rim3DMultiplayer.Active ? Rim3DMultiplayer.GetController?.Invoke(pawn)
                : Rim3DPreview.Instance?.ActiveWindow?.View?.Controller;
            return control?.Pawn == pawn && control.Active && !control.presentationOnly && !control.HasNativeControl ? control : null;
        }

        internal void WaitForManualApproach(LocalTargetInfo destination, PathEndMode endMode)
        {
            Pawn.pather.StopDead();
            manualDestination = (LocalTargetInfo)GenPath.ResolvePathMode(Pawn, destination.ToTargetInfo(Pawn.Map), ref endMode);
            manualPathEndMode = endMode;
            manualPathJobId = Pawn.CurJob?.loadID ?? -1;
            if (!manualDestination.IsValid)
            {
                Pawn.jobs.curDriver?.Notify_PatherFailed();
                return;
            }
            ResumeAtManualDestination();
            if (WaitingForManualApproach) Status = "Rim3D_ApproachManually".TranslateBindings().ToString();
        }

        void ResumeAtManualDestination()
        {
            if (!WaitingForManualApproach) { manualDestination = LocalTargetInfo.Invalid; return; }
            if (manualDestination.ThingDestroyed)
            {
                manualDestination = LocalTargetInfo.Invalid;
                Pawn.jobs.curDriver?.Notify_PatherFailed();
                return;
            }
            if (!ReachabilityImmediate.CanReachImmediate(Pawn, manualDestination, manualPathEndMode)) return;
            manualDestination = LocalTargetInfo.Invalid;
            Status = "";
            Pawn.jobs.curDriver?.Notify_PatherArrived();
        }

        // Correct a small gap to a work spot without handing movement back to
        // vanilla pathing. Only a short, collision-free straight approach is allowed.
        bool ApproachNearbyAction(float deltaSeconds)
        {
            if (deltaSeconds <= 0 || !WaitingForManualApproach || IsResting || Pawn.CurJob?.def == JobDefOf.AttackMelee ||
                Pawn.CurJob?.def == JobDefOf.AttackStatic || Find.TickManager.Paused || Pawn.stances.FullBodyBusy)
                return false;
            var map = Pawn.Map;
            var origin = Cell(Position);
            var target = manualDestination;
            if (!target.Cell.InBounds(map) || target.HasThing && (!target.Thing.Spawned || target.Thing.Map != map))
                return false;
            var nearestTargetCell = target.HasThing ? target.Thing.OccupiedRect().ClosestCellTo(origin) : target.Cell;
            if (Math.Abs(nearestTargetCell.x - origin.x) > 3 || Math.Abs(nearestTargetCell.z - origin.z) > 3)
                return false;
            const float maxApproach = 1.5f;
            float bestDistance = maxApproach * maxApproach;
            Vector3 best = Position;
            bool found = false;
            // At most 25 local cells; no path request, allocations or map-wide search.
            for (int z = origin.z - 2; z <= origin.z + 2; z++)
                for (int x = origin.x - 2; x <= origin.x + 2; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!cell.InBounds(map) || cell == origin || map.fogGrid.IsFogged(cell)) continue;
                    // Touch work stops beside its target, even if its own cell is walkable.
                    if (manualPathEndMode == PathEndMode.Touch &&
                        (target.HasThing ? GenAdj.IsInside(cell, target.Thing) : cell == target.Cell)) continue;
                    var point = new Vector3(Mathf.Clamp(Position.x, x + .05f, x + .95f), 0,
                        Mathf.Clamp(Position.z, z + .05f, z + .95f));
                    float distance = (point - Position).sqrMagnitude;
                    if (distance >= bestDistance || !ReachabilityImmediate.CanReachImmediate(
                        cell, target, map, manualPathEndMode, Pawn)) continue;
                    int steps = Mathf.CeilToInt(Mathf.Sqrt(distance) / .06f);
                    bool clear = true;
                    for (int step = 1; step <= steps; step++)
                    {
                        var sample = Vector3.Lerp(Position, point, step / (float)steps);
                        var sampleCell = Cell(sample);
                        if (!CanOccupy(sample, false) || manualPathEndMode == PathEndMode.Touch &&
                            (target.HasThing ? GenAdj.IsInside(sampleCell, target.Thing) : sampleCell == target.Cell))
                        { clear = false; break; }
                    }
                    if (!clear) continue;
                    best = point;
                    bestDistance = distance;
                    found = true;
                }
            if (!found) return false;
            float remaining = Mathf.Clamp(deltaSeconds, 0, .1f);
            while (remaining > .000001f && WaitingForManualApproach)
            {
                float speed = SpeedAt(Position) * (Sneaking ? .45f : 1f);
                if (speed <= 0) break;
                float distance = Vector3.Distance(Position, best);
                if (distance <= .00001f) break;
                float step = Mathf.Min(distance, Mathf.Min(.06f, speed * remaining));
                var candidate = Vector3.MoveTowards(Position, best, step);
                if (!CanOccupy(candidate, true)) break;
                SetPosition(candidate);
                remaining -= step / speed;
                // Resume immediately at the first valid cell; never pull to its centre.
                ResumeAtManualDestination();
            }
            SyncVisualPosition();
            return true;
        }
        bool initiallyDrafted;
        bool initiallyFireAtWill;
        bool presentationOnly;
        int combatRecoveryUntil;
        public int LastMeleeAttackTick { get; private set; } = -1000;
        public bool CombatRecovering
        {
            get
            {
                PreserveCombatRecovery();
                return Find.TickManager.TicksGame < combatRecoveryUntil;
            }
        }

        void PreserveCombatRecovery()
        {
            if (Pawn?.stances?.curStance is Stance_Cooldown cooldown && cooldown.verb?.IsMeleeAttack == true)
            {
                combatRecoveryUntil = Math.Max(combatRecoveryUntil, Find.TickManager.TicksGame + cooldown.ticksLeft);
                LastMeleeAttackTick = cooldown.startedTick;
            }
        }
        internal bool AutomaticRecreationPath => IsActing && Pawn.CurJob.def.joyKind != null && Pawn.CurJob.targetA.Thing is Building;
        public bool AutomaticActionPath => HasNativeControl || AutomaticRecreationPath || IsActing &&
            (Pawn.CurJob.def == JobDefOf.Sow || Pawn.CurJob.def == JobDefOf.ManTurret ||
             Pawn.CurJob.def == JobDefOf.Clean || API.RimVisionSpatial.NativePath(Pawn, Pawn.CurJob));
        public bool IsActing => followingAction && Pawn?.CurJob != null && Pawn.CurJob.loadID == actionJobId;
        public bool IsResting => Pawn?.jobs?.curDriver is JobDriver_Rim3DSleep;
        public static bool IsSleeping(Pawn pawn) => pawn?.jobs?.curDriver?.asleep == true;
        public static void RetainSleepingPosture(Pawn pawn)
        {
            if (Rim3DMultiplayer.LocalInput || API.RimVisionControl.Resolve(pawn).Selected) return;
            if (!Eligible(pawn) || !IsSleeping(pawn) || pawn.jobs.curDriver.GetType() != typeof(JobDriver_LayDown)) return;
            var def = DefDatabase<JobDef>.GetNamedSilentFail("Recovery_Rim3DSleep");
            if (def == null || !pawn.jobs.IsCurrentJobPlayerInterruptible()) return;
            var bed = pawn.CurrentBed();
            var job = bed != null ? JobMaker.MakeJob(def, bed)
                : JobMaker.MakeJob(def, pawn.Position);
            job.playerForced = true;
            job.forceSleep = true;
            pawn.jobs.StartJob(job, JobCondition.InterruptForced, cancelBusyStances: true);
        }
        public void RememberCombatRecovery() => PreserveCombatRecovery();
        public PawnControlState State => IsResting
            ? IsSleeping(Pawn) ? PawnControlState.Sleeping
                : Pawn.GetPosture() != PawnPosture.Standing ? PawnControlState.LyingAwake : PawnControlState.GoingToBed
            : IsActing ? PawnControlState.Action : PawnControlState.Manual;
        public Vector3 Position { get; private set; }
        IntVec3 enteredFrom = IntVec3.Invalid;
        int cellsSinceClamor;
        public bool Sneaking { get; set; }
        internal const float CrouchDepth = .57f;
        internal bool IsCrouching => Sneaking && Active && !HasNativeControl && !IsResting && !IsActing &&
            Pawn.GetPosture() == PawnPosture.Standing;
        public bool Sprinting { get; set; }
        internal bool IsRunning { get; private set; }
        public float Stamina { get; private set; } = 1;
        bool exhausted;
        const float BodyRadius = .2f;
        public static float CollisionRadius => BodyRadius;
        static readonly FieldInfo visualPosition = typeof(PawnTweener).GetField("tweenedPos", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo visualFrame = typeof(PawnTweener).GetField("lastDrawFrame", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo visualTick = typeof(PawnTweener).GetField("lastDrawTick", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo visualSpring = typeof(PawnTweener).GetField("lastTickSpringPos", BindingFlags.Instance | BindingFlags.NonPublic);
        public string Status { get; private set; } = "";
        public Action<string> ItemNotice { get; set; }
        bool controlSuspended;
        public static bool Observable(Pawn pawn) => pawn != null && API.RimVisionControl.MapFor(pawn) != null && !pawn.Dead &&
            pawn.Faction == Faction.OfPlayer && pawn.pather != null && pawn.jobs != null;
        public bool Observing => Observable(Pawn);
        public bool InputBlocked => Observing && (!Eligible(Pawn) || (HasNativeControl ? NativeInputBlocked :
            Pawn.CurJob != null && Pawn.CurJob.loadID != controlJobId && Pawn.CurJob.loadID != actionJobId &&
            !IsResting && !Pawn.jobs.IsCurrentJobPlayerInterruptible()));
        public bool Active => Eligible(Pawn) && !InputBlocked;
        public static bool Eligible(Pawn pawn) => pawn != null && API.RimVisionControl.MapFor(pawn) != null &&
            (pawn.IsColonistPlayerControlled || !pawn.Spawned && pawn.IsColonist && pawn.MentalStateDef == null && (pawn.HostFaction == null || pawn.IsSlave)) &&
            !pawn.Dead && !pawn.Downed && !pawn.InMentalState && pawn.pather != null && pawn.jobs != null;
        public static bool CanAcquire(Pawn pawn) => Observable(pawn) && pawn.IsColonist && pawn.InMentalState ||
            Eligible(pawn) && (API.RimVisionControl.CanAcquire(pawn) ||
                !API.RimVisionControl.Resolve(pawn).Selected && (pawn.CurJob == null || pawn.jobs.IsCurrentJobPlayerInterruptible()));

        public bool Acquire(Pawn pawn)
        {
            if (Rim3DMultiplayer.LocalInput) return Rim3DMultiplayer.Acquire?.Invoke(this, pawn) == true;
            presentationOnly = false;
            bool observeBreak = Observable(pawn) && pawn.IsColonist && pawn.InMentalState;
            if (!observeBreak && !Eligible(pawn)) { Status = "Rim3D_PawnUnavailable".Translate().ToString(); return false; }
            if (!observeBreak && (API.RimVisionControl.Resolve(pawn).Selected ? !API.RimVisionControl.CanAcquire(pawn) :
                pawn.CurJob != null && !pawn.jobs.IsCurrentJobPlayerInterruptible()))
            { Status = "Rim3D_PawnUninterruptible".Translate().ToString(); return false; }
            var def = DefDatabase<JobDef>.GetNamedSilentFail("Recovery_Rim3DControl");
            if (def == null) { Status = "Rim3D_ReloadControlJob".Translate().ToString(); return false; }
            def.driverClass = typeof(JobDriver_Rim3DControl);
            def.casualInterruptible = false;
            def.suspendable = false;
            def.neverFleeFromEnemies = true;
            def.dropThingBeforeJob = false;
            def.carryThingAfterJob = true;
            def.alwaysShowWeapon = false;
            if (visualPosition == null || visualFrame == null || visualTick == null || visualSpring == null)
            { Status = "Rim3D_PawnRendererIncompatible".Translate().ToString(); return false; }
            Dispose();
            Pawn = pawn;
            controlDef = def;
            initiallyDrafted = pawn.Drafted;
            initiallyFireAtWill = pawn.drafter?.FireAtWill ?? true;
            var nativeEntry = API.RimVisionControl.Resolve(pawn);
            Position = nativeEntry.Selected && nativeEntry.Context.Valid ? nativeEntry.Position() : Rim3DMultiplayer.PawnPosition(pawn);
            if (!nativeEntry.Selected) Position = new Vector3(Position.x, 0, Position.z);
            enteredFrom = IntVec3.Invalid; cellsSinceClamor = 0;
            Stamina = 1; exhausted = Sneaking = Sprinting = false;
            combatRecoveryUntil = 0;
            LastMeleeAttackTick = -1000;
            PreserveCombatRecovery();
            if (observeBreak)
            {
                // Attach the camera without touching the break's jobs, path or combat settings.
                controlSuspended = true;
                Status = "Rim3D_ControlInterrupted".Translate().ToString();
                return true;
            }
            RefreshNativeControl(API.RimVisionControlChange.Acquired);
            if (HasNativeControl)
            {
                if (Active) { Status = ""; return true; }
                Dispose();
                return false;
            }
            if (pawn.drafter != null) pawn.drafter.FireAtWill = false;
            if (IsResting) { Status = ""; return true; }
            if (pawn.jobs.curDriver is JobDriver_LayDown && pawn.GetPosture() != PawnPosture.Standing)
            {
                bool sleeping = IsSleeping(pawn);
                var bed = pawn.CurrentBed();
                var restJob = CreateSleepJob(bed, sleeping);
                if (restJob == null) { Dispose(); return false; }
                suspendedJobs = pawn.jobs.CaptureAndClearJobQueue();
                pawn.jobs.StartJob(restJob, JobCondition.InterruptForced, cancelBusyStances: true);
                Status = "";
                return IsResting;
            }
            controlJob = JobMaker.MakeJob(def);
            controlJob.playerForced = true;
            controlJob.locomotionUrgency = LocomotionUrgency.Jog;
            controlJobId = controlJob.loadID;
            pawn.jobs.StartJob(controlJob, JobCondition.InterruptForced, resumeCurJobAfterwards: true,
                cancelBusyStances: true, keepCarryingThingOverride: true);
            suspendedJobs = pawn.jobs.CaptureAndClearJobQueue();
            if (!Active) { Dispose(); Status = "Rim3D_ControlTransferFailed".Translate().ToString(); return false; }
            Status = "";
            return true;
        }

        public bool OrderAction(Job job)
        {
            // Jobs must be created inside the synchronized action, never in UI.
            if (Rim3DMultiplayer.LocalInput)
            {
                if (job != null) JobMaker.ReturnToPool(job);
                return false;
            }
            RefreshNativeControl();
            if (HasNativeControl) return job != null && NativeCommand(job);
            if (IsResting)
            {
                if (job != null) JobMaker.ReturnToPool(job);
                Status = "Rim3D_StandUpHint".Translate().ToString(); return false;
            }
            if (!Active || job == null) return false;
            if (job.def == JobDefOf.AttackMelee && !CanUseCombatSkill(Pawn, SkillDefOf.Melee) ||
                job.def == JobDefOf.AttackStatic && !CanUseCombatSkill(Pawn, SkillDefOf.Shooting))
            {
                Status = (job.def == JobDefOf.AttackMelee ? "Rim3D_MeleeDisabled" : "Rim3D_ShootingDisabled").Translate().ToString();
                JobMaker.ReturnToPool(job);
                return false;
            }
            PreserveCombatRecovery();
            job.playerForced = true;
            if (!job.TryMakePreToilReservations(Pawn, errorOnFailed: false))
            {
                Pawn.ClearReservationsForJob(job);
                JobMaker.ReturnToPool(job);
                Status = "Rim3D_ActionReservationFailed".Translate().ToString();
                return false;
            }
            Pawn.jobs.ClearQueuedJobs();
            followingAction = true;
            actionJob = job;
            actionJobId = job.loadID;
            Pawn.jobs.StartJob(job, JobCondition.InterruptForced, resumeCurJobAfterwards: false,
                cancelBusyStances: true, keepCarryingThingOverride: true);
            if (HasNativeControl)
            {
                RefreshNativeControl();
                return Active;
            }
            if (!IsActing)
            {
                ReturnToManual();
                Status = "Rim3D_StartActionFailed".Translate().ToString();
                return false;
            }
            // Capture cooldown before the queued control job cancels the stance.
            // The native attack job may finish in the same tick as the strike.
            if (job.def == JobDefOf.AttackMelee)
                Pawn.jobs.curDriver.AddFinishAction(_ => PreserveCombatRecovery());
            // Keep the return job outside the think tree: no wandering or
            // previously suspended colony work can run after this action.
            controlJob = NewControlJob();
            controlJobId = controlJob.loadID;
            Pawn.jobs.jobQueue.EnqueueFirst(controlJob);
            return true;
        }

        internal void FollowNativeJob(Job job)
        {
            if (!Active || HasNativeControl || controlSuspended || presentationOnly || job == null || job.def == controlDef || IsResting ||
                Pawn.CurJob != job || job.loadID == actionJobId) return;
            followingAction = true;
            actionJob = job;
            actionJobId = job.loadID;
            // Vanilla has already started and reserved the job. Track it without
            // restarting it, then return to manual control after it completes.
            for (int i = 0; i < Pawn.jobs.jobQueue.Count; i++)
                if (Pawn.jobs.jobQueue[i].job.def == controlDef) return;
            controlJob = NewControlJob();
            controlJobId = controlJob.loadID;
            Pawn.jobs.jobQueue.EnqueueLast(controlJob);
        }

        Job NewControlJob()
        {
            var job = JobMaker.MakeJob(controlDef);
            job.playerForced = true;
            job.locomotionUrgency = LocomotionUrgency.Jog;
            return job;
        }

        Job CreateSleepJob(Building_Bed bed, bool sleeping)
        {
            var def = DefDatabase<JobDef>.GetNamedSilentFail("Recovery_Rim3DSleep");
            if (def == null) { Status = "Rim3D_ReloadSleepJob".Translate().ToString(); return null; }
            // JobDriver_LayDown uses A for both the bed and a ground rest spot.
            var job = bed != null ? JobMaker.MakeJob(def, bed)
                : JobMaker.MakeJob(def, Pawn.Position);
            job.playerForced = true;
            job.forceSleep = sleeping;
            if (bed != null) job.targetB = SleepingSlot(Pawn, bed);
            return job;
        }

        public static bool CanSleepInBed(Pawn pawn, Building_Bed bed)
        {
            if (pawn == null || bed == null || !SleepingSlot(pawn, bed).IsValid) return false;
            if (RestUtility.IsValidBedFor(bed, pawn, pawn, checkSocialProperness: false)) return true;
            if (bed.IsOwner(pawn) || bed.Medical || bed.ForPrisoners || bed.ForSlaves ||
                bed.IsForbidden(pawn) || bed.IsBurning() || !RestUtility.CanUseBedEver(pawn, bed.def) ||
                bed.CompAssignableToPawn.IdeoligionForbids(pawn)) return false;
            return bed.OwnersForReading.Count < bed.SleepingSlotsCount;
        }

        internal static IntVec3 SleepingSlot(Pawn pawn, Building_Bed bed)
        {
            if (pawn == null || bed == null || !bed.Spawned || bed.Map != pawn.Map) return IntVec3.Invalid;
            var owners = bed.OwnersForReading;
            // Prefer the pawn's assigned side, then an unassigned side, then any
            // vacant assigned side. Cell reservations include approaching sleepers.
            for (int pass = 0; pass < 3; pass++)
                for (int i = 0; i < bed.SleepingSlotsCount; i++)
                {
                    var owner = i < owners.Count ? owners[i] : null;
                    if (pass == 0 ? owner != pawn : pass == 1 ? owner != null : owner == null || owner == pawn) continue;
                    var occupant = bed.GetCurOccupant(i);
                    var cell = bed.GetSleepingSlotPos(i);
                    if ((occupant == null || occupant == pawn) && pawn.CanReserve(cell)) return cell;
                }
            return IntVec3.Invalid;
        }

        public bool OrderSleep(Building_Bed bed)
        {
            if (Rim3DMultiplayer.LocalInput) return false;
            RefreshNativeControl();
            if (HasNativeControl) return false;
            if (!Active || IsResting || !CanSleepInBed(Pawn, bed) || Pawn.needs?.rest == null ||
                Pawn.health.hediffSet.HasHediffBlocksSleeping() || !Pawn.ageTracker.CurLifeStage.canVoluntarilySleep)
            { Status = "Rim3D_CannotSleepNow".Translate().ToString(); return false; }
            var job = CreateSleepJob(bed, true);
            if (job == null) return false;
            if (Pawn.Drafted) SetCombat(false);
            if (!job.TryMakePreToilReservations(Pawn, false))
            {
                Pawn.ClearReservationsForJob(job);
                JobMaker.ReturnToPool(job);
                Status = "Rim3D_BedUnavailable".Translate().ToString();
                return false;
            }
            if (bed != null && !bed.IsOwner(Pawn))
            {
                bed.CompAssignableToPawn.TryAssignPawn(Pawn);
                if (!bed.IsOwner(Pawn))
                {
                    Pawn.ClearReservationsForJob(job);
                    JobMaker.ReturnToPool(job);
                    Status = "Rim3D_BedUnavailable".Translate().ToString();
                    return false;
                }
            }
            followingAction = false;
            actionJob = null; actionJobId = -1;
            Pawn.jobs.ClearQueuedJobs();
            Pawn.jobs.StartJob(job, JobCondition.InterruptForced, cancelBusyStances: true);
            if (IsResting) return true;
            ReturnToManual();
            Status = "Rim3D_StartActionFailed".Translate().ToString();
            return false;
        }

        public void MaintainControl()
        {
            if (Rim3DMultiplayer.LocalInput) return;
            RefreshNativeControl();
            if (!Active) return;
            if (HasNativeControl)
            {
                if (_nativeControl.Context.Valid) Position = _nativeControl.Position();
                return;
            }
            PreserveCombatRecovery();
            if (Pawn.drafter?.FireAtWill == true) Pawn.drafter.FireAtWill = false;
            if (IsResting)
            {
                if (!WaitingForManualApproach)
                { Position = Rim3DMultiplayer.PawnPosition(Pawn); Position = new Vector3(Position.x, 0, Position.z); }
                return;
            }
            var currentJob = Pawn.CurJob;
            if (!IsActing && currentJob?.playerForced == true &&
                (currentJob.verbToUse is Verb_CastBase || API.RimVisionSpatial.NativePath(Pawn, currentJob)))
                FollowNativeJob(currentJob);
            if (IsActing) return;
            var equipped = Pawn.equipment?.Primary?.TryGetComp<CompEquippable>();
            if (equipped != null && !API.RimVisionCombat.Handles(Pawn, API.RimVisionAttackKind.Ranged))
                foreach (var verb in equipped.AllVerbs)
                    if (verb is Verb_LaunchProjectile) verb.Reset();
            if (Pawn.CurJob != null && Pawn.CurJob.loadID == controlJobId && Pawn.CurJob.def == controlDef)
            {
                if (followingAction)
                {
                    SyncActionPosition();
                    enteredFrom = IntVec3.Invalid;
                    Pawn.pather.StopDead();
                }
                followingAction = false;
                actionJob = null;
                actionJobId = -1;
                return;
            }
            ReturnToManual();
        }

        public void ReturnToManual()
        {
            if (Rim3DMultiplayer.LocalInput) { Rim3DMultiplayer.Cancel?.Invoke(this); return; }
            RefreshNativeControl();
            if (HasNativeControl) { NativeCommand(null); return; }
            if (Pawn == null || !Eligible(Pawn)) return;
            PreserveCombatRecovery();
            if (IsResting && !WaitingForManualApproach) return;
            manualDestination = LocalTargetInfo.Invalid;
            followingAction = false;
            actionJob = null;
            actionJobId = -1;
            SyncActionPosition();
            enteredFrom = IntVec3.Invalid;
            Pawn.jobs.ClearQueuedJobs();
            Pawn.pather.StopDead();
            controlJob = NewControlJob();
            controlJobId = controlJob.loadID;
            Pawn.jobs.StartJob(controlJob, JobCondition.InterruptForced, resumeCurJobAfterwards: false,
                cancelBusyStances: false, keepCarryingThingOverride: true);
            Status = "";
        }

        internal static bool CanUseCombatSkill(Pawn pawn, SkillDef skill) => pawn != null &&
            !pawn.WorkTagIsDisabled(WorkTags.Violent) && pawn.skills?.GetSkill(skill)?.TotallyDisabled != true;

        public void SetCombat(bool enabled)
        {
            if (enabled && !CanUseCombatSkill(Pawn, SkillDefOf.Melee) && !CanUseCombatSkill(Pawn, SkillDefOf.Shooting))
            { Status = "Rim3D_CombatDisabled".Translate().ToString(); return; }
            if (Rim3DMultiplayer.LocalInput) { Status = ""; Rim3DMultiplayer.Combat?.Invoke(this, enabled); return; }
            RefreshNativeControl();
            if (HasNativeControl) { NativeCommand(null, enabled); return; }
            if (Pawn?.drafter == null) return;
            PreserveCombatRecovery();
            if (IsResting) { Status = "Rim3D_StandUpHint".Translate().ToString(); return; }
            ReturnToManual();
            if (enabled && Pawn.carryTracker?.CarriedThing is Thing carried)
            {
                string cargoLabel = carried.LabelCap.ToString();
                int cargoCount = carried.stackCount;
                if (!CanStoreCargo(Pawn, carried) || TransferCargo(Pawn.carryTracker.innerContainer,
                    Pawn.inventory.innerContainer, carried, cargoCount) != cargoCount)
                {
                    if (!Pawn.carryTracker.TryDropCarriedThing(Pawn.Position, ThingPlaceMode.Near, out _))
                    { Status = "Rim3D_CombatDropFailed".Translate().ToString(); return; }
                    ItemNotice?.Invoke("Rim3D_CombatCargoDropped".Translate(cargoLabel).ToString());
                }
                else ItemNotice?.Invoke("Rim3D_CombatCargoStored".Translate(cargoLabel).ToString());
            }
            // The native undraft setter explicitly drops cargo, even when the
            // current job retains it. Detach only during this synchronous change.
            var cargo = Pawn.carryTracker?.CarriedThing;
            if (cargo != null) cargo = Pawn.carryTracker.innerContainer.Take(cargo, cargo.stackCount);
            try
            {
                Pawn.drafter.Drafted = enabled;
                Pawn.drafter.FireAtWill = false;
                ReturnToManual();
            }
            finally
            {
                if (cargo != null && !cargo.Destroyed && !Pawn.carryTracker.TryStartCarry(cargo))
                {
                    if (!CanStoreCargo(Pawn, cargo) || Pawn.inventory.innerContainer.TryAdd(cargo, canMergeWithExistingStacks: true) != true)
                        GenPlace.TryPlaceThing(cargo, Pawn.Position, Pawn.Map, ThingPlaceMode.Near);
                }
            }
        }

        public static bool CanStoreCargo(Pawn pawn, Thing thing, int count = -1)
        {
            if (pawn?.inventory == null || thing == null) return false;
            return !MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, count < 0 ? thing.stackCount : count);
        }

        internal static int TransferCargo(ThingOwner source, ThingOwner destination, Thing thing, int count)
        {
            int before = thing.stackCount;
            source.TryTransferToContainer(thing, destination, count, canMergeWithExistingStacks: true);
            // RimWorld returns the surviving portion's count, which can be zero
            // after a successful merge. Measure what actually left the source.
            return before - (source.Contains(thing) ? thing.stackCount : 0);
        }

        public static Vector3 Direction(Vector2 input, float yaw)
        {
            var vector = new Vector3(input.x, 0, input.y);
            if (vector.sqrMagnitude > 1) vector.Normalize();
            return Quaternion.Euler(0, yaw, 0) * vector;
        }

        static IntVec3 Cell(Vector3 position) => new IntVec3(Mathf.FloorToInt(position.x), 0, Mathf.FloorToInt(position.z));

        // Use the game's terrain/thing/snow costs and the pawn's actual move stat, without diagonal grid rounding.
        public float SpeedAt(Vector3 position)
        {
            if (!Active) return 0;
            var cell = Cell(position);
            if (!cell.InBounds(Pawn.Map)) return 0;
            var terrain = Pawn.Map.terrainGrid.TerrainAt(cell);
            int? waterOverride = Pawn_PathFollower.GetPawnCellBaseCostOverride(Pawn, cell);
            float cost = Pawn.TicksPerMoveCardinal + Pawn.Map.pathing.For(Pawn).pathGrid.CalculatedCostAt(
                cell, false, IntVec3.Invalid, waterOverride);
            bool ignorePlants = Rim3DMultiplayer.Active
                ? Rim3DMultiplayer.PlantSlowdownDisabled?.Invoke() == true
                : Rim3DSettings.Current.DisablePlantSlowdown;
            if (ignorePlants) cost -= PlantMovementPenalty(cell, terrain, waterOverride);
            var edifice = cell.GetEdifice(Pawn.Map);
            if (edifice != null) cost += (int)edifice.PathWalkCostFor(Pawn);
            if (terrain.tags != null)
                foreach (var tag in terrain.tags)
                    if (Pawn.kindDef.moveSpeedFactorByTerrainTag.TryGetValue(tag, out var factor) && factor > 0) cost /= factor;
            cost = Mathf.Clamp(cost, 1, 450);
            float rate = Pawn.stances.stagger.Staggered ? Pawn.stances.stagger.StaggerMoveSpeedFactor : 1;
            if (Pawn.Flying) rate *= Pawn.RaceProps.flightSpeedFactor;
            return 60 * rate / cost;
        }

        int PlantMovementPenalty(IntVec3 cell, TerrainDef terrain, int? waterOverride)
        {
            var map = Pawn.Map;
            var things = map.thingGrid.ThingsListAt(cell);
            int plantCost = 0;
            for (int i = 0; i < things.Count; i++)
                if (things[i] is Plant) plantCost = Mathf.Max(plantCost, things[i].def.pathCost);
            if (plantCost == 0) return 0;

            // PathGrid takes the highest cost, rather than adding each obstacle.
            // Remove only the excess from plants, retaining all other penalties.
            int otherCost = waterOverride ?? (Pawn.Flying ? 0 : terrain.pathCost);
            otherCost = Mathf.Max(otherCost, WeatherBuildupUtility.MovementTicksAddOn(map.snowGrid.GetCategory(cell)));
            if (ModsConfig.OdysseyActive)
                otherCost = Mathf.Max(otherCost, WeatherBuildupUtility.MovementTicksAddOn(map.sandGrid.GetCategory(cell)));
            plantCost = 0;
            for (int i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (thing is Plant) plantCost = Mathf.Max(plantCost, thing.def.pathCost);
                else otherCost = Mathf.Max(otherCost, thing.def.pathCost);
            }
            return Mathf.Max(0, plantCost - otherCost);
        }

        bool CanOccupy(Vector3 position, bool openDoors)
        {
            var map = Pawn.Map;
            if (API.RimVisionSpatial.Resolve(map)?.CanStand(Pawn, position) == false) return false;
            if (position.x < BodyRadius || position.z < BodyRadius ||
                position.x > map.Size.x - BodyRadius || position.z > map.Size.z - BodyRadius) return false;
            int minX = Mathf.FloorToInt(position.x - BodyRadius), maxX = Mathf.FloorToInt(position.x + BodyRadius);
            int minZ = Mathf.FloorToInt(position.z - BodyRadius), maxZ = Mathf.FloorToInt(position.z + BodyRadius);
            for (int z = minZ; z <= maxZ; z++) for (int x = minX; x <= maxX; x++)
            {
                float nearestX = Mathf.Clamp(position.x, x, x + 1), nearestZ = Mathf.Clamp(position.z, z, z + 1);
                if ((position.x - nearestX) * (position.x - nearestX) + (position.z - nearestZ) * (position.z - nearestZ) >= BodyRadius * BodyRadius - .00001f) continue;
                var cell = new IntVec3(x, 0, z);
                // Read live terrain/things: cached walkability can lag a newly built or destroyed wall.
                if (map.pathing.For(Pawn).pathGrid.CalculatedCostAt(cell, false, IntVec3.Invalid) >= 10000) return false;
                var door = cell.GetDoor(map);
                if (door == null) continue;
                if (!door.FreePassage && !door.PawnCanOpen(Pawn)) return false;
                if (openDoors)
                {
                    door.CheckFriendlyTouched(Pawn);
                    if (!door.Open && door.PawnCanOpen(Pawn)) door.StartManualOpenBy(Pawn);
                }
                if (!door.Open || door.TicksTillFullyOpened > 0) { Status = "Rim3D_DoorOpening".Translate().ToString(); return false; }
            }
            return true;
        }

        public void Move(Vector2 input, float yaw) => Move(input, yaw, Time.unscaledDeltaTime);

        // Presentation movement only reads collision state; jobs, doors and the
        // thing grid are changed later by the synchronized controller.
        public Vector3 PreviewMovement(Vector3 position, Vector2 input, float yaw, float deltaSeconds)
        {
            var native = API.RimVisionControl.Resolve(Pawn);
            if (native.Selected) return native.Context.Valid ? native.Position() : Position;
            if (!Active || Find.TickManager.Paused || Pawn.stances.FullBodyBusy) return position;
            // A wall can appear between the client's snapshots. Rebase from the
            // authoritative position instead of predicting from inside its edge.
            if (!CanOccupy(position, false) && CanOccupy(Position, false)) position = Position;
            var direction = Direction(input, yaw);
            if (direction.sqrMagnitude <= .00001f) return position;
            bool running = Sprinting && !Sneaking && !exhausted;
            float remaining = Mathf.Clamp(deltaSeconds, 0, .1f);
            var speedCell = IntVec3.Invalid;
            float cellSpeed = 0;
            while (remaining > .000001f)
            {
                var cell = Cell(position);
                if (cell != speedCell) { cellSpeed = SpeedAt(position); speedCell = cell; }
                float speed = cellSpeed * (Sneaking ? .45f : running ? 1.6f : 1);
                if (speed <= 0) break;
                float seconds = Mathf.Min(remaining, .06f / speed);
                var displacement = direction * speed * seconds;
                var candidate = position + displacement;
                if (CanOccupy(candidate, false)) position = candidate;
                else
                {
                    bool slid = false;
                    candidate = position + new Vector3(displacement.x, 0, 0);
                    if (Mathf.Abs(displacement.x) > .000001f && CanOccupy(candidate, false)) { position = candidate; slid = true; }
                    candidate = position + new Vector3(0, 0, displacement.z);
                    if (Mathf.Abs(displacement.z) > .000001f && CanOccupy(candidate, false)) { position = candidate; slid = true; }
                    if (!slid) break;
                }
                remaining -= seconds;
            }
            return position;
        }

        public void NotifyMapTransfer()
        {
            if (API.RimVisionControl.MapFor(Pawn) == null) return;
            var native = API.RimVisionControl.Resolve(Pawn);
            if (native.Selected && native.Context.Valid) { Position = native.Position(); enteredFrom = IntVec3.Invalid; manualDestination = LocalTargetInfo.Invalid; return; }
            Position = Pawn.Position.ToVector3Shifted();
            Position = new Vector3(Position.x, 0, Position.z);
            enteredFrom = IntVec3.Invalid;
            manualDestination = LocalTargetInfo.Invalid;
        }

        public void ApplyClientPosition(Vector3 position)
        {
            if (!Rim3DMultiplayer.Simulating || HasNativeControl || !Active || ((IsActing || IsResting) && !WaitingForManualApproach) ||
                Find.TickManager.Paused || Pawn.stances.FullBodyBusy || !Cell(position).InBounds(Pawn.Map) ||
                !CanOccupy(position, false)) return;
            SetPosition(new Vector3(position.x, 0, position.z));
        }

        public bool Moving { get; private set; }
        public void Move(Vector2 input, float yaw, float deltaSeconds, bool movePosition = true)
        {
            var previous = Position;
            MoveCore(input, yaw, deltaSeconds, movePosition);
            if (deltaSeconds > 0 && (!Rim3DMultiplayer.LocalInput || !HasNativeControl))
                Moving = (Position - previous).sqrMagnitude > .00000001f;
        }

        void MoveCore(Vector2 input, float yaw, float deltaSeconds, bool movePosition)
        {
            if (Rim3DMultiplayer.LocalInput) { Rim3DMultiplayer.Move?.Invoke(this, input, yaw); return; }
            IsRunning = false;
            if (presentationOnly) { BindPresentation(null); return; }
            RefreshNativeControl();
            if (HasNativeControl)
            {
                if (Active) MoveNativeControl(input, yaw, deltaSeconds);
                else
                {
                    var native = API.RimVisionControl.Resolve(Pawn);
                    if (native.Context.Valid) Position = native.Position();
                    Status = "Rim3D_ControlInterrupted".Translate().ToString();
                }
                return;
            }
            if (InputBlocked)
            {
                if (!controlSuspended)
                {
                    controlSuspended = true;
                    manualDestination = LocalTargetInfo.Invalid;
                    followingAction = false;
                    actionJob = null;
                    actionJobId = -1;
                    if (Pawn.drafter != null) Pawn.drafter.FireAtWill = initiallyFireAtWill;
                }
                Sneaking = Sprinting = false;
                Position = Rim3DMultiplayer.PawnPosition(Pawn);
                Position = new Vector3(Position.x, 0, Position.z);
                Status = "Rim3D_ControlInterrupted".Translate().ToString();
                return;
            }
            if (!Active) { Status = "Rim3D_ControlInterrupted".Translate().ToString(); return; }
            if (controlSuspended)
            {
                controlSuspended = false;
                ReturnToManual();
            }
            MaintainControl();
            if (!Find.TickManager.Paused) ResumeAtManualDestination();
            bool approaching = input.sqrMagnitude <= .001f && ApproachNearbyAction(deltaSeconds);
            if (IsResting && !WaitingForManualApproach)
            {
                if (input.sqrMagnitude <= .001f || Find.TickManager.Paused)
                {
                    UpdateStamina(deltaSeconds, false);
                    return;
                }
                Pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                ReturnToManual();
            }
            if (followingAction)
            {
                if (!IsActing || input.sqrMagnitude > .001f && !WaitingForManualApproach && !API.RimVisionSpatial.NativePath(Pawn, Pawn.CurJob) &&
                    !(actionJob.verbToUse is Verb_CastBase) && !(Pawn.stances.curStance is Stance_Warmup)) ReturnToManual();
                else if (!WaitingForManualApproach)
                {
                    SyncActionPosition();
                    Status = "";
                    return;
                }
            }
            // An external teleport or an incapacitation must win over the local continuous position.
            if (Pawn.Position != Cell(Position))
            {
                Position = Pawn.Position.ToVector3Shifted();
                enteredFrom = IntVec3.Invalid;
            }
            float remaining = Mathf.Clamp(deltaSeconds, 0, .1f);
            var direction = Direction(input, yaw);
            // Looking changes facing even while standing still. Action jobs and
            // sleep return above and retain their own native facing rules.
            Pawn.Rotation = Rot4.FromAngleFlat(yaw);
            bool running = Sprinting && !Sneaking && !exhausted && direction.sqrMagnitude > .01f &&
                !Find.TickManager.Paused && !Pawn.stances.FullBodyBusy;
            IsRunning = running;
            UpdateStamina(remaining, running);
            Status = WaitingForManualApproach ? (approaching ? "Rim3D_ApproachingTarget" : "Rim3D_ApproachManually").TranslateBindings().ToString() : "";
            if (Find.TickManager.Paused || Pawn.stances.FullBodyBusy) remaining = 0;
            if (remaining > 0 && direction.sqrMagnitude > .00001f && API.RimVisionSpatial.TryStartTraversal(this, direction)) return;
            if (!movePosition && remaining > 0 && direction.sqrMagnitude > .00001f)
                CanOccupy(Position + direction * .06f, true);
            if (movePosition && direction.sqrMagnitude > .00001f)
            {
                var speedCell = IntVec3.Invalid;
                float cellSpeed = 0;
                // Small swept steps prevent tunnelling, even for fast pawns or a long frame.
                while (remaining > .000001f)
                {
                    var currentCell = Cell(Position);
                    if (currentCell != speedCell) { cellSpeed = SpeedAt(Position); speedCell = currentCell; }
                    float speed = cellSpeed * (Sneaking ? .45f : running ? 1.6f : 1);
                    if (speed <= 0) break;
                    float seconds = Mathf.Min(remaining, .06f / speed);
                    var displacement = direction * speed * seconds;
                    var candidate = Position + displacement;
                    if (CanOccupy(candidate, true)) SetPosition(candidate);
                    else
                    {
                        bool slid = false;
                        candidate = Position + new Vector3(displacement.x, 0, 0);
                        if (Mathf.Abs(displacement.x) > .000001f && CanOccupy(candidate, true)) { SetPosition(candidate); slid = true; }
                        candidate = Position + new Vector3(0, 0, displacement.z);
                        if (Mathf.Abs(displacement.z) > .000001f && CanOccupy(candidate, true)) { SetPosition(candidate); slid = true; }
                        if (!slid) { if (Status.Length == 0) Status = "Rim3D_PassageBlocked".Translate().ToString(); break; }
                    }
                    remaining -= seconds;
                }
            }
            SyncVisualPosition();
            if (!Find.TickManager.Paused) ResumeAtManualDestination();
        }

        void UpdateStamina(float deltaSeconds, bool running)
        {
            if (!Find.TickManager.Paused)
                Stamina = Mathf.Clamp01(Stamina + Mathf.Clamp(deltaSeconds, 0, .1f) * (running ? -.16f : .11f));
            if (Stamina <= 0) exhausted = true;
            else if (Stamina >= .3f) exhausted = false;
        }

        void SetPosition(Vector3 position)
        {
            var cell = Cell(position);
            var oldCell = Pawn.Position;
            Position = position;
            if (cell == oldCell) return;
            var oldDoor = oldCell.GetDoor(Pawn.Map);
            // The setter updates the thing grid, regions, cover and gas tracking.
            Pawn.Position = cell;
            enteredFrom = oldCell;
            Pawn.pather.StopDead();
            var door = cell.GetDoor(Pawn.Map);
            if (door != null) Pawn.Map.fogGrid.Notify_PawnEnteringDoor(door, Pawn);
            Pawn.filth.Notify_EnteredNewCell();
            if (Pawn.BodySize > .9f) Pawn.Map.snowGrid.AddDepth(cell, -.001f);
            if (++cellsSinceClamor >= (Sneaking ? 24 : Sprinting ? 6 : 12))
            {
                GenClamor.DoClamor(Pawn, Sneaking ? 2 : Sprinting ? 10 : 7, ClamorDefOf.Movement);
                cellsSinceClamor = 0;
            }
            if (oldDoor != null && !oldDoor.HoldOpen && oldDoor.PawnCanOpen(Pawn)) oldDoor.StartManualCloseBy(Pawn);
        }

        void SyncActionPosition()
        {
            // Starting stationary work must not pull the continuous position
            // toward the cell centre (which can be inside a tree). Only follow
            // native placement when the pawn actually changes cell or lies down.
            if (AutomaticActionPath || Pawn.Position != Cell(Position) || Pawn.GetPosture() != PawnPosture.Standing)
            {
                Position = Rim3DMultiplayer.Active ? Pawn.Position.ToVector3Shifted() : Rim3DMultiplayer.PawnPosition(Pawn);
                Position = new Vector3(Position.x, 0, Position.z);
            }
            // Native construction moves a pawn aside using cells, while manual
            // control has a body radius. Its old offset can overlap the new wall.
            if (Pawn.GetPosture() == PawnPosture.Standing && !CanOccupy(Position, false))
            {
                var centre = Pawn.Position.ToVector3Shifted();
                if (CanOccupy(centre, false)) { Position = centre; enteredFrom = IntVec3.Invalid; }
            }
            if (!AutomaticActionPath) SyncVisualPosition();
        }

        void SyncVisualPosition()
        {
            // Multiplayer draws the shared continuous position through its
            // presentation patch; native tweener state is not simulation state.
            if (Rim3DMultiplayer.Active) return;
            // Keep the original 2D pawn renderer in the same continuous position as the first-person camera.
            var tweener = Pawn.Drawer.tweener;
            visualPosition.SetValue(tweener, Position);
            visualSpring.SetValue(tweener, Position);
            visualFrame.SetValue(tweener, RealTime.frameCount);
            visualTick.SetValue(tweener, GenTicks.TicksGame);
        }
        public void Dispose()
        {
            if (Rim3DMultiplayer.LocalInput) { Rim3DMultiplayer.Release?.Invoke(this); return; }
            if (presentationOnly) { BindPresentation(null); return; }
            var pawn = Pawn;
            bool nativeControl = HasNativeControl || _nativeControl.Selected;
            DetachNativeControl(API.RimVisionControlChange.Released);
            Pawn = null;
            bool resting = pawn?.jobs?.curDriver is JobDriver_Rim3DSleep;
            bool canRestore = Eligible(pawn) && !controlSuspended;
            if (pawn != null && pawn.jobs != null && !resting && canRestore && !nativeControl)
            {
                pawn.jobs.ClearQueuedJobs();
                pawn.pather?.StopDead();
                if (pawn.CurJob != null && (pawn.CurJob.loadID == controlJobId || pawn.CurJob.loadID == actionJobId))
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            }
            controlJob = null;
            controlDef = null;
            actionJob = null;
            controlJobId = actionJobId = -1;
            followingAction = false;
            if (!resting && canRestore && !nativeControl && pawn?.drafter != null && pawn.Drafted != initiallyDrafted) pawn.drafter.Drafted = initiallyDrafted;
            manualDestination = LocalTargetInfo.Invalid;
            if (!nativeControl && pawn?.drafter != null) pawn.drafter.FireAtWill = initiallyFireAtWill;
            if (nativeControl) RestoreNativeQueue(pawn);
            else if (canRestore && suspendedJobs != null) pawn.jobs.RestoreCapturedJobs(suspendedJobs);
            suspendedJobs = null;
            controlSuspended = false;
            _restoreNativeControl = false;
            Moving = false;
        }

        // Presentation copies never own jobs, reservations or the suspended queue.
        public void BindPresentation(Pawn pawn, PawnControl source = null)
        {
            presentationOnly = true;
            Pawn = pawn;
            Moving = source?.Moving ?? false;
            if (source == null)
            {
                _nativeControl = default;
                _nativeControlFailed = false;
                var native = API.RimVisionControl.Resolve(pawn);
                Position = native.Selected && native.Context.Valid ? native.Position() : pawn?.Position.ToVector3Shifted() ?? Vector3.zero;
                followingAction = false;
                controlJobId = actionJobId = -1;
                Status = pawn == null ? "Rim3D_ControlReleased".Translate().ToString() : "Rim3D_MultiplayerWaiting".Translate().ToString();
                return;
            }
            Position = source.Position;
            _nativeControl = source._nativeControl;
            _nativeControlFailed = source._nativeControlFailed;
            controlDef = source.controlDef;
            controlJobId = source.controlJobId;
            actionJobId = source.actionJobId;
            followingAction = source.followingAction;
            manualDestination = source.manualDestination;
            manualPathEndMode = source.manualPathEndMode;
            manualPathJobId = source.manualPathJobId;
            Stamina = source.Stamina;
            exhausted = source.exhausted;
            combatRecoveryUntil = source.combatRecoveryUntil;
            LastMeleeAttackTick = source.LastMeleeAttackTick;
            Status = source.Status;
        }

        public void SetPresentationPosition(Vector3 position)
        {
            if (presentationOnly) Position = position;
        }

        public void ExposeData()
        {
            var pawn = Pawn;
            var position = Position;
            float stamina = Stamina;
            int meleeTick = LastMeleeAttackTick;
            bool sneaking = Sneaking, sprinting = Sprinting;
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Defs.Look(ref controlDef, "controlDef");
            Scribe_Values.Look(ref position, "position");
            Scribe_Values.Look(ref controlJobId, "controlJobId", -1);
            Scribe_Values.Look(ref actionJobId, "actionJobId", -1);
            Scribe_Deep.Look(ref suspendedJobs, "suspendedJobs");
            Scribe_Values.Look(ref followingAction, "followingAction");
            Scribe_Values.Look(ref controlSuspended, "controlSuspended");
            Scribe_TargetInfo.Look(ref manualDestination, "manualDestination");
            Scribe_Values.Look(ref manualPathEndMode, "manualPathEndMode");
            Scribe_Values.Look(ref manualPathJobId, "manualPathJobId", -1);
            Scribe_Values.Look(ref initiallyDrafted, "initiallyDrafted");
            Scribe_Values.Look(ref initiallyFireAtWill, "initiallyFireAtWill", true);
            Scribe_Values.Look(ref combatRecoveryUntil, "combatRecoveryUntil");
            Scribe_Values.Look(ref meleeTick, "lastMeleeAttackTick", -1000);
            Scribe_Values.Look(ref enteredFrom, "enteredFrom", IntVec3.Invalid);
            Scribe_Values.Look(ref cellsSinceClamor, "cellsSinceClamor");
            Scribe_Values.Look(ref stamina, "stamina", 1f);
            Scribe_Values.Look(ref exhausted, "exhausted");
            Scribe_Values.Look(ref sneaking, "sneaking");
            Scribe_Values.Look(ref sprinting, "sprinting");
            bool isRunning = IsRunning;
            Scribe_Values.Look(ref isRunning, "isRunning");
            IsRunning = isRunning;
            Pawn = pawn; Position = position; Stamina = stamina;
            LastMeleeAttackTick = meleeTick; Sneaking = sneaking; Sprinting = sprinting;
            if (Scribe.mode == LoadSaveMode.PostLoadInit) _restoreNativeControl = true;
        }
    }
}


