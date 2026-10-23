using System.Collections.Generic;
using Recovery.Rim3D;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace RimVision.ApiExample
{
    public sealed class TwoFloorPresentation : RimVisionSpatialProvider
    {
        readonly Map _lower;
        readonly Map _upper;
        readonly CellRect _opening;
        readonly float _height;

        public TwoFloorPresentation(Map lower, Map upper, CellRect opening, float height = 2.12f)
        { _lower = lower; _upper = upper; _opening = opening; _height = height; }

        public override bool Handles(Map map) => map == _lower || map == _upper;
        public override float Elevation(Map map) => map == _upper ? _height : 0;
        public override void CollectLayers(Map activeMap, List<Map> maps)
        { maps.Add(activeMap == _lower ? _upper : _lower); }
        public override bool DrawFloor(Map map, IntVec3 cell) => map != _upper || !_opening.Contains(cell);
        public override bool DrawRoof(Map map, IntVec3 cell) => map != _lower || !_opening.Contains(cell);
        public override bool CanStand(Pawn pawn, Vector3 position) => pawn.Map != _upper ||
            position.x <= _opening.minX - PawnControl.CollisionRadius || position.x >= _opening.maxX + 1 + PawnControl.CollisionRadius ||
            position.z <= _opening.minZ - PawnControl.CollisionRadius || position.z >= _opening.maxZ + 1 + PawnControl.CollisionRadius;
    }
}
