using System.Collections.Generic;
using Multiplayer.API;
using Recovery.Rim3D;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.Multiplayer
{
    // Global saved state also reaches late joiners and newly generated maps.
    public sealed class HostMovementSettings : GameComponent
    {
        static readonly WorkMinigameKind[] _minigameKinds = (WorkMinigameKind[])System.Enum.GetValues(typeof(WorkMinigameKind));
        public bool DisablePlantSlowdown;
        public bool CompassDisabled;
        bool _shootWhileRunningAllowed;
        float _nextShootWhileRunningSend;
        internal bool ShootWhileRunningAllowed
        {
            get => _shootWhileRunningAllowed;
            set => _shootWhileRunningAllowed = value;
        }
        bool _minigameRewardsAllowed;
        float _nextMinigameSettingsSend;
        Dictionary<WorkMinigameKind, float> _minigameMultipliers = new Dictionary<WorkMinigameKind, float>();
        internal float MinigameRewardMultiplier(WorkMinigameKind kind) =>
            _minigameMultipliers.TryGetValue(kind, out float value) ? WorkMinigames.ClampRewardMultiplier(value) : 1;
        internal void SetMinigameRewardMultiplier(WorkMinigameKind kind, float value)
        {
            if (!System.Enum.IsDefined(typeof(WorkMinigameKind), kind)) return;
            _minigameMultipliers[kind] = WorkMinigames.ClampRewardMultiplier(value);
        }
        internal bool MinigameRewardsAllowed
        {
            get => _minigameRewardsAllowed;
            set => _minigameRewardsAllowed = value;
        }
        bool _pawnLockEnabled;
        string _hostName;
        Dictionary<string, Pawn> _lockedPawns = new Dictionary<string, Pawn>();
        List<string> _lockedOwners;
        List<Pawn> _lockedPawnValues;
        float _nextPawnLockSend;
        internal bool PawnLockEnabled => _pawnLockEnabled;
        float _nextCompassSend;
        float nextSend;

        public HostMovementSettings(Game game) { }

        internal bool AllowsPawn(string owner, Pawn pawn) => !_pawnLockEnabled || owner == _hostName ||
            !string.IsNullOrEmpty(owner) && (!_lockedPawns.TryGetValue(owner, out var locked) || locked == pawn);

        internal void RegisterPawn(string owner, Pawn pawn)
        {
            if (_pawnLockEnabled && owner != _hostName && !string.IsNullOrEmpty(owner) &&
                pawn != null && !_lockedPawns.ContainsKey(owner)) _lockedPawns.Add(owner, pawn);
        }

        internal void SetPawnLock(bool enabled, string hostName)
        {
            _pawnLockEnabled = enabled;
            _hostName = hostName;
            if (!enabled) { _lockedPawns.Clear(); return; }
            foreach (var map in Find.Maps) map.GetComponent<ControlSessions>().RegisterLockedPawns(this);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref DisablePlantSlowdown, "rim3DDisablePlantSlowdown", false);
            Scribe_Values.Look(ref CompassDisabled, "rim3DCompassDisabled", false);
            Scribe_Values.Look(ref _shootWhileRunningAllowed, "rim3DShootWhileRunningAllowed", false);
            Scribe_Values.Look(ref _minigameRewardsAllowed, "rim3DMinigameRewardsAllowed", false);
            Scribe_Collections.Look(ref _minigameMultipliers, "rim3DMinigameRewardMultipliers", LookMode.Value, LookMode.Value);
            if (_minigameMultipliers == null) _minigameMultipliers = new Dictionary<WorkMinigameKind, float>();
            Scribe_Values.Look(ref _pawnLockEnabled, "rim3DPawnLockEnabled", false);
            Scribe_Values.Look(ref _hostName, "rim3DPawnLockHost");
            Scribe_Collections.Look(ref _lockedPawns, "rim3DLockedPawns", LookMode.Value, LookMode.Reference,
                ref _lockedOwners, ref _lockedPawnValues);
            if (_lockedPawns == null) _lockedPawns = new Dictionary<string, Pawn>();
        }

        public override void GameComponentUpdate()
        {
            if (!MP.IsInMultiplayer || !MP.IsHosting || !MP.InInterface) return;
            bool shootWhileRunning = Rim3DSettings.Current.ShootWhileRunning;
            if (_shootWhileRunningAllowed != shootWhileRunning && Time.unscaledTime >= _nextShootWhileRunningSend)
            {
                _nextShootWhileRunningSend = Time.unscaledTime + 1;
                SyncCommands.SetShootWhileRunning(shootWhileRunning);
            }
            bool rewardsAllowed = Rim3DSettings.Current.MinigameWorkBonus;
            if (Time.unscaledTime >= _nextMinigameSettingsSend)
            {
                _nextMinigameSettingsSend = Time.unscaledTime + 1;
                if (_minigameRewardsAllowed != rewardsAllowed) SyncCommands.SetMinigameRewards(rewardsAllowed);
                foreach (var kind in _minigameKinds)
                {
                    float multiplier = Rim3DSettings.Current.MinigameRewardMultiplier(kind);
                    if (MinigameRewardMultiplier(kind) != multiplier) SyncCommands.SetMinigameMultiplier((int)kind, multiplier);
                }
            }
            bool pawnLock = Rim3DSettings.Current.LockClientPawns;
            if ((_pawnLockEnabled != pawnLock || pawnLock && _hostName != MP.PlayerName) &&
                Time.unscaledTime >= _nextPawnLockSend)
            {
                _nextPawnLockSend = Time.unscaledTime + 1;
                SyncCommands.SetPawnLock(pawnLock, MP.PlayerName);
            }
            bool compassDisabled = !Rim3DSettings.Current.Compass;
            if (CompassDisabled != compassDisabled && Time.unscaledTime >= _nextCompassSend)
            {
                _nextCompassSend = Time.unscaledTime + 1;
                SyncCommands.SetCompassDisabled(compassDisabled);
            }
            bool disabled = Rim3DSettings.Current.DisablePlantSlowdown;
            if (DisablePlantSlowdown == disabled || Time.unscaledTime < nextSend) return;
            // Throttle retries while the synchronized command is in flight.
            nextSend = Time.unscaledTime + 1;
            SyncCommands.SetPlantSlowdown(disabled);
        }
    }
}
