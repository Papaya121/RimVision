using System;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    [Flags]
    public enum RimVisionPresentationChannels
    {
        None = 0, Visibility = 1, PawnPose = 2, CameraPose = 4, PawnAnimation = 8, WeaponAnimation = 16,
        Legacy = Visibility | PawnPose | CameraPose, All = Legacy | PawnAnimation | WeaponAnimation
    }

    public readonly struct RimVisionAnimationContext
    {
        public Pawn Pawn { get; }
        public Thing Item { get; }
        public Map Map { get; }
        public Pawn Observer { get; }
        public bool FirstPerson { get; }
        public bool Carried { get; }
        public float AimBlend { get; }
        public float Recoil { get; }
        public int MeleeStartedTick { get; }
        public bool Paused => Rim3DMultiplayer.Active ? (Rim3DMultiplayer.MinigameTickRate?.Invoke(Map) ?? 0) <= 0 : Find.TickManager.Paused;
        public int Tick => Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
            ? Rim3DMultiplayer.MapTicks(Map) : Find.TickManager.TicksGame;
        public float TickFraction => RimVisionPresentation.TickFraction(Map);
        public float DeltaSeconds => Paused ? 0 : Mathf.Min(Time.unscaledDeltaTime, .1f);
        PawnControl Control
        {
            get
            {
                var view = Rim3DPreview.Instance?.ActiveWindow?.View;
                return view?.ControlledPawn == Pawn ? view.Controller : Rim3DMultiplayer.GetController?.Invoke(Pawn);
            }
        }
        public bool Moving => Pawn?.pather?.Moving == true || Control?.Moving == true;
        public bool Sprinting => Control?.Sprinting == true;
        public bool Sneaking => Control?.Sneaking == true;
        internal RimVisionAnimationContext(Pawn pawn, Thing item, Map map, Pawn observer, bool firstPerson, bool carried,
            float aim, float recoil, int meleeTick)
        { Pawn = pawn; Item = item; Map = map; Observer = observer; FirstPerson = firstPerson; Carried = carried;
            AimBlend = aim; Recoil = recoil; MeleeStartedTick = meleeTick; }
    }

    public struct RimVisionPawnAnimationFrame
    {
        public RimVisionPose Pose { get; set; }
        public Vector3 Scale { get; set; }
        public bool AnimateWalk { get; set; }
        internal bool Valid => Pose.Valid && ValidScale(Scale);
        internal static bool ValidScale(Vector3 scale) => RimVisionControl.Finite(scale) &&
            Mathf.Abs(scale.x) > .0001f && Mathf.Abs(scale.y) > .0001f && Mathf.Abs(scale.z) > .0001f;
    }

    public struct RimVisionWeaponAnimationFrame
    {
        public RimVisionPose Pose { get; set; }
        public Vector3 Scale { get; set; }
        public RimVisionPose RightHand { get; set; }
        public RimVisionPose LeftHand { get; set; }
        public Vector3 RightHandScale { get; set; }
        public Vector3 LeftHandScale { get; set; }
        public Vector3 Muzzle { get; set; }
        public bool DrawItem { get; set; }
        public bool DrawRightHand { get; set; }
        public bool DrawLeftHand { get; set; }
        public Mesh Mesh { get; set; }
        public Material Material { get; set; }
        internal bool Valid => Pose.Valid && RightHand.Valid && LeftHand.Valid && RimVisionPawnAnimationFrame.ValidScale(Scale) &&
            RimVisionPawnAnimationFrame.ValidScale(RightHandScale) && RimVisionPawnAnimationFrame.ValidScale(LeftHandScale) &&
            RimVisionControl.Finite(Muzzle);
    }
}
