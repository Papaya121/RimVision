using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    public struct RimVisionPose
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public RimVisionPose(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
        internal bool Valid => RimVisionControl.Finite(Position) && RimVisionControl.Finite(Rotation.x) &&
            RimVisionControl.Finite(Rotation.y) && RimVisionControl.Finite(Rotation.z) && RimVisionControl.Finite(Rotation.w) &&
            RimVisionControl.Finite(Quaternion.Dot(Rotation, Rotation)) && Quaternion.Dot(Rotation, Rotation) > .0001f;
    }

    public abstract class RimVisionPresentationProvider
    {
        public virtual RimVisionPresentationChannels Channels => RimVisionPresentationChannels.Legacy;
        public abstract bool Handles(Pawn pawn);
        public virtual bool DrawPawn(Pawn pawn, Pawn observer, bool defaultVisible) => defaultVisible;
        public virtual void PawnPose(Pawn pawn, ref RimVisionPose pose) { }
        public virtual void CameraPose(Pawn actor, ref RimVisionPose pose) { }
        public virtual void PawnAnimation(RimVisionAnimationContext context, ref RimVisionPawnAnimationFrame frame) { }
        public virtual void WeaponAnimation(RimVisionAnimationContext context, ref RimVisionWeaponAnimationFrame frame) { }
    }

    public static partial class RimVisionPresentation
    {
        sealed class TickSample
        {
            public TickSample() { }
            internal int Tick = -1;
            internal float Time, Fraction;
        }

        static readonly ConditionalWeakTable<Map, TickSample> TickSamples = new ConditionalWeakTable<Map, TickSample>();
        static readonly ApiRegistry<RimVisionPresentationProvider> Providers = new ApiRegistry<RimVisionPresentationProvider>("presentation");
        public static IDisposable RegisterProvider(string id, RimVisionPresentationProvider provider, int priority = 0) =>
            Providers.Add(id, provider ?? throw new ArgumentNullException(nameof(provider)), priority);

        static readonly ApiRegistry<RimVisionPresentationProvider> Modifiers = new ApiRegistry<RimVisionPresentationProvider>("presentation.modifiers");
        public static IDisposable RegisterModifier(string id, RimVisionPresentationProvider modifier, int priority = 0) =>
            Modifiers.Add(id, modifier ?? throw new ArgumentNullException(nameof(modifier)), priority);

        public static float TickFraction(Map map)
        {
            if (map == null || Current.Game == null || !Current.Game.Maps.Contains(map)) return 0;
            int tick = Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
                ? Rim3DMultiplayer.MapTicks(map) : Find.TickManager.TicksGame;
            var sample = TickSamples.GetOrCreateValue(map);
            float now = UnityEngine.Time.unscaledTime;
            if (sample.Tick != tick)
            {
                sample.Tick = tick;
                sample.Time = now;
                sample.Fraction = 0;
            }
            float rate = Rim3DMultiplayer.Active ? Rim3DMultiplayer.MinigameTickRate?.Invoke(map) ?? 0 :
                Find.TickManager.Paused ? 0 : Find.TickManager.TickRateMultiplier;
            if (rate > 0) sample.Fraction = Mathf.Clamp01(sample.Fraction + Mathf.Max(0, now - sample.Time) * 60 * rate);
            sample.Time = now;
            return sample.Fraction;
        }

        internal static bool HasProviders => Providers.Entries.Length > 0 || Modifiers.Entries.Length > 0;

        internal static bool DrawPawn(Pawn pawn, Pawn observer, bool visible)
        {
            if (!HasProviders) return visible;
            return Visibility(Modifiers, pawn, observer, Visibility(Providers, pawn, observer, visible, true), false);
        }

        static bool Visibility(ApiRegistry<RimVisionPresentationProvider> registry, Pawn pawn, Pawn observer, bool visible, bool first)
        {
            Rand.PushState();
            try
            {
                foreach (var entry in registry.Entries)
                {
                    if (!entry.Active) continue;
                    bool selected = false;
                    try
                    {
                        if ((entry.Value.Channels & RimVisionPresentationChannels.Visibility) == 0 || !(selected = entry.Value.Handles(pawn))) continue;
                        visible = entry.Value.DrawPawn(pawn, observer, visible);
                    }
                    catch (Exception error) { Report(entry.Id, error); }
                    if (selected && first) break;
                }
                return visible;
            }
            finally { Rand.PopState(); }
        }

        internal static void Apply(Pawn pawn, bool camera, ref Vector3 position, ref Quaternion rotation)
        {
            if (pawn == null || !HasProviders) return;
            var pose = new RimVisionPose(position, rotation);
            ApplyPose(Providers, pawn, camera, ref pose, true);
            ApplyPose(Modifiers, pawn, camera, ref pose, false);
            position = pose.Position; rotation = pose.Rotation.normalized;
        }

        static void ApplyPose(ApiRegistry<RimVisionPresentationProvider> registry, Pawn pawn, bool camera, ref RimVisionPose pose, bool first)
        {
            Rand.PushState();
            try
            {
                foreach (var entry in registry.Entries)
                {
                    if (!entry.Active) continue;
                    bool selected = false;
                    try
                    {
                        var channel = camera ? RimVisionPresentationChannels.CameraPose : RimVisionPresentationChannels.PawnPose;
                        if ((entry.Value.Channels & channel) == 0 || !(selected = entry.Value.Handles(pawn))) continue;
                        var candidate = pose;
                        if (camera) entry.Value.CameraPose(pawn, ref candidate);
                        else entry.Value.PawnPose(pawn, ref candidate);
                        if (candidate.Valid) pose = candidate;
                    }
                    catch (Exception error) { Report(entry.Id, error); }
                    if (selected && first) break;
                }
            }
            finally { Rand.PopState(); }
        }

        internal static void AnimatePawn(RimVisionAnimationContext context, ref RimVisionPawnAnimationFrame frame)
        {
            if (!HasProviders) return;
            var weapon = default(RimVisionWeaponAnimationFrame);
            Animate(Providers, context, ref frame, ref weapon, false, true);
            Animate(Modifiers, context, ref frame, ref weapon, false, false);
        }

        internal static void AnimateWeapon(RimVisionAnimationContext context, ref RimVisionWeaponAnimationFrame frame)
        {
            if (!HasProviders) return;
            var pawn = default(RimVisionPawnAnimationFrame);
            Animate(Providers, context, ref pawn, ref frame, true, true);
            Animate(Modifiers, context, ref pawn, ref frame, true, false);
        }

        static void Animate(ApiRegistry<RimVisionPresentationProvider> registry, RimVisionAnimationContext context,
            ref RimVisionPawnAnimationFrame pawn, ref RimVisionWeaponAnimationFrame weapon, bool item, bool first)
        {
            Rand.PushState();
            try
            {
                foreach (var entry in registry.Entries)
                {
                    if (!entry.Active) continue;
                    bool selected = false;
                    try
                    {
                        var channel = item ? RimVisionPresentationChannels.WeaponAnimation : RimVisionPresentationChannels.PawnAnimation;
                        if ((entry.Value.Channels & channel) == 0 || !(selected = entry.Value.Handles(context.Pawn))) continue;
                        if (item)
                        {
                            var candidate = weapon;
                            entry.Value.WeaponAnimation(context, ref candidate);
                            if (candidate.Valid) weapon = candidate;
                        }
                        else
                        {
                            var candidate = pawn;
                            entry.Value.PawnAnimation(context, ref candidate);
                            if (candidate.Valid) pawn = candidate;
                        }
                    }
                    catch (Exception error) { Report(entry.Id, error); }
                    if (selected && first) break;
                }
            }
            finally { Rand.PopState(); }
        }

        static void Report(string id, Exception error) { RimVisionDiagnostics.ReportFault("presentation", id, error); Log.ErrorOnce("RimVision presentation bridge " + id + ": " + error, id.GetHashCode()); }
    }
}
