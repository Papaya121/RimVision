using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D.API
{
    public enum RimVisionJobStatus { None, Queued, Running, Succeeded, Failed, Interrupted, Rejected, Detached }
    public readonly struct RimVisionJobEvent
    {
        public int RequestId { get; }
        public string ActionId { get; }
        public Pawn Pawn { get; }
        public int JobId { get; }
        public JobDef JobDef { get; }
        public RimVisionJobStatus Status { get; }
        public JobCondition Condition { get; }
        internal RimVisionJobEvent(int requestId, string action, Pawn pawn, Job job, RimVisionJobStatus status, JobCondition condition)
        { RequestId = requestId; ActionId = action; Pawn = pawn; JobId = job.loadID; JobDef = job.def; Status = status; Condition = condition; }
    }
    public static class RimVisionJobs
    {
        sealed class TrackedJob
        {
            internal int RequestId;
            internal string Action;
            internal Pawn Pawn;
            internal Action<RimVisionJobEvent> Callback;
            internal bool Started, Ordering = true;
            internal void Notify(Job job, RimVisionJobStatus status, JobCondition condition = JobCondition.Ongoing)
            {
                try { Callback(new RimVisionJobEvent(RequestId, Action, Pawn, job, status, condition)); }
                catch (Exception error) { RimVisionDiagnostics.ReportFault("jobs", Action, error); }
            }
        }
        static ConditionalWeakTable<Job, TrackedJob> Tracked = new ConditionalWeakTable<Job, TrackedJob>();
        static System.WeakReference<Game> _game;
        internal static void Clear()
        { Tracked = new ConditionalWeakTable<Job, TrackedJob>(); _game = null; }
        static void EnsureGame()
        {
            var current = Current.Game;
            if (_game == null || !_game.TryGetTarget(out var previous) || previous != current)
            { Tracked = new ConditionalWeakTable<Job, TrackedJob>(); _game = current == null ? null : new System.WeakReference<Game>(current); }
        }
        internal static bool Order(PawnControl control, Job job, int request, string action, Action<RimVisionJobEvent> callback)
        {
            EnsureGame();
            if (job == null || request == 0 || callback == null) return control.OrderAction(job);
            if (Tracked.TryGetValue(job, out _)) return false;
            var tracked = new TrackedJob { RequestId = request, Action = action, Pawn = control.Pawn, Callback = callback };
            Tracked.Add(job, tracked);
            tracked.Notify(job, RimVisionJobStatus.Queued);
            bool accepted = false;
            try { accepted = control.OrderAction(job); return accepted; }
            finally
            {
                tracked.Ordering = false;
                if (!accepted && Tracked.TryGetValue(job, out var pending))
                { Tracked.Remove(job); pending.Notify(job, RimVisionJobStatus.Rejected, JobCondition.Incompletable); }
            }
        }
        public static void NotifyStarted(Pawn pawn, Job job)
        {
            EnsureGame();
            if (!Rim3DMultiplayer.GameExecution || job == null || !Tracked.TryGetValue(job, out var tracked) || tracked.Pawn != pawn || tracked.Started) return;
            tracked.Started = true; tracked.Notify(job, RimVisionJobStatus.Running);
        }
        public static void NotifyEnded(Pawn pawn, Job job, JobCondition condition)
        {
            EnsureGame();
            if (!Rim3DMultiplayer.GameExecution || job == null || !Tracked.TryGetValue(job, out var tracked) || tracked.Pawn != pawn) return;
            Tracked.Remove(job);
            var status = condition == JobCondition.Succeeded ? RimVisionJobStatus.Succeeded :
                condition == JobCondition.Incompletable || condition == JobCondition.Errored || condition == JobCondition.ErroredPather ? RimVisionJobStatus.Failed : RimVisionJobStatus.Interrupted;
            tracked.Notify(job, status, condition);
        }
        internal static void ReturnedToPool(Job job)
        {
            EnsureGame();
            if (job == null || !Rim3DMultiplayer.GameExecution || !Tracked.TryGetValue(job, out var tracked)) return;
            if (tracked.Ordering && !tracked.Started) { Tracked.Remove(job); tracked.Notify(job, RimVisionJobStatus.Rejected, JobCondition.Incompletable); return; }
            NotifyEnded(tracked.Pawn, job, JobCondition.InterruptForced);
        }
    }
}

namespace Recovery.Rim3D
{
    [HarmonyPatch(typeof(Verse.Profile.MemoryUtility), nameof(Verse.Profile.MemoryUtility.ClearAllMapsAndWorld))]
    static class ApiJobGameClosedPatch
    {
        static void Prefix() => API.RimVisionJobs.Clear();
    }
    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Notify_Starting))]
    static class ApiJobStartedPatch
    {
        static void Prefix(JobDriver __instance) => API.RimVisionJobs.NotifyStarted(__instance.pawn, __instance.job);
    }
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    static class ApiJobTrackerStartedPatch
    {
        static void Postfix(Pawn_JobTracker __instance, Job __0)
        {
            var driver = __instance.curDriver;
            if (driver?.job == __0) API.RimVisionJobs.NotifyStarted(driver.pawn, __0);
        }
    }
    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Cleanup))]
    static class ApiJobEndedPatch
    {
        static void Prefix(JobDriver __instance, JobCondition condition) => API.RimVisionJobs.NotifyEnded(__instance.pawn, __instance.job, condition);
    }
    [HarmonyPatch(typeof(JobMaker), nameof(JobMaker.ReturnToPool))]
    static class ApiJobPooledPatch
    {
        static void Prefix(Job job) => API.RimVisionJobs.ReturnedToPool(job);
    }
}
