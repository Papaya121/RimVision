using System;
using System.Collections.Generic;
using Recovery.Rim3D.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed partial class ViewTargeting
        {
            internal bool TryPickOrderTarget(out RimVisionSpatialHit hit) =>
                _view.TryPickSpatialView(new Ray(_view.CameraMapPosition, _view.camera.transform.forward), 80, out hit);

            readonly HashSet<IntVec3> _spatialCells = new HashSet<IntVec3>();
            readonly HashSet<Thing> _spatialThings = new HashSet<Thing>();

            bool TryRaycastThing(Thing thing, Ray ray, out float hit)
            {
                hit = 0;
                if (_view._objectPresentation.TryRaycastApiModel(thing, ray, out hit, out bool custom)) return true;
                if (custom) return false;
                if (thing is Pawn pawn)
                {
                    var distance = _view.RaycastPawnSprite(pawn, ray, false);
                    if (!distance.HasValue) return false;
                    hit = distance.Value;
                    return true;
                }
                var center = thing.TrueCenter();
                var size = thing.RotatedSize;
                float height = thing is Blueprint || thing is Frame ? Mathf.Max(.04f, _view.ConstructionHeight(thing.def.entityDefToBuild)) :
                    thing.def.category == ThingCategory.Plant ? Mathf.Clamp((thing.Graphic?.drawSize.y ?? 1) *
                        (thing.def.plant.IsTree ? 3.5f : .4f), .2f, 9) :
                    thing.def.category == ThingCategory.Item ?
                        (thing.def.graphicData?.texPath?.IndexOf("/Chunk/ChunkStone", StringComparison.OrdinalIgnoreCase) >= 0 ? .54f : .18f) :
                    IsGroundSpot(thing.def) || IsConduit(thing.def) ? .04f :
                    thing is Building_Bed bed ? _view.BedSurfaceHeight(bed) :
                    thing is Building_Door ? 1.7f : thing.def.Fillage == FillCategory.Full || thing.def.mineable ? 2 : .65f;
                if (thing is Building_Door door && door.Open) return false;
                center.y = height * .5f;
                if (thing.def.category == ThingCategory.Item) center.y += _view.GroundThingSurfaceHeight(center);
                var bounds = new Bounds(center, new Vector3(Mathf.Max(.3f, size.x), height, Mathf.Max(.3f, size.z)));
                if (IsWallTorch(thing.def.entityDefToBuild ?? thing.def)) bounds = WallTorchBounds(thing);
                return _view._objectPresentation.TryRaycastOptionalModel(thing, ray, out hit) || bounds.IntersectRay(ray, out hit);
            }

            internal bool TryPickSpatial(Ray ray, float maximumDistance, Pawn excludedPawn, out RimVisionSpatialHit hit, RimVisionPickFilter filter)
            {
                hit = default;
                float closest = maximumDistance;
                bool found = (filter.Mask & RimVisionPickMask.Floor) != 0 && TrySurface(ray, 0, false, ref closest, ref hit);
                found = (filter.Mask & RimVisionPickMask.Roof) != 0 && TrySurface(ray, _view.SpatialRoofHeight, true, ref closest, ref hit) || found;
                _spatialCells.Clear();
                _spatialThings.Clear();
                for (float distance = 0; distance <= closest; distance += .4f)
                {
                    var point = ray.GetPoint(distance);
                    var cell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.z));
                    for (int z = -1; z <= 1; z++)
                    for (int x = -1; x <= 1; x++)
                    {
                        var candidate = cell + new IntVec3(x, 0, z);
                        if (!candidate.InBounds(_view.map) || !_spatialCells.Add(candidate) || _view.map.fogGrid.IsFogged(candidate)) continue;
                        foreach (var thing in candidate.GetThingList(_view.map)) _spatialThings.Add(thing);
                    }
                }
                foreach (var thing in _spatialThings)
                {
                    if (!thing.Spawned || thing == excludedPawn || thing == filter.ExcludedThing || !_view.PickSpatialThing(thing) ||
                        !(thing is Pawn || thing is Blueprint || thing is Frame) &&
                        thing.def.category != ThingCategory.Building && thing.def.category != ThingCategory.Item &&
                        thing.def.category != ThingCategory.Plant) continue;
                    var mask = thing is Pawn ? RimVisionPickMask.Pawns : thing is Blueprint || thing is Frame ? RimVisionPickMask.Plans :
                        thing.def.category == ThingCategory.Building ? RimVisionPickMask.Buildings :
                        thing.def.category == ThingCategory.Item ? RimVisionPickMask.Items : RimVisionPickMask.Plants;
                    if ((filter.Mask & mask) == 0 || filter.SolidOnly && thing.def.Fillage != FillCategory.Full &&
                        !(thing.def.category == ThingCategory.Building && thing.def.fillPercent >= .25f) && thing.def.plant?.IsTree != true) continue;
                    if (!TryRaycastThing(thing, ray, out float distance) || distance < 0 || distance > closest) continue;
                    closest = distance;
                    hit = new RimVisionSpatialHit(_view.map, thing, thing.Position, ray.GetPoint(distance), distance);
                    found = true;
                }
                return found;
            }

            bool TrySurface(Ray ray, float height, bool roof, ref float closest, ref RimVisionSpatialHit hit)
            {
                if (!new Plane(Vector3.up, Vector3.up * height).Raycast(ray, out float distance) || distance > closest) return false;
                var point = ray.GetPoint(distance);
                var cell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.z));
                if (!cell.InBounds(_view.map) || (roof
                    ? !_view.RoofVisible(cell, _view.map.roofGrid.RoofAt(cell))
                    : !_view.DrawSpatialFloor(cell) || _view.map.terrainGrid.TerrainAt(cell) == null)) return false;
                closest = distance;
                hit = new RimVisionSpatialHit(_view.map, null, cell, point, distance);
                return true;
            }
        }
    }
}
