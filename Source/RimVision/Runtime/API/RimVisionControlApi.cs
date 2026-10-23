using System;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D.API
{
    public enum RimVisionControlChange { Acquired, Changed, Restored, Unavailable, Released }

    public readonly struct RimVisionControlContext
    {
        public Pawn Actor { get; }
        public Thing Carrier { get; }
        public Map Map { get; }
        public bool Multiplayer => Rim3DMultiplayer.Active;
        readonly bool _contained;
        internal bool Valid => Actor != null && !Actor.Destroyed && !Actor.Dead && Carrier?.Spawned == true && !Carrier.Destroyed &&
            Carrier.Map == Map && (Actor.Spawned ? Actor.Map == Map : _contained && RimVisionControl.ContainedBy(Actor, Carrier));
        internal RimVisionControlContext(Pawn actor, Thing carrier, bool contained)
        { Actor = actor; Carrier = carrier; Map = carrier?.Map; _contained = contained; }
    }

    public readonly struct RimVisionMovementInput
    {
        public Vector2 Input { get; }
        public Vector3 Direction { get; }
        public float Yaw { get; }
        public float DeltaSeconds { get; }
        public int Tick { get; }
        public bool Sneaking { get; }
        public bool Sprinting { get; }
        public float Vertical { get; }
        public float Pitch { get; }
        public float LookYaw { get; }
        public Vector3 Input3D => new Vector3(Input.x, Vertical, Input.y);
        public Vector3 Direction3D => Direction + Vector3.up * Vertical;
        public Vector3 LookDirection => Quaternion.Euler(Pitch, LookYaw, 0) * Vector3.forward;
        internal RimVisionMovementInput(Vector2 input, float yaw, float delta, bool sneaking, bool sprinting, float vertical = 0, float pitch = 0, float lookYaw = 0)
        {
            var movement = Vector3.ClampMagnitude(new Vector3(input.x, Mathf.Clamp(vertical, -1, 1), input.y), 1);
            Input = new Vector2(movement.x, movement.z); Yaw = yaw; DeltaSeconds = Mathf.Clamp(delta, 0, .1f);
            Direction = Quaternion.Euler(0, yaw, 0) * new Vector3(Input.x, 0, Input.y);
            Tick = Find.TickManager.TicksGame; Sneaking = sneaking; Sprinting = sprinting;
            Vertical = movement.y; Pitch = Mathf.Clamp(pitch, -89.9f, 89.9f); LookYaw = Mathf.Repeat(lookYaw, 360);
        }
    }

    public abstract class RimVisionControlProvider
    {
        public virtual void CollectActors(Map map, System.Collections.Generic.List<Pawn> actors) { }
        public virtual bool SupportsContainedActor => false;
        public virtual bool SupportsVerticalMovement => false;
        public abstract bool Handles(Pawn actor);
        public abstract Thing Carrier(Pawn actor);
        public abstract AcceptanceReport CanControl(RimVisionControlContext context);
        public virtual Vector3 Position(RimVisionControlContext context) => context.Carrier.Position.ToVector3Shifted();
        public virtual void Attached(RimVisionControlContext context, RimVisionControlChange reason) { }
        public virtual void Detached(RimVisionControlContext context, RimVisionControlChange reason) { }
        public abstract void Move(RimVisionControlContext context, RimVisionMovementInput input);
        public virtual void Cancel(RimVisionControlContext context) { }
        public virtual bool SetCombat(RimVisionControlContext context, bool enabled) => false;
        public virtual bool OrderJob(RimVisionControlContext context, Job job) => false;
    }

    public static class RimVisionControl
    {
        internal static readonly ApiRegistry<RimVisionControlProvider> Providers = new ApiRegistry<RimVisionControlProvider>("control", true);
        public static IDisposable RegisterProvider(string id, RimVisionControlProvider provider, int priority = 0) =>
            Providers.Add(id, provider ?? throw new ArgumentNullException(nameof(provider)), priority);

        public static Map MapFor(Pawn actor)
        {
            if (actor?.Spawned == true) return actor.Map;
            var binding = Resolve(actor);
            return binding.Selected && binding.Context.Valid ? binding.Context.Map : null;
        }

        public static void CollectActors(Map map, System.Collections.Generic.List<Pawn> actors)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (actors == null) throw new ArgumentNullException(nameof(actors));
            int start = actors.Count;
            Rand.PushState();
            try
            {
                foreach (var entry in Providers.Entries)
                {
                    if (!entry.Active) continue;
                    try { entry.Value.CollectActors(map, actors); }
                    catch (Exception error) { Log.ErrorOnce("RimVision actors " + entry.Id + ": " + error, entry.Id.GetHashCode()); }
                }
                for (int i = actors.Count - 1; i >= start; i--)
                    if (actors[i] == null || MapFor(actors[i]) != map || !PawnControl.CanAcquire(actors[i]) || actors.IndexOf(actors[i]) != i)
                        actors.RemoveAt(i);
            }
            finally { Rand.PopState(); }
        }

        internal static bool ContainedBy(Pawn actor, Thing carrier)
        {
            var holder = actor.ParentHolder;
            for (int remaining = 32; holder != null && remaining-- > 0; holder = holder.ParentHolder)
                if (ReferenceEquals(holder, carrier)) return true;
            return false;
        }

        public static bool TryGetContext(Pawn actor, out RimVisionControlContext context)
        {
            var binding = Resolve(actor);
            context = binding.Context;
            return binding.Selected && context.Valid;
        }

        internal readonly struct Binding
        {
            internal readonly ApiRegistry<RimVisionControlProvider>.Entry Entry;
            internal readonly RimVisionControlContext Context;
            internal bool Selected => Entry != null;
            internal RimVisionControlProvider Provider => Entry.Value;
            internal Binding(ApiRegistry<RimVisionControlProvider>.Entry entry, Pawn actor, Thing carrier, bool contained = false)
            { Entry = entry; Context = new RimVisionControlContext(actor, carrier, contained); }
            internal bool Same(Binding other) => Entry == other.Entry && Context.Actor == other.Context.Actor &&
                Context.Carrier == other.Context.Carrier && Context.Map == other.Context.Map;
            internal AcceptanceReport Validate()
            {
                if (!Context.Valid) return false;
                Rand.PushState();
                try { return Provider.CanControl(Context); }
                catch (Exception error) { Report(error); return false; }
                finally { Rand.PopState(); }
            }
            internal Vector3 Position()
            {
                Rand.PushState();
                try
                {
                    var position = Provider.Position(Context);
                    if (Finite(position) && position.ToIntVec3().InBounds(Context.Map))
                        return new Vector3(position.x, Provider.SupportsVerticalMovement ? position.y : 0, position.z);
                }
                catch (Exception error) { Report(error); }
                finally { Rand.PopState(); }
                return Context.Carrier.Position.ToVector3Shifted();
            }
            internal void Report(Exception error) { RimVisionDiagnostics.ReportFault("control", Entry.Id, error); Log.ErrorOnce("RimVision control bridge " + Entry.Id + ": " + error, Entry.Id.GetHashCode()); }
        }

        internal static Binding Resolve(Pawn actor)
        {
            if (actor == null || Providers.Entries.Length == 0) return default;
            Rand.PushState();
            try
            {
                foreach (var entry in Providers.Entries)
                {
                    if (!entry.Active) continue;
                    try
                    {
                        if (entry.Value.Handles(actor)) return new Binding(entry, actor, entry.Value.Carrier(actor), entry.Value.SupportsContainedActor);
                    }
                    catch (Exception error)
                    {
                        var failed = new Binding(entry, actor, null);
                        failed.Report(error);
                        return failed;
                    }
                }
                return default;
            }
            finally { Rand.PopState(); }
        }

        internal static bool CanAcquire(Pawn actor)
        { var binding = Resolve(actor); return binding.Selected && binding.Validate().Accepted; }

        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
