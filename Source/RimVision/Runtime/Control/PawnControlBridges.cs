using System;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public sealed partial class PawnControl
    {
        API.RimVisionControl.Binding _nativeControl;
        bool _nativeControlFailed;
        bool _restoreNativeControl;
        float _nativeVertical, _nativePitch, _nativeLookYaw;
        public void SetNativeAxes(float vertical, float pitch, float lookYaw)
        {
            if (Rim3DMultiplayer.LocalInput || !API.RimVisionControl.Finite(vertical) || !API.RimVisionControl.Finite(pitch) || !API.RimVisionControl.Finite(lookYaw)) return;
            _nativeVertical = Mathf.Clamp(vertical, -1, 1); _nativePitch = Mathf.Clamp(pitch, -89.9f, 89.9f); _nativeLookYaw = Mathf.Repeat(lookYaw, 360);
        }
        public bool HasNativeControl => API.RimVisionControl.Resolve(Pawn).Selected;

        bool NativeInputBlocked
        {
            get
            {
                var binding = API.RimVisionControl.Resolve(Pawn);
                return binding.Selected && (!binding.Validate().Accepted ||
                    _nativeControlFailed && _nativeControl.Same(binding));
            }
        }

        void RefreshNativeControl(API.RimVisionControlChange reason = API.RimVisionControlChange.Changed)
        {
            if (presentationOnly || Rim3DMultiplayer.LocalInput) return;
            bool restoring = _restoreNativeControl;
            _restoreNativeControl = false;
            var next = API.RimVisionControl.Resolve(Pawn);
            if (!Eligible(Pawn) || !next.Context.Valid) next = default;
            if (_nativeControl.Same(next)) return;
            DetachNativeControl(next.Selected ? API.RimVisionControlChange.Changed : API.RimVisionControlChange.Unavailable);
            _nativeControl = next;
            if (!next.Selected) return;
            manualDestination = LocalTargetInfo.Invalid;
            Pawn.jobs.jobQueue.RemoveAll(Pawn, job => job.loadID == controlJobId);
            if (controlJobId >= 0 && Pawn.drafter != null) Pawn.drafter.FireAtWill = initiallyFireAtWill;
            controlJob = null;
            controlJobId = -1;
            followingAction = false;
            actionJob = null;
            actionJobId = -1;
            try
            {
                next.Provider.Attached(next.Context, restoring ? API.RimVisionControlChange.Restored : reason);
                Position = next.Position();
            }
            catch (Exception error) { _nativeControlFailed = true; next.Report(error); }
        }

        void DetachNativeControl(API.RimVisionControlChange reason)
        {
            var old = _nativeControl;
            _nativeControl = default;
            _nativeControlFailed = false;
            _nativeVertical = _nativePitch = _nativeLookYaw = 0;
            if (!old.Selected) return;
            try { old.Provider.Detached(old.Context, reason); }
            catch (Exception error) { old.Report(error); }
        }

        void MoveNativeControl(Vector2 input, float yaw, float deltaSeconds)
        {
            var binding = API.RimVisionControl.Resolve(Pawn);
            if (!binding.Selected || !_nativeControl.Same(binding) || _nativeControlFailed || !binding.Validate().Accepted) return;
            if (!Find.TickManager.Paused && deltaSeconds > 0 &&
                API.RimVisionControl.Finite(input.x) && API.RimVisionControl.Finite(input.y) && API.RimVisionControl.Finite(yaw))
            {
                try { binding.Provider.Move(binding.Context, new API.RimVisionMovementInput(input, yaw, deltaSeconds, Sneaking, Sprinting,
                    binding.Provider.SupportsVerticalMovement ? _nativeVertical : 0, _nativePitch, _nativeLookYaw)); }
                catch (Exception error) { _nativeControlFailed = true; binding.Report(error); }
            }
            if (binding.Context.Valid) Position = binding.Position();
            Status = "";
        }

        bool NativeCommand(Job job, bool? combat = null)
        {
            var binding = API.RimVisionControl.Resolve(Pawn);
            bool accepted = false;
            bool executionFailed = false;
            if (binding.Selected && Active && _nativeControl.Same(binding) && !_nativeControlFailed)
            {
                try
                {
                    if (job != null) accepted = binding.Provider.OrderJob(binding.Context, job);
                    else if (combat.HasValue) accepted = binding.Provider.SetCombat(binding.Context, combat.Value);
                    else { binding.Provider.Cancel(binding.Context); accepted = true; }
                }
                catch (Exception error) { executionFailed = _nativeControlFailed = true; binding.Report(error); }
            }
            if (job != null && !accepted && !executionFailed) JobMaker.ReturnToPool(job);
            Status = accepted ? "" : "Rim3D_ControlInterrupted".Translate().ToString();
            return accepted;
        }

        void RestoreNativeQueue(Pawn pawn)
        {
            if (suspendedJobs == null || pawn?.jobs == null) return;
            QueuedJob queued;
            while ((queued = suspendedJobs.Dequeue()) != null)
                pawn.jobs.jobQueue.EnqueueLast(queued.job, queued.tag);
        }
    }
}
