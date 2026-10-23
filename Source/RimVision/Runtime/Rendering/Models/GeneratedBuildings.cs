using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly Dictionary<(ThingDef, Material, Mesh), GeneratedBuilding> _generatedBuildings =
            new Dictionary<(ThingDef, Material, Mesh), GeneratedBuilding>();
        readonly Dictionary<Material, Color32[]> _generatedBuildingPixels = new Dictionary<Material, Color32[]>();
        Material _generatedBuildingSideMaterial;
        bool _lastAutoGenerateBuildings;

        static bool HasFallbackBuildingBody(ThingDef def) =>
            def.passability == Traversability.Impassable || def.pathCost >= 25 ||
            def.Fillage == FillCategory.Full || def.mineable;

        static bool HasGeneratedBuildingBody(Thing thing) => HasFallbackBuildingBody(thing.def) ||
            thing.def.fillPercent >= .1f || thing.def.surfaceType != SurfaceType.None ||
            thing.def.building?.isSittable == true || thing is Building_Bed;

        void UpdateGeneratedBuildingSettings() => _objectPresentation.UpdateGeneratedBuildingSettings();

        Color32[] ReadGeneratedBuildingPixels(Material material) => _objectPresentation.ReadGeneratedBuildingPixels(material);

        GeneratedBuilding GetGeneratedBuilding(Thing thing, Graphic graphic, Material source) => _objectPresentation.GetGeneratedBuilding(thing, graphic, source);

        bool TryDrawGeneratedBuilding(Thing thing, Graphic graphic, Material source, Vector3 center) => _objectPresentation.TryDrawGeneratedBuilding(thing, graphic, source, center);

        float GeneratedBuildingSurfaceHeight(Thing thing, Graphic graphic, Vector3 position) => _objectPresentation.GeneratedBuildingSurfaceHeight(thing, graphic, position);

        enum GeneratedBuildingShape { Solid, Table, Seat }

        sealed class GeneratedBuilding
        {
            readonly Vector3 _origin, _uAxis, _vAxis;
            internal Geometry Top { get; } = new Geometry();
            internal Geometry Body { get; } = new Geometry();

            GeneratedBuilding(Vector3 origin, Vector3 uAxis, Vector3 vAxis)
            { _origin = origin; _uAxis = uAxis; _vAxis = vAxis; }

            internal static GeneratedBuilding Create(Mesh mesh, Color32[] pixels, Color tint, float height,
                ThingDef def, GeneratedBuildingShape shape)
            {
                var vertices = mesh.vertices;
                var uvs = mesh.uv;
                if (vertices.Length != 4 || uvs.Length != 4 || pixels.Length != 64 * 80) return null;
                var duv1 = uvs[1] - uvs[0]; var duv2 = uvs[2] - uvs[0];
                float determinant = duv1.x * duv2.y - duv2.x * duv1.y;
                if (Mathf.Abs(determinant) < .00001f) return null;
                var edge1 = vertices[1] - vertices[0]; var edge2 = vertices[2] - vertices[0];
                var uAxis = (edge1 * duv2.y - edge2 * duv1.y) / determinant;
                var vAxis = (edge2 * duv1.x - edge1 * duv2.x) / determinant;
                var origin = vertices[0] - uAxis * uvs[0].x - vAxis * uvs[0].y;
                if ((origin + uAxis * uvs[3].x + vAxis * uvs[3].y - vertices[3]).sqrMagnitude > .00001f) return null;
                origin.y = uAxis.y = vAxis.y = 0;
                if (Mathf.Abs(uAxis.x * vAxis.z - vAxis.x * uAxis.z) < .00001f) return null;
                var filled = LargestComponent(pixels, tint.a);
                var points = new List<Vector2>();
                for (int y = 0; y < 80; y++)
                {
                    int left = 64, right = -1;
                    for (int x = 0; x < 64; x++)
                        if (filled[y * 64 + x]) { left = Mathf.Min(left, x); right = x; }
                    if (right < 0) continue;
                    points.Add(new Vector2(left / 64f, y / 80f));
                    points.Add(new Vector2(left / 64f, (y + 1) / 80f));
                    points.Add(new Vector2((right + 1) / 64f, y / 80f));
                    points.Add(new Vector2((right + 1) / 64f, (y + 1) / 80f));
                }
                if (points.Count < 3) return null;
                var hull = Hull(points);
                if (hull.Count < 3) return null;
                var result = new GeneratedBuilding(origin, uAxis, vAxis);
                var color = BodyColor(pixels, filled) * tint;
                if (shape == GeneratedBuildingShape.Seat) hull = result.SeatOutline(def);
                float bottom = shape == GeneratedBuildingShape.Solid ? 0 : height - .08f;
                result.Build(hull, bottom, height, color, shape != GeneratedBuildingShape.Seat);
                if (shape != GeneratedBuildingShape.Solid) result.BuildLegs(hull, bottom, color);
                var outlineBounds = result.Body.GetBounds();
                if (result.Top.Vertices.Count > 0) outlineBounds.Encapsulate(result.Top.GetBounds());
                result.Top.SetOutlineBounds(0, outlineBounds);
                result.Body.SetOutlineBounds(0, outlineBounds);
                return result;
            }

            static bool[] LargestComponent(Color32[] pixels, float alpha)
            {
                var visited = new bool[pixels.Length];
                var queue = new int[pixels.Length];
                var largest = new List<int>();
                for (int start = 0; start < pixels.Length; start++)
                {
                    if (visited[start] || pixels[start].a * alpha < 128) continue;
                    int head = 0, tail = 1;
                    queue[0] = start; visited[start] = true;
                    while (head < tail)
                    {
                        int index = queue[head++];
                        void Add(int neighbor)
                        {
                            if (visited[neighbor] || pixels[neighbor].a * alpha < 128) return;
                            visited[neighbor] = true; queue[tail++] = neighbor;
                        }
                        if (index % 64 > 0) Add(index - 1);
                        if (index % 64 < 63) Add(index + 1);
                        if (index >= 64) Add(index - 64);
                        if (index + 64 < pixels.Length) Add(index + 64);
                    }
                    if (tail <= largest.Count) continue;
                    largest.Clear();
                    for (int i = 0; i < tail; i++) largest.Add(queue[i]);
                }
                var filled = new bool[pixels.Length];
                foreach (int index in largest) filled[index] = true;
                return filled;
            }

            static float Cross(Vector2 a, Vector2 b, Vector2 c) =>
                (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

            static List<Vector2> Hull(List<Vector2> points)
            {
                points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                var hull = new List<Vector2>();
                foreach (var point in points)
                {
                    while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= .000001f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(point);
                }
                int lower = hull.Count;
                for (int i = points.Count - 2; i >= 0; i--)
                {
                    while (hull.Count > lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= .000001f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(points[i]);
                }
                hull.RemoveAt(hull.Count - 1);
                return hull;
            }

            static Color BodyColor(Color32[] pixels, bool[] filled)
            {
                var histogram = new int[256];
                int count = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (!filled[i]) continue;
                    var pixel = pixels[i];
                    int value = (pixel.r * 3 + pixel.g * 6 + pixel.b) / 10;
                    histogram[value]++; count++;
                }
                int low = 0, high = 255, accumulated = 0;
                bool lowFound = false;
                for (int i = 0; i < histogram.Length; i++)
                {
                    accumulated += histogram[i];
                    if (!lowFound && accumulated >= count * .35f) { low = i; lowFound = true; }
                    if (accumulated >= count * .8f) { high = i; break; }
                }
                var color = Color.black;
                count = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (!filled[i]) continue;
                    var pixel = pixels[i];
                    int value = (pixel.r * 3 + pixel.g * 6 + pixel.b) / 10;
                    if (value < low || value > high) continue;
                    color += (Color)pixel; count++;
                }
                return count > 0 ? color / count : new Color(.6f, .6f, .6f);
            }

            Vector3 Point(Vector2 uv, float height) => _origin + _uAxis * uv.x + _vAxis * uv.y + Vector3.up * height;

            List<Vector2> SeatOutline(ThingDef def)
            {
                float scale = def.building?.multiSittable == true ? .9f : .72f;
                float width = Mathf.Min(_uAxis.magnitude, def.size.x * scale) / _uAxis.magnitude;
                float depth = Mathf.Min(_vAxis.magnitude, def.size.z * scale) / _vAxis.magnitude;
                float left = .5f - width * .5f, right = .5f + width * .5f;
                float bottom = .5f - depth * .5f, top = .5f + depth * .5f;
                float bevelU = width * .08f, bevelV = depth * .08f;
                return new List<Vector2>
                {
                    new Vector2(left + bevelU, bottom), new Vector2(right - bevelU, bottom),
                    new Vector2(right, bottom + bevelV), new Vector2(right, top - bevelV),
                    new Vector2(right - bevelU, top), new Vector2(left + bevelU, top),
                    new Vector2(left, top - bevelV), new Vector2(left, bottom + bevelV)
                };
            }

            void BuildLegs(List<Vector2> hull, float height, Color color)
            {
                var min = Vector2.one; var max = Vector2.zero; var center = Vector2.zero;
                foreach (var point in hull)
                { min = Vector2.Min(min, point); max = Vector2.Max(max, point); center += point; }
                center /= hull.Count;
                float extent = Mathf.Min((max.x - min.x) * _uAxis.magnitude, (max.y - min.y) * _vAxis.magnitude);
                float thickness = Mathf.Min(Mathf.Clamp(extent * .09f, .045f, .12f), extent * .18f);
                var halfSize = new Vector2(thickness / _uAxis.magnitude, thickness / _vAxis.magnitude) * .5f;
                for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                {
                    var position = new Vector2(Mathf.Lerp(min.x, max.x, x == 0 ? .2f : .8f),
                        Mathf.Lerp(min.y, max.y, y == 0 ? .2f : .8f));
                    for (int step = 0; step < 8; step++)
                    {
                        if (Inside(hull, position - halfSize) && Inside(hull, position + halfSize) &&
                            Inside(hull, position + new Vector2(halfSize.x, -halfSize.y)) &&
                            Inside(hull, position + new Vector2(-halfSize.x, halfSize.y))) break;
                        position = Vector2.Lerp(position, center, .25f);
                    }
                    var a = position - halfSize;
                    var b = position + new Vector2(halfSize.x, -halfSize.y);
                    var c = position + halfSize;
                    var d = position + new Vector2(-halfSize.x, halfSize.y);
                    BodyFace(Point(a, 0), Point(a, height), Point(b, height), Point(b, 0), color);
                    BodyFace(Point(b, 0), Point(b, height), Point(c, height), Point(c, 0), color);
                    BodyFace(Point(c, 0), Point(c, height), Point(d, height), Point(d, 0), color);
                    BodyFace(Point(d, 0), Point(d, height), Point(a, height), Point(a, 0), color);
                }
            }

            static bool Inside(List<Vector2> hull, Vector2 point)
            {
                for (int i = 0; i < hull.Count; i++)
                    if (Cross(hull[i], hull[(i + 1) % hull.Count], point) < -.000001f) return false;
                return true;
            }

            void Build(List<Vector2> hull, float bottom, float height, Color color, bool texturedTop)
            {
                var center = Vector2.zero;
                foreach (var point in hull) center += point;
                center /= hull.Count;
                float bevel = Mathf.Min(.06f, (height - bottom) * .2f);
                for (int i = 0; i < hull.Count; i++)
                {
                    var a = hull[i]; var b = hull[(i + 1) % hull.Count];
                    var innerA = Vector2.Lerp(a, center, .08f);
                    var innerB = Vector2.Lerp(b, center, .08f);
                    BodyFace(Point(innerA, bottom), Point(a, bottom + bevel), Point(b, bottom + bevel), Point(innerB, bottom), color);
                    BodyFace(Point(a, bottom + bevel), Point(a, height - bevel), Point(b, height - bevel), Point(b, bottom + bevel), color);
                    BodyFace(Point(a, height - bevel), Point(innerA, height), Point(innerB, height), Point(b, height - bevel), color);
                    var topA = Point(innerA, height); var topB = Point(innerB, height);
                    var topCenter = Point(center, height);
                    var top = texturedTop ? Top : Body;
                    if (Vector3.Cross(topA - topCenter, topB - topCenter).y > 0)
                        top.Quad(topCenter, topA, topB, topB, Vector3.up, center, innerA, innerB, innerB);
                    else top.Quad(topCenter, topB, topA, topA, Vector3.up, center, innerB, innerA, innerA);
                    if (!texturedTop)
                    {
                        color.a = .5f;
                        for (int vertex = 0; vertex < 4; vertex++) top.Colors.Add(color);
                    }
                    var bottomCenter = Point(center, bottom);
                    var bottomA = Point(innerA, bottom); var bottomB = Point(innerB, bottom);
                    if (Vector3.Cross(bottomA - bottomCenter, bottomB - bottomCenter).y < 0)
                        Body.Quad(bottomCenter, bottomA, bottomB, bottomB, Vector3.down, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                    else Body.Quad(bottomCenter, bottomB, bottomA, bottomA, Vector3.down, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                    color.a = .5f;
                    for (int vertex = 0; vertex < 4; vertex++) Body.Colors.Add(color);
                }
            }

            void BodyFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
            {
                var normal = Vector3.Cross(b - a, c - a).normalized;
                if (_uAxis.x * _vAxis.z - _vAxis.x * _uAxis.z > 0)
                    Body.Quad(a, b, c, d, normal, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                else Body.Quad(a, d, c, b, -normal, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                color.a = .5f;
                for (int i = 0; i < 4; i++) Body.Colors.Add(color);
            }

            internal float SurfaceHeight(Vector3 position) => Mathf.Max(SurfaceHeight(Top, position), SurfaceHeight(Body, position));

            static float SurfaceHeight(Geometry geometry, Vector3 position)
            {
                float height = 0;
                for (int i = 0; i < geometry.Indices.Count; i += 3)
                {
                    int index = geometry.Indices[i];
                    if (geometry.Normals[index].y <= 0) continue;
                    var a = geometry.Vertices[index];
                    var b = geometry.Vertices[geometry.Indices[i + 1]] - a;
                    var c = geometry.Vertices[geometry.Indices[i + 2]] - a;
                    float determinant = b.x * c.z - c.x * b.z;
                    if (Mathf.Abs(determinant) < .0000001f) continue;
                    var offset = position - a;
                    float u = (offset.x * c.z - c.x * offset.z) / determinant;
                    float v = (b.x * offset.z - offset.x * b.z) / determinant;
                    if (u >= -.001f && v >= -.001f && u + v <= 1.001f)
                        height = Mathf.Max(height, a.y + b.y * u + c.y * v);
                }
                return height;
            }
        }

        readonly ObjectPresentation _objectPresentation;

        sealed partial class ObjectPresentation
        {
            readonly MapView _view;

            internal ObjectPresentation(MapView view)
            {
                _view = view;
            }

            internal void UpdateGeneratedBuildingSettings()
            {
                bool enabled = Rim3DSettings.Current.AutoGenerateBuildings;
                if (_view._lastAutoGenerateBuildings == enabled) return;
                _view._lastAutoGenerateBuildings = enabled;
                foreach (var key in _view.chunkRoots.Keys) _view.dirtyChunks.Add(key);
            }

            internal Color32[] ReadGeneratedBuildingPixels(Material material)
            {
                if (_view._generatedBuildingPixels.TryGetValue(material, out var cached)) return cached;
                var previous = RenderTexture.active;
                RenderTexture sample = null;
                Texture2D pixels = null;
                Color32[] colors = null;
                try
                {
                    sample = RenderTexture.GetTemporary(64, 80, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(material.mainTexture, sample, material.mainTextureScale, material.mainTextureOffset);
                    RenderTexture.active = sample;
                    pixels = new Texture2D(64, 80, TextureFormat.RGBA32, false);
                    pixels.ReadPixels(new Rect(0, 0, 64, 80), 0, 0, false);
                    colors = pixels.GetPixels32();
                }
                catch (Exception error) { Log.Warning("RimVision generated building: " + error.Message); }
                finally
                {
                    RenderTexture.active = previous;
                    if (sample != null) RenderTexture.ReleaseTemporary(sample);
                    if (pixels != null) UnityEngine.Object.Destroy(pixels);
                }
                _view._generatedBuildingPixels.Add(material, colors);
                return colors;
            }

            internal GeneratedBuilding GetGeneratedBuilding(Thing thing, Graphic graphic, Material source)
            {
                if (!Rim3DSettings.Current.AutoGenerateBuildings || thing.def.category != ThingCategory.Building ||
                    thing is Building_Door || graphic is Graphic_Linked || IsGroundSpot(thing.def) ||
                    IsConduit(thing.def) || IsWallTorch(thing) || IsDropPod(thing) ||
                    thing.def.defName == "Vent" || thing.def.defName == "NatureShrine_Large" ||
                    !HasGeneratedBuildingBody(thing) || source == null || source.mainTexture == null ||
                    source.mainTexture == BaseContent.BadTex) return null;
                var mesh = graphic.MeshAt(thing.Rotation);
                if (mesh == null || !mesh.isReadable) return null;
                var key = (thing.def, source, mesh);
                if (_view._generatedBuildings.TryGetValue(key, out var cached)) return cached;
                var material = _view.ConvertMaterial(source, false);
                var pixels = _view.ReadGeneratedBuildingPixels(material);
                float height = thing is Building_Bed ? .45f : thing.def.building?.isSittable == true ? .55f
                    : thing.def.surfaceType == SurfaceType.Eat ? .75f : Mathf.Lerp(.4f, 1.05f, Mathf.Clamp01(thing.def.fillPercent));
                var shape = thing is Building_Bed ? GeneratedBuildingShape.Solid
                    : thing.def.building?.isSittable == true ? GeneratedBuildingShape.Seat
                    : thing.def.surfaceType == SurfaceType.Eat ? GeneratedBuildingShape.Table : GeneratedBuildingShape.Solid;
                var generated = pixels == null ? null : GeneratedBuilding.Create(mesh, pixels, material.color, height, thing.def, shape);
                _view._generatedBuildings.Add(key, generated);
                return generated;
            }

            internal bool TryDrawGeneratedBuilding(Thing thing, Graphic graphic, Material source, Vector3 center)
            {
                var generated = _view.GetGeneratedBuilding(thing, graphic, source);
                if (generated == null) return false;
                var offset = graphic.DrawOffset(thing.Rotation);
                offset.y = .015f;
                var rotation = graphic.QuatFromRot(thing.Rotation);
                if (generated.Top.Vertices.Count > 0)
                    _view.Batch(_view.ConvertMaterial(source, false), center).Append(generated.Top, center + offset, rotation);
                if (_view._generatedBuildingSideMaterial == null)
                    _view._generatedBuildingSideMaterial = _view.WeaponVolumeMaterial(_view.NewMaterial(Texture2D.whiteTexture, Color.white, true));
                _view.Batch(_view._generatedBuildingSideMaterial, center).Append(generated.Body, center + offset, rotation);
                return true;
            }

            internal float GeneratedBuildingSurfaceHeight(Thing thing, Graphic graphic, Vector3 position)
            {
                var generated = _view.GetGeneratedBuilding(thing, graphic, graphic.MatAt(thing.Rotation, thing));
                if (generated == null) return 0;
                var offset = graphic.DrawOffset(thing.Rotation);
                offset.y = .015f;
                var local = Quaternion.Inverse(graphic.QuatFromRot(thing.Rotation)) *
                    (position - thing.TrueCenter() - offset);
                float height = generated.SurfaceHeight(local);
                return height > 0 ? height + offset.y : 0;
            }

        }
    }
}
