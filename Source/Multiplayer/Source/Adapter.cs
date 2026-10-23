using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using Recovery.Rim3D;
using Recovery.Rim3D.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Recovery.Rim3D.Multiplayer
{
    public sealed class CompatibilityMod : Mod
    {
        public CompatibilityMod(ModContentPack content) : base(content)
        {
            // Do not JIT any API-dependent method until the optional dependency
            // is present. RimWorld may load this assembly with Multiplayer off.
            CompatibilityDescription.Install();
            LongEventHandler.ExecuteWhenFinished(TryInitialize);
        }

        static void TryInitialize()
        {
            if (ModLister.GetActiveModWithIdentifier("rwmt.Multiplayer", true) == null) return;
            // Block local gameplay mutations if an enabled MP dependency cannot
            // initialize. Prefer its real session flag when that API is available.
            Rim3DMultiplayer.NativeSimulation = () => MP.IsInMultiplayer && !MP.InInterface;
            Rim3DMultiplayer.IsActive = () => true;
            try
            {
                var api = Array.Find(AppDomain.CurrentDomain.GetAssemblies(),
                    assembly => assembly.GetName().Name == "0MultiplayerAPI");
                var mp = api?.GetType("Multiplayer.API.MP", false);
                var active = mp?.GetProperty("IsInMultiplayer", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                if (active != null)
                    Rim3DMultiplayer.IsActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), active);
                if (mp == null || api.GetType("Multiplayer.API.SyncWorkerDelegate`1", false) == null)
                {
                    Log.Message("[RimVision Multiplayer] Compatibility skipped: Multiplayer API is unavailable or incompatible.");
                    return;
                }
                Initialize();
            }
            catch (Exception error)
            {
                Log.Message("[RimVision Multiplayer] Compatibility could not initialize (" + error.GetType().Name + "). Check the Multiplayer version and load order.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Initialize()
        {
                // Explicit constructors avoid compiler-generated API delegate
                // cache fields: RimWorld scans those fields even when MP is off.
                MP.RegisterSyncWorker<RimVisionCombatInputCommand>(new SyncWorkerDelegate<RimVisionCombatInputCommand>(SerializeCombatInput));
                MP.RegisterSyncWorker<RimVisionApiRequestCommand>(new SyncWorkerDelegate<RimVisionApiRequestCommand>(SerializeApiRequest));
                MP.RegisterSyncWorker<Rim3DCommand>(new SyncWorkerDelegate<Rim3DCommand>(SerializeCommand));
                MP.RegisterSyncWorker<ControlInput>(new SyncWorkerDelegate<ControlInput>(SerializeInput));
                MP.RegisterSyncWorker<ColonyOrder>(new SyncWorkerDelegate<ColonyOrder>(SerializeColonyOrder));
                foreach (string name in new[] { "Claim", "Release", "Input", "Combat", "Cancel", "Action", "NativeAxes", "ApiContract", "ApiRequest", "CombatInput", "LayerAction", "OverviewPlace", "ColonyOrder", "ToggleAcceleration", "SetPlantSlowdown", "SetCompassDisabled", "SetPawnLock", "SetStatusMenu", "SetMenuPlayers", "SetMinigameRewards", "SetMinigameMultiplier", "MinigameReward", "SetShootWhileRunning", "PrepareNeighborMaps", "WeaponAim" })
                {
                    var method = MP.RegisterSyncMethod(typeof(SyncCommands), name);
                    // Designators read Find.CurrentMap; capture the issuing map
                    // instead of using each recipient's currently viewed map.
                    if (name == "Action") method.SetContext(SyncContext.MapSelected);
                    if (name == "SetPawnLock" || name == "SetMinigameRewards" || name == "SetMinigameMultiplier" || name == "SetShootWhileRunning" || name == "PrepareNeighborMaps") method.SetHostOnly();
                }
                new Harmony("papaya.rimvision.multiplayer").PatchAll(typeof(CompatibilityMod).Assembly);
                Adapter.Install();
                Log.Message("[RimVision Multiplayer] Synchronized control, actions and persistent map sessions registered.");
        }
        static void SerializeColonyOrder(SyncWorker worker, ref ColonyOrder order)
        {
            if (!worker.isWriting) order = new ColonyOrder();
            worker.Bind(ref order.Map);
            worker.Bind(ref order.Tool);
            worker.Bind(ref order.Cells);
            worker.Bind(ref order.Targets);
            worker.Bind(ref order.Area);
            worker.Bind(ref order.Zone);
        }

        static void SerializeCombatInput(SyncWorker worker, ref RimVisionCombatInputCommand command)
        {
            if (!worker.isWriting) command = new RimVisionCombatInputCommand();
            int kind = (int)command.Kind, phase = (int)command.Phase;
            worker.Bind(ref kind); worker.Bind(ref phase);
            command.Kind = (RimVisionAttackKind)kind; command.Phase = (RimVisionInputPhase)phase;
            worker.Bind(ref command.Target); worker.Bind(ref command.Cell); worker.Bind(ref command.Origin);
            worker.Bind(ref command.Direction); worker.Bind(ref command.Mode);
        }
        static void SerializeApiRequest(SyncWorker worker, ref RimVisionApiRequestCommand command)
        {
            if (!worker.isWriting) command = new RimVisionApiRequestCommand();
            worker.Bind(ref command.RequestId); worker.Bind(ref command.Id); worker.Bind(ref command.TargetMap);
            worker.Bind(ref command.Target); worker.Bind(ref command.Cell); worker.Bind(ref command.Value);
        }
        static void SerializeInput(SyncWorker worker, ref ControlInput input)
        {
            if (!worker.isWriting) input = new ControlInput();
            worker.Bind(ref input.X);
            worker.Bind(ref input.Z);
            worker.Bind(ref input.Yaw);
            worker.Bind(ref input.Pitch);
            worker.Bind(ref input.Sneaking);
            worker.Bind(ref input.Sprinting);
            worker.Bind(ref input.Position);
            worker.Bind(ref input.BlockAutomaticPawnSwitch);
        }
        static void SerializeCommand(SyncWorker worker, ref Rim3DCommand command)
        {
            if (!worker.isWriting) command = new Rim3DCommand();
            int action = (int)command.Action;
            worker.Bind(ref action); command.Action = (Rim3DAction)action;
            worker.Bind(ref command.Target);
            worker.Bind(ref command.Cell);
            worker.Bind(ref command.Count);
            worker.Bind(ref command.Kind);
            worker.Bind(ref command.BuildDef);
            worker.Bind(ref command.Stuff);
            worker.Bind(ref command.Rotation);
            worker.Bind(ref command.Recipe);
            worker.Bind(ref command.Style);
            worker.Bind(ref command.StartWork);
            worker.Bind(ref command.Origin);
            worker.Bind(ref command.Direction);
            if (command.Action == Rim3DAction.Fire || command.Action == Rim3DAction.FireControl)
                worker.Bind(ref command.AimPawnHeight);
        }
    }

    static class Adapter
    {
        sealed class Binding
        {
            public Pawn Pawn;
            public string Owner, Token;
            public string ApiFingerprint;
            public bool Pending, NormalizeEntrySpeed;
            public float PendingUntil, NextSend, LastSend;
            public Vector2 LastInput;
            public float LastYaw, LastPitch, LastVertical, LastLookYaw, LastLookPitch;
            public bool ExtendedLook;
            public bool LastSneaking, LastSprinting, LastWeaponAiming, Sent;
            public Vector3 Position, LastAuthoritativePosition;
            public bool PositionInitialized;
            public Map PositionMap;
            public Queue<CombatFeedback> CombatFeedback;
        }
        readonly struct CombatFeedback
        {
            public readonly string Status;
            public readonly bool Drafted, Changed;
            public CombatFeedback(string status, bool drafted, bool changed)
            { Status = status; Drafted = drafted; Changed = changed; }
        }
        static readonly ConditionalWeakTable<PawnControl, Binding> bindings = new ConditionalWeakTable<PawnControl, Binding>();
        static MapView LocalView => Rim3DPreview.Instance?.ActiveWindow?.View;
        static readonly Type timeControls = AccessTools.TypeByName("Multiplayer.Client.AsyncTime.TimeControlPatch");
        static readonly MethodInfo uiSpeed = AccessTools.PropertyGetter(timeControls, "CurTimeSpeedUI");
        static readonly MethodInfo toggleSpeed = AccessTools.Method(timeControls, "TogglePaused");
        static readonly MethodInfo sendSpeed = AccessTools.Method(timeControls, "SendTimeVote");
        static readonly object[] normalSpeedVote = CreateNormalSpeedVote();

        static object[] CreateNormalSpeedVote()
        {
            var parameters = sendSpeed?.GetParameters();
            if (parameters == null || parameters.Length != 1) return null;
            var voteType = parameters[0].ParameterType;
            // MP uses TimeVote in newer versions and TimeSpeed in older ones.
            // Box the actual parameter enum once; reflection cannot convert enums.
            return voteType.IsEnum && Enum.IsDefined(voteType, "Normal")
                ? new[] { Enum.Parse(voteType, "Normal") } : null;
        }

        public static void Install()
        {
            Rim3DMultiplayer.IsActive = () => MP.IsInMultiplayer;
            Rim3DMultiplayer.PrepareNeighborMaps = map =>
            {
                if (MP.InInterface && MP.IsHosting) SyncCommands.PrepareNeighborMaps(map);
            };
            Rim3DMultiplayer.Acquire = Acquire;
            Rim3DMultiplayer.AvailableForLocal = pawn =>
            {
                var session = RimVisionControl.MapFor(pawn)?.GetComponent<ControlSessions>()?.Find(pawn);
                return session == null || session.Owner == MP.PlayerName;
            };
            Rim3DMultiplayer.Release = Release;
            Rim3DMultiplayer.Move = Move;
            Rim3DMultiplayer.Combat = (control, enabled) =>
            {
                if (GetBinding(control, out var binding)) SyncCommands.Combat(binding.Pawn, binding.Owner, binding.Token, enabled);
            };
            Rim3DMultiplayer.Cancel = control =>
            {
                if (GetBinding(control, out var binding)) SyncCommands.Cancel(binding.Pawn, binding.Owner, binding.Token);
            };
            Rim3DMultiplayer.Send = Send;
            Rim3DMultiplayer.SendLayerAction = SendLayerAction;
            Rim3DMultiplayer.SendApiRequest = SendApiRequest;
            Rim3DMultiplayer.SendCombatInput = (control, command) =>
            {
                if (!GetBinding(control, out var binding)) return;
                SendActionPosition(binding, control);
                SyncCommands.CombatInput(binding.Pawn, binding.Owner, binding.Token, command);
            };
            Rim3DMultiplayer.SendOverviewPlace = SyncCommands.OverviewPlace;
            Rim3DMultiplayer.SendColonyOrder = SendColonyOrder;
            Rim3DMultiplayer.GetPosition = Position;
            Rim3DMultiplayer.MapTicks = MultiplayerControlPausePatch.MapTicks;
            Rim3DMultiplayer.GetController = pawn => RimVisionControl.MapFor(pawn)?.GetComponent<ControlSessions>()?.Find(pawn)?.Controller;
            Rim3DMultiplayer.GetAim = Aim;
            Rim3DMultiplayer.GetWeaponAiming = WeaponAiming;
            Rim3DMultiplayer.Notice = Notice;
            Rim3DMultiplayer.GrowingZoneCreated = (pawn, zone) =>
            {
                if (IsLocalPawn(pawn)) LocalView.NotifyGrowingZoneCreated(zone);
            };
            Rim3DMultiplayer.SowingResult = (pawn, cell, jobId, started) =>
            {
                if (IsLocalPawn(pawn)) LocalView.NotifySowingResult(cell, jobId, started);
            };
            Rim3DMultiplayer.TogglePause = TogglePause;
            Rim3DMultiplayer.IsHost = () => MP.IsHosting;
            Rim3DMultiplayer.MinigameRewardsAllowed = () =>
                Current.Game?.GetComponent<HostMovementSettings>()?.MinigameRewardsAllowed == true;
            Rim3DMultiplayer.MinigameRewardMultiplier = kind =>
                Current.Game?.GetComponent<HostMovementSettings>()?.MinigameRewardMultiplier(kind) ?? 1;
            Rim3DMultiplayer.MinigameTickRate = MultiplayerControlPausePatch.TickRate;
            Rim3DMultiplayer.MinigameReward = (control, jobId, toilIndex, milestone) =>
            {
                if (MP.InInterface && GetBinding(control, out var binding))
                    SyncCommands.MinigameReward(binding.Pawn, binding.Owner, binding.Token, jobId, toilIndex, milestone);
            };
            Rim3DMultiplayer.PawnLockEnabled = () =>
                Current.Game?.GetComponent<HostMovementSettings>()?.PawnLockEnabled == true;
            Rim3DMultiplayer.PawnAllowed = pawn =>
                Current.Game?.GetComponent<HostMovementSettings>()?.AllowsPawn(MP.PlayerName, pawn) != false;
            Rim3DMultiplayer.CompassDisabled = () =>
                Current.Game?.GetComponent<HostMovementSettings>()?.CompassDisabled == true;
            Rim3DMultiplayer.ShootWhileRunningAllowed = () =>
                Current.Game?.GetComponent<HostMovementSettings>()?.ShootWhileRunningAllowed == true;
            Rim3DMultiplayer.PlantSlowdownDisabled = () =>
                Current.Game?.GetComponent<HostMovementSettings>()?.DisablePlantSlowdown == true;
            Rim3DMultiplayer.ActionAccelerationEnabled = map => map?.GetComponent<ControlSessions>()?.ActionAccelerationEnabled == true;
            Rim3DMultiplayer.ActionAccelerationActive = map => map?.GetComponent<ControlSessions>()?.AcceleratingActions == true;
            Rim3DMultiplayer.ToggleActionAcceleration = map =>
            {
                if (MP.InInterface && map != null) SyncCommands.ToggleAcceleration(map);
            };
            Rim3DMultiplayer.ConfirmHit = (pawn, killed) =>
            {
                if (IsLocalPawn(pawn)) LocalView.ShowMultiplayerShot(true, killed);
            };
            Rim3DMultiplayer.AnimationEvent = notification => LocalView?.NotifyAnimationEvent(notification);
            Rim3DMultiplayer.Shot = pawn =>
            {
                if (IsLocalPawn(pawn)) LocalView.ShowMultiplayerShot(false, false);
            };
            Rim3DMultiplayer.ShotSound = ShotAudio.Enqueue;
            if (uiSpeed == null || toggleSpeed == null || sendSpeed == null || normalSpeedVote == null)
                Log.Warning("[RimVision Multiplayer] Multiplayer time-vote UI not found; use Multiplayer's normal time controls.");
        }

        static bool Acquire(PawnControl control, Pawn pawn)
        {
            if (!MP.InInterface) return false;
            if (!PawnControl.CanAcquire(pawn)) return false;
            if (Rim3DMultiplayer.PawnLocked &&
                (Rim3DMultiplayer.PawnAllowed?.Invoke(pawn) == false ||
                bindings.TryGetValue(control, out var currentBinding) && currentBinding.Pawn != pawn)) return false;
            var session = RimVisionControl.MapFor(pawn).GetComponent<ControlSessions>().Find(pawn);
            if (session != null && session.Owner != MP.PlayerName)
            { LocalView?.ShowMultiplayerNotice("Rim3D_MultiplayerOwner".Translate(session.Owner).ToString(), false); return false; }
            Release(control);
            var binding = new Binding
            {
                Pawn = pawn, Owner = MP.PlayerName, Token = Guid.NewGuid().ToString("N"),
                Pending = true, NormalizeEntrySpeed = true, PendingUntil = Time.unscaledTime + 15f
            };
            bindings.Add(control, binding);
            control.BindPresentation(pawn);
            SyncCommands.Claim(pawn, binding.Owner, binding.Token);
            SendApiContract(binding);
            return true;
        }
        static void Release(PawnControl control)
        {
            if (bindings.TryGetValue(control, out var binding))
            {
                if (RimVisionControl.MapFor(binding.Pawn) != null) SyncCommands.Release(binding.Pawn, binding.Owner, binding.Token);
                bindings.Remove(control);
            }
            control.BindPresentation(null);
        }
        static bool GetBinding(PawnControl control, out Binding binding, bool requireInterface = true)
        {
            binding = null;
            if (requireInterface && !MP.InInterface) return false;
            if (!bindings.TryGetValue(control, out binding) || RimVisionControl.MapFor(binding.Pawn) == null) return false;
            return binding.Pending || RimVisionControl.MapFor(binding.Pawn).GetComponent<ControlSessions>().Owns(binding.Pawn, binding.Owner, binding.Token, out _);
        }
        public static void ClaimResult(Pawn pawn, string token, bool accepted)
        {
            var view = LocalView;
            if (view == null || !bindings.TryGetValue(view.Controller, out var binding) || binding.Pawn != pawn || binding.Token != token) return;
            binding.Pending = false;
            if (!accepted)
            {
                bindings.Remove(view.Controller);
                view.Controller.BindPresentation(null);
                view.ShowMultiplayerNotice("Rim3D_MultiplayerControlFailed".Translate().ToString(), false);
            }
        }
        static void Move(PawnControl control, Vector2 input, float yaw)
        {
            if (!MP.InInterface) return;
            if (!bindings.TryGetValue(control, out var binding)) { control.BindPresentation(null); return; }
            SendApiContract(binding);
            ShowCombatFeedback(binding);
            if (!PawnControl.Observable(binding.Pawn))
            { Release(control); return; }
            var session = RimVisionControl.MapFor(binding.Pawn).GetComponent<ControlSessions>().Find(binding.Pawn);
            if (session == null)
            {
                foreach (var source in Current.Game.Maps)
                    if (source.GetComponent<ControlSessions>().Owns(binding.Pawn, binding.Owner, binding.Token, out _)) return;
            }
            if (binding.PositionMap != RimVisionControl.MapFor(binding.Pawn))
            {
                binding.PositionMap = RimVisionControl.MapFor(binding.Pawn);
                binding.PositionInitialized = false;
                binding.Sent = false;
            }
            var localPosition = control.Position;
            if (session?.Owner == binding.Owner && session.Token == binding.Token)
            {
                binding.Pending = false;
                control.BindPresentation(binding.Pawn, session.Controller);
                var authoritative = session.Controller.AutomaticActionPath && !session.Controller.HasNativeControl
                    ? binding.Pawn.DrawPos : session.Controller.Position;
                if (!session.Controller.HasNativeControl) authoritative.y = 0;
                if (!binding.PositionInitialized || control.InputBlocked || session.Controller.HasNativeControl ||
                    ((session.Controller.IsActing || session.Controller.IsResting) && input.sqrMagnitude <= .001f) ||
                    (authoritative - binding.LastAuthoritativePosition).sqrMagnitude > 9)
                    localPosition = binding.Position = authoritative;
                else localPosition = binding.Position;
                binding.LastAuthoritativePosition = authoritative;
                binding.PositionInitialized = true;
            }
            else if (!binding.Pending || Time.unscaledTime > binding.PendingUntil)
            {
                Release(control);
                LocalView?.ShowMultiplayerNotice("Rim3D_MultiplayerControlEnded".Translate().ToString(), false);
                return;
            }
            // The owner supplies the continuous position. Never feed the delayed
            // network echo or remote interpolation back into its camera.
            binding.Position = control.PreviewMovement(localPosition, input, yaw, Time.unscaledDeltaTime);
            control.SetPresentationPosition(binding.Position);
            yaw = Mathf.Round(Mathf.Repeat(yaw, 360) * 2) * .5f;
            float pitch = Mathf.Round((LocalView?.ViewPitch ?? 0) * 2) * .5f;
            float now = Time.unscaledTime;
            bool stopped = input == Vector2.zero && binding.LastInput != Vector2.zero;
            float vertical = control.HasNativeControl ? LocalView?.NativeVerticalInput ?? 0 : 0;
            bool extendedLook = control.HasNativeControl || LocalView?.CustomCamera == true;
            bool weaponAiming = LocalView?.WeaponAiming == true;
            var aim = LocalView?.ViewAimDirection ?? Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
            float lookYaw = Mathf.Round(Mathf.Repeat(Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg, 360) * 2) * .5f;
            float lookPitch = Mathf.Round(-Mathf.Asin(Mathf.Clamp(aim.y, -1, 1)) * Mathf.Rad2Deg * 2) * .5f;
            bool changed = extendedLook != binding.ExtendedLook || (extendedLook && (lookYaw != binding.LastLookYaw || lookPitch != binding.LastLookPitch)) ||
                vertical != binding.LastVertical || input != binding.LastInput || yaw != binding.LastYaw || pitch != binding.LastPitch ||
                control.Sneaking != binding.LastSneaking || control.Sprinting != binding.LastSprinting || weaponAiming != binding.LastWeaponAiming;
            float heartbeat = input == Vector2.zero ? .25f : 1f / 30f;
            if (!binding.Sent || stopped || input != binding.LastInput || (changed && now >= binding.NextSend) || now - binding.LastSend >= heartbeat)
            {
                SyncCommands.Input(binding.Pawn, binding.Owner, binding.Token, new ControlInput
                {
                    X = input.x, Z = input.y, Yaw = yaw, Pitch = pitch, Sneaking = control.Sneaking,
                    Sprinting = control.Sprinting, Position = binding.Position,
                    BlockAutomaticPawnSwitch = Rim3DSettings.Current.BlockAutomaticPawnSwitch || Rim3DMultiplayer.PawnLocked
                });
                if (extendedLook || binding.ExtendedLook)
                    SyncCommands.NativeAxes(binding.Pawn, binding.Owner, binding.Token, vertical, lookYaw, lookPitch, extendedLook);
                if (!binding.Sent || weaponAiming != binding.LastWeaponAiming)
                    SyncCommands.WeaponAim(binding.Pawn, binding.Owner, binding.Token, weaponAiming);
                binding.LastWeaponAiming = weaponAiming;
                binding.LastVertical = vertical; binding.LastLookYaw = lookYaw; binding.LastLookPitch = lookPitch;
                binding.ExtendedLook = extendedLook;
                binding.LastInput = input; binding.LastYaw = yaw; binding.LastPitch = pitch;
                binding.LastSneaking = control.Sneaking; binding.LastSprinting = control.Sprinting;
                binding.LastSend = now; binding.NextSend = now + 1f / 30f; binding.Sent = true;
            }
        }
        public static void CombatResult(Pawn pawn, string owner, string token, string status, bool drafted, bool changed)
        {
            var view = LocalView;
            if (view == null || !bindings.TryGetValue(view.Controller, out var binding) ||
                binding.Pawn != pawn || binding.Owner != owner || binding.Token != token) return;
            // Confirmation only queues presentation data; audio runs later in UI.
            if (binding.CombatFeedback == null) binding.CombatFeedback = new Queue<CombatFeedback>();
            binding.CombatFeedback.Enqueue(new CombatFeedback(status, drafted, changed));
        }
        static void ShowCombatFeedback(Binding binding)
        {
            while (binding.CombatFeedback?.Count > 0)
            {
                var feedback = binding.CombatFeedback.Dequeue();
                if (!string.IsNullOrEmpty(feedback.Status) || !feedback.Drafted)
                    LocalView?.ShowMultiplayerNotice(!string.IsNullOrEmpty(feedback.Status) ? feedback.Status :
                        "Rim3D_PeacefulMode".Translate().ToString(), false);
                if (!feedback.Changed) continue;
                Rand.PushState();
                try { (feedback.Drafted ? SoundDefOf.DraftOn : SoundDefOf.DraftOff).PlayOneShotOnCamera(); }
                finally { Rand.PopState(); }
            }
        }
        static void Send(MapView view, Rim3DCommand command)
        {
            if (!GetBinding(view.Controller, out var binding)) return;
            // Actions use the owner's latest position, including movement since
            // the last periodic snapshot. Both commands execute in network order.
            SendActionPosition(binding, view.Controller);
            SyncCommands.Action(binding.Pawn, binding.Owner, binding.Token, command);
        }
        static void SendColonyOrder(PawnControl control, ColonyOrder order)
        {
            if (!GetBinding(control, out var binding)) return;
            SyncCommands.ColonyOrder(binding.Pawn, binding.Owner, binding.Token, order);
        }

        static void SendApiRequest(PawnControl control, RimVisionApiRequestCommand command)
        {
            if (!GetBinding(control, out var binding)) { LocalView?.NotifyApiResult(command.RequestId, false, null); return; }
            SendActionPosition(binding, control);
            SyncCommands.ApiRequest(binding.Pawn, binding.Owner, binding.Token, command);
        }
        static void SendApiContract(Binding binding)
        {
            string fingerprint = RimVisionCapabilities.GameplayFingerprint;
            if (binding.ApiFingerprint == fingerprint) return;
            SyncCommands.ApiContract(binding.Pawn, binding.Owner, binding.Token, fingerprint);
            binding.ApiFingerprint = fingerprint;
        }
        internal static void ApiContractResult(string owner, string fingerprint)
        {
            if (RimVisionCapabilities.MatchesGameplayFingerprint(fingerprint)) return;
            var error = new InvalidOperationException("RimVision gameplay API contracts differ from " + owner + ".");
            RimVisionDiagnostics.ReportFault("multiplayer.contracts", owner, error);
            Log.ErrorOnce(error.Message, ("RimVisionApiContract:" + owner).GetHashCode());
            LocalView?.ShowMultiplayerNotice("Rim3D_ApiContractMismatch".Translate(owner).ToString(), false);
        }
        internal static void ApiJobResult(Pawn pawn, string owner, string token, RimVisionJobEvent notification)
        {
            var view = LocalView;
            if (view != null && view.ControlledPawn == pawn && GetBinding(view.Controller, out var binding, false) &&
                binding.Owner == owner && binding.Token == token) view.NotifyApiJobResult(notification);
        }
        internal static void ApiRequestResult(Pawn pawn, string owner, string token, int requestId, bool success, string reason)
        {
            var view = LocalView;
            if (view != null && view.ControlledPawn == pawn && GetBinding(view.Controller, out var binding, false) &&
                binding.Owner == owner && binding.Token == token) view.NotifyApiResult(requestId, success, reason);
        }

        static void SendLayerAction(PawnControl control, string id, Map targetMap, Thing target, IntVec3 cell, int value)
        {
            if (!GetBinding(control, out var binding)) return;
            SendActionPosition(binding, control);
            SyncCommands.LayerAction(binding.Pawn, binding.Owner, binding.Token, id, targetMap, target, cell, value);
        }
        static void SendActionPosition(Binding binding, PawnControl control)
        {
            SyncCommands.Input(binding.Pawn, binding.Owner, binding.Token,
                new ControlInput { X = binding.LastInput.x, Z = binding.LastInput.y, Yaw = binding.LastYaw, Pitch = binding.LastPitch,
                    Sneaking = control.Sneaking, Sprinting = control.Sprinting, Position = control.Position });
        }
        static Vector3 Position(Pawn pawn)
        {
            var session = RimVisionControl.MapFor(pawn)?.GetComponent<ControlSessions>()?.Find(pawn);
            if (session != null && session.Controller.Active && !session.Controller.IsActing && !session.Controller.IsResting) return session.Controller.Position;
            return pawn?.Position.ToVector3Shifted() ?? Vector3.zero;
        }
        static ControlSession RenderSession(Pawn pawn)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface || RimVisionControl.MapFor(pawn) == null) return null;
            var session = RimVisionControl.MapFor(pawn).GetComponent<ControlSessions>()?.Find(pawn);
            if (session == null) return null;
            if (!session.Controller.Active || session.Controller.HasNativeControl || session.Controller.IsActing || session.Controller.IsResting)
            {
                session.RenderInitialized = false;
                return null;
            }
            if (session.RenderFrame == Time.frameCount) return session;
            session.RenderFrame = Time.frameCount;
            var position = session.Controller.Position;
            if (!session.RenderInitialized || (session.RenderPosition - position).sqrMagnitude > 9)
            {
                session.RenderPosition = position;
                session.RenderYaw = session.Yaw; session.RenderPitch = session.Pitch;
                session.RenderInitialized = true;
            }
            else
            {
                float blend = 1 - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, .1f) * 25);
                session.RenderPosition = Vector3.Lerp(session.RenderPosition, position, blend);
                session.RenderYaw = Mathf.LerpAngle(session.RenderYaw, session.Yaw, blend);
                session.RenderPitch = Mathf.Lerp(session.RenderPitch, session.Pitch, blend);
            }
            return session;
        }
        public static Vector3? VisualPosition(Pawn pawn)
        {
            if (MP.IsInMultiplayer && MP.InInterface && IsLocalPawn(pawn))
                return LocalView.Controller.AutomaticActionPath ? (Vector3?)null : LocalView.Controller.Position;
            return RenderSession(pawn)?.RenderPosition;
        }
        public static Vector3? Aim(Pawn pawn)
        {
            if (pawn?.Drafted != true || pawn.equipment?.Primary?.def.IsRangedWeapon != true ||
                pawn.CurJob?.def.neverShowWeapon == true) return null;
            if (MP.IsInMultiplayer && MP.InInterface && IsLocalPawn(pawn) &&
                bindings.TryGetValue(LocalView.Controller, out var binding))
                return binding.ExtendedLook ? LocalView.ViewAimDirection : Quaternion.Euler(LocalView.ViewPitch, binding.LastYaw, 0) * Vector3.forward;
            var authoritative = MP.IsInMultiplayer && MP.InInterface ? RimVisionControl.MapFor(pawn)?.GetComponent<ControlSessions>()?.Find(pawn) : null;
            if (authoritative?.ExtendedLook == true && authoritative.Controller.Active && MultiplayerControlPausePatch.MapTicks(RimVisionControl.MapFor(pawn)) < authoritative.InputUntil)
                return Quaternion.Euler(authoritative.LookPitch, authoritative.LookYaw, 0) * Vector3.forward;
            var session = RenderSession(pawn);
            return session == null ? (Vector3?)null : Quaternion.Euler(session.RenderPitch, session.RenderYaw, 0) * Vector3.forward;
        }
        static bool WeaponAiming(Pawn pawn)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface || !Aim(pawn).HasValue) return false;
            if (IsLocalPawn(pawn)) return LocalView.WeaponAiming;
            var session = RimVisionControl.MapFor(pawn)?.GetComponent<ControlSessions>()?.Find(pawn);
            return session?.WeaponAiming == true && !session.Controller.IsActing && !session.Controller.IsResting &&
                MultiplayerControlPausePatch.MapTicks(RimVisionControl.MapFor(pawn)) < session.InputUntil;
        }
        static bool IsLocalPawn(Pawn pawn)
        {
            var view = LocalView;
            return view != null && view.ControlledPawn == pawn && GetBinding(view.Controller, out var binding, false) && binding.Pawn == pawn;
        }
        static void Notice(Pawn pawn, string text, bool floating)
        {
            if (IsLocalPawn(pawn)) LocalView.ShowMultiplayerNotice(text, floating);
        }
        static void TogglePause()
        {
            if (uiSpeed == null || toggleSpeed == null || sendSpeed == null || Event.current == null) return;
            var vote = toggleSpeed.Invoke(null, new[] { uiSpeed.Invoke(null, null) });
            sendSpeed.Invoke(null, new[] { vote });
        }

        public static void ApplyEntrySpeed(MapView view)
        {
            if (view?.FirstPerson != true || !MP.InInterface || Event.current == null || normalSpeedVote == null ||
                !bindings.TryGetValue(view.Controller, out var binding) || binding.Pending || !binding.NormalizeEntrySpeed) return;
            // Use MP's time vote once on entry. Its UI method requires a GUI event.
            sendSpeed.Invoke(null, normalSpeedVote);
            binding.NormalizeEntrySpeed = false;
        }
    }

    [HarmonyPatch(typeof(PreviewWindow), nameof(PreviewWindow.DoWindowContents))]
    static class FirstPersonEntrySpeedPatch
    {
        static void Prefix(PreviewWindow __instance)
        {
            if (MP.IsInMultiplayer) Adapter.ApplyEntrySpeed(__instance.View);
        }
    }

    [HarmonyPatch(typeof(PreviewWindow), "UpdateMode")]
    static class PreviewTimePatch
    {
        static bool Prefix(PreviewWindow __instance)
        {
            if (!MP.IsInMultiplayer) return true;
            __instance.forcePause = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.Paused), MethodType.Getter)]
    static class MultiplayerControlPausePatch
    {
        static readonly Type _asyncTimeType = AccessTools.TypeByName("Multiplayer.Client.AsyncTimeComp");
        static readonly AccessTools.FieldRef<Map> _executingMap = AccessTools.StaticFieldRefAccess<Map>(
            AccessTools.Field(_asyncTimeType, "executingCmdMap"));
        static readonly AccessTools.FieldRef<Map> _tickingMap = AccessTools.StaticFieldRefAccess<Map>(
            AccessTools.Field(_asyncTimeType, "tickingMap"));
        static readonly Func<Map, object> _asyncTime = AccessTools.MethodDelegate<Func<Map, object>>(
            AccessTools.Method(AccessTools.TypeByName("Multiplayer.Client.Extensions"), "AsyncTime", new[] { typeof(Map) }));
        static readonly ParameterExpression _pausedInstance = Expression.Parameter(typeof(object));
        static readonly Func<object, bool> _isPaused = Expression.Lambda<Func<object, bool>>(
            Expression.Call(Expression.Convert(_pausedInstance, _asyncTimeType),
                AccessTools.PropertyGetter(_asyncTimeType, "Paused")), _pausedInstance).Compile();
        static readonly Func<object, int> _mapTicks = Expression.Lambda<Func<object, int>>(
            Expression.Field(Expression.Convert(_pausedInstance, _asyncTimeType), "mapTicks"), _pausedInstance).Compile();
        static readonly Func<object, float> _tickRate = Expression.Lambda<Func<object, float>>(
            Expression.Call(AccessTools.Method(AccessTools.TypeByName("Multiplayer.Client.TickPatch"), "ActualRateMultiplier"),
                Expression.Convert(_pausedInstance, _asyncTimeType),
                Expression.Property(Expression.Convert(_pausedInstance, _asyncTimeType), "DesiredTimeSpeed")), _pausedInstance).Compile();

        internal static float TickRate(Map map)
        {
            var component = map == null ? null : _asyncTime(map);
            return component == null ? 0 : _tickRate(component);
        }

        internal static int MapTicks(Map map)
        {
            var component = map == null ? null : _asyncTime(map);
            return component == null ? Find.TickManager.TicksGame : _mapTicks(component);
        }

        [HarmonyPriority(Priority.Last)]
        static void Postfix(ref bool __result)
        {
            if (!MP.IsInMultiplayer || !Rim3DMultiplayer.Simulating) return;
            var map = _executingMap() ?? _tickingMap();
            var component = map == null ? null : _asyncTime(map);
            if (component != null) __result = _isPaused(component);
        }
    }

    [HarmonyPatch]
    static class MultiplayerSleepMapSpeed
    {
        static readonly AccessTools.FieldRef<TimeSlower, int> forceNormalUntil =
            AccessTools.FieldRefAccess<TimeSlower, int>("forceNormalSpeedUntil");
        static MethodBase TargetMethod() => AccessTools.Method(
            AccessTools.TypeByName("Multiplayer.Client.AsyncTimeComp"), "TickRateMultiplier");
        static void Postfix(Map ___map, TimeSlower ___slower, int ___mapTicks, ref float __result)
        {
            if (!MP.IsInMultiplayer || __result <= 0 || ___mapTicks < forceNormalUntil(___slower)) return;
            var control = ___map.GetComponent<ControlSessions>();
            if (control?.AcceleratingSleep == true)
                __result = Mathf.Max(__result, 10f);
            else if (control?.AcceleratingActions == true) __result = Mathf.Max(__result, 6f);
            else if (control?.HasActiveController == true) __result = Mathf.Min(__result, 1f);
        }
    }

    [HarmonyPatch]
    static class MultiplayerSleepWorldSpeed
    {
        static MethodBase TargetMethod() => AccessTools.Method(
            AccessTools.TypeByName("Multiplayer.Client.AsyncTime.AsyncWorldTimeComp"), "TickRateMultiplier");
        static void Postfix(ref float __result)
        {
            if (!MP.IsInMultiplayer || __result <= 0) return;
            bool sleepingController = false;
            bool working = false;
            foreach (var map in Find.Maps)
            {
                var control = map.GetComponent<ControlSessions>();
                if (control?.AcceleratingSleep == true)
                {
                    sleepingController = true;
                    continue;
                }
                if (!ControlSessions.NoAwakeControllableColonists(map))
                {
                    if (control?.AcceleratingActions != true) return;
                    working = true;
                }
            }
            if (working) __result = Mathf.Max(__result, 6f);
            else if (sleepingController) __result = Mathf.Max(__result, 10f);
        }
    }
    [HarmonyPatch(typeof(PreviewWindow), "RestoreSpeed")]
    static class RestoreTimePatch
    {
        static bool Prefix() => !MP.IsInMultiplayer;
    }
    [HarmonyPatch(typeof(PreviewWindow), MethodType.Constructor, new[] { typeof(Map), typeof(Pawn), typeof(bool), typeof(Vector3) })]
    static class PreviewWindowPatch
    {
        static void Postfix(PreviewWindow __instance)
        {
            if (MP.IsInMultiplayer) { __instance.forcePause = false; __instance.preventSave = false; }
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "EndCurrentJob")]
    static class CombatRecoveryPatch
    {
        static void Prefix(Pawn ___pawn)
        {
            if (!MP.IsInMultiplayer || RimVisionControl.MapFor(___pawn) == null) return;
            RimVisionControl.MapFor(___pawn).GetComponent<ControlSessions>()?.Find(___pawn)?.Controller.RememberCombatRecovery();
        }
    }

    [HarmonyPatch(typeof(PawnTweener), "TweenedPos", MethodType.Getter)]
    static class ContinuousPositionPatch
    {
        [HarmonyPriority(Priority.Last)]
        static void Postfix(Pawn ___pawn, ref Vector3 __result)
        {
            if (Adapter.VisualPosition(___pawn) is Vector3 position) __result = position;
        }
    }

    [HarmonyPatch]
    static class PlayerCursor
    {
        static MethodBase TargetMethod() => AccessTools.Method(
            AccessTools.TypeByName("Multiplayer.Client.PlayerCursors"), "SendCursor");

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(UI), nameof(UI.MouseMapPosition));
            var replacement = AccessTools.Method(typeof(PlayerCursor), nameof(CursorPosition));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(original)) instruction.operand = replacement;
                yield return instruction;
            }
        }

        static Vector3 CursorPosition()
        {
            var window = Rim3DPreview.Instance?.ActiveWindow;
            var view = window?.View;
            // Reuse MP's visual packet. A 2D observer sees the cursor at the
            // controlled pawn's feet; closing 3D restores normal mouse coordinates.
            if (MP.IsInMultiplayer && window?.Map == Find.CurrentMap && view?.ControlledPawn is Pawn pawn && pawn.Spawned)
            {
                var position = view.Controller.Position;
                return new Vector3(position.x, 0, position.z);
            }
            return UI.MouseMapPosition();
        }
    }
    internal static class CompatibilityDescription
    {
        static readonly FieldInfo DescriptionField = AccessTools.Field(typeof(ModMetaData), "description");
        static readonly PropertyInfo InstalledMods = AccessTools.Property(typeof(ModLister), "AllInstalledMods");
        static readonly FieldInfo InstalledModsField = AccessTools.Field(typeof(ModLister), "allInstalledMods");
        static readonly Dictionary<ModMetaData, string> EnglishDescriptions = new Dictionary<ModMetaData, string>();

        internal static void Install()
        {
            var target = AccessTools.Method(typeof(Page_ModsConfig), "DoWindowContents");
            if (target == null || DescriptionField == null)
            {
                Log.Warning("[RimVision Multiplayer] Description localization could not be installed.");
                return;
            }
            new Harmony("papaya.rimvision.multiplayer.description").Patch(target,
                prefix: new HarmonyMethod(typeof(CompatibilityDescription), nameof(Update)));
        }

        static void Update()
        {
            if (LanguageDatabase.activeLanguage == null) return;
            var mods = InstalledMods?.GetValue(null, null) ?? InstalledModsField?.GetValue(null);
            if (!(mods is System.Collections.IEnumerable entries)) return;

            bool russian = string.Equals(LanguageDatabase.activeLanguage.LegacyFolderName, "Russian",
                StringComparison.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!(entry is ModMetaData metadata) ||
                    !string.Equals(metadata.PackageId, "papaya.rimvision.multiplayer",
                        StringComparison.OrdinalIgnoreCase)) continue;

                if (!EnglishDescriptions.TryGetValue(metadata, out var english))
                {
                    english = (string)DescriptionField.GetValue(metadata);
                    EnglishDescriptions.Add(metadata, english);
                }
                DescriptionField.SetValue(metadata, russian ? Russian : english);
            }
        }

        static readonly string Russian = ReadRussianDescription();

        static string ReadRussianDescription()
        {
            using (var reader = new System.IO.StreamReader(typeof(CompatibilityDescription).Assembly.GetManifestResourceStream("RimVision.Version.txt")))
                return reader.ReadToEnd().Trim() + "\n\n" + RussianBody;
        }

        const string RussianBody = @"RIMVISION – СОВМЕСТИМОСТЬ С MULTIPLAYER

