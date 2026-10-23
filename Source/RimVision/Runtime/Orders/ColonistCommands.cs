using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using UnityEngine;
using Recovery.Rim3D.API;

namespace Recovery.Rim3D
{
    internal sealed class ColonistCommandController
    {
        readonly PawnControl _control;
        readonly Action<string> _notice;
        readonly Action _releaseMouse;
        Pawn _pawn;

        internal bool Active => _pawn != null;
        internal string Label => _pawn?.LabelShortCap ?? "";
        internal static bool Available(Pawn pawn) => pawn?.Spawned == true && pawn.IsColonistPlayerControlled &&
            !pawn.Dead && !pawn.Downed && pawn.MentalState == null && PawnControl.ForPawn(pawn) == null &&
            (!Rim3DMultiplayer.Active || Rim3DMultiplayer.GetController?.Invoke(pawn)?.Active != true);

        internal ColonistCommandController(PawnControl control, Action<string> notice, Action releaseMouse)
        { _control = control; _notice = notice; _releaseMouse = releaseMouse; }

        internal void Select(Pawn pawn)
        {
            if (!Available(pawn) || pawn == _control.Pawn) return;
            _pawn = pawn;
            _notice("Rim3D_OrdersColonistControls".TranslateBindings(pawn.LabelShortCap));
        }

        internal void Close() => _pawn = null;

        internal void Update(bool hasTarget, RimVisionSpatialHit hit)
        {
            if (!Available(_pawn) || _pawn == _control.Pawn) { Close(); return; }
            if (!hasTarget || hit.Map != _pawn.Map || !ViewBindings.MouseUp(0)) return;
            var pawn = _pawn;
            var map = hit.Map;
            var target = hit.Thing;
            var cell = hit.Cell;
            List<FloatMenuOption> options;
            var previousTarget = FPVFloatMenuTargetPatch.Target;
            var previousPawn = FloatMenuMakerMap.makingFor;
            var previousProvider = FloatMenuMakerMap.currentProvider;
            Rand.PushState();
            try
            {
                using (new OrderContext(pawn.Map, null))
                {
                    FPVFloatMenuTargetPatch.Target = target;
                    options = FloatMenuMakerMap.GetOptions(new List<Pawn> { pawn }, cell.ToVector3Shifted(), out _);
                }
            }
            finally
            {
                FPVFloatMenuTargetPatch.Target = previousTarget;
                FloatMenuMakerMap.makingFor = previousPawn;
                FloatMenuMakerMap.currentProvider = previousProvider;
                Rand.PopState();
            }
            foreach (var option in options)
            {
                var callback = option.action;
                if (callback == null) continue;
                option.action = () =>
                {
                    if (!Available(pawn) || pawn.Map != map || pawn == _control.Pawn || !_control.Active || !OrderExecutor.VisibleCell(map, cell) ||
                        target != null && !OrderExecutor.VisibleThing(pawn.Map, target)) return;
                    Close();
                    using (new OrderContext(pawn.Map, null)) callback();
                };
            }
            if (options.Count == 0) { _notice("Rim3D_OrdersNoTargets".Translate(0)); return; }
            _releaseMouse();
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
