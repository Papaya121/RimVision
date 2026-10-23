using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly Dictionary<Material, Material> chunkMaterials = new Dictionary<Material, Material>();
        readonly Dictionary<Skyfaller, GameObject> dropPods = new Dictionary<Skyfaller, GameObject>();
        readonly List<Skyfaller> expiredDropPods = new List<Skyfaller>();

        static readonly Geometry RockVolume = RoundedVolume(
            new[] { .02f, .18f, .43f, .54f }, new[] { .38f, .48f, .29f, .08f }, true);
        static readonly Geometry PodVolume = RoundedVolume(
            new[] { 0f, .16f, 1.05f, 1.38f }, new[] { .24f, .44f, .36f, .10f }, false);
        static readonly Geometry PodSurface = PodArtwork();

        static bool IsDropPod(Thing thing) => thing.def.graphicData?.texPath != null &&
            thing.def.graphicData.texPath.StartsWith("Things/Special/DropPod", StringComparison.OrdinalIgnoreCase);

        // Shared, closed low-poly bodies. Each ring contributes only eight faces.
        static Geometry RoundedVolume(float[] heights, float[] radii, bool irregular)
        {
            var geometry = new Geometry();
            var rings = new Vector3[heights.Length, 8];
            for (int r = 0; r < heights.Length; r++)
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI / 4;
                float radius = radii[r] * (irregular ? 1 + .12f * Mathf.Sin(i * 2.7f + r) : 1);
                rings[r, i] = new Vector3(Mathf.Cos(angle) * radius, heights[r], Mathf.Sin(angle) * radius);
            }
            for (int r = 0; r < heights.Length - 1; r++)
            for (int i = 0; i < 8; i++)
            {
                int next = (i + 1) % 8;
                var a = rings[r, i]; var b = rings[r + 1, i];
                var c = rings[r + 1, next]; var d = rings[r, next];
                geometry.Quad(a, b, c, d, Vector3.Cross(b - a, c - a).normalized,
                    new Vector2(i / 8f, r / 3f), new Vector2(i / 8f, (r + 1) / 3f),
                    new Vector2((i + 1) / 8f, (r + 1) / 3f), new Vector2((i + 1) / 8f, r / 3f));
            }
            for (int i = 0; i < 8; i++)
            {
                int next = (i + 1) % 8, top = heights.Length - 1;
                var center = Vector3.up * heights[top];
                geometry.Quad(center, rings[top, next], rings[top, i], center, Vector3.up,
                    Vector2.one * .5f, Vector2.zero, Vector2.one, Vector2.one * .5f);
                geometry.Quad(Vector3.up * heights[0], rings[0, i], rings[0, next], Vector3.up * heights[0], Vector3.down,
                    Vector2.one * .5f, Vector2.zero, Vector2.one, Vector2.one * .5f);
            }
            return geometry;
        }

        void DrawItemVolume(Thing thing, Material source, Vector3 center, float width, float depth) => _objectPresentation.DrawItemVolume(thing, source, center, width, depth);

        Material PodShellMaterial() => _objectPresentation.PodShellMaterial();

        static Geometry PodArtwork()
        {
            var geometry = new Geometry();
            // Physical hatch and reinforcing ribs; no whole-pod sprite over the hull.
            geometry.Box(new Vector3(0, .26f, -.365f), new Vector3(.40f, .72f, .045f), new Rect(0, 0, 1, 1));
            foreach (float x in new[] { -.27f, .27f })
                geometry.Box(new Vector3(x, .17f, -.23f), new Vector3(.04f, .85f, .06f), new Rect(0, 0, 1, 1));
            return geometry;
        }

        Material PodDetailMaterial() => _objectPresentation.PodDetailMaterial();

        void DrawLandedPod(Material source, Vector3 center) => _objectPresentation.DrawLandedPod(source, center);

        void TrackDropPod(Thing thing) => _objectPresentation.TrackDropPod(thing);

        void UpdateDropPods() => _objectPresentation.UpdateDropPods();

        readonly Dictionary<Skyfaller, GameObject> pendingPodObjects = new Dictionary<Skyfaller, GameObject>();

        void DrawVent(Thing vent, Material source, Vector3 center) => _objectPresentation.DrawVent(vent, source, center);

        sealed partial class ObjectPresentation
        {
            Material _podShell, _podDetails;

            internal float GroundItemHeightOffset(Thing thing)
            {
                uint order = unchecked((uint)thing.thingIDNumber * 2654435761u);
                int rank = 0;
                foreach (var other in _view.map.thingGrid.ThingsListAt(thing.Position))
                {
                    if (other == thing || other.def.category != ThingCategory.Item || other is Corpse) continue;
                    uint otherOrder = unchecked((uint)other.thingIDNumber * 2654435761u);
                    if (otherOrder < order) rank++;
                }
                int cellSlot = (thing.Position.x % 3 + thing.Position.z % 3 * 3) * 5 % 9;
                return (rank * 9 + cellSlot) * .001f;
            }

            internal void DrawItemVolume(Thing thing, Material source, Vector3 center, float width, float depth)
            {
                if (thing.def.graphicData.texPath?.IndexOf("/Chunk/ChunkStone", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!_view.chunkMaterials.TryGetValue(source, out var material))
                    {
                        material = _view.NewMaterial(ModAssets.LoadTexture("textures/terrain/surfaces/RoughHewnRock") ?? Texture2D.whiteTexture,
                            source.HasProperty("_Color") ? source.GetColor("_Color") : thing.DrawColor, true);
                        material.SetFloat("_SurfaceTile", 1);
                        _view.chunkMaterials.Add(source, material);
                    }
                    _view.Batch(material, center).Append(RockVolume, center, Quaternion.Euler(0, thing.thingIDNumber % 360, 0));
                    return;
                }
                // Reuse the cached silhouette extrusion for all remaining item sprites.
                // Transparent padding stays empty, and each stack still joins its chunk batch.
                var sprite = _view.ConvertMaterial(source, false);
                float halfThickness = Mathf.Min(width * .065f, depth * .09f);
                _view.Batch(_view.WeaponVolumeMaterial(sprite), center).Append(_view.WeaponVolume(sprite, width, depth, false),
                    center + Vector3.up * (halfThickness + .015f), Quaternion.Euler(90, 0, 0));
            }

            internal Material PodShellMaterial()
            {
                if (_podShell == null) _podShell = _view.NewMaterial(Texture2D.whiteTexture, new Color(.36f, .40f, .43f), true);
                return _podShell;
            }

            internal Material PodDetailMaterial()
            {
                if (_podDetails == null) _podDetails = _view.NewMaterial(Texture2D.whiteTexture, new Color(.15f, .19f, .21f), true);
                return _podDetails;
            }

            internal void DrawLandedPod(Material source, Vector3 center)
            {
                _view.Batch(_view.PodShellMaterial(), center).Append(PodVolume, center, Quaternion.identity);
                _view.Batch(_view.PodDetailMaterial(), center).Append(PodSurface, center, Quaternion.identity);
            }

            internal void TrackDropPod(Thing thing)
            {
                // Notifications may precede SpawnSetup, so create the mesh on the next update.
                if (thing is Skyfaller falling && IsDropPod(thing) && !_view.dropPods.ContainsKey(falling))
                    _view.dropPods.Add(falling, null);
            }

            internal void UpdateDropPods()
            {
                _view.expiredDropPods.Clear();
                foreach (var entry in _view.dropPods)
                {
                    var pod = entry.Key;
                    if (pod.Destroyed || !pod.Spawned || pod.Map != _view.map) { _view.expiredDropPods.Add(pod); continue; }
                    var obj = entry.Value;
                    if (obj == null)
                    {
                        _view.geometryParent = null;
                        obj = _view.CreateMesh(_view.PodShellMaterial(), PodVolume, "Flying drop pod");
                        _view.geometryParent = obj.transform;
                        _view.CreateMesh(_view.PodDetailMaterial(), PodSurface, "Pod hatch");
                        _view.geometryParent = null;
                        // Dictionary values are assigned after enumeration below.
                        _view.pendingPodObjects.Add(pod, obj);
                    }
                    obj.SetActive(!_view.map.fogGrid.IsFogged(pod.Position));
                    var landing = pod.TrueCenter();
                    var flight = pod.DrawPos;
                    // Native flight flattens altitude into Z. Lift that displacement into Y,
                    // preserving the native timing, speed, lateral drift and impact cell.
                    obj.transform.localPosition = new Vector3(flight.x, Mathf.Abs(flight.z - landing.z), landing.z);
                }
                foreach (var entry in _view.pendingPodObjects) _view.dropPods[entry.Key] = entry.Value;
                _view.pendingPodObjects.Clear();
                foreach (var pod in _view.expiredDropPods)
                {
                    var obj = _view.dropPods[pod];
                    if (obj != null)
                    {
                        _view.RemoveMovingShadows(obj.transform);
                        foreach (var filter in obj.GetComponentsInChildren<MeshFilter>())
                        {
                            // Shadow children share the same meshes.
                            if (_view.meshes.Remove(filter.sharedMesh)) UnityEngine.Object.Destroy(filter.sharedMesh);
                        }
                        UnityEngine.Object.Destroy(obj);
                    }
                    _view.dropPods.Remove(pod);
                }
            }

            internal void DrawVent(Thing vent, Material source, Vector3 center)
            {
                var rotation = Quaternion.Euler(0, vent.Rotation.AsAngle, 0);
                Building wall = null;
                // Prefer the walls along the vent's width, then check the other axis.
                for (int i = 0; i < 4 && wall == null; i++)
                {
                    var direction = rotation * (i == 0 ? Vector3.right : i == 1 ? Vector3.left : i == 2 ? Vector3.forward : Vector3.back);
                    var cell = vent.Position + new IntVec3(Mathf.RoundToInt(direction.x), 0, Mathf.RoundToInt(direction.z));
                    if (!cell.InBounds(_view.map) || !_view.SolidNeighbor(cell)) continue;
                    var candidate = cell.GetEdifice(_view.map);
                    if (candidate.def.defName != "Vent") wall = candidate;
                }
                var wallSource = wall?.Graphic is Graphic_Linked linked ? linked.SubGraphic.MatSingleFor(wall) : null;
                var surround = wallSource != null ? _view.LinkedBuildingMaterial(wallSource) :
                    _view.BuildingSideMaterial(wall != null ? wall.Graphic.MatAt(wall.Rotation, wall) : source);
                var body = new Geometry();
                body.Box(Vector3.zero, new Vector3(1, .65f, 1), new Rect(0, 0, 1, .65f));
                body.Box(Vector3.up * 1.12f, new Vector3(1, .88f, 1), new Rect(0, 0, 1, .88f));
                body.Box(new Vector3(-.43f, .65f, 0), new Vector3(.14f, .47f, 1), new Rect(0, 0, .14f, .47f));
                body.Box(new Vector3(.43f, .65f, 0), new Vector3(.14f, .47f, 1), new Rect(0, 0, .14f, .47f));
                // Close the visible underside above the opening.
                body.Quad(new Vector3(-.5f, 1.12f, -.5f), new Vector3(.5f, 1.12f, -.5f),
                    new Vector3(.5f, 1.12f, .5f), new Vector3(-.5f, 1.12f, .5f), Vector3.down,
                    Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
                _view.Batch(surround, center).Append(body, center, rotation);
                var grille = new Geometry();
                grille.Box(new Vector3(0, .65f, 0), new Vector3(.72f, .47f, .98f), new Rect(0, 0, 1, 1));
                _view.Batch(_view.PodShellMaterial(), center).Append(grille, center, rotation);
                // Horizontal slats on both room-facing sides.
                var slats = new Geometry();
                for (int i = 0; i < 5; i++)
                {
                    slats.Box(new Vector3(0, .69f + i * .08f, -.505f), new Vector3(.70f, .026f, .03f), new Rect(0, 0, 1, 1));
                    slats.Box(new Vector3(0, .69f + i * .08f, .505f), new Vector3(.70f, .026f, .03f), new Rect(0, 0, 1, 1));
                }
                _view.Batch(_view.BuildingSideMaterial(source), center).Append(slats, center, rotation);
            }

        }
    }
}