Увидьте свою колонию вместе с друзьями.

Пройдитесь по собственной базе, возьмите управление поселенцем и участвуйте в жизни колонии вместе с другими игроками. Это дополнение связывает управление от первого лица в RimVision с Multiplayer и синхронизирует ваши действия в общей игре.

ВОЗМОЖНОСТИ

• Перемещайтесь и управляйте поселенцами от первого лица в сетевой игре.
• Сражайтесь, используйте экипировку и взаимодействуйте с окружением.
• Стройте, переносите ресурсы и работайте за производственными столами.
• Управляйте инвентарём, переносите предметы и персонажей, ешьте и спите.
• Размечайте зоны, устанавливайте и снимайте крыши.
• Сохраняйте сеансы управления при сохранении и загрузке колонии.
• Используйте собственную камеру, интерфейс и визуальные эффекты.

Действия, влияющие на колонию, синхронизируются через Multiplayer. Камера и настройки изображения остаются локальными у каждого игрока.

ТРЕБОВАНИЯ

• RimWorld 1.6.
• Harmony.
• Multiplayer.
• RimVision.

Это дополнение к RimVision. Всем игрокам нужны одинаковые версии RimVision, Multiplayer и мода совместимости, а также одинаковые списки активных модов.

УСТАНОВКА

