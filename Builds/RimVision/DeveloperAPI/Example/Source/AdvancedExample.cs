using System;
using System.Collections.Generic;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace RimVision.ApiExample
{
    public static class AdvancedExample
    {
        public static IDisposable Register(Func<RimVisionViewState, float> forwardAxis = null, string modelDefName = null)
        {
            var installation = new Installation();
            try
            {
                installation.Add(RimVisionApi.RegisterExtension("example.rimvision:advanced-events", () => new Observer()));
                installation.Add(RimVisionInteractions.RegisterProvider("example.rimvision:hold-repair", new RepairInteraction()));
                installation.Add(RimVisionInput.RegisterGesture("example.rimvision:tracked-repair", "Repair and track job",
                    new RimVisionInputGesture(KeyCode.R, RimVisionInputModifiers.Control), default,
                    RimVisionInputChannels.Interaction, RimVisionInputContext.Pawn, state => state.CanControl,
                    (state, phase) =>
                    {
                        if (phase == RimVisionInputPhase.Pressed)
                            RimVisionApi.SubmitAction("example.rimvision:repair", state.AimedThing, state.AimedCell);
                        return true;
                    }, 10));
                if (forwardAxis != null)
                    installation.Add(RimVisionInput.RegisterAxis("example.rimvision:forward-axis", "Forward axis", RimVisionAxisSource.Custom,
                        RimVisionInputChannels.Movement, RimVisionInputContext.Pawn, state => state.CanControl,
                        (state, axis) => RimVisionInput.SetControlAxis(RimVisionControlAxis.MoveZ, axis.Value), read: forwardAxis));
                if (!string.IsNullOrEmpty(modelDefName))
                    installation.Add(RimVisionModels.RegisterProvider("example.rimvision:cube-model", new CubeModel(modelDefName)));
                return installation;
            }
            catch { installation.Dispose(); throw; }
        }

        sealed class RepairInteraction : RimVisionInteractionProvider
        {
            public override bool Evaluate(RimVisionViewState state, ref RimVisionInteraction interaction)
            {
                if (!(state.AimedThing is Building building) || !building.def.useHitPoints || building.HitPoints >= building.MaxHitPoints) return false;
                interaction.ActionId = "example.rimvision:repair";
                interaction.Label = "Repair (hold)";
                interaction.Description = "Uses the repair action registered by ExampleMod and tracks its native Job.";
                interaction.HoldSeconds = .6f;
                return true;
            }
        }

        sealed class Observer : RimVisionExtension
        {
            public override void JobChanged(RimVisionView view, RimVisionJobEvent notification)
            { Log.Message("API job " + notification.RequestId + ": " + notification.Status + " / " + notification.Condition); }
            public override void Lifecycle(RimVisionView view, RimVisionLifecycleEvent notification)
            { Log.Message("API view: " + notification.Kind + " / " + notification.Reason); }
            public override void AnimationEvent(RimVisionView view, RimVisionAnimationEvent notification)
            {
                if (notification.Kind != RimVisionAnimationEventKind.Damage || notification.Pawn?.Spawned != true) return;
                var effect = view.CreateEffect(notification.Pawn.Map,
                    new RimVisionPose(notification.Pawn.DrawPos + Vector3.up, Quaternion.identity), .2f,
                    (root, state) => root.GetComponent<Light>().intensity = 2 * (1 - state.Progress));
                var light = effect.Root.AddComponent<Light>();
                light.type = LightType.Point; light.color = new Color(1, .2f, .1f); light.range = 3; light.intensity = 2;
            }
        }

        sealed class CubeModel : RimVisionModelProvider
        {
            readonly string _defName;
            internal CubeModel(string defName) { _defName = defName; }
            public override bool Handles(Thing thing) => thing.def.defName == _defName;
            public override void Create(RimVisionModelContext context)
            {
                var mesh = context.OwnResource(new Mesh());
                mesh.vertices = new[]
                {
                    new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                    new Vector3(-.5f,-.5f,.5f), new Vector3(.5f,-.5f,.5f), new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f)
                };
                mesh.triangles = new[] {0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5};
                mesh.RecalculateNormals(); mesh.RecalculateBounds();
                context.Root.AddComponent<MeshFilter>().sharedMesh = mesh;
                context.Root.AddComponent<MeshRenderer>().sharedMaterial = context.CreateSurfaceMaterial(Texture2D.whiteTexture, Color.cyan);
            }
            public override void Update(RimVisionModelContext context, ref RimVisionModelFrame frame)
            {
                var pose = frame.Pose; pose.Position += Vector3.up * .5f; frame.Pose = pose;
                frame.PickingBounds = new Bounds(Vector3.zero, Vector3.one);
            }
        }

        sealed class Installation : IDisposable
        {
            readonly List<IDisposable> _handles = new List<IDisposable>();
            internal void Add(IDisposable handle) => _handles.Add(handle);
            public void Dispose()
            {
                for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
                _handles.Clear();
            }
        }
    }
}
