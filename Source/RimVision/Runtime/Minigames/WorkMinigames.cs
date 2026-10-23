using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly WorkMinigameSession _workMinigame = new WorkMinigameSession();

        public void UpdateMinigame() => _workMinigame.Update(controller,
            FirstPerson && ControlledPawn?.CurJob?.def != JobDefOf.ManTurret && !FallPresentationActive && !eventCamera && !cameraFlight,
            !ExternalInputBlocked && !constructionMenu && !actionMenu && !statusMenu, progressMotes, _workActions.SowingZone);

        public void DrawMinigame(Rect viewport) => _workMinigame.DrawLauncher(viewport,
            !ExternalInputBlocked && !constructionMenu && !actionMenu && !statusMenu);
    }

    public abstract class WorkMinigame : IDisposable
    {
        public virtual string Title => "Rim3D_MinigameTitle".Translate().ToString();
        public virtual string ScoreLabel(int score) => "Rim3D_MinigamePoints".Translate(score).ToString();
        public virtual int PointsPerReward => WorkMinigames.PointsPerReward;
        public virtual Vector2 WindowSize => new Vector2(430, 470);
        public abstract string Instructions { get; }
        public abstract int Score { get; }
        public abstract void Restart();
        public abstract void Update(bool paused);
        public abstract void Draw(Rect rect);
        public virtual void Dispose() { }
    }

    public enum WorkMinigameKind
    {
        Snake, Mining, Woodcutting, Research, Building, Repair, Crafting,
        Sowing, Harvest, Dismantling, Smoothing, Bandaging, Chess, Horseshoes
    }

    public static class WorkMinigames
    {
        internal static readonly WorkMinigameKind[] Kinds = (WorkMinigameKind[])Enum.GetValues(typeof(WorkMinigameKind));
        public const float MinimumSeconds = 5f;
        public const int PointsPerReward = 5;
        public const float WorkFractionPerReward = .05f;
        internal const int ResearchTicksPerReward = 600;
        internal const int ActivityTicksPerReward = 600;
        sealed class Registration
        {
            internal readonly Func<Pawn, WorkMinigame> Create;
            internal readonly Func<Pawn, float> RemainingTicks;
            internal readonly Func<Pawn, float, bool> AdvanceWork;
            internal Registration(Func<Pawn, WorkMinigame> create, Func<Pawn, float> remainingTicks, Func<Pawn, float, bool> advanceWork)
            { Create = create; RemainingTicks = remainingTicks; AdvanceWork = advanceWork; }
        }
        static readonly Dictionary<JobDef, Registration> _games = new Dictionary<JobDef, Registration>();
        static readonly Dictionary<WorkMinigameKind, Registration> _defaults = new Dictionary<WorkMinigameKind, Registration>();

        public static void Register(JobDef job, Func<WorkMinigame> create, Func<Pawn, float> remainingTicks = null)
            => Register(job, create, remainingTicks, null);

        public static void Register(JobDef job, Func<WorkMinigame> create, Func<Pawn, float> remainingTicks,
            Func<Pawn, float, bool> advanceWork)
        {
            if (job == null || create == null) throw new ArgumentNullException();
            _games[job] = new Registration(pawn => create(), remainingTicks, advanceWork);
        }

        public static IDisposable RegisterForPawn(JobDef job, Func<Pawn, WorkMinigame> create,
            Func<Pawn, float> remainingTicks = null, Func<Pawn, float, bool> advanceWork = null, bool replace = false)
        {
            if (job == null || create == null) throw new ArgumentNullException();
            if (!replace && _games.ContainsKey(job)) throw new InvalidOperationException("A minigame is already registered for " + job.defName);
            var registration = new Registration(create, remainingTicks, advanceWork);
            _games[job] = registration;
            return new RegistrationHandle(() =>
            {
                if (_games.TryGetValue(job, out var current) && current == registration) _games.Remove(job);
            });
        }

        public static IDisposable RegisterDefault(WorkMinigameKind kind, Func<Pawn, WorkMinigame> create, bool replace = false)
        {
            if (!Enum.IsDefined(typeof(WorkMinigameKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (create == null) throw new ArgumentNullException(nameof(create));
            if (!replace && _defaults.ContainsKey(kind)) throw new InvalidOperationException("A default minigame is already registered for " + kind);
            var registration = new Registration(create, null, null);
            _defaults[kind] = registration;
            return new RegistrationHandle(() =>
            {
                if (_defaults.TryGetValue(kind, out var current) && current == registration) _defaults.Remove(kind);
            });
        }

        sealed class RegistrationHandle : IDisposable
        {
            Action _remove;
            internal RegistrationHandle(Action remove) { _remove = remove; }
            public void Dispose() { var remove = _remove; _remove = null; remove?.Invoke(); }
        }

        internal static bool IsWoodcutting(JobDef job, Thing target) => job.driverClass != null &&
            target is Plant plant && plant.def.plant.IsTree && !plant.def.plant.isStump &&
            (typeof(JobDriver_PlantCut).IsAssignableFrom(job.driverClass) ||
                typeof(JobDriver_PlantHarvest).IsAssignableFrom(job.driverClass) && plant.def.plant.harvestTag == "Wood");

        internal static WorkMinigame Create(Pawn pawn)
        {
            var job = pawn.CurJob;
            if (_games.TryGetValue(job.def, out var registration))
                return registration.Create(pawn) ?? throw new InvalidOperationException("Minigame factory returned null for " + job.def.defName);
            var kind = Kind(job.def, job.targetA.Thing, job.bill is Bill_Medical);
            if (_defaults.TryGetValue(kind, out var defaultRegistration))
                return defaultRegistration.Create(pawn) ?? throw new InvalidOperationException("Minigame factory returned null for " + kind);
            switch (kind)
            {
                case WorkMinigameKind.Chess: return new ChessMinigame();
                case WorkMinigameKind.Horseshoes: return new HorseshoesMinigame();
                case WorkMinigameKind.Research: return new ResearchMinigame();
                case WorkMinigameKind.Mining: return new MiningMinigame();
                case WorkMinigameKind.Woodcutting: return new WoodcuttingMinigame();
                case WorkMinigameKind.Building: return new BuildingMinigame();
                case WorkMinigameKind.Repair: return new RepairMinigame();
                case WorkMinigameKind.Crafting: return new CraftingMinigame();
                case WorkMinigameKind.Sowing: return new SowingMinigame();
                case WorkMinigameKind.Harvest: return new HarvestMinigame();
                case WorkMinigameKind.Smoothing: return new SmoothingMinigame();
                case WorkMinigameKind.Dismantling: return new DismantlingMinigame();
                case WorkMinigameKind.Bandaging: return new BandagingMinigame();
                default: return new SnakeMinigame();
            }
        }

        internal static WorkMinigameKind Kind(JobDef job, Thing target, bool medicalBill)
        {
            if (job.defName == "Play_Chess") return WorkMinigameKind.Chess;
            if (job.driverClass != null && typeof(JobDriver_PlayHorseshoes).IsAssignableFrom(job.driverClass)) return WorkMinigameKind.Horseshoes;
            if (job.driverClass != null && typeof(JobDriver_Research).IsAssignableFrom(job.driverClass)) return WorkMinigameKind.Research;
            if (job.driverClass != null && typeof(JobDriver_Mine).IsAssignableFrom(job.driverClass)) return WorkMinigameKind.Mining;
            if (IsWoodcutting(job, target)) return WorkMinigameKind.Woodcutting;
            var driver = job.driverClass;
            if (driver == null) return WorkMinigameKind.Snake;
            if (typeof(JobDriver_ConstructFinishFrame).IsAssignableFrom(driver)) return WorkMinigameKind.Building;
            if (typeof(JobDriver_Repair).IsAssignableFrom(driver)) return WorkMinigameKind.Repair;
            if (typeof(JobDriver_DoBill).IsAssignableFrom(driver) && !medicalBill) return WorkMinigameKind.Crafting;
            if (typeof(JobDriver_PlantSow).IsAssignableFrom(driver)) return WorkMinigameKind.Sowing;
            if (typeof(JobDriver_PlantHarvest).IsAssignableFrom(driver)) return WorkMinigameKind.Harvest;
            if (typeof(JobDriver_SmoothWall).IsAssignableFrom(driver) || typeof(JobDriver_SmoothFloor).IsAssignableFrom(driver))
                return WorkMinigameKind.Smoothing;
            if (typeof(JobDriver_RemoveBuilding).IsAssignableFrom(driver) || typeof(JobDriver_RemoveFloor).IsAssignableFrom(driver))
                return WorkMinigameKind.Dismantling;
            if (typeof(JobDriver_TendPatient).IsAssignableFrom(driver) || typeof(JobDriver_Rim3DTend).IsAssignableFrom(driver))
                return WorkMinigameKind.Bandaging;
            return WorkMinigameKind.Snake;
        }

        public static float ClampRewardMultiplier(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 1 : Mathf.Clamp(value, 0, 5);

        internal static float RewardMultiplier(WorkMinigameKind kind) => ClampRewardMultiplier(Rim3DMultiplayer.Active
            ? Rim3DMultiplayer.MinigameRewardMultiplier?.Invoke(kind) ?? 1 : Rim3DSettings.Current.MinigameRewardMultiplier(kind));
        internal static bool TryDuration(Pawn pawn, out float ticks)
        {
            ticks = 0;
            if (!_games.TryGetValue(pawn.CurJob.def, out var registration) || registration.RemainingTicks == null) return false;
            ticks = registration.RemainingTicks(pawn);
            return true;
        }

        internal static bool TryAdvanceWork(Pawn pawn, float fraction, out bool applied)
        {
            applied = false;
            if (!_games.TryGetValue(pawn.CurJob.def, out var registration) || registration.AdvanceWork == null) return false;
            applied = registration.AdvanceWork(pawn, fraction);
            return true;
        }
    }

    sealed class WorkDurationEstimate
    {
        static readonly FieldInfo _toils = typeof(JobDriver).GetField("toils", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _plantWork = typeof(JobDriver_PlantWork).GetField("workDone", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _sowWork = typeof(JobDriver_PlantSow).GetField("sowWorkDone", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _driverStartTick = typeof(JobDriver).GetField("startTick", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _miningHitTicks = typeof(JobDriver_Mine).GetField("ticksToPickHit", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _repairTicks = typeof(JobDriver_Repair).GetField("ticksToNextRepair", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _floorWork = typeof(JobDriver_AffectFloor).GetField("workLeft", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo _floorSpeed = typeof(JobDriver_AffectFloor).GetProperty("SpeedStat", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _wallWork = typeof(JobDriver_SmoothWall).GetField("workLeft", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _removalWork = typeof(JobDriver_RemoveBuilding).GetField("workLeft", BindingFlags.Instance | BindingFlags.NonPublic);
        int _sampleTick = -1;
        float _sampleProgress;

        internal static Toil CurrentToil(Pawn pawn)
        {
            var driver = pawn?.jobs?.curDriver;
            return driver != null && _toils?.GetValue(driver) is List<Toil> toils &&
                driver.CurToilIndex >= 0 && driver.CurToilIndex < toils.Count ? toils[driver.CurToilIndex] : null;
        }

        internal void Reset() { _sampleTick = -1; }

        internal static float SowingZoneTicks(Pawn pawn, Zone_Growing zone)
        {
            var plant = zone.GetPlantDefToGrow();
            if (plant?.plant == null) return 0;
            int cells = 0;
            foreach (var cell in zone.Cells)
                if (cell == pawn.CurJob.targetA.Cell || cell.GetPlant(zone.Map) == null) cells++;
            return cells * plant.plant.sowWork / Mathf.Max(.0001f, pawn.GetStatValue(StatDefOf.PlantWorkSpeed));
        }

        internal bool TryRemaining(Pawn pawn, IEnumerable<MoteProgressBar> motes, out float ticks)
        {
            if (WorkMinigames.TryDuration(pawn, out ticks)) return true;
            var driver = pawn.jobs.curDriver;
            var toil = CurrentToil(pawn);
            var job = pawn.CurJob;
            var target = job.targetA.Thing;
            ticks = 0;
            if (toil == null || toil.defaultCompleteMode == ToilCompleteMode.PatherArrival ||
                toil.defaultCompleteMode == ToilCompleteMode.Instant) return false;
            if (driver is JobDriver_ConstructFinishFrame && target is Frame frame)
            {
                float speed = pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f;
                if (frame.Stuff != null) speed *= frame.Stuff.GetStatValueAbstract(StatDefOf.ConstructionSpeedFactor);
                ticks = (frame.WorkToBuild - frame.workDone) / Mathf.Max(.0001f, speed);
                return true;
            }
            if (driver is JobDriver_DoBill bill && job.RecipeDef != null && toil.activeSkill != null)
            {
                var recipe = job.RecipeDef;
                float speed = recipe.workSpeedStat == null ? 1 : pawn.GetStatValue(recipe.workSpeedStat);
                if (recipe.workTableSpeedStat != null && target is Building_WorkTable table)
                    speed *= table.GetStatValue(recipe.workTableSpeedStat);
                if (DebugSettings.fastCrafting) speed *= 30;
                ticks = bill.workLeft / Mathf.Max(.0001f, speed);
                return true;
            }
            if (driver is JobDriver_PlantWork && target is Plant plant && _plantWork != null && toil.activeSkill != null)
            {
                ticks = (plant.def.plant.harvestWork - (float)_plantWork.GetValue(driver)) /
                    Mathf.Max(.0001f, JobDriver_PlantWork.WorkDonePerTick(pawn, plant));
                return true;
            }
            if (driver is JobDriver_PlantSow && target is Plant sownPlant && _sowWork != null && toil.activeSkill != null)
            {
                ticks = (sownPlant.def.plant.sowWork - (float)_sowWork.GetValue(driver)) /
                    Mathf.Max(.0001f, pawn.GetStatValue(StatDefOf.PlantWorkSpeed));
                return true;
            }
            if (driver is JobDriver_Mine && target != null && toil.activeSkill != null)
            {
                float speed = pawn.GetStatValue(StatDefOf.MiningSpeed);
                if (pawn.Faction != Faction.OfPlayer) speed = Mathf.Max(.6f, speed);
                float interval = (float)Math.Round(100f / Mathf.Max(.0001f, speed));
                int pending = _miningHitTicks == null ? -1000 : (int)_miningHitTicks.GetValue(driver);
                ticks = Mathf.Max(0, Mathf.Ceil(target.HitPoints / (target.def.building.isNaturalRock ? 80f : 40f)) - 1) * interval +
                    (pending < -100 ? interval : Mathf.Max(0, pending));
                return true;
            }
            if (driver is JobDriver_Repair && target != null && toil.activeSkill != null)
            {
                float pending = _repairTicks == null ? 80f : (float)_repairTicks.GetValue(driver);
                ticks = Mathf.Ceil((pending + Mathf.Max(0, target.MaxHitPoints - target.HitPoints - 1) * 20f) /
                    Mathf.Max(.0001f, pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f));
                return true;
            }
            if (driver is JobDriver_AffectFloor && toil.activeSkill != null && _floorWork != null && _floorSpeed != null)
            {
                var stat = _floorSpeed.GetValue(driver, null) as StatDef;
                float speed = stat != null && !stat.Worker.IsDisabledFor(pawn) ? pawn.GetStatValue(stat) : 1;
                ticks = (float)_floorWork.GetValue(driver) / Mathf.Max(.0001f, speed * 1.7f);
                return true;
            }
            if (driver is JobDriver_SmoothWall && toil.activeSkill != null && _wallWork != null)
            {
                ticks = (float)_wallWork.GetValue(driver) / Mathf.Max(.0001f, pawn.GetStatValue(StatDefOf.SmoothingSpeed) * 1.7f);
                return true;
            }
            if (driver is JobDriver_RemoveBuilding && toil.activeSkill != null && _removalWork != null)
            {
                ticks = (float)_removalWork.GetValue(driver) / Mathf.Max(.0001f, pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f);
                return true;
            }
            if (driver is JobDriver_Research && target is Building_ResearchBench && toil.activeSkill != null)
            {
                var project = Find.ResearchManager.GetProject(null);
                if (project == null) return false;
                float speed = pawn.GetStatValue(StatDefOf.ResearchSpeed) * target.GetStatValue(StatDefOf.ResearchSpeedFactor) *
                    .00825f * Find.Storyteller.difficulty.researchSpeedFactor;
                if (pawn.Faction != null) speed /= project.CostFactor(pawn.Faction.def.techLevel);
                if (DebugSettings.fastResearch) speed *= 500;
                ticks = (project.Cost - project.ProgressReal) / Mathf.Max(.0001f, speed);
                return true;
            }
            if (driver is JobDriver_Reading && job.playerForced && !job.doUntilGatheringEnded)
            {
                ticks = float.MaxValue;
                return true;
            }
            if (job.def.joyKind != null && pawn.needs?.joy != null)
            {
                var joy = pawn.needs.joy;
                float gain = job.def.joyGainRate * .36f / 2500f * joy.tolerances.JoyFactorFromTolerance(job.def.joyKind);
                if (target is Building source) gain *= source.GetStatValue(StatDefOf.JoyGainFactor);
                ticks = (1 - joy.CurLevel) / Mathf.Max(.0001f, gain);
                if ((job.doUntilGatheringEnded || !ManualWorkSessions.ContinuousRecreation(driver)) && _driverStartTick != null)
                    ticks = Mathf.Min(ticks, job.def.joyDuration - (Find.TickManager.TicksGame - (int)_driverStartTick.GetValue(driver)));
                return true;
            }
            if (toil.defaultCompleteMode == ToilCompleteMode.Delay && toil.defaultDuration > 0)
            {
                ticks = driver.ticksLeftThisToil;
                return true;
            }
            foreach (var mote in motes)
            {
                var linked = mote.link1.Target.Thing;
                if (mote.Destroyed || !mote.Spawned || mote.Alpha <= 0 ||
                    linked == null || linked != pawn && linked != target && linked != job.targetB.Thing) continue;
                float progress = mote.progress;
                int now = driver.debugTicksSpentThisToil;
                if (_sampleTick < 0 || progress < _sampleProgress)
                { _sampleTick = now; _sampleProgress = progress; return false; }
                if (now <= _sampleTick || progress <= _sampleProgress) return false;
                ticks = (1 - progress) * (now - _sampleTick) / (progress - _sampleProgress);
                return true;
            }
            return false;
        }
    }

    public sealed class WorkMinigameRewards : GameComponent
    {
        public sealed class Progress : IExposable
        {
            internal int JobId = -1, Milestone;
            internal int ResearchRewardToilIndex = -1, ResearchRewardTicks;
            public void ExposeData()
            {
                Scribe_Values.Look(ref JobId, "jobId", -1);
                Scribe_Values.Look(ref Milestone, "milestone");
                Scribe_Values.Look(ref ResearchRewardToilIndex, "researchRewardToilIndex", -1);
                Scribe_Values.Look(ref ResearchRewardTicks, "researchRewardTicks");
            }
        }

        Dictionary<int, Progress> _progress = new Dictionary<int, Progress>();
        public WorkMinigameRewards(Game game) { }

        internal int Completed(Pawn pawn, int jobId) => _progress.TryGetValue(pawn.thingIDNumber, out var progress) &&
            progress.JobId == jobId ? progress.Milestone : 0;

        public void Apply(PawnControl control, int jobId, int toilIndex, int milestone)
        {
            if (Rim3DMultiplayer.LocalInput || control?.Active != true || !control.IsActing) return;
            bool allowed = Rim3DMultiplayer.Active ? Rim3DMultiplayer.MinigameRewardsAllowed?.Invoke() == true :
                Rim3DSettings.Current.MinigameWorkBonus;
            var pawn = control.Pawn;
            var driver = pawn?.jobs?.curDriver;
            if (!allowed || pawn == null || pawn.Dead || pawn.Downed || pawn.Drafted || pawn.pather.Moving ||
                pawn.CurJob?.loadID != jobId || driver == null || driver.CurToilIndex != toilIndex ||
                control.WaitingForManualApproach) return;
            bool paused = Rim3DMultiplayer.Active ? (Rim3DMultiplayer.MinigameTickRate?.Invoke(pawn.Map) ?? 0) <= 0 :
                Find.TickManager.Paused;
            if (paused) return;
            if (milestone <= Completed(pawn, jobId)) return;
            _progress.TryGetValue(pawn.thingIDNumber, out var progress);
            bool research = driver is JobDriver_Research;
            int researchTicks = progress != null && progress.JobId == jobId && progress.ResearchRewardToilIndex == toilIndex ?
                progress.ResearchRewardTicks : 0;
            if (research && driver.debugTicksSpentThisToil / 2 - researchTicks < WorkMinigames.ResearchTicksPerReward) return;
            var toil = WorkDurationEstimate.CurrentToil(pawn);
            if (toil == null || toil.defaultCompleteMode == ToilCompleteMode.Instant ||
                toil.defaultCompleteMode == ToilCompleteMode.PatherArrival ||
                !WorkProgressBonus.Apply(pawn, toil, WorkMinigames.WorkFractionPerReward)) return;
            if (progress == null)
                _progress.Add(pawn.thingIDNumber, progress = new Progress());
            progress.JobId = jobId;
            progress.Milestone = milestone;
            progress.ResearchRewardToilIndex = research ? toilIndex : -1;
            progress.ResearchRewardTicks = research ? researchTicks + WorkMinigames.ResearchTicksPerReward : 0;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref _progress, "rim3DMinigameRewards", LookMode.Value, LookMode.Deep);
            if (_progress == null) _progress = new Dictionary<int, Progress>();
        }
    }

    static class WorkProgressBonus
    {
        static readonly FieldInfo _plantWork = typeof(JobDriver_PlantWork).GetField("workDone", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _sowWork = typeof(JobDriver_PlantSow).GetField("sowWorkDone", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _floorWork = typeof(JobDriver_AffectFloor).GetField("workLeft", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo _floorTotal = typeof(JobDriver_AffectFloor).GetProperty("BaseWorkAmount", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo _floorSpeed = typeof(JobDriver_AffectFloor).GetProperty("SpeedStat", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _wallWork = typeof(JobDriver_SmoothWall).GetField("workLeft", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo _wallTotal = typeof(JobDriver_SmoothWall).GetProperty("BaseWorkAmount", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _removalWork = typeof(JobDriver_RemoveBuilding).GetField("workLeft", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _removalTotal = typeof(JobDriver_RemoveBuilding).GetField("totalNeededWork", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool Apply(Pawn pawn, Toil toil, float fraction)
        {
            var job = pawn.CurJob;
            float multiplier = WorkMinigames.RewardMultiplier(WorkMinigames.Kind(job.def, job.targetA.Thing, job.bill is Bill_Medical));
            if (multiplier <= 0) return false;
            fraction *= multiplier;
            if (WorkMinigames.TryAdvanceWork(pawn, fraction, out bool applied)) return applied;
            var driver = pawn.jobs.curDriver;
            var target = job.targetA.Thing;
            if (driver is JobDriver_Research && target is Building_ResearchBench bench && toil.activeSkill != null)
            {
                var project = Find.ResearchManager.GetProject(null);
                if (project == null || project.IsFinished || !project.CanBeResearchedAt(bench, ignoreResearchBenchPowerStatus: false)) return false;
                float factor = .00825f * Find.Storyteller.difficulty.researchSpeedFactor;
                if (pawn.Faction != null) factor /= project.CostFactor(pawn.Faction.def.techLevel);
                if (DebugSettings.fastResearch) factor *= 500;
                if (factor <= 0) return false;
                float before = project.ProgressReal;
                float rate = pawn.GetStatValue(StatDefOf.ResearchSpeed) * bench.GetStatValue(StatDefOf.ResearchSpeedFactor);
                float work = Mathf.Min(project.Cost * Mathf.Min(fraction, .01f * multiplier),
                    rate * factor * WorkMinigames.ResearchTicksPerReward * multiplier, project.Cost - project.ProgressReal);
                if (work <= 0) return false;
                Find.ResearchManager.ResearchPerformed(work / factor, pawn);
                return project.IsFinished || project.ProgressReal > before;
            }
            if (driver is JobDriver_ConstructFinishFrame && target is Frame frame && toil.activeSkill != null)
            {
                if (frame.WorkToBuild <= 0 || frame.workDone >= frame.WorkToBuild) return false;
                float speed = pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f;
                if (frame.Stuff != null) speed *= frame.Stuff.GetStatValueAbstract(StatDefOf.ConstructionSpeedFactor);
                fraction = LimitedFraction(frame.WorkToBuild, speed, fraction, multiplier);
                if (fraction <= 0) return false;
                frame.workDone = Mathf.Min(frame.WorkToBuild, frame.workDone + frame.WorkToBuild * fraction);
                return true;
            }
            if (driver is JobDriver_DoBill bill && job.bill != null && toil.activeSkill != null)
            {
                float total = job.bill.GetWorkAmount(job.targetB.Thing);
                if (total <= 0 || bill.workLeft <= 0) return false;
                if (!(job.bill is Bill_Medical) && job.RecipeDef != null)
                {
                    var recipe = job.RecipeDef;
                    float speed = recipe.workSpeedStat == null ? 1 : pawn.GetStatValue(recipe.workSpeedStat);
                    if (recipe.workTableSpeedStat != null && target is Building_WorkTable table)
                        speed *= table.GetStatValue(recipe.workTableSpeedStat);
                    if (DebugSettings.fastCrafting) speed *= 30;
                    fraction = LimitedFraction(total, speed, fraction, multiplier);
                }
                if (fraction <= 0) return false;
                bill.workLeft = Mathf.Max(0, bill.workLeft - total * fraction);
                if (job.targetB.Thing is UnfinishedThing unfinished) unfinished.workLeft = bill.workLeft;
                return true;
            }
            if (driver is JobDriver_PlantWork && target is Plant plant && toil.activeSkill != null)
            {
                float total = plant.def.plant.harvestWork;
                if (driver is JobDriver_PlantHarvest && !WorkMinigames.IsWoodcutting(job.def, target))
                    fraction = LimitedFraction(total, JobDriver_PlantWork.WorkDonePerTick(pawn, plant), fraction, multiplier);
                return AdvanceCompleted(driver, _plantWork, total, fraction);
            }
            if (driver is JobDriver_PlantSow && target is Plant sown && toil.activeSkill != null)
                return AdvanceCompleted(driver, _sowWork, sown.def.plant.sowWork,
                    LimitedFraction(sown.def.plant.sowWork, pawn.GetStatValue(StatDefOf.PlantWorkSpeed), fraction, multiplier));
            if (driver is JobDriver_Mine && target?.def.mineable == true && toil.activeSkill != null)
            {
                int damage = Mathf.Min(Mathf.CeilToInt(target.MaxHitPoints * fraction), target.HitPoints - 1);
                if (damage <= 0) return false;
                int before = target.HitPoints;
                target.TakeDamage(new DamageInfo(DamageDefOf.Mining, damage, 0, -1, pawn));
                return target.Destroyed || target.HitPoints < before;
            }
            if (driver is JobDriver_Repair && target is Building building && toil.activeSkill != null)
            {
                fraction = LimitedFraction(building.MaxHitPoints, pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f / 20f, fraction, multiplier);
                int hitPoints = Mathf.Min(building.MaxHitPoints - 1, building.HitPoints + Mathf.CeilToInt(building.MaxHitPoints * fraction));
                if (hitPoints <= building.HitPoints) return false;
                building.HitPoints = hitPoints;
                pawn.Map.listerBuildingsRepairable.Notify_BuildingRepaired(building);
                return true;
            }
            if (driver is JobDriver_AffectFloor && toil.activeSkill != null && _floorTotal != null)
            {
                float total = (int)_floorTotal.GetValue(driver, null);
                if (driver is JobDriver_SmoothFloor || driver is JobDriver_RemoveFloor)
                {
                    var speedStat = _floorSpeed?.GetValue(driver, null) as StatDef;
                    float speed = speedStat == null || speedStat.Worker.IsDisabledFor(pawn) ? 1 : pawn.GetStatValue(speedStat);
                    fraction = LimitedFraction(total, speed * 1.7f, fraction, multiplier);
                }
                return AdvanceRemaining(driver, _floorWork, total, fraction);
            }
            if (driver is JobDriver_SmoothWall && toil.activeSkill != null && _wallTotal != null)
            {
                float total = (int)_wallTotal.GetValue(driver, null);
                return AdvanceRemaining(driver, _wallWork, total, LimitedFraction(total,
                    pawn.GetStatValue(StatDefOf.SmoothingSpeed) * 1.7f, fraction, multiplier));
            }
            if (driver is JobDriver_RemoveBuilding && toil.activeSkill != null && _removalTotal != null)
            {
                float total = (float)_removalTotal.GetValue(driver);
                return AdvanceRemaining(driver, _removalWork, total, LimitedFraction(total,
                    pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f, fraction, multiplier));
            }
            if (driver.uninstallWorkLeft > 0 && target?.def.building != null &&
                toil.defaultCompleteMode == ToilCompleteMode.Never)
            {
                driver.uninstallWorkLeft = Mathf.Max(0, driver.uninstallWorkLeft - target.def.building.uninstallWork * fraction);
                return true;
            }
            if (job.def.joyKind != null && pawn.needs?.joy != null)
            {
                pawn.needs.joy.GainJoy(fraction, job.def.joyKind);
                return true;
            }
            if (toil.defaultCompleteMode == ToilCompleteMode.Delay && driver.ticksLeftThisToil > 1)
            {
                int total = Mathf.Max(toil.defaultDuration, driver.ticksLeftThisToil + driver.debugTicksSpentThisToil);
                int ticks = Mathf.CeilToInt(total * fraction);
                if (driver is JobDriver_TendPatient || driver is JobDriver_Rim3DTend)
                    ticks = Mathf.Min(ticks, Mathf.CeilToInt(WorkMinigames.ActivityTicksPerReward * multiplier));
                driver.ticksLeftThisToil = Mathf.Max(1, driver.ticksLeftThisToil - ticks);
                return true;
            }
            return false;
        }

        static float LimitedFraction(float total, float speed, float fraction, float multiplier) => total > 0 ?
            Mathf.Max(0, Mathf.Min(fraction, speed * WorkMinigames.ActivityTicksPerReward * multiplier / total)) : 0;

        static bool AdvanceRemaining(JobDriver driver, FieldInfo field, float total, float fraction)
        {
            if (field == null || total <= 0 || fraction <= 0) return false;
            float left = (float)field.GetValue(driver);
            if (left <= 0) return false;
            field.SetValue(driver, Mathf.Max(0, left - total * fraction));
            return true;
        }

        static bool AdvanceCompleted(JobDriver driver, FieldInfo field, float total, float fraction)
        {
            if (field == null || total <= 0 || fraction <= 0) return false;
            float done = (float)field.GetValue(driver);
            if (done < 0 || done >= total) return false;
            field.SetValue(driver, Mathf.Min(total, done + total * fraction));
            return true;
        }
    }

    sealed class WorkMinigameSession : IDisposable
    {
        readonly WorkDurationEstimate _estimate = new WorkDurationEstimate();
        readonly Queue<int> _pendingRewards = new Queue<int>();
        PawnControl _control;
        Job _job;
        Zone_Growing _sowingZone;
        int _toilIndex = -1, _points, _lastScore, _sentMilestones, _nextEstimateTick, _startTick, _rewardPointBase;
        float _initialRate;
        float _nextRewardSend;
        bool _evaluated;
        WorkMinigame _game;
        MinigameWindow _window;
        bool Open => _window?.IsOpen == true;
        internal bool HasWindowInput => Open && Find.WindowStack.GetsInput(_window);
        internal bool InputFocused => Application.isFocused && HasWindowInput;

        internal void Update(PawnControl control, bool firstPerson, bool canOpen, IEnumerable<MoteProgressBar> motes, Zone_Growing sowingZone)
        {
            var pawn = control.Pawn;
            if (!firstPerson || !Rim3DSettings.Current.Minigames || !control.Active ||
                control.InputBlocked || pawn?.jobs?.curDriver == null)
            { Dispose(); return; }
            if (!control.IsActing)
            {
                if (_control == control && _sowingZone != null && _sowingZone == sowingZone) _game?.Update(true);
                else Dispose();
                return;
            }
            var job = pawn.CurJob;
            var kind = WorkMinigames.Kind(job.def, job.targetA.Thing, job.bill is Bill_Medical);
            if (!Rim3DSettings.Current.MinigameEnabled(kind)) { Dispose(); return; }
            bool rewardEnabled = Rim3DSettings.Current.MinigameWorkBonusEnabled && WorkMinigames.RewardMultiplier(kind) > 0;
            if (job.def != JobDefOf.Sow) sowingZone = null;
            int toilIndex = pawn.jobs.curDriver.CurToilIndex;
            if (_control != control || _job != job || _sowingZone != sowingZone)
            {
                bool keepGame = _control == control && _sowingZone != null && _sowingZone == sowingZone && _game != null;
                var jobRewards = Current.Game.GetComponent<WorkMinigameRewards>();
                int carry = 0;
                if (keepGame)
                {
                    int confirmed = jobRewards.Completed(pawn, _job.loadID);
                    foreach (int pendingMilestone in _pendingRewards) if (pendingMilestone > confirmed) carry++;
                }
                if (!keepGame) Dispose();
                _control = control; _job = job;
                _sowingZone = sowingZone;
                _toilIndex = -1;
                _pendingRewards.Clear();
                _nextRewardSend = 0;
                _sentMilestones = jobRewards.Completed(pawn, job.loadID);
                if (keepGame)
                {
                    for (int i = 0; i < carry; i++) _pendingRewards.Enqueue(++_sentMilestones);
                    _rewardPointBase = _points - _sentMilestones * Mathf.Max(1, _game.PointsPerReward);
                }
            }
            int workTick = pawn.jobs.curDriver.debugTicksSpentThisToil;
            if (_toilIndex != toilIndex || workTick < _startTick)
            {
                _toilIndex = toilIndex;
                _startTick = workTick;
                _nextEstimateTick = 0;
                _initialRate = 0;
                _evaluated = false;
                _estimate.Reset();
            }
            bool moving = control.WaitingForManualApproach || pawn.pather.Moving;
            float rate = Rim3DMultiplayer.Active ? Rim3DMultiplayer.MinigameTickRate?.Invoke(pawn.Map) ?? 0 :
                Find.TickManager.TickRateMultiplier;
            if (!moving && _initialRate <= 0 && rate > 0) _initialRate = rate;
            if (_game == null && !_evaluated && !moving && rate > 0 && workTick >= _nextEstimateTick)
            {
                _nextEstimateTick = workTick + 15;
                if (_estimate.TryRemaining(pawn, motes, out float ticks))
                {
                    _evaluated = true;
                    float duration = ticks + workTick;
                    if (sowingZone != null) duration = Mathf.Max(duration, WorkDurationEstimate.SowingZoneTicks(pawn, sowingZone));
                    if (!float.IsNaN(duration) && !float.IsInfinity(duration) && duration / (60f * _initialRate) >= WorkMinigames.MinimumSeconds)
                    {
                        _game = WorkMinigames.Create(pawn);
                        _points = _sentMilestones * Mathf.Max(1, _game.PointsPerReward);
                    }
                }
            }
            if (Rim3DSettings.Current.HideInterface && Open) _window.Close(false);
            if (_game != null && _window == null && !moving && canOpen) Show();
            bool paused = moving || rate <= 0 || !Application.isFocused || Find.WindowStack.WindowsForcePause ||
                Open && !InputFocused;
            if (Open)
            {
                _game.Update(paused);
                _points += Mathf.Max(0, _game.Score - _lastScore);
                _lastScore = _game.Score;
                int milestones = (_points - _rewardPointBase) / Mathf.Max(1, _game.PointsPerReward);
                while (!paused && _sentMilestones < milestones)
                {
                    _sentMilestones++;
                    Rand.PushState();
                    try { SoundDefOf.TinyBell.PlayOneShotOnCamera(); }
                    finally { Rand.PopState(); }
                    if (rewardEnabled)
                        _pendingRewards.Enqueue(_sentMilestones);
                }
            }
            if (!rewardEnabled) { _pendingRewards.Clear(); return; }
            var rewards = Current.Game.GetComponent<WorkMinigameRewards>();
            int completed = rewards.Completed(pawn, job.loadID);
            while (_pendingRewards.Count > 0 && _pendingRewards.Peek() <= completed)
            {
                _pendingRewards.Dequeue();
                _nextRewardSend = 0;
            }
            if (paused || _pendingRewards.Count == 0 || Time.unscaledTime < _nextRewardSend) return;
            int milestone = _pendingRewards.Peek();
            _nextRewardSend = Time.unscaledTime + 1;
            if (Rim3DMultiplayer.Active)
                Rim3DMultiplayer.MinigameReward?.Invoke(control, job.loadID, toilIndex, milestone);
            else rewards.Apply(control, job.loadID, toilIndex, milestone);
        }

        internal void DrawLauncher(Rect viewport, bool visible)
        {
            if (_game == null || Open || !visible) return;
            if (Widgets.ButtonText(new Rect(viewport.center.x - 82, viewport.yMax - 86, 164, 32), "Rim3D_MinigameOpen".Translate())) Show();
        }

        void Show()
        {
            if (_game == null || Open || Rim3DSettings.Current.HideInterface) return;
            _window = new MinigameWindow(this);
            Find.WindowStack.Add(_window);
        }

        public void Dispose()
        {
            if (Open) _window.Close(false);
            _game?.Dispose();
            _window = null; _game = null; _job = null; _control = null;
            _sowingZone = null;
            _rewardPointBase = 0;
            _toilIndex = -1; _points = _lastScore = _sentMilestones = _nextEstimateTick = 0;
            _evaluated = false;
            _initialRate = 0;
            _nextRewardSend = 0;
            _pendingRewards.Clear();
            _estimate.Reset();
        }

        sealed class MinigameWindow : Window
        {
            readonly WorkMinigameSession _session;
            public override Vector2 InitialSize => _session._game.WindowSize + new Vector2(0, 28);
            internal MinigameWindow(WorkMinigameSession session)
            {
                _session = session;
                layer = WindowLayer.Dialog;
                forcePause = false;
                preventCameraMotion = absorbInputAroundWindow = true;
                doCloseX = draggable = true;
                closeOnAccept = false;
            }

            public override void DoWindowContents(Rect rect)
            {
                if (_session._game == null) return;
                var font = Text.Font;
                var anchor = Text.Anchor;
                try
                {
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.Font = GameFont.Medium;
                    Widgets.Label(new Rect(0, 0, rect.width - 30, 30), _session._game.Title);
                    Text.Font = GameFont.Small;
                    Widgets.Label(new Rect(0, 32, rect.width, 28), _session._game.ScoreLabel(_session._points));
                    var job = _session._job;
                    float multiplier = WorkMinigames.RewardMultiplier(WorkMinigames.Kind(job.def, job.targetA.Thing, job.bill is Bill_Medical));
                    Widgets.Label(new Rect(0, 60, rect.width, 28), Rim3DSettings.Current.MinigameWorkBonusEnabled && multiplier > 0
                        ? "Rim3D_MinigameRewardScale".Translate(multiplier.ToString("0.00"))
                        : "Rim3D_MinigameExperienceOff".Translate());
                    _session._game.Draw(new Rect(0, 96, rect.width, rect.height - 96));
                }
                finally
                {
                    Text.Font = font;
                    Text.Anchor = anchor;
                }
            }
        }
    }

    sealed class MiningMinigame : WorkMinigame
    {
        const int HitsToBreak = 3;
        const float TargetRadius = .12f, PerfectRadius = .035f;
        readonly System.Random _random = new System.Random();
        Texture2D _rockTexture;
        Vector2 _weakSpot, _impact;
        int _score, _hits;
        float _phase, _target, _cooldown, _feedbackTime;
        bool _paused = true;
        string _feedback;
        Color _feedbackColor;
        public override string Title => "Rim3D_MinigameMining".Translate().ToString();
        public override int PointsPerReward => 1;
        public override string Instructions => "Rim3D_MiningHint".Translate().ToString();
        public override int Score => _score;

        internal MiningMinigame() { Restart(); }

        public override void Restart()
        {
            _score = _hits = 0;
            _phase = _feedbackTime = 0;
            _cooldown = .4f;
            _paused = true;
            _feedback = null;
            NextWeakSpot();
        }

        void NextWeakSpot()
        {
            float x = .28f + (float)_random.NextDouble() * .44f;
            _weakSpot = new Vector2(x, 1f - VeinHeight(x));
            _hits = 0;
            MoveTarget();
        }

        void MoveTarget() => _target = .2f + (float)_random.NextDouble() * .6f;

        public override void Update(bool paused)
        {
            _paused = paused;
            if (paused) return;
            float delta = Mathf.Min(Time.unscaledDeltaTime, .05f);
            _feedbackTime = Mathf.Max(0, _feedbackTime - delta);
            _cooldown = Mathf.Max(0, _cooldown - delta);
            if (_cooldown > 0) return;
            _phase = (_phase + delta * (.75f + _hits * .12f)) % 2f;
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return)) Strike();
        }

        void Strike()
        {
            if (_paused || _cooldown > 0) return;
            float distance = Mathf.Abs(Mathf.PingPong(_phase, 1f) - _target);
            _impact = _weakSpot;
            _cooldown = .35f;
            _feedbackTime = 1.4f;
            if (distance > TargetRadius)
            {
                _hits = Mathf.Max(0, _hits - 1);
                _feedback = "Rim3D_MiningMiss".Translate().ToString();
                _feedbackColor = new Color(.95f, .45f, .35f);
            }
            else
            {
                bool perfect = distance <= PerfectRadius;
                _hits++;
                _feedbackColor = perfect ? new Color(1f, .8f, .3f) : new Color(.45f, .9f, .7f);
                if (_hits == HitsToBreak)
                {
                    int points = perfect ? 2 : 1;
                    _score += points;
                    _feedback = "Rim3D_MiningExtracted".Translate(points).ToString();
                    NextWeakSpot();
                    return;
                }
                _feedback = (perfect ? "Rim3D_MiningPerfect" : "Rim3D_MiningHit").Translate().ToString();
            }
            MoveTarget();
        }

        static float VeinHeight(float x) => .5f + Mathf.Sin(x * 9f) * .12f + Mathf.Sin(x * 23f) * .035f;

        void CreateRockTexture()
        {
            const int width = 384, height = 192;
            var seeds = new Vector2[24];
            var random = new System.Random(1843);
            for (int i = 0; i < seeds.Length; i++)
                seeds[i] = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float u = (x + .5f) / width, v = (y + .5f) / height;
                    float dx = (u - .5f) / .47f, dy = (v - .5f) / .44f;
                    float radius = dx * dx + dy * dy + Mathf.Sin(u * 31f + v * 17f) * .065f;
                    float noise = Mathf.PerlinNoise(x * .045f, y * .045f);
                    Color pixel = new Color(.045f, .06f, .075f);
                    if (radius < 1f)
                    {
                        float nearest = float.MaxValue, second = float.MaxValue;
                        int face = 0;
                        for (int i = 0; i < seeds.Length; i++)
                        {
                            float sx = (u - seeds[i].x) * 1.6f, sy = v - seeds[i].y;
                            float distance = sx * sx + sy * sy;
                            if (distance < nearest) { second = nearest; nearest = distance; face = i; }
                            else if (distance < second) second = distance;
                        }
                        float light = .65f + (face * 7 % 11) * .055f + v * .3f + noise * .22f;
                        light *= Mathf.Lerp(.4f, 1f, Mathf.Clamp01((1f - radius) * 9f));
                        if (second - nearest < .0012f) light *= .5f;
                        pixel = new Color(.27f, .3f, .34f) * light;
                        float vein = Mathf.Abs(v - VeinHeight(u));
                        if (u > .32f && u < .68f) vein = Mathf.Min(vein, Mathf.Abs(v - (.14f + u * .8f)));
                        if (vein < .013f + noise * .022f)
                        {
                            pixel = Color.Lerp(new Color(.36f, .22f, .1f), new Color(.95f, .69f, .28f), noise) * light;
                            if (vein < .004f) pixel = new Color(1f, .84f, .48f) * light;
                        }
                    }
                    pixel.a = 1;
                    pixels[y * width + x] = pixel;
                }
            _rockTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "RimVision mining rock",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _rockTexture.SetPixels32(pixels);
            _rockTexture.Apply(false, true);
        }

        static void DrawStroke(Vector2 start, Vector2 end, Color color, float width)
        {
            var matrix = GUI.matrix;
            try
            {
                var delta = end - start;
                GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
                Widgets.DrawBoxSolid(new Rect(start.x, start.y - width * .5f, delta.magnitude, width), color);
            }
            finally { GUI.matrix = matrix; }
        }

        void DrawRock(Rect board)
        {
            if (_rockTexture == null) CreateRockTexture();
            GUI.DrawTexture(board, _rockTexture);
            var gold = new Color(.95f, .73f, .36f);
            var focus = new Vector2(board.x + _weakSpot.x * board.width, board.y + _weakSpot.y * board.height);
            for (int hit = 0; hit < _hits; hit++)
                for (int branch = 0; branch < 3; branch++)
                {
                    float angle = branch * 2.1f + hit * .9f;
                    var bend = focus + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (15 + hit * 10);
                    var end = bend + new Vector2(Mathf.Cos(angle + .6f), Mathf.Sin(angle + .6f)) * 17;
                    DrawStroke(focus, bend, new Color(.025f, .03f, .04f), 3);
                    DrawStroke(bend, end, new Color(.025f, .03f, .04f), 2);
                }
            float pulse = 14f + Mathf.Sin(_phase * Mathf.PI * 2) * 2f;
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * .5f;
                var start = focus + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * pulse;
                var end = focus + new Vector2(Mathf.Cos(a + Mathf.PI * .5f), Mathf.Sin(a + Mathf.PI * .5f)) * pulse;
                DrawStroke(start, end, gold, 1.5f);
            }
            var impact = _cooldown > 0 && _feedbackTime > 0 ?
                new Vector2(board.x + _impact.x * board.width, board.y + _impact.y * board.height) : focus;
            float recoil = _feedbackTime > 0 ? _cooldown / .35f : 0;
            var head = impact + new Vector2(26, -24) * recoil;
            DrawStroke(head + new Vector2(2, 3), head + new Vector2(39, 57), new Color(.03f, .035f, .04f), 9);
            DrawStroke(head, head + new Vector2(36, 54), new Color(.5f, .32f, .17f), 6);
            DrawStroke(head + new Vector2(2, 4), head + new Vector2(32, 49), new Color(.72f, .5f, .28f), 2);
            DrawStroke(head + new Vector2(-19, 7), head + new Vector2(20, -8), new Color(.64f, .7f, .75f), 7);
            DrawStroke(head + new Vector2(-19, 4), head + new Vector2(18, -10), new Color(.88f, .91f, .93f), 2);
            DrawStroke(head + new Vector2(20, -8), head + new Vector2(25, 1), new Color(.64f, .7f, .75f), 4);
            if (recoil > 0)
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * 2.4f;
                    float distance = (1f - recoil) * (24 + i * 3);
                    var spark = impact + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                    var tint = _feedbackColor;
                    tint.a = recoil;
                    Widgets.DrawBoxSolid(new Rect(spark.x, spark.y, 3, 3), tint);
                }
        }

        public override void Draw(Rect rect)
        {
            var font = Text.Font;
            var anchor = Text.Anchor;
            var color = GUI.color;
            bool enabled = GUI.enabled;
            try
            {
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.Label(new Rect(rect.x, rect.y, rect.width, 32), Instructions);
                var board = new Rect(rect.x, rect.y + 36, rect.width, Mathf.Max(60, rect.height - 190));
                DrawRock(board);
                Text.Anchor = TextAnchor.MiddleCenter;
                Text.Font = GameFont.Small;
                GUI.color = _feedbackTime > 0 ? _feedbackColor : Color.white;
                Widgets.Label(new Rect(rect.x, board.yMax + 3, rect.width - 68, 28), _feedbackTime > 0 ? _feedback :
                    "Rim3D_MiningRockProgress".Translate(_hits, HitsToBreak).ToString());
                GUI.color = color;
                for (int i = 0; i < HitsToBreak; i++)
                    Widgets.DrawBoxSolid(new Rect(rect.xMax - 60 + i * 20, board.yMax + 13, 12, 5),
                        i < _hits ? new Color(.95f, .73f, .36f) : new Color(.25f, .28f, .3f));
                var bar = new Rect(rect.x + 8, board.yMax + 39, rect.width - 16, 18);
                Widgets.DrawBoxSolid(new Rect(bar.x - 2, bar.y - 2, bar.width + 4, bar.height + 4), new Color(.25f, .28f, .3f));
                Widgets.DrawBoxSolid(bar, new Color(.065f, .085f, .1f));
                Widgets.DrawBoxSolid(new Rect(bar.x + (_target - TargetRadius) * bar.width, bar.y,
                    TargetRadius * 2 * bar.width, bar.height), new Color(.19f, .5f, .4f));
                Widgets.DrawBoxSolid(new Rect(bar.x + (_target - PerfectRadius) * bar.width, bar.y,
                    PerfectRadius * 2 * bar.width, bar.height), new Color(.95f, .73f, .36f));
                Widgets.DrawBoxSolid(new Rect(Mathf.Clamp(bar.x + Mathf.PingPong(_phase, 1f) * bar.width - 1.5f,
                    bar.x, bar.xMax - 3), bar.y - 4,
                    3, bar.height + 8), Color.white);
                GUI.enabled = enabled && !_paused && _cooldown <= 0;
                var button = new Rect(rect.x + 8, bar.yMax + 16, rect.width - 16, 34);
                var edge = GUI.enabled && button.Contains(Event.current.mousePosition) ?
                    new Color(.95f, .73f, .36f) : new Color(.38f, .34f, .26f);
                Widgets.DrawBoxSolid(button, edge);
                Widgets.DrawBoxSolid(new Rect(button.x + 1, button.y + 1, button.width - 2, button.height - 2),
                    new Color(.09f, .11f, .13f));
                Widgets.Label(button, "Rim3D_MiningStrike".Translate());
                if (Widgets.ButtonInvisible(button)) Strike();
                GUI.enabled = enabled;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rect.x, bar.yMax + 54, rect.width, 36), "Rim3D_MiningReward".Translate(
                    PointsPerReward, WorkMinigames.WorkFractionPerReward * 100));
            }
            finally
            {
                GUI.enabled = enabled;
                GUI.color = color;
                Text.Font = font;
                Text.Anchor = anchor;
            }
        }

        public override void Dispose()
        {
            if (_rockTexture != null) UnityEngine.Object.Destroy(_rockTexture);
            _rockTexture = null;
        }
    }

    sealed class SnakeMinigame : WorkMinigame
    {
        const int Size = 16;
        readonly List<Vector2Int> _body = new List<Vector2Int>(Size * Size);
        readonly System.Random _random = new System.Random();
        Vector2Int _direction, _nextDirection, _apple;
        float _nextStep;
        bool _lost, _won;
        int _score;
        public override string Title => "Rim3D_MinigameSnake".Translate().ToString();
        public override int Score => _score;
        public override string ScoreLabel(int score) => "Rim3D_MinigameScore".Translate(score).ToString();
        public override string Instructions => "Rim3D_MinigameHint".Translate(
            PointsPerReward, WorkMinigames.WorkFractionPerReward * 100).ToString();

        internal SnakeMinigame() { Restart(); }
        public override void Restart()
        {
            _body.Clear();
            _body.Add(new Vector2Int(5, 8)); _body.Add(new Vector2Int(4, 8)); _body.Add(new Vector2Int(3, 8));
            _direction = _nextDirection = Vector2Int.right;
            _lost = _won = false; _score = 0;
            _nextStep = Time.unscaledTime + .5f;
            PlaceApple();
        }

        void PlaceApple()
        {
            int free = Size * Size - _body.Count;
            if (free == 0) { _won = true; return; }
            int chosen = _random.Next(free);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!_body.Contains(cell) && chosen-- == 0) { _apple = cell; return; }
                }
        }

        public override void Update(bool paused)
        {
            if (paused) { _nextStep = Time.unscaledTime + .18f; return; }
            if (_lost || _won) return;
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) Turn(Vector2Int.down);
            else if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) Turn(Vector2Int.up);
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) Turn(Vector2Int.left);
            else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) Turn(Vector2Int.right);
            if (Time.unscaledTime < _nextStep) return;
            _nextStep = Time.unscaledTime + .18f;
            _direction = _nextDirection;
            var head = _body[0] + _direction;
            bool apple = head == _apple;
            if (head.x < 0 || head.y < 0 || head.x >= Size || head.y >= Size)
            { _lost = true; return; }
            int collision = _body.IndexOf(head);
            if (collision >= 0 && (apple || collision < _body.Count - 1)) { _lost = true; return; }
            if (!apple) _body.RemoveAt(_body.Count - 1);
            _body.Insert(0, head);
            if (apple) { _score++; PlaceApple(); }
        }

        void Turn(Vector2Int direction)
        {
            if (direction != -_direction) _nextDirection = direction;
        }

        public override void Draw(Rect rect)
        {
            float side = Mathf.Min(rect.width, rect.height);
            var board = new Rect(rect.center.x - side / 2, rect.y, side, side);
            Widgets.DrawBoxSolid(board, new Color(.035f, .055f, .07f));
            float cell = side / Size;
            foreach (var part in _body)
                Widgets.DrawBoxSolid(new Rect(board.x + part.x * cell + 1, board.y + part.y * cell + 1, cell - 2, cell - 2),
                    part == _body[0] ? Color.white : new Color(.35f, .8f, .5f));
            if (!_won) Widgets.DrawBoxSolid(new Rect(board.x + _apple.x * cell + 2, board.y + _apple.y * cell + 2, cell - 4, cell - 4),
                new Color(.95f, .3f, .25f));
            if (!_lost && !_won) return;
            var panel = new Rect(board.x + 12, board.center.y - 26, board.width - 24, 52);
            Widgets.DrawBoxSolid(panel, new Color(.06f, .08f, .1f, .95f));
            if (Widgets.ButtonText(new Rect(panel.x + 8, panel.y + 8, panel.width - 16, 36), "Rim3D_MinigameRestart".Translate()))
                Restart();
        }
    }
}
