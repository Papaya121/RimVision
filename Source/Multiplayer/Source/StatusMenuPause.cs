using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Recovery.Rim3D;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.Multiplayer
{
    public sealed class StatusMenuPause : GameComponent
    {
        sealed class PauseSession : ExposableSession
        {
            public PauseSession(Map map) : base(map) { }

            public override Map Map => null;
            public override bool IsCurrentlyPausing(Map map) => ShouldPause(map);
            public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry) => null;
        }

        static readonly FieldInfo _playerType = AccessTools.Field(
            AccessTools.TypeByName("Multiplayer.Client.PlayerInfo"), "type");
        static readonly object _arbiterType = _playerType == null ? null : Enum.Parse(_playerType.FieldType, "Arbiter");
        List<string> _players = new List<string>();
        List<string> _openMenus = new List<string>();
        float _nextMenuSend, _nextPlayersSend;
        bool _sentMenu, _lastMenuOpen;

        public StatusMenuPause(Game game) { }

        public override void FinalizeInit()
        {
            if (!MP.IsInMultiplayer) return;
            var sessions = MP.GetGlobalSessionManager();
            if (sessions.GetFirstOfType<PauseSession>() == null)
                sessions.AddSession(new PauseSession(null));
        }

        internal static bool ShouldPause(Map map)
        {
            var state = Current.Game?.GetComponent<StatusMenuPause>();
            if (!MP.IsInMultiplayer || state == null || state._players.Count == 0) return false;
            foreach (var player in state._players)
                if (!state._openMenus.Contains(player)) return false;
            return true;
        }

        internal void SetMenu(string owner, bool open)
        {
            if (open)
            {
                if (!_openMenus.Contains(owner)) _openMenus.Add(owner);
            }
            else _openMenus.Remove(owner);
        }

        internal void SetPlayers(string[] players)
        {
            _players.Clear();
            _players.AddRange(players);
            for (int i = _openMenus.Count - 1; i >= 0; i--)
                if (!_players.Contains(_openMenus[i])) _openMenus.RemoveAt(i);
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref _players, "rim3DMenuPlayers", LookMode.Value);
            Scribe_Collections.Look(ref _openMenus, "rim3DOpenStatusMenus", LookMode.Value);
            if (_players == null) _players = new List<string>();
            if (_openMenus == null) _openMenus = new List<string>();
        }

        public override void GameComponentUpdate()
        {
            if (!MP.IsInMultiplayer || !MP.InInterface) return;
            float now = Time.unscaledTime;
            var window = Rim3DPreview.Instance?.ActiveWindow;
            bool open = window?.IsOpen == true && window.View?.StatusMenuOpen == true;
            string owner = MP.PlayerName;
            if (!_sentMenu || open != _lastMenuOpen ||
                now >= _nextMenuSend && _openMenus.Contains(owner) != open)
            {
                _sentMenu = true;
                _lastMenuOpen = open;
                _nextMenuSend = now + 1;
                SyncCommands.SetStatusMenu(owner, open);
            }
            if (now < _nextPlayersSend) return;
            _nextPlayersSend = now + 1;
            var connected = MP.GetPlayers();
            var players = new List<string>(connected.Count);
            int coordinatorId = int.MaxValue;
            string coordinator = null;
            foreach (var player in connected)
                if (_playerType == null || !Equals(_playerType.GetValue(player), _arbiterType))
                {
                    players.Add(player.Username);
                    if (player.Id < coordinatorId)
                    {
                        coordinatorId = player.Id;
                        coordinator = player.Username;
                    }
                }
            if (coordinator != owner) return;
            players.Sort(StringComparer.Ordinal);
            bool changed = players.Count != _players.Count;
            for (int i = 0; !changed && i < players.Count; i++)
                changed = players[i] != _players[i];
            if (changed) SyncCommands.SetMenuPlayers(players.ToArray());
        }
    }
}
