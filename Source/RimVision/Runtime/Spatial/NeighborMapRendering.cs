using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    sealed class NeighborMapRendering : IDisposable
    {
        const int Step = 4;
        readonly Map _source;
        readonly GameObject _root;
        readonly Shader _shader;
        readonly int _layer;
        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly Dictionary<Material, Material> _ground = new Dictionary<Material, Material>();
        readonly Dictionary<Material, Material> _rocks = new Dictionary<Material, Material>();
        readonly Dictionary<Material, Material> _plants = new Dictionary<Material, Material>();
        readonly HashSet<int> _attempted = new HashSet<int>();
        float _nextCheck;

        internal NeighborMapRendering(Map source, Transform parent, Shader shader, int layer)
        {
            _source = source;
            _shader = shader;
            _layer = layer;
            _root = new GameObject("RimVision neighboring maps") { layer = layer, hideFlags = HideFlags.DontSave };
            _root.transform.SetParent(parent, false);
        }

        internal void Update()
        {
            bool enabled = Rim3DSettings.Current.NeighborMaps;
            if (_root.activeSelf != enabled) _root.SetActive(enabled);
            if (!enabled || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + .25f;
            foreach (var parent in Find.World.pocketMaps)
            {
                if (!(parent is NeighborMapParent neighbor) || neighbor.sourceMap != _source) continue;
                var map = neighbor.Map;
                if (map == null || map.Disposed || !_attempted.Add(map.uniqueID)) continue;
                Rand.PushState(map.uniqueID);
                try { Build(map, neighbor.OffsetX, neighbor.OffsetZ); }
                catch (Exception error) { Log.Error("[RimVision] Could not display neighboring map: " + error); }
                finally { Rand.PopState(); }
                break;
            }
        }

        Material Convert(Material source, Dictionary<Material, Material> cache)
        {
            if (cache.TryGetValue(source, out var material)) return material;
            material = new Material(_shader)
            {
                name = "RimVision neighbor " + source.name,
                hideFlags = HideFlags.DontSave,
                mainTexture = source.mainTexture,
                mainTextureScale = source.mainTextureScale,
                mainTextureOffset = source.mainTextureOffset,
                color = source.color
            };
            material.SetFloat("_Opaque", cache == _plants ? 0 : 1);
            material.SetFloat("_Ground", cache == _ground ? 1 : 0);
            material.SetFloat("_Foliage", cache == _plants ? 1 : 0);
            material.SetInt("_StencilWriteMask", 0);
            cache.Add(source, material);
            return material;
        }

        static Geometry Batch(Dictionary<Material, Geometry> batches, Material material)
        {
            if (!batches.TryGetValue(material, out var geometry))
                batches.Add(material, geometry = new Geometry());
            return geometry;
        }

        void Build(Map map, int offsetX, int offsetZ)
        {
            var batches = new Dictionary<Material, Geometry>();
            int width = (map.Size.x + Step - 1) / Step;
            int depth = (map.Size.z + Step - 1) / Step;
            var terrains = new TerrainDef[width, depth];
            var rocks = new ThingDef[width, depth];
            var heights = new float[width, depth];
            var trees = new Plant[width, depth];
            for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                var center = new IntVec3(Mathf.Min(x * Step + Step / 2, map.Size.x - 1), 0,
                    Mathf.Min(z * Step + Step / 2, map.Size.z - 1));
                terrains[x, z] = map.terrainGrid.TerrainAt(center);
                for (int cz = z * Step; cz < Mathf.Min((z + 1) * Step, map.Size.z); cz++)
                for (int cx = x * Step; cx < Mathf.Min((x + 1) * Step, map.Size.x); cx++)
                {
                    var cell = new IntVec3(cx, 0, cz);
                    var rock = cell.GetEdifice(map);
                    if (rock?.def.building?.isNaturalRock == true)
                    {
                        rocks[x, z] = rock.def;
                        float height = map.roofGrid.RoofAt(cell)?.isThickRoof == true ? 8 : 3;
                        heights[x, z] = Mathf.Max(heights[x, z], height);
                    }
                    var plant = cell.GetPlant(map);
                    if (trees[x, z] == null && plant?.def.plant.IsTree == true && !plant.def.plant.isStump)
                        trees[x, z] = plant;
                }
            }
            for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                var terrain = terrains[x, z];
                if (terrain == null) continue;
                int end = x + 1;
                while (end < width && terrains[end, z] == terrain) end++;
                float x0 = x * Step, x1 = Mathf.Min(end * Step, map.Size.x);
                float z0 = z * Step, z1 = Mathf.Min((z + 1) * Step, map.Size.z);
                Batch(batches, Convert(terrain.DrawMatSingle, _ground)).Quad(
                    new Vector3(x0, 0, z0), new Vector3(x0, 0, z1),
                    new Vector3(x1, 0, z1), new Vector3(x1, 0, z0), Vector3.up,
                    new Vector2(x0, z0), new Vector2(x0, z1), new Vector2(x1, z1), new Vector2(x1, z0));
                x = end - 1;
            }
            for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                float x0 = x * Step, x1 = Mathf.Min((x + 1) * Step, map.Size.x);
                float z0 = z * Step, z1 = Mathf.Min((z + 1) * Step, map.Size.z);
                float height = heights[x, z];
                if (rocks[x, z] != null)
                {
                    int faces = 0;
                    if (z == 0 || heights[x, z - 1] < height) faces |= 1;
                    if (z == depth - 1 || heights[x, z + 1] < height) faces |= 2;
                    if (x == 0 || heights[x - 1, z] < height) faces |= 4;
                    if (x == width - 1 || heights[x + 1, z] < height) faces |= 8;
                    Batch(batches, Convert(rocks[x, z].graphic.MatSingle, _rocks)).Box(
                        new Vector3((x0 + x1) * .5f, 0, (z0 + z1) * .5f),
                        new Vector3(x1 - x0, height, z1 - z0), new Rect(0, 0, 1, 1),
                        new Rect(0, 0, 1, 1), faces, false);
                    continue;
                }
                var plant = trees[x, z];
                if (plant == null) continue;
                var graphic = plant.Graphic;
                var source = graphic.MatAt(plant.Rotation, plant);
                if (source == null || source.mainTexture == null || source.mainTexture == BaseContent.BadTex) continue;
                var size = graphic.drawSize * plant.def.plant.visualSizeRange.LerpThroughRange(plant.Growth)
                    * Rim3DSettings.Current.TreeScaleFor(plant.def);
                Batch(batches, Convert(source, _plants)).DiagonalCards(plant.Position.ToVector3Shifted(),
                    size.x, size.y, 0);
            }
            var holder = new GameObject("Neighbor " + offsetX + "," + offsetZ)
                { layer = _layer, hideFlags = HideFlags.DontSave };
            holder.transform.SetParent(_root.transform, false);
            holder.transform.localPosition = new Vector3(offsetX * _source.Size.x, 0, offsetZ * _source.Size.z);
            foreach (var entry in batches)
            {
                var mesh = new Mesh { name = "RimVision neighbor", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
                _meshes.Add(mesh);
                var geometry = entry.Value;
                mesh.SetVertices(geometry.Vertices);
                mesh.SetNormals(geometry.Normals);
                mesh.SetUVs(0, geometry.Uvs);
                mesh.SetTriangles(geometry.Indices, 0);
                mesh.RecalculateBounds();
                var obj = new GameObject(entry.Key.name) { layer = _layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(holder.transform, false);
                obj.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = entry.Key;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            foreach (var mesh in _meshes) UnityEngine.Object.Destroy(mesh);
            foreach (var material in _ground.Values) UnityEngine.Object.Destroy(material);
            foreach (var material in _rocks.Values) UnityEngine.Object.Destroy(material);
            foreach (var material in _plants.Values) UnityEngine.Object.Destroy(material);
            _meshes.Clear();
            _ground.Clear();
            _rocks.Clear();
            _plants.Clear();
            _attempted.Clear();
        }
    }
}
