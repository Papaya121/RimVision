using System.Collections.Generic;
using Recovery.Rim3D.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class FPVManualBullet
    {
        readonly List<Map> _flightMaps = new List<Map>();

        bool TickSpatialFlight(int delta)
        {
            var provider = RimVisionSpatial.Resolve(Map);
            if (provider == null) return false;
            Map sourceMap = Map;
            float distance = Mathf.Min(remainingDistance, def.projectile.SpeedTilesPerTick * delta);
            var ray = new Ray(flightPosition, flightDirection);
            float closest = distance;
            Thing hit = null;
            Map impactMap = null;
            bool surfaceHit = false;
            RimVisionSpatial.CollectGroup(sourceMap, _flightMaps);
            foreach (Map layer in _flightMaps)
            {
                if (layer != sourceMap && !provider.CanShootAcross(sourceMap, layer)) continue;
                float offset = RimVisionSpatial.MapOffset(sourceMap, layer);
                if (!RimVisionSpatial.Finite(offset)) continue;
                var localRay = new Ray(ray.origin - Vector3.up * offset, ray.direction);
                bool floorHit = SpatialSurface(provider, layer, localRay, false, ref closest);
                bool roofHit = SpatialSurface(provider, layer, localRay, true, ref closest);
                if (floorHit || roofHit)
                {
                    hit = null;
                    impactMap = layer;
                    surfaceHit = true;
                }
                candidates.Clear();
                visitedCells.Clear();
                for (float step = 0; step <= closest + .35f; step += .35f)
                {
                    var point = localRay.GetPoint(Mathf.Min(step, closest));
                    var cell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.z));
                    for (int z = -1; z <= 1; z++)
                    for (int x = -1; x <= 1; x++)
                    {
                        var nearby = cell + new IntVec3(x, 0, z);
                        if (!nearby.InBounds(layer) || !visitedCells.Add(nearby)) continue;
                        foreach (Thing thing in nearby.GetThingList(layer)) candidates.Add(thing);
                    }
                }
                orderedCandidates.Clear();
                orderedCandidates.AddRange(candidates);
                orderedCandidates.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
                foreach (Thing thing in orderedCandidates)
                {
                    if (thing == launcher || !thing.Spawned || thing.Destroyed || thing is Projectile || thing is Blueprint || thing is Frame ||
                        !(thing is Pawn) && thing.def.category != ThingCategory.Building && thing.def.category != ThingCategory.Plant ||
                        MapView.IsGroundSpot(thing.def) || thing is Building_Door door && door.Open ||
                        !provider.BlocksProjectile(thing)) continue;
                    if (!SpatialThingHit(thing, layer == sourceMap, localRay, out float candidate) || candidate < 0 || candidate > closest) continue;
                    if (candidate == closest && hit != null && hit.thingIDNumber < thing.thingIDNumber) continue;
                    closest = candidate;
                    hit = thing;
                    impactMap = layer;
                    surfaceHit = false;
                }
            }
            Vector3 end = ray.GetPoint(closest);
            foreach (Map layer in _flightMaps)
            {
                if (layer != sourceMap && !provider.CanShootAcross(sourceMap, layer)) continue;
                float offset = RimVisionSpatial.MapOffset(sourceMap, layer);
                if (!RimVisionSpatial.Finite(offset)) continue;
                Vector3 from = flightPosition - Vector3.up * offset;
                Vector3 to = end - Vector3.up * offset;
                float roof = provider.RoofHeight(layer);
                if (Mathf.Max(from.y, to.y) < 0 || Mathf.Min(from.y, to.y) > roof) continue;
                foreach (Thing thing in layer.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor))
                {
                    if (thing.TryGetComp<CompProjectileInterceptor>()?.CheckIntercept(this, from, to) != true) continue;
                    MoveSpatialProjectile(layer, to, sourceMap);
                    Impact(null, true);
                    return true;
                }
            }
            flightPosition = end;
            remainingDistance -= closest;
            if (impactMap != null && (hit != null || surfaceHit))
            {
                float offset = RimVisionSpatial.MapOffset(sourceMap, impactMap);
                MoveSpatialProjectile(impactMap, end - Vector3.up * offset, sourceMap);
                Impact(hit);
                return true;
            }
            var position = new IntVec3(Mathf.FloorToInt(flightPosition.x), 0, Mathf.FloorToInt(flightPosition.z));
            if (!position.InBounds(sourceMap)) { Destroy(); return true; }
            Position = position;
            if (remainingDistance <= .001f) Impact(null);
            return true;
        }

        static bool SpatialSurface(RimVisionSpatialProvider provider, Map map, Ray ray, bool roof, ref float closest)
        {
            float height = roof ? provider.RoofHeight(map) : 0;
            if (!RimVisionSpatial.Finite(height) || Mathf.Abs(ray.direction.y) < .00001f) return false;
            float distance = (height - ray.origin.y) / ray.direction.y;
            if (distance < 0 || distance > closest) return false;
            Vector3 point = ray.GetPoint(distance);
            var cell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.z));
            if (!cell.InBounds(map) || roof && !map.roofGrid.Roofed(cell) ||
                !roof && map.terrainGrid.TerrainAt(cell) == null || !provider.BlocksProjectile(map, cell, roof)) return false;
            closest = distance;
            return true;
        }

        bool SpatialThingHit(Thing thing, bool localMap, Ray ray, out float distance)
        {
            distance = 0;
            if (!DeterministicFlight && localMap && thing is Pawn pawnSprite && PawnRayHit != null)
            {
                float? hit = PawnRayHit(pawnSprite, ray);
                if (hit.HasValue) { distance = hit.Value; return true; }
                return false;
            }
            if (!DeterministicFlight && localMap && thing is Building_Bed bedSprite && BedRayHit != null)
            {
                float? hit = BedRayHit(bedSprite, ray);
                if (hit.HasValue) { distance = hit.Value; return true; }
                return false;
            }
            Vector3 center = thing.TrueCenter();
            var size = thing.RotatedSize;
            float width = size.x, depth = size.z, height;
            if (thing is Pawn pawn)
            {
                center = Rim3DMultiplayer.GetPosition?.Invoke(pawn) ?? pawn.Position.ToVector3Shifted();
                bool prone = pawn.Downed || pawn.GetPosture() != PawnPosture.Standing;
                height = prone ? .35f : CollisionHeight(pawn);
                width = depth = .8f;
                center.y = RimVisionSpatial.Height(pawn, center) + height * .5f;
                if (prone)
                {
                    if (pawn.CurrentBed() is Building_Bed bed)
                    {
                        center = MapView.BedPawnPosition(pawn, bed, false);
                        center.y = (MapView.IsGroundSpot(bed.def) ? .025f : .65f) + .045f + height * .5f;
                        width = bed.Rotation.IsHorizontal ? bed.def.size.z : .8f;
                        depth = bed.Rotation.IsHorizontal ? .8f : bed.def.size.z;
                    }
                    else if (pawn.Rotation.IsHorizontal) width = 1.6f * MapView.PawnScaleForBullets;
                    else depth = 1.6f * MapView.PawnScaleForBullets;
                }
            }
            else
            {
                height = thing is Plant plant ? Mathf.Clamp((thing.def.graphicData?.drawSize.y ?? 1) *
                    (thing.def.plant.IsTree ? 3.5f : .4f) * Mathf.Lerp(.35f, 1, plant.Growth), .08f, 9) :
                    thing is Building_Door ? 1.7f : thing.def.Fillage == FillCategory.Full || thing.def.mineable ? 2 : .65f;
                if (thing is Plant) width = depth = .35f;
                center.y = height * .5f;
            }
            return new Bounds(center, new Vector3(width, height, depth)).IntersectRay(ray, out distance);
        }

        void MoveSpatialProjectile(Map destinationMap, Vector3 position, Map sourceMap)
        {
            if (destinationMap != sourceMap)
            {
                float offset = RimVisionSpatial.MapOffset(sourceMap, destinationMap);
                DeSpawn();
                origin -= Vector3.up * offset;
                destination -= Vector3.up * offset;
                GenSpawn.Spawn(this, new IntVec3(Mathf.Clamp(Mathf.FloorToInt(position.x), 0, destinationMap.Size.x - 1), 0,
                    Mathf.Clamp(Mathf.FloorToInt(position.z), 0, destinationMap.Size.z - 1)), destinationMap);
            }
            flightPosition = position;
            Position = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(position.x), 0, destinationMap.Size.x - 1), 0,
                Mathf.Clamp(Mathf.FloorToInt(position.z), 0, destinationMap.Size.z - 1));
        }
    }
}
