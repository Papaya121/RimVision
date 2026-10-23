using Multiplayer.API;
using RimWorld;
using Recovery.Rim3D;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.Multiplayer
{
    // Keep synchronized methods below nine arguments: Harmony's Mono invoker
    // fails emitting short integer operands for the old eleven-argument Input.
    public sealed class ControlInput
    {
        public float X, Z, Yaw, Pitch;
        public bool Sneaking, Sprinting;
        public bool BlockAutomaticPawnSwitch;
        public Vector3 Position;
    }

    public static class SyncCommands
    {
        // During reload/teardown MP deliberately stops intercepting UI methods.
        // Such calls must not fall through into local simulation mutations.
        static bool Executing => !MP.IsInMultiplayer || MP.IsExecutingSyncCommand;
        public static void ColonyOrder(Pawn pawn, string owner, string token, ColonyOrder order)
        {
        }

        public static void OverviewPlace(Map map, BuildableDef def, ThingDef stuff, IntVec3 cell, Rot4 rotation)
        {
            if (!Executing || map == null) return;
            using (Rim3DMultiplayer.EnterSimulation())
                MapView.ExecuteOverviewPlacement(map, def, stuff, cell, rotation);
        }

        public static void PrepareNeighborMaps(Map source)
        {
            if (!Executing || Current.Game == null) return;
            using (Rim3DMultiplayer.EnterSimulation())
                Current.Game.GetComponent<NeighborMaps>().Prepare(source);
        }
        public static void SetShootWhileRunning(bool allowed)
        {
            if (!Executing || Current.Game == null) return;
            Current.Game.GetComponent<HostMovementSettings>().ShootWhileRunningAllowed = allowed;
        }
        public static void SetMinigameRewards(bool allowed)
        {
            if (!Executing || Current.Game == null) return;
            Current.Game.GetComponent<HostMovementSettings>().MinigameRewardsAllowed = allowed;
        }
        public static void MinigameReward(Pawn pawn, string owner, string token, int jobId, int toilIndex, int milestone)
        {
            if (!Executing || Current.Game == null || !Session(pawn, owner, token, out var session)) return;
            using (Rim3DMultiplayer.EnterSimulation())
                Current.Game.GetComponent<WorkMinigameRewards>().Apply(session.Controller, jobId, toilIndex, milestone);
        }
        public static void SetMinigameMultiplier(int kind, float multiplier)
        {
            if (!Executing || Current.Game == null) return;
            Current.Game.GetComponent<HostMovementSettings>().SetMinigameRewardMultiplier((WorkMinigameKind)kind, multiplier);
        }
        public static void SetStatusMenu(string owner, bool open)
        {
            if (!Executing || Current.Game == null || string.IsNullOrEmpty(owner)) return;
            Current.Game.GetComponent<StatusMenuPause>().SetMenu(owner, open);
        }
        public static void SetMenuPlayers(string[] players)
        {
            if (!Executing || Current.Game == null || players == null) return;
            Current.Game.GetComponent<StatusMenuPause>().SetPlayers(players);
        }
        public static void SetPawnLock(bool enabled, string hostName)
        {
            if (!Executing || Current.Game == null || string.IsNullOrEmpty(hostName)) return;
            Current.Game.GetComponent<HostMovementSettings>().SetPawnLock(enabled, hostName);
        }
        public static void SetCompassDisabled(bool disabled)
        {
            if (!Executing || Current.Game == null) return;
            Current.Game.GetComponent<HostMovementSettings>().CompassDisabled = disabled;
        }
        public static void SetPlantSlowdown(bool disabled)
        {
            if (!Executing || Current.Game == null) return;
            Current.Game.GetComponent<HostMovementSettings>().DisablePlantSlowdown = disabled;
        }
        public static void ToggleAcceleration(Map map)
        {
            if (!Executing || map == null) return;
            bool enabled = !map.GetComponent<ControlSessions>().ActionAccelerationEnabled;
            foreach (var currentMap in Find.Maps)
                currentMap.GetComponent<ControlSessions>().ActionAccelerationEnabled = enabled;
        }
        static bool Session(Pawn pawn, string owner, string token, out ControlSession session)
        {
            session = null;
            if (RimVisionControl.MapFor(pawn) == null || !RimVisionControl.MapFor(pawn).GetComponent<ControlSessions>().Owns(pawn, owner, token, out session)) return false;
            session.LeaseUntil = Find.TickManager.TicksGame + (PawnControl.IsSleeping(pawn) ? 7200 : 3600);
            return session.Controller.Active;
        }
        public static void Claim(Pawn pawn, string owner, string token)
        {
            if (!Executing) return;
            bool accepted = false;
            Rim3DMultiplayer.Simulate(() =>
            {
                if (RimVisionControl.MapFor(pawn) != null) accepted = RimVisionControl.MapFor(pawn).GetComponent<ControlSessions>().Claim(pawn, owner, token);
            });
            if (MP.IsExecutingSyncCommandIssuedBySelf) Adapter.ClaimResult(pawn, token, accepted);
        }
        public static void Release(Pawn pawn, string owner, string token)
        {
            if (!Executing) return;
            Rim3DMultiplayer.Simulate(() => RimVisionControl.MapFor(pawn)?.GetComponent<ControlSessions>().Release(pawn, owner, token));
        }
        public static void Input(Pawn pawn, string owner, string token, ControlInput input)
        {
            if (!Executing || input == null) return;
            float x = input.X, z = input.Z, yaw = input.Yaw, pitch = input.Pitch;
            bool sneaking = input.Sneaking, sprinting = input.Sprinting;
            Vector3 position = input.Position;
            if (!Finite(x) || !Finite(z) || !Finite(yaw) || !Finite(pitch) ||
                !Finite(position.x) || !Finite(position.y) || !Finite(position.z) || !Session(pawn, owner, token, out var session)) return;
            session.X = Mathf.Clamp(x, -1, 1); session.Z = Mathf.Clamp(z, -1, 1);
            session.Yaw = Mathf.Repeat(yaw, 360);
            session.Pitch = Mathf.Clamp(pitch, -75, 75);
            session.Sneaking = sneaking; session.Sprinting = sprinting;
            session.BlockAutomaticPawnSwitch = input.BlockAutomaticPawnSwitch;
            session.InputUntil = Find.TickManager.TicksGame + 120;
            if (x != 0 || z != 0) session.View.CancelSimulationConstruction();
            using (Rim3DMultiplayer.EnterSimulation())
            {
                session.Controller.Sneaking = sneaking;
                session.Controller.Sprinting = sprinting;
                session.Controller.Move(new Vector2(session.X, session.Z), session.Yaw, 0, false);
                // Idle snapshots may precede the simulation's short automatic
                // approach. Do not rewind it to the owner's delayed position.
                if (!session.Controller.WaitingForManualApproach ||
                    new Vector2(session.X, session.Z).sqrMagnitude > .001f)
                    session.Controller.ApplyClientPosition(position);
            }
        }
        public static void WeaponAim(Pawn pawn, string owner, string token, bool aiming)
        {
            if (!Executing || !Session(pawn, owner, token, out var session)) return;
            session.WeaponAiming = aiming;
        }
        public static void NativeAxes(Pawn pawn, string owner, string token, float vertical, float lookYaw, float lookPitch, bool extendedLook)
        {
            if (!Executing || !Finite(vertical) || !Finite(lookYaw) || !Finite(lookPitch) || !Session(pawn, owner, token, out var session)) return;
            session.Vertical = Mathf.Clamp(vertical, -1, 1);
            session.LookYaw = Mathf.Repeat(lookYaw, 360); session.LookPitch = Mathf.Clamp(lookPitch, -89.9f, 89.9f);
            session.ExtendedLook = extendedLook;
            using (Rim3DMultiplayer.EnterSimulation()) session.Controller.SetNativeAxes(session.Vertical, session.LookPitch, session.LookYaw);
        }
        public static void Combat(Pawn pawn, string owner, string token, bool enabled)
        {
            if (!Executing) return;
            if (Session(pawn, owner, token, out var session)) Rim3DMultiplayer.Simulate(() =>
            {
                session.View.CancelSimulationConstruction();
                session.View.CancelSynchronizedManualFire();
                bool wasDrafted = pawn.Drafted;
                session.Controller.SetCombat(enabled);
                Adapter.CombatResult(pawn, owner, token, session.Controller.Status, pawn.Drafted, pawn.Drafted != wasDrafted);
            });
        }
        public static void Cancel(Pawn pawn, string owner, string token)
        {
            if (!Executing) return;
            if (Session(pawn, owner, token, out var session)) Rim3DMultiplayer.Simulate(() =>
            {
                session.View.CancelSimulationConstruction();
                session.View.CancelSynchronizedManualFire();
                session.Controller.ReturnToManual();
            });
        }
        public static void Action(Pawn pawn, string owner, string token, Rim3DCommand command)
        {
            if (!Executing) return;
            if (command == null || !Session(pawn, owner, token, out var session)) return;
            Rim3DMultiplayer.Simulate(() => session.View.ExecuteSynchronized(command));
        }
        public static void CombatInput(Pawn pawn, string owner, string token, RimVisionCombatInputCommand command)
        {
            if (!Executing || command == null || !Session(pawn, owner, token, out var session)) return;
            using (Rim3DMultiplayer.EnterSimulation()) session.View.ExecuteCombatInput(command);
        }
        public static void ApiContract(Pawn pawn, string owner, string token, string fingerprint)
        {
            if (!Executing || fingerprint == null || fingerprint.Length != 64 || !Session(pawn, owner, token, out _)) return;
            Adapter.ApiContractResult(owner, fingerprint);
        }
        public static void ApiRequest(Pawn pawn, string owner, string token, RimVisionApiRequestCommand command)
        {
            if (!Executing || command == null) return;
            bool success = false;
            string reason = null;
            if (Session(pawn, owner, token, out var session))
                using (Rim3DMultiplayer.EnterSimulation()) success = session.View.ExecuteApiRequest(command, out reason, notification => Adapter.ApiJobResult(pawn, owner, token, notification));
            Adapter.ApiRequestResult(pawn, owner, token, command.RequestId, success, reason);
        }
        public static void LayerAction(Pawn pawn, string owner, string token, string id, Map targetMap, Thing target, IntVec3 cell, int value)
        {
            if (!Executing || !Session(pawn, owner, token, out var session)) return;
            using (Rim3DMultiplayer.EnterSimulation())
                session.View.ExecuteLayerAction(id, targetMap, target, cell, value);
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
