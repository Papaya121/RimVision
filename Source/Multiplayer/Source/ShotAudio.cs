using System.Collections.Generic;
using Multiplayer.API;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Recovery.Rim3D.Multiplayer
{
    public sealed class ShotAudio : GameComponent
    {
        readonly struct Shot
        {
            internal readonly TargetInfo Source;
            internal readonly SoundDef Cast, Tail;
            internal readonly float CreatedAt;

            internal Shot(Pawn pawn, SoundDef cast, SoundDef tail)
            {
                Source = new TargetInfo(pawn.Position, pawn.Map);
                Cast = cast;
                Tail = tail;
                CreatedAt = Time.unscaledTime;
            }
        }

        readonly Queue<Shot> _pending = new Queue<Shot>();

        public ShotAudio(Game game) { }

        internal static void Enqueue(Pawn pawn, SoundDef cast, SoundDef tail)
        {
            if (pawn?.Map == null || pawn.Map != Find.CurrentMap || cast == null && tail == null) return;
            var audio = Current.Game?.GetComponent<ShotAudio>();
            if (audio == null) return;
            if (audio._pending.Count >= 128) audio._pending.Dequeue();
            audio._pending.Enqueue(new Shot(pawn, cast, tail));
        }

        public override void GameComponentUpdate()
        {
            if (!MP.IsInMultiplayer) { _pending.Clear(); return; }
            if (!MP.InInterface) return;
            while (_pending.Count > 0)
            {
                var shot = _pending.Dequeue();
                if (shot.Source.Map != Find.CurrentMap || Time.unscaledTime - shot.CreatedAt > 1) continue;
                Rand.PushState();
                try
                {
                    shot.Cast?.PlayOneShot(shot.Source);
                    shot.Tail?.PlayOneShotOnCamera(shot.Source.Map);
                }
                finally { Rand.PopState(); }
            }
        }
    }
}
