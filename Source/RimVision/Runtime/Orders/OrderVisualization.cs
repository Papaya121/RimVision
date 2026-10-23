using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    internal sealed class OrderVisualization : IDisposable
    {
        readonly Transform _root;
        readonly Func<Color, Material> _makeMaterial;
        readonly Func<Material, Geometry, string, GameObject> _makeMesh;
        readonly Geometry _geometry = new Geometry();
        GameObject _object;
        Mesh _mesh;
        Material _material;

        internal OrderVisualization(Transform root, Func<Color, Material> makeMaterial,
            Func<Material, Geometry, string, GameObject> makeMesh)
        { _root = root; _makeMaterial = makeMaterial; _makeMesh = makeMesh; }

        internal void Hide()
        {
            if (_object != null) _object.SetActive(false);
        }

        internal void Show(IEnumerable<IntVec3> cells, IEnumerable<Thing> things, float offset, Designator_Build build, Rot4 rotation)
        {
            _geometry.Vertices.Clear(); _geometry.Normals.Clear(); _geometry.Uvs.Clear();
            _geometry.CardCenters.Clear(); _geometry.Indices.Clear();
            foreach (var cell in cells)
            {
                if (build != null)
                {
                    var rect = GenAdj.OccupiedRect(cell, rotation, build.PlacingDef.Size);
                    Box(new Vector3((rect.minX + rect.maxX + 1) * .5f, .07f, (rect.minZ + rect.maxZ + 1) * .5f),
                        rect.Width * .98f, rect.Height * .98f, build.PlacingDef is TerrainDef ? .04f : .75f);
                }
                else
                {
                    var center = cell.ToVector3Shifted(); center.y = .055f;
                    _geometry.FloorCard(center, .88f, .88f);
                }
            }
            foreach (var thing in things)
            {
                float height = thing is Plant plant ? Mathf.Clamp((plant.Graphic?.drawSize.y ?? 1) *
                    (plant.def.plant.IsTree ? 3.5f : .4f), .2f, 9) : thing is Pawn ? 1.6f :
                    thing.def.Fillage == FillCategory.Full ? 2 : .6f;
                var center = thing.TrueCenter(); center.y = .065f;
                Box(center, thing.RotatedSize.x + .04f, thing.RotatedSize.z + .04f, height);
            }
            if (_geometry.Vertices.Count == 0) { Hide(); return; }
            if (_object == null)
            {
                _material = _makeMaterial(new Color(.3f, .95f, .5f));
                _material.SetFloat("_SurfaceOpacity", .45f);
                _material.SetFloat("_ZWrite", 0);
                _material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                _object = _makeMesh(_material, _geometry, "Colony order preview");
                _object.transform.SetParent(_root, false);
                _mesh = _object.GetComponent<MeshFilter>().sharedMesh;
                _mesh.MarkDynamic();
            }
            else
            {
                _mesh.Clear();
                _mesh.SetVertices(_geometry.Vertices); _mesh.SetNormals(_geometry.Normals);
                _mesh.SetUVs(0, _geometry.Uvs); _mesh.SetUVs(1, _geometry.CardCenters);
                _mesh.SetTriangles(_geometry.Indices, 0); _mesh.RecalculateBounds();
            }
            _object.transform.localPosition = Vector3.up * offset;
            _object.SetActive(true);
        }

        void Box(Vector3 center, float width, float depth, float height)
        {
            var a = center + new Vector3(-width * .5f, 0, -depth * .5f);
            var b = a + Vector3.right * width;
            var c = b + Vector3.forward * depth;
            var d = a + Vector3.forward * depth;
            var up = Vector3.up * height;
            const float line = .035f;
            _geometry.OutlineLine(a, b, line); _geometry.OutlineLine(b, c, line);
            _geometry.OutlineLine(c, d, line); _geometry.OutlineLine(d, a, line);
            _geometry.OutlineLine(a, a + up, line); _geometry.OutlineLine(b, b + up, line);
            _geometry.OutlineLine(c, c + up, line); _geometry.OutlineLine(d, d + up, line);
            _geometry.OutlineLine(a + up, b + up, line); _geometry.OutlineLine(b + up, c + up, line);
            _geometry.OutlineLine(c + up, d + up, line); _geometry.OutlineLine(d + up, a + up, line);
        }

        public void Dispose() => Hide();
    }
}
