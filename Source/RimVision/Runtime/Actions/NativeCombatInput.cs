using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed class RimVisionCombatInputCommand
    {
        public RimVisionAttackKind Kind;
        public RimVisionInputPhase Phase;
        public Thing Target;
        public IntVec3 Cell;
        public Vector3 Origin, Direction;
        public int Mode;
    }

    public sealed partial class MapView
    {
        public bool RequestCombatInput(RimVisionCombatInputCommand command) => _combatActions.RequestNativeInput(command);
        public void ExecuteCombatInput(RimVisionCombatInputCommand command) => _combatActions.ExecuteNativeInput(command);
        public void CancelNativeCombatInput() => _combatActions.CloseNativeInput();
        internal void TickNativeCombatInput() => _combatActions.TickNativeAttack();

        sealed partial class CombatActions
        {
            readonly Queue<(Pawn Pawn, RimVisionCombatInputCommand Command)> _nativeInputRequests = new Queue<(Pawn, RimVisionCombatInputCommand)>();
            ApiRegistry<RimVisionCombatProvider>.Entry _nativeAttackProvider;
            RimVisionCombatContext _nativeAttackContext;
            Vector3 _nativeAttackOffset;
            int _nativeAttackUntil;
            bool _nativeInputHeld;
            Pawn _nativeInputPawn;
            Thing _nativeInputWeapon;
            float _nextNativeInput;

            internal bool RequestNativeInput(RimVisionCombatInputCommand command)
            {
                if (command == null || _view._apiDisposed || _view.simulationOnly || _view._visualLayer || !Enum.IsDefined(typeof(RimVisionInputPhase), command.Phase) ||
                    !Enum.IsDefined(typeof(RimVisionAttackKind), command.Kind) || Rim3DMultiplayer.Simulating || !_view.FirstPerson) return false;
                bool ending = command.Phase == RimVisionInputPhase.Released || command.Phase == RimVisionInputPhase.Cancelled;
                if (!ending && (!_view.controller.Active || _view.ApiInputBlocked(RimVisionInputChannels.Combat) ||
                    !RimVisionCombat.UsesInputPhases(_view.ControlledPawn, command.Kind))) return false;
                var copy = new RimVisionCombatInputCommand { Kind = command.Kind, Phase = command.Phase, Target = command.Target,
                    Cell = command.Cell, Origin = command.Origin, Direction = command.Direction, Mode = command.Mode };
                if (Rim3DMultiplayer.Active)
                {
                    if (Rim3DMultiplayer.SendCombatInput == null) return false;
                    Rim3DMultiplayer.SendCombatInput(_view.controller, copy);
                }
                else
                {
                    if (_nativeInputRequests.Count >= 256) return false;
                    _nativeInputRequests.Enqueue((_view.ControlledPawn, copy));
                }
                return true;
            }

            internal void FlushNativeInput()
            {
                int count = _nativeInputRequests.Count;
                while (count-- > 0 && _nativeInputRequests.Count > 0)
                {
                    var request = _nativeInputRequests.Dequeue();
                    if (!Rim3DMultiplayer.Active && request.Pawn == _view.ControlledPawn) ExecuteNativeInput(request.Command);
                }
            }

            internal void UpdateNativeInput()
            {
                var pawn = _view.ControlledPawn;
                var weapon = pawn?.equipment?.Primary;
                if (_nativeInputPawn != pawn || _nativeInputWeapon != weapon)
                { ResetNativeInput(); _nativeInputPawn = pawn; _nativeInputWeapon = weapon; }
                if (!_nativeInputHeld && !ViewBindings.MouseDown(0)) return;
                _inputBurstRemaining = 0; _firePressUntil = -1;
                if (_sentFireHeld) _view.SendManualFireControl(false, 0);
                bool held = ViewBindings.MouseHeld(0) && !Find.TickManager.Paused;
                if (held != _nativeInputHeld || held && Time.unscaledTime >= _nextNativeInput)
                {
                    var phase = held ? _nativeInputHeld ? RimVisionInputPhase.Held : RimVisionInputPhase.Pressed :
                        Find.TickManager.Paused ? RimVisionInputPhase.Cancelled : RimVisionInputPhase.Released;
                    _view.PickFPVTarget();
                    if (SendNativeInput(phase)) _nativeInputHeld = held;
                    _nextNativeInput = Time.unscaledTime + .1f;
                }
            }

            bool SendNativeInput(RimVisionInputPhase phase) => RequestNativeInput(new RimVisionCombatInputCommand
            {
                Kind = RimVisionAttackKind.Ranged, Phase = phase, Mode = 0,
                Target = _view.aimedThing, Cell = _view.aimedCell,
                Origin = _view._viewCamera.AimRay.origin, Direction = _view._viewCamera.AimRay.direction
            });

            internal void ResetNativeInput()
            {
                if (_nativeInputHeld) SendNativeInput(RimVisionInputPhase.Cancelled);
                _nativeInputHeld = false; _nextNativeInput = 0;
            }

            internal void ExecuteNativeInput(RimVisionCombatInputCommand command)
            {
                if (command == null || Rim3DMultiplayer.LocalInput || !Enum.IsDefined(typeof(RimVisionInputPhase), command.Phase) ||
                    !Enum.IsDefined(typeof(RimVisionAttackKind), command.Kind)) return;
                if (command.Phase == RimVisionInputPhase.Cancelled || command.Phase == RimVisionInputPhase.Released)
                {
                    if (_nativeAttackProvider != null && _nativeAttackContext.Kind == command.Kind) EndNativeAttack(command.Phase);
                    return;
                }
                var pawn = _view.ControlledPawn;
                if (!_view.controller.Active || pawn?.Drafted != true || Find.TickManager.Paused ||
                    !PawnControl.CanUseCombatSkill(pawn, command.Kind == RimVisionAttackKind.Ranged ? RimWorld.SkillDefOf.Shooting : RimWorld.SkillDefOf.Melee) ||
                    !RimVisionControl.Finite(command.Origin) || !RimVisionControl.Finite(command.Direction) || command.Direction.sqrMagnitude < .0001f) return;
                var provider = RimVisionCombat.InputProvider(pawn, command.Kind);
                var context = new RimVisionCombatContext(pawn, command.Kind, command.Target, command.Cell,
                    command.Origin, command.Direction.normalized, command.Mode, command.Phase == RimVisionInputPhase.Pressed);
                if (command.Kind == RimVisionAttackKind.Melee && (command.Target == null || command.Target == pawn ||
                    command.Target is Pawn deadPawn && deadPawn.Dead || !pawn.Drafted && !command.Target.HostileTo(pawn))) return;
                if (provider == null || !RimVisionCombat.ValidateInput(provider, context, command.Phase)) return;
                if (command.Phase == RimVisionInputPhase.Pressed)
                {
                    CancelNativeAttack();
                    if (!RimVisionCombat.ExecuteInput(provider, context, command.Phase))
                    { RimVisionCombat.ExecuteInput(provider, context, RimVisionInputPhase.Cancelled); return; }
                    _nativeAttackProvider = provider;
                }
                if (_nativeAttackProvider != provider || _nativeAttackContext.Actor != null && _nativeAttackContext.Actor != pawn) return;
                _nativeAttackContext = context;
                _nativeAttackOffset = command.Origin - _view.controller.Position;
                _nativeAttackUntil = Find.TickManager.TicksGame + 120;
            }

            internal void TickNativeAttack()
            {
                var provider = _nativeAttackProvider;
                if (provider == null || Rim3DMultiplayer.LocalInput) return;
                var old = _nativeAttackContext;
                if (!provider.Active || !_view.controller.Active || old.Actor != _view.ControlledPawn ||
                    !old.Actor.Drafted || RimVisionControl.MapFor(old.Actor) != _view.map || Find.TickManager.TicksGame >= _nativeAttackUntil ||
                    RimVisionCombat.InputProvider(old.Actor, old.Kind) != provider)
                { CancelNativeAttack(); return; }
                var context = new RimVisionCombatContext(old.Actor, old.Kind, old.Target, old.Cell,
                    _view.controller.Position + _nativeAttackOffset, old.Direction, old.Mode, false);
                if (!RimVisionCombat.ValidateInput(provider, context, RimVisionInputPhase.Held) ||
                    !RimVisionCombat.ExecuteInput(provider, context, RimVisionInputPhase.Held)) CancelNativeAttack();
                else _nativeAttackContext = context;
            }

            internal void CloseNativeInput()
            {
                ResetNativeInput();
                _nativeInputRequests.Clear();
                _nativeInputPawn = null; _nativeInputWeapon = null;
                CancelNativeAttack();
            }

            internal void CancelNativeAttack() => EndNativeAttack(RimVisionInputPhase.Cancelled);
            void EndNativeAttack(RimVisionInputPhase phase)
            {
                if (Rim3DMultiplayer.LocalInput) return;
                var provider = _nativeAttackProvider;
                var context = _nativeAttackContext;
                _nativeAttackProvider = null;
                _nativeAttackContext = default;
                if (provider != null) RimVisionCombat.ExecuteInput(provider, context, phase);
            }
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
    static class NativeCombatInputTickPatch
    {
        static void Postfix(Map __instance)
        {
            if (Rim3DMultiplayer.Active) return;
            var view = Rim3DPreview.Instance?.ActiveWindow?.View;
            if (view?.SpatialMap == __instance) view.TickNativeCombatInput();
        }
    }
}
