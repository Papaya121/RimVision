using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    public enum RimVisionLifecycleKind { Opened, Closing, MapChanged, ControlAcquired, ControlReleased, CarrierChanged, Paused, Resumed }
    public enum RimVisionViewCloseReason { ViewClosed, MapChanged, MapRemoved, RegistrationRemoved, CallbackFailed }
    public readonly struct RimVisionLifecycleEvent
    {
        public RimVisionLifecycleKind Kind { get; }
        public RimVisionViewCloseReason Reason { get; }
        public Map PreviousMap { get; }
        public Pawn PreviousPawn { get; }
        internal RimVisionLifecycleEvent(RimVisionLifecycleKind kind, RimVisionViewCloseReason reason = default, Map map = null, Pawn pawn = null)
        { Kind = kind; Reason = reason; PreviousMap = map; PreviousPawn = pawn; }
    }
    public enum RimVisionAnimationEventKind { Equip, Reload, Ability, Damage }
    public enum RimVisionAnimationPhase { Begin, Progress, Impact, Complete, Cancelled }
    public readonly struct RimVisionAnimationEvent
    {
        public Pawn Pawn { get; }
        public Thing Item { get; }
        public RimVisionAnimationEventKind Kind { get; }
        public RimVisionAnimationPhase Phase { get; }
        public float Progress { get; }
        public float Magnitude { get; }
        public int Tick { get; }
        internal RimVisionAnimationEvent(Pawn pawn, Thing item, RimVisionAnimationEventKind kind, RimVisionAnimationPhase phase, float progress, float magnitude)
        { Pawn = pawn; Item = item; Kind = kind; Phase = phase; Progress = Mathf.Clamp01(progress); Magnitude = magnitude;
            var map = RimVisionControl.MapFor(pawn);
            Tick = map != null && Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null ? Rim3DMultiplayer.MapTicks(map) : Find.TickManager?.TicksGame ?? 0;
        }
    }
    public static partial class RimVisionPresentation
    {
        public static void NotifyAnimation(Pawn pawn, RimVisionAnimationEventKind kind, RimVisionAnimationPhase phase,
            Thing item = null, float progress = 0, float magnitude = 0)
        {
            if (pawn == null || !Enum.IsDefined(typeof(RimVisionAnimationEventKind), kind) || !Enum.IsDefined(typeof(RimVisionAnimationPhase), phase) ||
                !RimVisionControl.Finite(progress) || !RimVisionControl.Finite(magnitude)) return;
            var notification = new RimVisionAnimationEvent(pawn, item, kind, phase, progress, magnitude);
            if (Rim3DMultiplayer.Active && Rim3DMultiplayer.GameExecution) Rim3DMultiplayer.AnimationEvent?.Invoke(notification);
            else Rim3DPreview.Instance?.ActiveWindow?.View?.NotifyAnimationEvent(notification);
        }
    }
}

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        public void NotifyAnimationEvent(API.RimVisionAnimationEvent notification)
        {
            if (!_apiDisposed && !simulationOnly && API.RimVisionControl.MapFor(notification.Pawn) == map &&
                (notification.Pawn == ControlledPawn || notification.Pawn.Spawned && !map.fogGrid.IsFogged(notification.Pawn.Position)))
                _apiExtensions?.NotifyAnimation(notification);
        }
    }
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    static class ApiDamageAnimationPatch
    {
        static void Postfix(Pawn __instance, float totalDamageDealt)
        {
            if (totalDamageDealt > 0)
                API.RimVisionPresentation.NotifyAnimation(__instance, API.RimVisionAnimationEventKind.Damage, API.RimVisionAnimationPhase.Impact, magnitude: totalDamageDealt);
        }
    }
}