Включите необходимые моды у каждого игрока в следующем порядке:

Harmony → Core и DLC → Multiplayer → RimVision → RimVision – Multiplayer Compatibility.

После установки или обновления модов перезапустите RimWorld, затем создайте сетевую игру или подключитесь к сессии Multiplayer.

КАК НАЧАТЬ

Загрузите колонию и используйте привычное управление RimVision:

O: включить 3D или вернуться к обычному виду.
V: переключить общий обзор и вид от первого лица.
WASD: перемещение. Мышь: осмотр. Alt: освободить курсор.
E: взаимодействовать. ПКМ: меню действий.
F: боевой режим. ЛКМ: атака в боевом режиме.
B: строительство и зоны. Tab: персонаж и инвентарь.
Q: отменить действие.

Остальные действия показаны в контекстных подсказках.

СОВМЕСТИМОСТЬ И РАЗРАБОТКА

Дополнение находится в активной разработке. Отсутствие рассинхронизаций не гарантировано.

Сообщая о проблеме, приложите лог игры, отчёт Multiplayer о рассинхронизации при его наличии, список модов и шаги для повторения ошибки. Укажите, что делал каждый игрок в момент её появления.

БЛАГОДАРНОСТИ

Спасибо команде Multiplayer, Brrainz за Harmony, Tynan и Ludeon Studios за RimWorld и всем, кто помогает с проверкой мода и сообщает об ошибках.

RimWorld принадлежит Ludeon Studios.";
    }

}
