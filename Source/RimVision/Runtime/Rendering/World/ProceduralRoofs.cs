using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed class RoofProfile : IExposable
    {
        List<IntVec3> _cells = new List<IntVec3>();
        List<IntVec3> _footprint = new List<IntVec3>();
        HashSet<IntVec3> _outline;
        float[,] _heights;
        bool _rectangular;
        int _minX, _minZ, _width, _depth;
        internal List<IntVec3> Cells => _cells;
        internal List<IntVec3> Footprint => _footprint;
        internal bool Rectangular { get { Prepare(); return _rectangular; } }

        public RoofProfile() { }
        internal RoofProfile(List<IntVec3> cells, List<IntVec3> footprint)
        {
            _cells = cells;
            _footprint = footprint;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref _cells, "cells", LookMode.Value);
            Scribe_Collections.Look(ref _footprint, "footprint", LookMode.Value);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            if (_cells == null) _cells = new List<IntVec3>();
            if (_footprint == null) _footprint = new List<IntVec3>(_cells);
            _outline = null;
            _heights = null;
        }

        internal bool Contains(IntVec3 cell)
        {
            Prepare();
            return _outline.Contains(cell);
        }

        internal float Height(float x, float z)
        {
            Prepare();
            int ix = Mathf.Clamp(Mathf.RoundToInt((x - _minX) * 2), 0, _width * 2);
            int iz = Mathf.Clamp(Mathf.RoundToInt((z - _minZ) * 2), 0, _depth * 2);
            return 2.12f + _heights[ix, iz];
        }

        void Prepare()
        {
            if (_heights != null) return;
            _outline = new HashSet<IntVec3>(_footprint);
            _minX = _minZ = int.MaxValue;
            int maxX = int.MinValue, maxZ = int.MinValue;
            foreach (var cell in _footprint)
            {
                _minX = Math.Min(_minX, cell.x); _minZ = Math.Min(_minZ, cell.z);
                maxX = Math.Max(maxX, cell.x); maxZ = Math.Max(maxZ, cell.z);
            }
            _width = maxX - _minX + 1; _depth = maxZ - _minZ + 1;
            int w = _width * 2, d = _depth * 2;
            _heights = new float[w + 1, d + 1];
            bool rectangle = _rectangular = _outline.Count == _width * _depth;
            int shortSide = Math.Min(_width, _depth), longSide = Math.Max(_width, _depth);
            bool shed = rectangle && shortSide <= 4;
            bool gable = rectangle && !shed && longSide > shortSide * 1.4f;
            float maximum = 0;
            for (int z = 0; z <= d; z++)
            for (int x = 0; x <= w; x++)
            {
                float h;
                if (rectangle)
                {
                    float across = _width <= _depth ? x * .5f : z * .5f;
                    h = shed ? across : gable ? Mathf.Min(across, shortSide - across) :
                        Mathf.Min(Mathf.Min(x, w - x), Mathf.Min(z, d - z)) * .5f;
                }
                else
                {
                    bool inside = true;
                    int firstX = (x - 1) / 2, lastX = x / 2;
                    int firstZ = (z - 1) / 2, lastZ = z / 2;
                    if (x == 0 || z == 0 || x == w || z == d) inside = false;
                    else for (int cz = firstZ; cz <= lastZ; cz++)
                        for (int cx = firstX; cx <= lastX; cx++)
                            if (!_outline.Contains(new IntVec3(_minX + cx, 0, _minZ + cz))) inside = false;
                    h = inside ? w + d : 0;
                }
                _heights[x, z] = h;
                maximum = Mathf.Max(maximum, h);
            }
            if (!rectangle)
            {
                for (int z = 1; z <= d; z++)
                for (int x = 1; x <= w; x++)
                    _heights[x, z] = Mathf.Min(_heights[x, z], .5f + Mathf.Min(
                        _heights[x - 1, z], Mathf.Min(_heights[x, z - 1],
                            Mathf.Min(_heights[x - 1, z - 1], x < w ? _heights[x + 1, z - 1] : 0))));
                maximum = 0;
                for (int z = d - 1; z >= 0; z--)
                for (int x = w - 1; x >= 0; x--)
                {
                    _heights[x, z] = Mathf.Min(_heights[x, z], .5f + Mathf.Min(
                        _heights[x + 1, z], Mathf.Min(_heights[x, z + 1],
                            Mathf.Min(_heights[x + 1, z + 1], x > 0 ? _heights[x - 1, z + 1] : 0))));
                    maximum = Mathf.Max(maximum, _heights[x, z]);
                }
            }
            float slope = Mathf.Min(shed ? .3f : .55f, 2.4f / Mathf.Max(.5f, maximum));
            for (int z = 0; z <= d; z++)
            for (int x = 0; x <= w; x++) _heights[x, z] *= slope;
        }
    }

    public sealed class RoofStyles : MapComponent
    {
        List<RoofProfile> _profiles = new List<RoofProfile>();
        readonly Dictionary<IntVec3, RoofProfile> _lookup = new Dictionary<IntVec3, RoofProfile>();
        internal event Action<IntVec3> Changed;
        public RoofStyles(Map map) : base(map) { }
        internal RoofProfile At(IntVec3 cell) => _lookup.TryGetValue(cell, out var profile) ? profile : null;

        internal void Set(List<IntVec3> cells, bool procedural)
        {
            var coverage = new HashSet<IntVec3>();
            var footprint = new HashSet<IntVec3>();
            var joined = new HashSet<RoofProfile>();
            foreach (var cell in cells)
            {
                if (map.roofGrid.RoofAt(cell)?.isNatural == true) continue;
                footprint.Add(cell);
                if (map.roofGrid.Roofed(cell)) coverage.Add(cell);
                if (procedural && At(cell) is RoofProfile previous) joined.Add(previous);
            }
            foreach (var profile in joined)
            {
                foreach (var cell in profile.Footprint) footprint.Add(cell);
                foreach (var cell in profile.Cells)
                    if (map.roofGrid.RoofAt(cell)?.isNatural == false) coverage.Add(cell);
            }
            if (coverage.Count == 0) return;
            var ordered = new List<IntVec3>(coverage);
            ordered.Sort(CompareCells);
            var replaced = new HashSet<RoofProfile>();
            foreach (var cell in ordered)
            {
                if (At(cell) is RoofProfile previous) replaced.Add(previous);
                _lookup.Remove(cell);
            }
            foreach (var previous in replaced)
            {
                previous.Cells.RemoveAll(coverage.Contains);
                if (previous.Cells.Count == 0) _profiles.Remove(previous);
            }
            if (procedural)
            {
                var outline = new List<IntVec3>(footprint);
                outline.Sort(CompareCells);
                var profile = new RoofProfile(ordered, outline);
                _profiles.Add(profile);
                foreach (var cell in ordered) _lookup[cell] = profile;
            }
            foreach (var cell in ordered) Changed?.Invoke(cell);
        }

        static int CompareCells(IntVec3 a, IntVec3 b) => a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x);

        internal void Remove(IntVec3 cell)
        {
            if (!_lookup.TryGetValue(cell, out var profile)) return;
            _lookup.Remove(cell);
            profile.Cells.Remove(cell);
            if (profile.Cells.Count == 0) _profiles.Remove(profile);
            Changed?.Invoke(cell);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref _profiles, "rimVisionRoofs", LookMode.Deep);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            if (_profiles == null) _profiles = new List<RoofProfile>();
            _lookup.Clear();
            _profiles.RemoveAll(profile => profile == null || profile.Cells.Count == 0 || profile.Footprint.Count == 0);
            foreach (var profile in _profiles)
                foreach (var cell in profile.Cells) _lookup[cell] = profile;
        }
    }

    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
    static class RoofStyleRemovalPatch
    {
        static void Postfix(Map ___map, IntVec3 __0, RoofDef __1)
        {
            if (__1 == null || __1.isNatural) ___map.GetComponent<RoofStyles>()?.Remove(__0);
        }
    }

    public sealed partial class MapView
    {


        void BuildProceduralRoof(IntVec3 cell, RoofProfile profile, Material ceiling) => _worldGeometry.BuildProceduralRoof(cell, profile, ceiling);

        float RoofOverhang(IntVec3 neighbor, RoofProfile profile) => _worldGeometry.RoofOverhang(neighbor, profile);

        static void RoofTriangle(Geometry geometry, Vector3 a, Vector3 b, Vector3 c)
        {
            int start = geometry.Vertices.Count;
            var normal = Vector3.Cross(b - a, c - a).normalized;
            geometry.Vertices.Add(a); geometry.Vertices.Add(b); geometry.Vertices.Add(c);
            for (int i = 0; i < 3; i++)
            {
                geometry.Normals.Add(normal);
                geometry.CardCenters.Add(Vector3.zero);
            }
            geometry.Uvs.Add(new Vector2(a.x, a.z));
            geometry.Uvs.Add(new Vector2(b.x, b.z));
            geometry.Uvs.Add(new Vector2(c.x, c.z));
            geometry.Indices.Add(start); geometry.Indices.Add(start + 1); geometry.Indices.Add(start + 2);
        }

        void RoofEdge(IntVec3 cell, RoofProfile profile, Geometry geometry, Vector3 a, Vector3 b, IntVec3 offset) => _worldGeometry.RoofEdge(cell, profile, geometry, a, b, offset);

        sealed partial class WorldGeometry
        {
            Material _proceduralRoofMaterial;

            internal void BuildProceduralRoof(IntVec3 cell, RoofProfile profile, Material ceiling)
            {
                if (_proceduralRoofMaterial == null)
                {
                    _proceduralRoofMaterial = _view.NewMaterial(ModAssets.LoadTexture("textures/terrain/surfaces/RimVisionRoof") ??
                        Texture2D.whiteTexture, Color.white, true);
                    _proceduralRoofMaterial.SetFloat("_SurfaceTile", 1);
                    _proceduralRoofMaterial.SetFloat("_SurfaceRepeat", 1);
                    _proceduralRoofMaterial.SetVector("_SurfaceScale", new Vector4(.25f, .375f, 0, 0));
                    _proceduralRoofMaterial.SetFloat("_Roof", 1);
                }
                var top = _view.Batch(_proceduralRoofMaterial, cell.ToVector3());
                var sides = _view.Batch(ceiling, cell.ToVector3());
                float minX = cell.x - _view.RoofOverhang(cell + IntVec3.West, profile);
                float maxX = cell.x + 1 + _view.RoofOverhang(cell + IntVec3.East, profile);
                float minZ = cell.z - _view.RoofOverhang(cell + IntVec3.South, profile);
                float maxZ = cell.z + 1 + _view.RoofOverhang(cell + IntVec3.North, profile);
                for (int z = 0; z < 2; z++)
                for (int x = 0; x < 2; x++)
                {
                    float left = x == 0 ? minX : cell.x + .5f;
                    float right = x == 0 ? cell.x + .5f : maxX;
                    float bottom = z == 0 ? minZ : cell.z + .5f;
                    float upper = z == 0 ? cell.z + .5f : maxZ;
                    var a = new Vector3(left, profile.Height(left, bottom), bottom);
                    var b = new Vector3(left, profile.Height(left, upper), upper);
                    var c = new Vector3(right, profile.Height(right, upper), upper);
                    var d = new Vector3(right, profile.Height(right, bottom), bottom);
                    if (a.y + c.y >= b.y + d.y)
                    { RoofTriangle(top, a, b, c); RoofTriangle(top, a, c, d); }
                    else
                    { RoofTriangle(top, a, b, d); RoofTriangle(top, b, c, d); }
                    if (z == 0) _view.RoofEdge(cell, profile, sides, a, d, IntVec3.South);
                    if (z == 1) _view.RoofEdge(cell, profile, sides, c, b, IntVec3.North);
                    if (x == 0) _view.RoofEdge(cell, profile, sides, b, a, IntVec3.West);
                    if (x == 1) _view.RoofEdge(cell, profile, sides, d, c, IntVec3.East);
                }
                sides.Quad(new Vector3(minX, 2, minZ), new Vector3(maxX, 2, minZ),
                    new Vector3(maxX, 2, maxZ), new Vector3(minX, 2, maxZ), Vector3.down,
                    Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
            }

            internal float RoofOverhang(IntVec3 neighbor, RoofProfile profile) => profile.Rectangular && !profile.Contains(neighbor) &&
                (!neighbor.InBounds(_view.map) || !_view.map.roofGrid.Roofed(neighbor)) ? .16f : 0;

            internal void RoofEdge(IntVec3 cell, RoofProfile profile, Geometry geometry, Vector3 a, Vector3 b, IntVec3 offset)
            {
                var neighbor = cell + offset;
                if (neighbor.InBounds(_view.map) && _view.map.roofGrid.RoofAt(neighbor)?.isNatural == false &&
                    _view.map.GetComponent<RoofStyles>().At(neighbor) == profile) return;
                bool outer = !profile.Contains(neighbor) || neighbor.InBounds(_view.map) && _view.map.roofGrid.Roofed(neighbor);
                float baseA = outer ? 2 : a.y - .12f, baseB = outer ? 2 : b.y - .12f;
                geometry.Quad(new Vector3(a.x, baseA, a.z), a, b, new Vector3(b.x, baseB, b.z),
                    new Vector3(offset.x, 0, offset.z), Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
            }

        }
    }
}
