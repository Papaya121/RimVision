using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {

        readonly Dictionary<Material, Material> doorPanels = new Dictionary<Material, Material>();
        readonly Dictionary<Color, Material> woodFenceMaterials = new Dictionary<Color, Material>();

        Material wallTorchWood, wallTorchIron, wallTorchWrap;
        static readonly Vector3 WallTorchBase = new Vector3(0, .94f, .35f);
        static readonly Quaternion WallTorchTilt = Quaternion.Euler(-12, 0, 0);
        static readonly Vector3 WallTorchFlame = WallTorchBase + WallTorchTilt * new Vector3(0, .61f, 0);
        static readonly Geometry WallTorchHandle = WallTorchPrism(0, .46f, .055f, .075f);
        static readonly Geometry WallTorchHead = WallTorchPrism(.43f, .18f, .105f, .12f);
        static readonly Geometry WallTorchMount = CreateWallTorchMount();

        Material WallDamageMaterial(Material source) => _worldGeometry.WallDamageMaterial(source);

        void DrawWallDamage(Building wall, Geometry surface, int start, Vector3 center, float height) => _worldGeometry.DrawWallDamage(wall, surface, start, center, height);

        static Vector2 WallDamageUv(Vector3 point, Vector3 normal, float height, Vector2 rotation, Vector2 offset, float scale)
        {
            var uv = normal.y > .5f ? new Vector2(point.x, point.z)
                : new Vector2(Vector3.Dot(point, Vector3.Cross(Vector3.up, normal)), point.y / height - .5f);
            uv = (uv - offset) / scale;
            return new Vector2(uv.x * rotation.x - uv.y * rotation.y,
                uv.x * rotation.y + uv.y * rotation.x) + Vector2.one * .5f;
        }

        void DrawWall(Geometry geometry, Vector3 center, float height, int faces, IntVec3 cell) => _worldGeometry.DrawWall(geometry, center, height, faces, cell);

        bool WallDoorNeighbor(IntVec3 cell) => _worldGeometry.WallDoorNeighbor(cell);

        static Vector3 WallOutlinePoint(int index, int corners)
        {
            const float bevel = .045f;
            float southWest = (corners & 1) != 0 ? bevel : 0;
            float northWest = (corners & 2) != 0 ? bevel : 0;
            float northEast = (corners & 4) != 0 ? bevel : 0;
            float southEast = (corners & 8) != 0 ? bevel : 0;
            switch (index)
            {
                case 0: return new Vector3(.5f - southEast, 0, -.5f);
                case 1: return new Vector3(-.5f + southWest, 0, -.5f);
                case 2: return new Vector3(-.5f, 0, -.5f + southWest);
                case 3: return new Vector3(-.5f, 0, .5f - northWest);
                case 4: return new Vector3(-.5f + northWest, 0, .5f);
                case 5: return new Vector3(.5f - northEast, 0, .5f);
                case 6: return new Vector3(.5f, 0, .5f - northEast);
                default: return new Vector3(.5f, 0, -.5f + southEast);
            }
        }

        void ApplyWoodSurface(Material material) => _worldGeometry.ApplyWoodSurface(material);

        Material WoodFenceMaterial(Thing thing) => _worldGeometry.WoodFenceMaterial(thing);
        static bool IsWallTorch(BuildableDef def) => def != null && def.defName == "TorchWallLamp";
        static bool IsWallTorch(Thing thing) => IsWallTorch(thing.def);
        static Geometry wallTorchOutline;

        static Geometry WallTorchConstructionOutline()
        {
            if (wallTorchOutline != null) return wallTorchOutline;
            wallTorchOutline = new Geometry();
            OutlineBox(wallTorchOutline, new Vector3(0, .98f, .49f), new Vector3(.18f, .34f, .035f));
            OutlineBox(wallTorchOutline, new Vector3(0, 1.06f, .40f), new Vector3(.075f, .055f, .19f));
            var handle = new Geometry();
            OutlineBox(handle, Vector3.zero, new Vector3(.13f, .46f, .13f));
            wallTorchOutline.Append(handle, WallTorchBase, WallTorchTilt);
            var head = new Geometry();
            OutlineBox(head, new Vector3(0, .43f, 0), new Vector3(.24f, .18f, .24f));
            wallTorchOutline.Append(head, WallTorchBase, WallTorchTilt);
            return wallTorchOutline;
        }

        static Bounds WallTorchBounds(Thing thing)
        {
            var center = thing.TrueCenter();
            center.y = 0;
            var rotation = Quaternion.Euler(0, thing.Rotation.AsAngle, 0);
            var size = thing.Rotation.AsInt % 2 == 0 ? new Vector3(.30f, .70f, .40f) : new Vector3(.40f, .70f, .30f);
            return new Bounds(center + rotation * new Vector3(0, 1.25f, .32f), size);
        }

        static Vector3 WallTorchFirePosition(Thing thing)
        {
            var center = thing.TrueCenter();
            center.y = 0;
            return center + Quaternion.Euler(0, thing.Rotation.AsAngle, 0) * WallTorchFlame;
        }

        void DrawWallTorch(Thing thing, Vector3 center) => _worldGeometry.DrawWallTorch(thing, center);

        static Geometry CreateWallTorchMount()
        {
            var geometry = new Geometry();
            var uv = new Rect(0, 0, 1, 1);
            WallTorchBox(geometry, new Vector3(0, .98f, .49f), new Vector3(.18f, .34f, .035f), uv);
            WallTorchBox(geometry, new Vector3(0, 1.06f, .40f), new Vector3(.075f, .055f, .19f), uv);
            geometry.Append(WallTorchPrism(.14f, .05f, .078f, .078f), Vector3.zero, Quaternion.identity);
            geometry.Append(WallTorchPrism(.44f, .035f, .11f, .113f), Vector3.zero, Quaternion.identity);
            geometry.Append(WallTorchPrism(.54f, .025f, .121f, .123f), Vector3.zero, Quaternion.identity);
            return geometry;
        }

        static void WallTorchBox(Geometry geometry, Vector3 bottom, Vector3 size, Rect uv)
        {
            geometry.Box(bottom, size, uv);
            var x = Vector3.right * (size.x * .5f);
            var z = Vector3.forward * (size.z * .5f);
            geometry.Quad(bottom - x - z, bottom + x - z, bottom + x + z, bottom - x + z,
                Vector3.down, Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
        }

        static Geometry WallTorchPrism(float bottom, float height, float lowerRadius, float upperRadius)
        {
            var geometry = new Geometry();
            var lower = new Vector3[6];
            var upper = new Vector3[6];
            for (int i = 0; i < 6; i++)
            {
                float angle = i * Mathf.PI / 3;
                var radial = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                lower[i] = WallTorchBase + WallTorchTilt * (Vector3.up * bottom + radial * lowerRadius);
                upper[i] = WallTorchBase + WallTorchTilt * (Vector3.up * (bottom + height) + radial * upperRadius);
            }
            for (int i = 0; i < 6; i++)
            {
                int next = (i + 1) % 6;
                var normal = Vector3.Cross(upper[i] - lower[i], upper[next] - lower[i]).normalized;
                geometry.Quad(lower[i], upper[i], upper[next], lower[next], normal,
                    Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
            }
            var up = WallTorchTilt * Vector3.up;
            for (int i = 0; i < 2; i++)
            {
                int b = 1 + i * 2;
                geometry.Quad(upper[0], upper[b + 2], upper[b + 1], upper[b], up,
                    Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
                geometry.Quad(lower[0], lower[b], lower[b + 1], lower[b + 2], -up,
                    Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
            }
            return geometry;
        }

        Material ResourceRockMaterial(Material atlasSource) => _worldGeometry.ResourceRockMaterial(atlasSource);

        Texture2D WallBrickTexture(Texture atlas) => _worldGeometry.WallBrickTexture(atlas);

        void DisposeWallTextures() => _worldGeometry.DisposeWallTextures();
        static readonly Func<Building_Door, float> DoorOpenFraction = (Func<Building_Door, float>)Delegate.CreateDelegate(
            typeof(Func<Building_Door, float>), typeof(Building_Door).GetProperty("OpenPct", BindingFlags.Instance | BindingFlags.NonPublic).GetGetMethod(true));

        Material BuildingSideMaterial(Material source) => _worldGeometry.BuildingSideMaterial(source);

        Building DoorSideWall(Building_Door door, IntVec3 direction) => _worldGeometry.DoorSideWall(door, direction);

        void CreateDoor(Building_Door door, Material source, Vector3 center) => _worldGeometry.CreateDoor(door, source, center);

        void AnimateDoor(Building_Door door, Transform frame) => _worldGeometry.AnimateDoor(door, frame);

        void CreateFenceGate(Building_Door door, Material source, Vector3 center) => _worldGeometry.CreateFenceGate(door, source, center);

        readonly WorldGeometry _worldGeometry;

        sealed partial class WorldGeometry
        {
            readonly MapView _view;

            internal WorldGeometry(MapView view)
            {
                _view = view;
            }

            readonly Dictionary<Material, Material> _buildingSides = new Dictionary<Material, Material>();

            readonly Dictionary<Material, Material> _resourceRockMaterials = new Dictionary<Material, Material>();

            Material _doorHardware;

            Material _autodoorIndicator;

            readonly Dictionary<Texture, Texture2D> _wallBrickTextures = new Dictionary<Texture, Texture2D>();

            readonly Dictionary<Material, Material> _wallDamageMaterials = new Dictionary<Material, Material>();

            struct WallDamageSurface
            {
                internal Building Wall;
                internal Geometry Surface;
                internal Vector3 Center;
                internal float Height;
            }

            readonly Dictionary<Vector2Int, List<WallDamageSurface>> _wallDamageSurfaces = new Dictionary<Vector2Int, List<WallDamageSurface>>();
            readonly Dictionary<Vector2Int, GameObject> _wallDamageRoots = new Dictionary<Vector2Int, GameObject>();
            readonly HashSet<Vector2Int> _dirtyWallDamage = new HashSet<Vector2Int>();
            readonly Dictionary<Material, Geometry> _wallDamageBatches = new Dictionary<Material, Geometry>();

            internal void MarkWallDamage(IntVec3 cell)
            {
                if (cell.InBounds(_view.map)) _dirtyWallDamage.Add(new Vector2Int(cell.x / ChunkSize, cell.z / ChunkSize));
            }

            internal void UpdateWallDamage()
            {
                if (_dirtyWallDamage.Count == 0) return;
                Vector2Int key;
                using (var iterator = _dirtyWallDamage.GetEnumerator()) { iterator.MoveNext(); key = iterator.Current; }
                if (_view.dirtyChunks.Contains(key)) { _dirtyWallDamage.Remove(key); return; }
                RebuildWallDamage(key);
            }

            internal void RebuildWallDamage(Vector2Int key)
            {
                _dirtyWallDamage.Remove(key);
                if (_wallDamageRoots.TryGetValue(key, out var oldRoot))
                {
                    foreach (var filter in oldRoot.GetComponentsInChildren<MeshFilter>(true))
                    {
                        _view._outlines.UnregisterOutlineRenderer(filter.GetComponent<MeshRenderer>());
                        _view.meshes.Remove(filter.sharedMesh);
                        UnityEngine.Object.Destroy(filter.sharedMesh);
                    }
                    oldRoot.SetActive(false);
                    UnityEngine.Object.Destroy(oldRoot);
                    _wallDamageRoots.Remove(key);
                }
                if (!_wallDamageSurfaces.TryGetValue(key, out var surfaces) || !_view.chunkRoots.TryGetValue(key, out var chunkRoot)) return;
                var previousParent = _view.geometryParent;
                try
                {
                    foreach (var entry in surfaces)
                        if (entry.Wall.Spawned && !entry.Wall.Destroyed && !_view.map.fogGrid.IsFogged(entry.Wall.Position))
                            AppendWallDamage(entry.Wall, entry.Surface, 0, entry.Center, entry.Height);
                    if (_wallDamageBatches.Count == 0) return;
                    var damageRoot = new GameObject("Wall damage") { layer = Layer, hideFlags = HideFlags.DontSave };
                    damageRoot.transform.SetParent(chunkRoot.transform, false);
                    _wallDamageRoots.Add(key, damageRoot);
                    _view.geometryParent = damageRoot.transform;
                    foreach (var batch in _wallDamageBatches) CreateMesh(batch.Key, batch.Value, "Wall damage");
                }
                finally { _view.geometryParent = previousParent; _wallDamageBatches.Clear(); }
            }

            void ForgetWallDamage(Vector2Int key)
            {
                _wallDamageSurfaces.Remove(key);
                _wallDamageRoots.Remove(key);
                _dirtyWallDamage.Remove(key);
            }

            internal void ClearWallDamage()
            {
                _wallDamageSurfaces.Clear();
                _wallDamageRoots.Clear();
                _dirtyWallDamage.Clear();
                _wallDamageBatches.Clear();
            }

            internal Material WallDamageMaterial(Material source)
            {
                if (_wallDamageMaterials.TryGetValue(source, out var material)) return material;
                material = new Material(_view.ConvertMaterial(source, false))
                { hideFlags = HideFlags.DontSave, renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 5 };
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0);
                material.SetFloat("_Opaque", 1);
                material.SetFloat("_Ghost", Mathf.Max(.0001f, material.color.a));
                material.SetFloat("_WallBevel", 1);
                material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
                material.SetInt("_StencilWriteMask", 0);
                material.SetOverrideTag("RenderType", "Transparent");
                _view.materials.Add(material);
                _wallDamageMaterials.Add(source, material);
                return material;
            }

            internal void DrawWallDamage(Building wall, Geometry surface, int start, Vector3 center, float height)
            {
                var data = wall.def.graphicData?.damageData;
                if (!wall.def.useHitPoints || !wall.def.drawDamagedOverlay || data?.enabled == false) return;
                var key = ChunkKey(center);
                if (!_wallDamageSurfaces.TryGetValue(key, out var surfaces))
                    _wallDamageSurfaces.Add(key, surfaces = new List<WallDamageSurface>());
                var copy = new Geometry();
                for (int v = start; v < surface.Vertices.Count; v++)
                {
                    copy.Vertices.Add(surface.Vertices[v]);
                    copy.Normals.Add(surface.Normals[v]);
                }
                surfaces.Add(new WallDamageSurface { Wall = wall, Surface = copy, Center = center, Height = height });
            }

            void AppendWallDamage(Building wall, Geometry surface, int start, Vector3 center, float height)
            {
                var data = wall.def.graphicData?.damageData;
                if (!wall.def.useHitPoints || !wall.def.drawDamagedOverlay || data?.enabled == false ||
                    wall.HitPoints >= wall.MaxHitPoints) return;
                var overlays = BuildingsDamageSectionLayerUtility.GetOverlays(wall);
                var scratches = BuildingsDamageSectionLayerUtility.GetScratchMats(wall);
                var random = new System.Random(wall.thingIDNumber);
                int end = surface.Vertices.Count;
                for (int i = 0; i < overlays.Count; i++)
                {
                    var overlay = overlays[i];
                    Material source;
                    int sides = 15;
                    float angle = 0;
                    var offset = Vector2.zero;
                    float scale = 1;
                    switch (overlay)
                    {
                        case DamageOverlay.Scratch:
                            if (scratches.Count == 0) continue;
                            source = scratches[random.Next(scratches.Count)];
                            scale = .5f;
                            offset = new Vector2((float)random.NextDouble() * .35f - .175f,
                                (float)random.NextDouble() * .35f - .175f);
                            angle = (float)random.NextDouble() * 360;
                            break;
                        case DamageOverlay.TopEdge: source = data?.edgeTopMat; sides = 2; break;
                        case DamageOverlay.RightEdge: source = data?.edgeRightMat; sides = 8; angle = 90; break;
                        case DamageOverlay.BotEdge: source = data?.edgeBotMat; sides = 1; angle = 180; break;
                        case DamageOverlay.LeftEdge: source = data?.edgeLeftMat; sides = 4; angle = 270; break;
                        case DamageOverlay.TopLeftCorner: source = data?.cornerTLMat; sides = 6; break;
                        case DamageOverlay.TopRightCorner: source = data?.cornerTRMat; sides = 10; angle = 90; break;
                        case DamageOverlay.BotRightCorner: source = data?.cornerBRMat; sides = 9; angle = 180; break;
                        case DamageOverlay.BotLeftCorner: source = data?.cornerBLMat; sides = 5; angle = 270; break;
                        default: continue;
                    }
                    if (source == null) continue;
                    var material = WallDamageMaterial(source);
                    if (!_wallDamageBatches.TryGetValue(material, out var target))
                        _wallDamageBatches.Add(material, target = new Geometry());
                    for (int v = start; v < end; v += 4)
                    {
                        var normal = surface.Normals[v];
                        bool top = normal.y > .5f;
                        int face = (normal.z < -.5f ? 1 : 0) | (normal.z > .5f ? 2 : 0) |
                            (normal.x < -.5f ? 4 : 0) | (normal.x > .5f ? 8 : 0);
                        if (!top && (face & sides) == 0) continue;
                        float rotation = top || overlay == DamageOverlay.Scratch ? angle : 0;
                        if (!top && overlay != DamageOverlay.Scratch && (sides == 5 || sides == 6 || sides == 9 || sides == 10))
                        {
                            var corner = new Vector3((sides & 4) != 0 ? -.5f : .5f, 0,
                                (sides & 2) != 0 ? .5f : -.5f);
                            if (Vector3.Dot(corner, Vector3.Cross(Vector3.up, normal)) > 0) rotation = 90;
                        }
                        var lift = normal * (.002f + i * .0002f);
                        float radians = rotation * Mathf.Deg2Rad;
                        var uvRotation = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
                        var a = surface.Vertices[v]; var b = surface.Vertices[v + 1];
                        var c = surface.Vertices[v + 2]; var d = surface.Vertices[v + 3];
                        target.Quad(a + lift, b + lift, c + lift, d + lift, normal,
                            WallDamageUv(a - center, normal, height, uvRotation, offset, scale),
                            WallDamageUv(b - center, normal, height, uvRotation, offset, scale),
                            WallDamageUv(c - center, normal, height, uvRotation, offset, scale),
                            WallDamageUv(d - center, normal, height, uvRotation, offset, scale));
                    }
                }
            }

            internal void DrawWall(Geometry geometry, Vector3 center, float height, int faces, IntVec3 cell)
            {
                int bevelFaces = faces;
                if (_view.WallDoorNeighbor(cell + IntVec3.South)) bevelFaces &= ~1;
                if (_view.WallDoorNeighbor(cell + IntVec3.North)) bevelFaces &= ~2;
                if (_view.WallDoorNeighbor(cell + IntVec3.West)) bevelFaces &= ~4;
                if (_view.WallDoorNeighbor(cell + IntVec3.East)) bevelFaces &= ~8;
                int bevelCorners = 0;
                if ((bevelFaces & 5) == 5 && !_view.SolidNeighbor(cell + new IntVec3(-1, 0, -1))) bevelCorners |= 1;
                if ((bevelFaces & 6) == 6 && !_view.SolidNeighbor(cell + new IntVec3(-1, 0, 1))) bevelCorners |= 2;
                if ((bevelFaces & 10) == 10 && !_view.SolidNeighbor(cell + new IntVec3(1, 0, 1))) bevelCorners |= 4;
                if ((bevelFaces & 9) == 9 && !_view.SolidNeighbor(cell + new IntVec3(1, 0, -1))) bevelCorners |= 8;
                if (bevelCorners == 0)
                {
                    geometry.Box(center, new Vector3(1, height, 1), new Rect(0, 0, 1, height),
                        new Rect(0, 0, 1, 1), faces, false);
                    return;
                }
                var up = Vector3.up * height;
                var topCenter = center + up;
                int start = geometry.Vertices.Count;
                for (int i = 0; i < 8; i++)
                {
                    var a = center + WallOutlinePoint(i, bevelCorners);
                    var b = center + WallOutlinePoint((i + 1) % 8, bevelCorners);
                    if ((a - b).sqrMagnitude < 1e-8f) continue;
                    int face = i == 0 ? 1 : i == 2 ? 4 : i == 4 ? 2 : i == 6 ? 8 : 0;
                    if (face == 0 || (faces & face) != 0)
                    {
                        var normal = Vector3.Cross(b - a, up).normalized;
                        geometry.Quad(a, b, b + up, a + up, normal,
                            Vector2.zero, Vector2.right, new Vector2(1, height), new Vector2(0, height));
                    }
                    geometry.Quad(topCenter, a + up, b + up, b + up, Vector3.up,
                        new Vector2(topCenter.x, topCenter.z), new Vector2(a.x, a.z),
                        new Vector2(b.x, b.z), new Vector2(b.x, b.z));
                }
                geometry.SetOutlineBounds(start, new Bounds(center + up * .5f, new Vector3(1, height, 1)));
            }

            internal bool WallDoorNeighbor(IntVec3 cell) => cell.InBounds(_view.map) && cell.GetEdifice(_view.map) is Building_Door;

            internal void ApplyWoodSurface(Material material)
            {
                material.mainTexture = ModAssets.LoadTexture("textures/terrain/surfaces/WoodFloor") ?? Texture2D.whiteTexture;
                material.mainTextureScale = Vector2.one;
                material.mainTextureOffset = Vector2.zero;
                material.SetFloat("_SurfaceTile", 1);
                // Preserve the original artwork, with boards three times larger.
                material.SetVector("_SurfaceScale", new Vector4(1f / 3f, 1f / 3f, 0, 0));
            }

            internal Material WoodFenceMaterial(Thing thing)
            {
                var color = thing.DrawColor * new Color(.72f, .72f, .72f, 1);
                color.a = 1;
                if (!_view.woodFenceMaterials.TryGetValue(color, out var material))
                {
                    material = _view.NewMaterial(Texture2D.whiteTexture, color, true);
                    _view.ApplyWoodSurface(material);
                    material.SetFloat("_Cull", 2);
                    _view.woodFenceMaterials.Add(color, material);
                }
                return material;
            }

            internal void DrawWallTorch(Thing thing, Vector3 center)
            {
                if (_view.wallTorchWood == null)
                {
                    _view.wallTorchWood = _view.NewMaterial(Texture2D.whiteTexture, new Color(.36f, .20f, .085f), true);
                    _view.wallTorchIron = _view.NewMaterial(Texture2D.whiteTexture, new Color(.18f, .20f, .22f), true);
                    _view.wallTorchWrap = _view.NewMaterial(Texture2D.whiteTexture, new Color(.55f, .39f, .20f), true);
                }
                // The supporting wall is in the next cell along Rotation, with its
                // near face at local z=.5. Keep the head and flame on the room side.
                var rotation = Quaternion.Euler(0, thing.Rotation.AsAngle, 0);
                _view.Batch(_view.wallTorchWood, center).Append(WallTorchHandle, center, rotation);
                _view.Batch(_view.wallTorchIron, center).Append(WallTorchMount, center, rotation);
                _view.Batch(_view.wallTorchWrap, center).Append(WallTorchHead, center, rotation);
            }

            internal Material ResourceRockMaterial(Material atlasSource)
            {
                if (_resourceRockMaterials.TryGetValue(atlasSource, out var material)) return material;
                var tile = MaterialAtlasPool.SubMaterialFromAtlas(atlasSource, (LinkDirections)15);
                // Keep the ore's original artwork, recolor mask and both tints.
                material = new Material(_view.ConvertMaterial(tile, true)) { hideFlags = HideFlags.DontSave };
                // Repeat only the solid interior of the fully linked tile. Its inset
                // excludes top-down bevels and neighboring atlas cells on vertical faces.
                material.mainTextureOffset = tile.mainTextureOffset + tile.mainTextureScale * .125f;
                material.mainTextureScale = tile.mainTextureScale * .75f;
                material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
                material.SetFloat("_SurfaceTile", 1);
                material.SetFloat("_WallSurface", 1);
                _view.materials.Add(material);
                _resourceRockMaterials.Add(atlasSource, material);
                return material;
            }

            internal Texture2D WallBrickTexture(Texture atlas)
            {
                if (_wallBrickTextures.TryGetValue(atlas, out var texture)) return texture;
                var previous = RenderTexture.active;
                RenderTexture crop = null;
                Texture2D created = null;
                try
                {
                    // Native 320x320 atlas: two brick rows, entirely inside one tile.
                    // Normalized coordinates also work with scaled copies of that atlas.
                    crop = RenderTexture.GetTemporary(64, 16, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(atlas, crop, new Vector2(64f / 320, 16f / 320), new Vector2(176f / 320, 18f / 320));
                    RenderTexture.active = crop;
                    created = new Texture2D(64, 16, TextureFormat.RGBA32, true)
                    {
                        name = "RimVision wall bricks", hideFlags = HideFlags.DontSave,
                        wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4
                    };
                    created.ReadPixels(new Rect(0, 0, 64, 16), 0, 0, false);
                    created.Apply(true, true);
                    _wallBrickTextures.Add(atlas, created);
                    return created;
                }
                catch
                {
                    if (created != null) UnityEngine.Object.Destroy(created);
                    throw;
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (crop != null) RenderTexture.ReleaseTemporary(crop);
                }
            }

            internal void DisposeWallTextures()
            {
                _wallDamageMaterials.Clear();
                foreach (var texture in _wallBrickTextures.Values) UnityEngine.Object.Destroy(texture);
                _wallBrickTextures.Clear();
                _resourceRockMaterials.Clear();
            }

            internal Material BuildingSideMaterial(Material source)
            {
                if (_buildingSides.TryGetValue(source, out var material)) return material;
                var color = source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
                // Native furniture artwork is a top view. Give its volume a tinted solid edge.
                material = _view.NewMaterial(Texture2D.whiteTexture, color * new Color(.72f, .72f, .72f, 1), true);
                _buildingSides.Add(source, material);
                return material;
            }

            internal Building DoorSideWall(Building_Door door, IntVec3 direction)
            {
                var cell = door.Position + direction;
                while (cell.InBounds(_view.map))
                {
                    var building = cell.GetEdifice(_view.map);
                    // Treat aligned neighboring doors as one continuous opening.
                    if (building is Building_Door adjacent &&
                        adjacent.Rotation.AsInt % 2 == door.Rotation.AsInt % 2)
                    {
                        cell += direction;
                        continue;
                    }
                    return _view.SolidNeighbor(cell) ? building : null;
                }
                return null;
            }

            internal void CreateDoor(Building_Door door, Material source, Vector3 center)
            {
                if (door.def.defName == "FenceGate")
                {
                    _view.CreateFenceGate(door, source, center);
                    return;
                }
                var frame = new GameObject("Door frame: " + door.LabelShort) { layer = Layer, hideFlags = HideFlags.DontSave };
                frame.transform.SetParent(_view.geometryParent, false);
                frame.transform.localPosition = center;
                frame.transform.localRotation = Quaternion.Euler(0, door.Rotation.AsAngle, 0);
                var previousParent = _view.geometryParent;
                _view.geometryParent = frame.transform;
                var size = door.def.size;
                float width = size.x;
                bool automatic = door.def.defName == "Autodoor";
                float frameDepth = automatic ? .30f : .22f;
                float frameWidth = automatic ? .11f : .07f;
                float openingWidth = width - 2 * (frameWidth + .012f);
                float panelWidth = openingWidth * .5f - .004f;
                float panelHeight = automatic ? 1.46f : 1.59f;
                float panelDepth = .12f;
                float handleDepth = .115f;
                if (!_view.doorPanels.TryGetValue(source, out var panelMaterial))
                {
                    panelMaterial = new Material(_view.BuildingSideMaterial(source)) { hideFlags = HideFlags.DontSave };
                    if (door.Stuff != null && door.Stuff.defName.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _view.ApplyWoodSurface(panelMaterial);
                    }
                    _view.materials.Add(panelMaterial); _view.doorPanels.Add(source, panelMaterial);
                }
                if (_doorHardware == null) _doorHardware = _view.NewMaterial(Texture2D.whiteTexture, new Color(.25f,.29f,.32f), true);
                var trim = new Geometry();
                // Separate joined solids: no overlapping top faces or faces shared with adjacent doors/walls.
                trim.Box(new Vector3(-width * .5f + frameWidth * .5f + .006f, .012f, 0),
                    new Vector3(frameWidth, 1.618f, frameDepth), new Rect(0,0,1,1));
                trim.Box(new Vector3(width * .5f - frameWidth * .5f - .006f, .012f, 0),
                    new Vector3(frameWidth, 1.618f, frameDepth), new Rect(0,0,1,1));
                trim.Box(new Vector3(0, 1.64f, 0), new Vector3(openingWidth, .06f, frameDepth), new Rect(0,0,1,1));
                _view.CreateMesh(automatic ? _doorHardware : _view.BuildingSideMaterial(source), trim, "Door trim");
                // Two rigid sliding panels follow the native opening percentage, without scaling UVs.
                for (int side = -1; side <= 1; side += 2)
                {
                    var geometry = new Geometry();
                    geometry.Box(new Vector3(0, .015f, 0), new Vector3(panelWidth, panelHeight, panelDepth),
                        new Rect(0,0,panelWidth,panelHeight));
                    var panel = _view.CreateMesh(panelMaterial, geometry, "Sliding panel").transform;
                    panel.localPosition = new Vector3(side * openingWidth * .25f, 0, 0);
                    _view.geometryParent = panel;
                    var handle = new Geometry();
                    if (automatic)
                    {
                        for (int front = -1; front <= 1; front += 2)
                        {
                            float depth = front * .095f;
                            handle.Box(new Vector3(-side * (panelWidth * .5f - .035f), .22f, depth),
                                new Vector3(.035f, 1.14f, .03f), new Rect(0,0,1,1));
                            foreach (float height in new[] { .15f, 1.40f })
                                handle.Box(new Vector3(0, height, depth),
                                    new Vector3(Mathf.Max(.05f, panelWidth - .125f), .045f, .03f), new Rect(0,0,1,1));
                        }
                    }
                    else
                    {
                        handle.Box(new Vector3(-side * panelWidth * .35f, .7f, -handleDepth), new Vector3(.035f,.22f,.055f), new Rect(0,0,1,1));
                        handle.Box(new Vector3(-side * panelWidth * .35f, .7f, handleDepth), new Vector3(.035f,.22f,.055f), new Rect(0,0,1,1));
                    }
                    _view.CreateMesh(_doorHardware, handle, automatic ? "Autodoor panel edging" : "Door handles");
                    _view.geometryParent = frame.transform;
                }
                if (automatic)
                {
                    // Append fixed hardware after both panels: their animation indices stay unchanged.
                    var drive = new Geometry();
                    drive.Box(new Vector3(0, 1.49f, 0), new Vector3(openingWidth - .04f, .13f, .36f), new Rect(0,0,1,1));
                    for (int front = -1; front <= 1; front += 2)
                        drive.Box(new Vector3(width * .5f - frameWidth * .5f - .006f, .87f, front * (frameDepth * .5f + .045f)),
                            new Vector3(.085f, .18f, .055f), new Rect(0,0,1,1));
                    _view.CreateMesh(_doorHardware, drive, "Autodoor drive and sensors");
                    if (_autodoorIndicator == null)
                    {
                        _autodoorIndicator = _view.NewMaterial(Texture2D.whiteTexture, new Color(.35f, .82f, .77f), true);
                        _autodoorIndicator.SetFloat("_Unlit", 1);
                    }
                    var indicator = new Geometry();
                    for (int front = -1; front <= 1; front += 2)
                        indicator.Box(new Vector3(0, 1.535f, front * .20f),
                            new Vector3(openingWidth * .4f, .025f, .016f), new Rect(0,0,1,1));
                    _view.CreateMesh(_autodoorIndicator, indicator, "Autodoor indicator");
                }
                var sideDirection = door.Rotation.AsInt % 2 == 0 ? IntVec3.East : IntVec3.North;
                var leftWall = _view.DoorSideWall(door, sideDirection);
                var rightWall = _view.DoorSideWall(door, new IntVec3(-sideDirection.x, 0, -sideDirection.z));
                if (leftWall != null && rightWall != null)
                {
                    var wallGraphic = leftWall.Graphic;
                    var lintelMaterial = wallGraphic is Graphic_Linked linked
                        ? _view.LinkedBuildingMaterial(linked.SubGraphic.MatSingleFor(leftWall))
                        : _view.BuildingSideMaterial(source);
                    var lintel = new Geometry();
                    lintel.Box(new Vector3(0, 1.72f, 0), new Vector3(width, .28f, size.z),
                        new Rect(0, 0, width, .28f), new Rect(0, 0, width, size.z), 3, false);
                    // Box omits the bottom; the lintel is visible from underneath.
                    float halfWidth = width * .5f, halfDepth = size.z * .5f;
                    lintel.Quad(new Vector3(-halfWidth, 1.72f, -halfDepth),
                        new Vector3(halfWidth, 1.72f, -halfDepth),
                        new Vector3(halfWidth, 1.72f, halfDepth),
                        new Vector3(-halfWidth, 1.72f, halfDepth), Vector3.down,
                        Vector2.zero, new Vector2(width, 0),
                        new Vector2(width, size.z), new Vector2(0, size.z));
                    // Append after the panels to preserve their animation indices.
                    _view.CreateMesh(lintelMaterial, lintel, "Door wall lintel");
                }
                _view.geometryParent = previousParent;
                _view.doors.Add(door, frame.transform);
            }

            internal void AnimateDoor(Building_Door door, Transform frame)
            {
                float width = door.def.size.x;
                float open = Mathf.SmoothStep(0, 1, DoorOpenFraction(door));
                if (door.def.defName == "FenceGate")
                {
                    frame.GetChild(1).localRotation = Quaternion.Euler(0, -90f * open, 0);
                    return;
                }
                // Child zero is the trim; remaining children are rigid panels.
                float frameWidth = door.def.defName == "Autodoor" ? .11f : .07f;
                float openingWidth = width - 2 * (frameWidth + .012f);
                for (int i = 1; i <= 2; i++)
                    frame.GetChild(i).localPosition = new Vector3((i == 1 ? -1 : 1) * (openingWidth * .25f + open * width * .5f), 0, 0);
            }

            internal void CreateFenceGate(Building_Door door, Material source, Vector3 center)
            {
                var frame = new GameObject("Fence gate: " + door.LabelShort) { layer = Layer, hideFlags = HideFlags.DontSave };
                frame.transform.SetParent(_view.geometryParent, false);
                frame.transform.localPosition = center;
                frame.transform.localRotation = Quaternion.Euler(0, door.Rotation.AsAngle, 0);
                var previousParent = _view.geometryParent;
                _view.geometryParent = frame.transform;
                float width = door.def.size.x;
                float hingeX = -width * .5f + .13f;
                float leafWidth = width - .26f;
                Material material;
                if (door.Stuff != null && door.Stuff.defName.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0)
                    material = _view.WoodFenceMaterial(door);
                else if (!_view.doorPanels.TryGetValue(source, out material))
                {
                    material = new Material(_view.BuildingSideMaterial(source)) { hideFlags = HideFlags.DontSave };
                    _view.materials.Add(material);
                    _view.doorPanels.Add(source, material);
                }
                var posts = new Geometry();
                for (int side = -1; side <= 1; side += 2)
                    posts.Box(new Vector3(side * (width * .5f - .06f), 0, 0),
                        new Vector3(.12f, .6f, .20f), new Rect(0, 0, .12f, .6f));
                _view.CreateMesh(material, posts, "Gate posts");
                var geometry = new Geometry();
                foreach (float height in new[] { .20f, .40f })
                    geometry.Box(new Vector3(leafWidth * .5f, height, 0),
                        new Vector3(leafWidth, .10f, .09f), new Rect(0, 0, leafWidth, .1f));
                for (int side = 0; side <= 1; side++)
                    geometry.Box(new Vector3(side == 0 ? .035f : leafWidth - .035f, .16f, 0),
                        new Vector3(.07f, .39f, .10f), new Rect(0, 0, .07f, .39f));
                int braceStart = geometry.Vertices.Count;
                float braceRise = .22f;
                float braceLength = Mathf.Sqrt(leafWidth * leafWidth + braceRise * braceRise);
                geometry.Box(new Vector3(0, -.0225f, 0), new Vector3(braceLength, .045f, .07f),
                    new Rect(0, 0, braceLength, .045f));
                var braceRotation = Quaternion.Euler(0, 0, Mathf.Atan2(braceRise, leafWidth) * Mathf.Rad2Deg);
                for (int i = braceStart; i < geometry.Vertices.Count; i++)
                {
                    geometry.Vertices[i] = new Vector3(leafWidth * .5f, .35f, -.065f) + braceRotation * geometry.Vertices[i];
                    geometry.Normals[i] = braceRotation * geometry.Normals[i];
                }
                geometry.SetOutlineBounds(0, geometry.GetBounds());
                var leaf = _view.CreateMesh(material, geometry, "Swinging gate").transform;
                leaf.localPosition = new Vector3(hingeX, 0, 0);
                if (_doorHardware == null) _doorHardware = _view.NewMaterial(Texture2D.whiteTexture, new Color(.25f, .29f, .32f), true);
                _view.geometryParent = leaf;
                var latch = new Geometry();
                latch.Box(new Vector3(leafWidth - .07f, .43f, -.08f), new Vector3(.11f, .035f, .04f), new Rect(0, 0, 1, 1));
                _view.CreateMesh(_doorHardware, latch, "Gate latch");
                _view.geometryParent = previousParent;
                _view.doors.Add(door, frame.transform);
            }

        }
    }
}

