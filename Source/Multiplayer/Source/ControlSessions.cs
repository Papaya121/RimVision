using System.Collections.Generic;
using HarmonyLib;
using Recovery.Rim3D;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D.Multiplayer
{
    public sealed class ControlSession : IExposable
    {
        public PawnControl Controller = new PawnControl();
        public string Owner, Token;
        public float X, Z, Yaw, Pitch;
        public float Vertical, LookYaw, LookPitch;
        public bool ExtendedLook;
        public bool Sneaking, Sprinting;
        public bool WeaponAiming;
        bool _blockAutomaticPawnSwitch;
        internal bool BlockAutomaticPawnSwitch
        {
            get => _blockAutomaticPawnSwitch;
            set => _blockAutomaticPawnSwitch = value;
        }
        public int InputUntil, LeaseUntil;
        MapView view;
        Map map;
        // Local render caches are deliberately excluded from saves and commands.
        public bool RenderInitialized;
        public int RenderFrame = -1;
        public Vector3 RenderPosition;
        public float RenderYaw, RenderPitch;
        public ControlSession(Map map) { this.map = map; }
        public MapView View => view ?? (view = new MapView(map, Controller));
        internal void TransferToMap(Map destination)
        {
            view?.CancelNativeCombatInput();
            view?.CancelSimulationConstruction();
            view = null;
            map = destination;
            X = Z = Vertical = 0;
            ExtendedLook = false;
            WeaponAiming = false;
            InputUntil = 0;
            RenderInitialized = false;
            RenderFrame = -1;
            Controller.NotifyMapTransfer();
        }

        public void ExposeData()
        {
            Scribe_Deep.Look(ref Controller, "controller");
            Scribe_Values.Look(ref Owner, "owner");
            Scribe_Values.Look(ref Token, "token");
            Scribe_Values.Look(ref X, "x");
            Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Yaw, "yaw");
            Scribe_Values.Look(ref Pitch, "pitch");
            Scribe_Values.Look(ref Sneaking, "sneaking");
            Scribe_Values.Look(ref Sprinting, "sprinting");
            Scribe_Values.Look(ref WeaponAiming, "weaponAiming");
            Scribe_Values.Look(ref _blockAutomaticPawnSwitch, "blockAutomaticPawnSwitch");
            Scribe_Values.Look(ref InputUntil, "inputUntil");
            Scribe_Values.Look(ref LeaseUntil, "leaseUntil");
            if (Controller == null) Controller = new PawnControl();
            View.ExposeSimulationData();
        }
    }

    public sealed class ControlSessions : MapComponent
    {
        List<ControlSession> sessions = new List<ControlSession>();
        public bool ActionAccelerationEnabled = true;
        public bool AcceleratingActions
        {
            get
            {
                if (!ActionAccelerationEnabled || sessions.Count == 0) return false;
                bool working = false;
                foreach (var session in sessions)
                {
                    var control = session?.Controller;
                    var pawn = control?.Pawn;
                    if (pawn == null || !pawn.Spawned || RimVisionControl.MapFor(pawn) != map) continue;
                    if (pawn.Dead || pawn.Downed || pawn.InMentalState || PawnControl.IsSleeping(pawn)) continue;
                    if (!control.Active || !control.IsActing || control.WaitingForManualApproach ||
                        pawn.Drafted || pawn.pather.Moving || pawn.CurJob?.def == RimWorld.JobDefOf.AttackMelee ||
                        pawn.CurJob?.def == RimWorld.JobDefOf.AttackStatic) return false;
                    working = true;
                }
                return working;
            }
        }
        public ControlSessions(Map map) : base(map) { }
        public bool HasActiveController
        {
            get
            {
                foreach (var session in sessions)
                    if (session?.Controller?.Active == true) return true;
                return false;
            }
        }
        public bool AcceleratingSleep
        {
            get
            {
                bool sleeping = false;
                bool allLocked = true;
                foreach (var session in sessions)
                {
                    var pawn = session?.Controller?.Pawn;
                    if (pawn == null || !pawn.Spawned || RimVisionControl.MapFor(pawn) != map || pawn.Dead || pawn.Downed) continue;
                    if (!PawnControl.IsSleeping(pawn)) return false;
                    sleeping = true;
                    if (!session.BlockAutomaticPawnSwitch) allLocked = false;
                }
                return sleeping && (allLocked || NoAwakeControllableColonists(map));
            }
        }
        public static bool NoAwakeControllableColonists(Map map)
        {
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                if (!pawn.Dead && !pawn.Downed && !pawn.InMentalState && !PawnControl.IsSleeping(pawn)) return false;
            return true;
        }
        public ControlSession Find(Pawn pawn)
        {
            if (pawn == null) return null;
            for (int i = 0; i < sessions.Count; i++)
                if (sessions[i]?.Controller?.Pawn == pawn) return sessions[i];
            return null;
        }
        public bool Owns(Pawn pawn, string owner, string token, out ControlSession session)
        {
            session = Find(pawn);
            return session != null && session.Owner == owner && session.Token == token;
        }
        internal void RegisterLockedPawns(HostMovementSettings settings)
        {
            foreach (var session in sessions)
                if (session?.Controller?.Pawn != null) settings.RegisterPawn(session.Owner, session.Controller.Pawn);
        }

        public bool Claim(Pawn pawn, string owner, string token)
        {
            var settings = Current.Game.GetComponent<HostMovementSettings>();
            if (string.IsNullOrEmpty(owner) || !settings.AllowsPawn(owner, pawn)) return false;
            var existing = Find(pawn);
            if (existing != null && existing.Owner != owner) return false;
            if (!PawnControl.CanAcquire(pawn) || RimVisionControl.MapFor(pawn) != map || string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(token)) return false;
            for (int i = sessions.Count - 1; i >= 0; i--)
                if (sessions[i]?.Controller?.Pawn == null || sessions[i].Owner == owner) Remove(i);
            var session = new ControlSession(map) { Owner = owner, Token = token, Yaw = pawn.Rotation.AsAngle };
            if (!session.Controller.Acquire(pawn)) return false;
            session.LeaseUntil = Verse.Find.TickManager.TicksGame + (PawnControl.IsSleeping(pawn) ? 7200 : 3600);
            sessions.Add(session);
            settings.RegisterPawn(owner, pawn);
            sessions.Sort((a, b) => a.Controller.Pawn.thingIDNumber.CompareTo(b.Controller.Pawn.thingIDNumber));
            return true;
        }
        public void Release(Pawn pawn, string owner, string token)
        {
            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                var session = sessions[i];
                if (session?.Controller == null ||
                    (session.Controller.Pawn == pawn && session.Owner == owner && session.Token == token)) Remove(i);
            }
        }
        void Remove(int index)
        {
            var controller = sessions[index]?.Controller;
            sessions[index]?.View.CancelNativeCombatInput();
            // Invalid deep-scribed entries must disappear even without a controller.
            // Remove ownership before job cleanup can invoke other game callbacks.
            sessions.RemoveAt(index);
            if (controller == null) return;
            controller.Dispose();
        }
        internal void TransferPawn(Pawn pawn)
        {
            var session = Find(pawn);
            if (session == null || RimVisionControl.MapFor(pawn) == null || RimVisionControl.MapFor(pawn) == map) return;
            sessions.Remove(session);
            session.TransferToMap(RimVisionControl.MapFor(pawn));
            var destination = RimVisionControl.MapFor(pawn).GetComponent<ControlSessions>();
            destination.sessions.Add(session);
            destination.sessions.Sort((a, b) => a.Controller.Pawn.thingIDNumber.CompareTo(b.Controller.Pawn.thingIDNumber));
        }

        public override void MapComponentTick()
        {
            if (sessions.Count == 0) return;
            using (Rim3DMultiplayer.EnterSimulation())
            {
                int tick = Verse.Find.TickManager.TicksGame;
                for (int i = sessions.Count - 1; i >= 0; i--)
                {
                    var session = sessions[i];
                    var pawn = session?.Controller?.Pawn;
                    if (Rim3DMultiplayer.Active && RimVisionControl.MapFor(pawn) != null && RimVisionControl.MapFor(pawn) != map && tick < session.LeaseUntil)
                    {
                        TransferPawn(pawn);
                        continue;
                    }
                    if (Rim3DMultiplayer.Active && pawn != null && RimVisionControl.MapFor(pawn) == null && !pawn.Dead && tick < session.LeaseUntil)
                    { session.View.CancelNativeCombatInput(); continue; }
                    if (session?.Controller == null || !Rim3DMultiplayer.Active || !session.Controller.Observing || RimVisionControl.MapFor(session.Controller.Pawn) != map || tick >= session.LeaseUntil)
                    { Remove(i); continue; }
                    bool fresh = tick < session.InputUntil;
                    if (session.Controller.InputBlocked) session.View.CancelSimulationConstruction();
                    session.Controller.Sneaking = fresh && session.Sneaking;
                    session.Controller.Sprinting = fresh && session.Sprinting;
                    session.Controller.SetNativeAxes(fresh ? session.Vertical : 0, session.ExtendedLook ? session.LookPitch : session.Pitch,
                        session.ExtendedLook ? session.LookYaw : session.Yaw);
                    session.Controller.Move(fresh ? new Vector2(session.X, session.Z) : Vector2.zero, session.Yaw, 1f / 60f, false);
                    session.View.SimulationTick();
                }
            }
        }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref sessions, "rim3DControlSessions", LookMode.Deep, map);
            Scribe_Values.Look(ref ActionAccelerationEnabled, "rim3DActionAcceleration", true);
            if (sessions == null) sessions = new List<ControlSession>();
            // Wait until reference resolution finishes; loading must not dispose
            // controllers or end pawn jobs while restoring multiplayer state.
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                for (int i = sessions.Count - 1; i >= 0; i--)
                    if (sessions[i]?.Controller == null) sessions.RemoveAt(i);
        }
    }
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    static class ControlSessionMapTransferPatch
    {
        static void Postfix(Pawn __instance, bool respawningAfterLoad)
        {
            if (respawningAfterLoad || !Rim3DMultiplayer.Active || !__instance.Spawned) return;
            using (Rim3DMultiplayer.EnterSimulation())
                foreach (var source in Current.Game.Maps)
                    if (source != __instance.Map) source.GetComponent<ControlSessions>().TransferPawn(__instance);
        }
    }
}
