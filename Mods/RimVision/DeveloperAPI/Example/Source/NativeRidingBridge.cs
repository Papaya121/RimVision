using System;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace RimVision.ApiExample
{
    public static class NativeRidingBridge
    {
        public static IDisposable Register(string id, Func<Pawn, Pawn> mount,
            Func<RimVisionControlContext, AcceptanceReport> validate,
            Action<RimVisionControlContext, RimVisionMovementInput> steer,
            Action<RimVisionControlContext> cancel, float seatHeight)
        {
            if (mount == null) throw new ArgumentNullException(nameof(mount));
            if (validate == null) throw new ArgumentNullException(nameof(validate));
            if (steer == null) throw new ArgumentNullException(nameof(steer));
            if (cancel == null) throw new ArgumentNullException(nameof(cancel));
            var control = RimVisionControl.RegisterProvider(id, new RidingControl(mount, validate, steer, cancel));
            try { return new Registration(control, RimVisionPresentation.RegisterProvider(id, new RidingPose(mount, seatHeight))); }
            catch { control.Dispose(); throw; }
        }

        sealed class RidingControl : RimVisionControlProvider
        {
            readonly Func<Pawn, Pawn> _mount;
            readonly Func<RimVisionControlContext, AcceptanceReport> _validate;
            readonly Action<RimVisionControlContext, RimVisionMovementInput> _steer;
            readonly Action<RimVisionControlContext> _cancel;
            internal RidingControl(Func<Pawn, Pawn> mount, Func<RimVisionControlContext, AcceptanceReport> validate,
                Action<RimVisionControlContext, RimVisionMovementInput> steer, Action<RimVisionControlContext> cancel)
            { _mount = mount; _validate = validate; _steer = steer; _cancel = cancel; }
            public override bool Handles(Pawn actor) => _mount(actor) != null;
            public override Thing Carrier(Pawn actor) => _mount(actor);
            public override AcceptanceReport CanControl(RimVisionControlContext context) => _validate(context);
            public override void Move(RimVisionControlContext context, RimVisionMovementInput input) => _steer(context, input);
            public override void Cancel(RimVisionControlContext context) => _cancel(context);
        }

        sealed class RidingPose : RimVisionPresentationProvider
        {
            readonly Func<Pawn, Pawn> _mount;
            readonly float _seatHeight;
            internal RidingPose(Func<Pawn, Pawn> mount, float seatHeight) { _mount = mount; _seatHeight = seatHeight; }
            public override bool Handles(Pawn pawn) => _mount(pawn) != null;
            public override void PawnPose(Pawn pawn, ref RimVisionPose pose)
            {
                var position = _mount(pawn).DrawPos;
                pose.Position = new Vector3(position.x, pose.Position.y + _seatHeight, position.z);
            }
            public override void CameraPose(Pawn actor, ref RimVisionPose pose) => pose.Position += Vector3.up * _seatHeight;
        }

        sealed class Registration : IDisposable
        {
            readonly IDisposable _control, _presentation;
            internal Registration(IDisposable control, IDisposable presentation) { _control = control; _presentation = presentation; }
            public void Dispose() { _presentation.Dispose(); _control.Dispose(); }
        }
    }
}
