using System;
using Recovery.Rim3D;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimVision.ApiExample
{
    public sealed class ExampleMod : Mod
    {
        public ExampleMod(ModContentPack content) : base(content)
        {
            LongEventHandler.ExecuteWhenFinished(Register);
        }

        static void Register()
        {
            RimVisionApi.RegisterExtension("example.rimvision:status", () => new StatusOverlay());
            RimVisionApi.RegisterJobAction("example.rimvision:repair",
                context => "Repair (API example)",
                context => context.Target is Building building && building.def.useHitPoints,
                ValidateRepair,
                context => JobMaker.MakeJob(JobDefOf.Repair, context.Target));
            WorkMinigames.RegisterForPawn(JobDefOf.Repair, pawn => new TimingGame(pawn.LabelShort));
        }

        static AcceptanceReport ValidateRepair(RimVisionActionContext context)
        {
            var target = context.Target;
            if (!(target is Building) || !target.def.useHitPoints || target.Faction != context.Pawn.Faction)
                return "Select one of your buildings.";
            if (target.HitPoints >= target.MaxHitPoints) return "No repairs needed.";
            if (context.Pawn.Drafted || context.Pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) return "Pawn cannot repair.";
            if (target.IsForbidden(context.Pawn)) return "Target is forbidden.";
            if (!context.Pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)) return "Cannot reach.";
            return true;
        }
    }

    public sealed class StatusOverlay : RimVisionExtension
    {
        public override void DrawHud(RimVisionView view, Rect viewport)
        {
            var state = view.State;
            if (!state.FirstPerson || state.Pawn == null) return;
            Widgets.Label(new Rect(viewport.x + 16, viewport.y + 90, 300, 26),
                "API example: " + state.Pawn.LabelShort);
        }
    }

    public sealed class TimingGame : WorkMinigame
    {
        readonly string _pawnName;
        float _phase;
        int _score;
        bool _paused = true;
        public override string Title => "Timing: " + _pawnName;
        public override string Instructions => "Press Stop when the marker is in the green area.";
        public override int Score => _score;
        public override int PointsPerReward => 1;
        public override Vector2 WindowSize => new Vector2(440, 340);

        public TimingGame(string pawnName) { _pawnName = pawnName; Restart(); }

        public override void Restart() { _phase = 0; _score = 0; _paused = true; }

        public override void Update(bool paused)
        {
            _paused = paused;
            if (!paused) _phase = Mathf.Repeat(_phase + Mathf.Min(Time.unscaledDeltaTime, .05f) * .7f, 1);
        }

        public override void Draw(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 60), Instructions);
            var track = new Rect(rect.x + 12, rect.y + 80, rect.width - 24, 24);
            Widgets.DrawBoxSolid(track, new Color(.2f, .2f, .2f));
            Widgets.DrawBoxSolid(new Rect(track.x + track.width * .4f, track.y, track.width * .2f, track.height), Color.green);
            Widgets.DrawBoxSolid(new Rect(track.x + track.width * _phase - 2, track.y - 4, 4, track.height + 8), Color.white);
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && !_paused;
            if (Widgets.ButtonText(new Rect(rect.center.x - 60, track.yMax + 24, 120, 32), "Stop"))
            {
                if (_phase >= .4f && _phase <= .6f) _score++;
                _phase = 0;
            }
            GUI.enabled = enabled;
        }
    }
}
