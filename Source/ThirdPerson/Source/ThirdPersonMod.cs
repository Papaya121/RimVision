using System;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace RimVision.ThirdPerson
{
    public sealed class ThirdPersonMod : Mod
    {
        IDisposable _cameraRegistration;
        IDisposable _bindingRegistration;
        bool _enabled = true;

        public ThirdPersonMod(ModContentPack content) : base(content)
        {
            LongEventHandler.ExecuteWhenFinished(Register);
        }

        void Register()
        {
            if (_cameraRegistration != null) return;
            if (RimVisionApi.Version < new Version(1, 5, 0))
            {
                Log.Error("[RimVision Third Person] RimVision API 1.5 or newer is required.");
                return;
            }
            try
            {
                _cameraRegistration = RimVisionCamera.RegisterProvider("papaya.rimvision.thirdperson:camera", new ShoulderCamera(this));
                _bindingRegistration = RimVisionInput.RegisterBinding("papaya.rimvision.thirdperson:toggle",
                    "RimVisionThirdPerson_Toggle".Translate().ToString(), KeyCode.K, KeyCode.None,
                    RimVisionInputChannels.Shortcuts,
                    state => state.FirstPerson && state.CanControl && !state.CameraTransition && !state.Falling && !state.MortarAiming,
                    (state, phase) =>
                    {
                        if (phase == RimVisionInputPhase.Pressed) _enabled = !_enabled;
                        return true;
                    });
            }
            catch
            {
                _bindingRegistration?.Dispose();
                _cameraRegistration?.Dispose();
                _bindingRegistration = _cameraRegistration = null;
                throw;
            }
        }

        sealed class ShoulderCamera : RimVisionCameraProvider
        {
            readonly ThirdPersonMod _mod;
            internal ShoulderCamera(ThirdPersonMod mod) { _mod = mod; }
            public override bool Handles(RimVisionCameraContext context) => _mod._enabled &&
                context.Pawn != null && context.Mode == RimVisionCameraMode.Pawn;

            public override void Evaluate(RimVisionCameraContext context, ref RimVisionCameraFrame frame)
            {
                RimVisionCamera.ThirdPerson(context, ref frame, new Vector3(.35f, .1f, 0), Mathf.Clamp(context.Distance, 1, 6));
                frame.ZoomLimits = new Vector2(1, 6);
                frame.FieldOfView = Mathf.Clamp(frame.FieldOfView, 40, 75);
                frame.PitchLimits = new Vector2(-65, 75);
            }
        }
    }
}
