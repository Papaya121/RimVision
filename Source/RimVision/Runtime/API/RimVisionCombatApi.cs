using System;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    public enum RimVisionAttackKind { Ranged, Melee }

    public readonly struct RimVisionCombatContext
    {
        public Pawn Actor { get; }
        public Thing Carrier { get; }
        public Map Map { get; }
        public RimVisionAttackKind Kind { get; }
        public Thing Target { get; }
        public IntVec3 Cell { get; }
        public Vector3 Origin { get; }
        public Vector3 Direction { get; }
        public int Mode { get; }
        public bool Begin { get; }
        public int Tick => Find.TickManager.TicksGame;
        internal RimVisionCombatContext(Pawn actor, RimVisionAttackKind kind, Thing target, IntVec3 cell,
            Vector3 origin, Vector3 direction, int mode, bool begin)
        {
            Actor = actor; Map = RimVisionControl.MapFor(actor); Kind = kind; Target = target; Cell = cell;
            Origin = origin; Direction = direction; Mode = mode; Begin = begin;
            Carrier = RimVisionControl.TryGetContext(actor, out var context) ? context.Carrier : actor;
        }
    }

    public abstract class RimVisionCombatProvider
    {
        public virtual bool UsesInputPhases => false;
        public virtual AcceptanceReport ValidateInput(RimVisionCombatContext context, RimVisionInputPhase phase) => Validate(context);
        public virtual bool Input(RimVisionCombatContext context, RimVisionInputPhase phase) => phase == RimVisionInputPhase.Pressed ? Execute(context) : true;
        public abstract bool Handles(Pawn actor, RimVisionAttackKind kind);
        public abstract AcceptanceReport Validate(RimVisionCombatContext context);
        public abstract bool Execute(RimVisionCombatContext context);
    }

    public static class RimVisionCombat
    {
        static readonly ApiRegistry<RimVisionCombatProvider> Providers = new ApiRegistry<RimVisionCombatProvider>("combat", true);
        public static IDisposable RegisterProvider(string id, RimVisionCombatProvider provider, int priority = 0) =>
            Providers.Add(id, provider ?? throw new ArgumentNullException(nameof(provider)), priority);

        static ApiRegistry<RimVisionCombatProvider>.Entry Resolve(Pawn actor, RimVisionAttackKind kind, out bool failed)
        {
            failed = false;
            if (actor == null || Providers.Entries.Length == 0) return null;
            Rand.PushState();
            try
            {
                foreach (var entry in Providers.Entries)
                {
                    if (!entry.Active) continue;
                    try { if (entry.Value.Handles(actor, kind)) return entry; }
                    catch (Exception error) { failed = true; Report(entry.Id, error); return entry; }
                }
                return null;
            }
            finally { Rand.PopState(); }
        }

        public static void NotifyShot(Pawn pawn)
        {
            if (pawn == null) return;
            if (Rim3DMultiplayer.Active && Rim3DMultiplayer.Simulating) Rim3DMultiplayer.Shot?.Invoke(pawn);
            else
            {
                var view = Rim3DPreview.Instance?.ActiveWindow?.View;
                if (view?.ControlledPawn == pawn) view.ShowMultiplayerShot(false, false);
            }
        }

        public static bool RequestInput(RimVisionAttackKind kind, RimVisionInputPhase phase, Thing target, IntVec3 cell, Ray aim, int mode = 0) =>
            Rim3DPreview.Instance?.ActiveWindow?.View?.RequestCombatInput(new RimVisionCombatInputCommand
            { Kind = kind, Phase = phase, Target = target, Cell = cell, Origin = aim.origin, Direction = aim.direction, Mode = mode }) == true;

        internal static bool UsesInputPhases(Pawn pawn, RimVisionAttackKind kind) => InputProvider(pawn, kind) != null;
        internal static ApiRegistry<RimVisionCombatProvider>.Entry InputProvider(Pawn pawn, RimVisionAttackKind kind)
        {
            var provider = Resolve(pawn, kind, out bool failed);
            if (provider == null || failed) return null;
            Rand.PushState();
            try { return provider.Value.UsesInputPhases ? provider : null; }
            catch (Exception error) { Report(provider.Id, error); return null; }
            finally { Rand.PopState(); }
        }
        internal static bool ValidateInput(ApiRegistry<RimVisionCombatProvider>.Entry provider, RimVisionCombatContext context, RimVisionInputPhase phase)
        {
            if (context.Map == null || RimVisionControl.MapFor(context.Actor) != context.Map ||
                context.Target != null && (!context.Target.Spawned || context.Target.Destroyed || context.Target.Map != context.Map || context.Target.Position.Fogged(context.Map))) return false;
            Rand.PushState();
            try { return provider.Value.ValidateInput(context, phase).Accepted; }
            catch (Exception error) { Report(provider.Id, error); return false; }
            finally { Rand.PopState(); }
        }
        internal static bool ExecuteInput(ApiRegistry<RimVisionCombatProvider>.Entry provider, RimVisionCombatContext context, RimVisionInputPhase phase)
        {
            try { return provider.Value.Input(context, phase); }
            catch (Exception error) { Report(provider.Id, error); return false; }
        }

        internal static bool Handles(Pawn actor, RimVisionAttackKind kind) => Resolve(actor, kind, out _) != null;

        internal static bool TryExecute(RimVisionCombatContext context, out bool result, out string reason)
        {
            result = false; reason = null;
            var entry = Resolve(context.Actor, context.Kind, out var failed);
            if (entry == null) return false;
            if (failed || Rim3DMultiplayer.LocalInput || RimVisionControl.MapFor(context.Actor) != context.Map || context.Map == null ||
                context.Target != null && (!context.Target.Spawned || context.Target.Map != context.Map ||
                    context.Target.Destroyed || context.Target.Position.Fogged(context.Map))) return true;
            try
            {
                AcceptanceReport report;
                Rand.PushState();
                try { report = entry.Value.Validate(context); }
                finally { Rand.PopState(); }
                if (!report.Accepted) { reason = report.Reason; return true; }
                result = entry.Value.Execute(context);
            }
            catch (Exception error) { Report(entry.Id, error); }
            return true;
        }

        static void Report(string id, Exception error) { RimVisionDiagnostics.ReportFault("combat", id, error); Log.ErrorOnce("RimVision combat bridge " + id + ": " + error, id.GetHashCode()); }
    }
}
