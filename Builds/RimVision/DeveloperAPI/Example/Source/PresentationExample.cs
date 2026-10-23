using System;
using System.Collections.Generic;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace RimVision.ApiExample
{
    public static class PresentationExample
    {
        public static IDisposable Register()
        {
            var installation = new Installation();
            try
            {
                var state = new LocalState();
                installation.Add(RimVisionCamera.RegisterProvider("example.rimvision:third-person", new ShoulderCamera(state)));
                installation.Add(RimVisionHud.RegisterProvider("example.rimvision:crosshair", new Crosshair(state)));
                installation.Add(RimVisionInput.RegisterBinding("example.rimvision:camera-toggle", "Third-person camera", KeyCode.K, KeyCode.None,
                    RimVisionInputChannels.Shortcuts, view => view.FirstPerson && view.CanControl,
                    (view, phase) =>
                    {
                        if (phase == RimVisionInputPhase.Pressed) state.Enabled = !state.Enabled;
                        return true;
                    }));
                return installation;
            }
            catch { installation.Dispose(); throw; }
        }

        sealed class LocalState { internal bool Enabled = true; }

        sealed class ShoulderCamera : RimVisionCameraProvider
        {
            readonly LocalState _state;
            internal ShoulderCamera(LocalState state) { _state = state; }
            public override bool Handles(RimVisionCameraContext context) => _state.Enabled &&
                context.Pawn != null && context.Mode == RimVisionCameraMode.Pawn;
            public override void Evaluate(RimVisionCameraContext context, ref RimVisionCameraFrame frame)
            {
                RimVisionCamera.ThirdPerson(context, ref frame, new Vector3(.35f, 0, 0), Mathf.Clamp(context.Distance, 1, 6));
                frame.FieldOfView = 65;
                frame.PitchLimits = new Vector2(-65, 75);
            }
        }

        sealed class Crosshair : RimVisionHudProvider
        {
            readonly LocalState _state;
            internal Crosshair(LocalState state) { _state = state; }
            public override bool Handles(RimVisionHudContext context) => _state.Enabled &&
                context.Element == RimVisionHudElement.Crosshair && context.State.BodyVisible;
            public override void Layout(RimVisionHudContext context, ref RimVisionHudLayout layout)
            {
                layout.DrawDefault = false;
                layout.Bounds = new Rect(layout.Viewport.center.x - 6, layout.Viewport.center.y - 6, 12, 12);
            }
            public override void Draw(RimVisionHudContext context, RimVisionHudLayout layout, RimVisionHudPhase phase)
            {
                if (phase != RimVisionHudPhase.After || Event.current.type != EventType.Repaint) return;
                var center = layout.Viewport.center;
                Widgets.DrawBoxSolid(new Rect(center.x - 2, center.y - 2, 4, 4), Color.cyan);
            }
        }

        public sealed class HandOffset : RimVisionPresentationProvider
        {
            public override RimVisionPresentationChannels Channels => RimVisionPresentationChannels.WeaponAnimation;
            public override bool Handles(Pawn pawn) => pawn?.RaceProps.Humanlike == true;
            public override void WeaponAnimation(RimVisionAnimationContext context, ref RimVisionWeaponAnimationFrame frame)
            {
                if (!context.FirstPerson || context.Carried || context.Item == null) return;
                var hand = frame.LeftHand;
                hand.Position += new Vector3(.02f, 0, 0);
                frame.LeftHand = hand;
            }
        }

        sealed class Installation : IDisposable
        {
            readonly List<IDisposable> _registrations = new List<IDisposable>();
            internal void Add(IDisposable registration) => _registrations.Add(registration);
            public void Dispose()
            {
                for (int i = _registrations.Count - 1; i >= 0; i--) _registrations[i].Dispose();
                _registrations.Clear();
            }
        }
    }
}
