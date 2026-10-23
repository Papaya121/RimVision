using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using HarmonyLib;
using UnityEngine;
using RimWorld;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        OptionalModelLibrary optionalModels;
        readonly Dictionary<Material, Material> optionalModelMaterials = new Dictionary<Material, Material>();
        readonly Dictionary<Material, Material> optionalTwoSidedMaterials = new Dictionary<Material, Material>();
        readonly Dictionary<Color, Material> optionalSolidMaterials = new Dictionary<Color, Material>();
        readonly Dictionary<(Texture2D, Color, bool), Material> _optionalCustomMaterials = new Dictionary<(Texture2D, Color, bool), Material>();


        const int GroundCutoutStencil = 128;
        Material _groundCutoutMaterial;


        void UpdateAnimalPlantModelSettings() => _objectPresentation.UpdateAnimalPlantModelSettings();

        OptionalModelLibrary.Model ConstructionModel(BuildableDef def, string path = null) => _objectPresentation.ConstructionModel(def, path);

        int ConstructionLinks(OptionalModelLibrary.Model model, ThingDef def, IntVec3 position, Rot4 rotation) => _objectPresentation.ConstructionLinks(model, def, position, rotation);

        static Geometry ConstructionOutline(OptionalModelLibrary.Model model, ThingDef def, int links)
        {
            // Cache local contours; placement only changes the transform, and map chunks reuse the vertices.
            if (model.Outlines.TryGetValue(links, out var geometry)) return geometry;
            geometry = new Geometry();
            var edges = new HashSet<ulong>();
            var origin = new Vector3(def.size.x * .5f, -model.HeightOffset, def.size.z * .5f);
            foreach (var face in model.Faces)
            {
                if ((links & face.RequiredLinks) != face.RequiredLinks || (links & face.ForbiddenLinks) != 0) continue;
                for (int i = 0; i < face.Indices.Length; i++)
                {
                    int a = face.Indices[i], b = face.Indices[(i + 1) % face.Indices.Length];
                    ulong edge = ((ulong)(uint)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    if (edges.Add(edge)) geometry.OutlineLine(model.Vertices[a] - origin, model.Vertices[b] - origin, .025f);
                }
            }
            model.Outlines.Add(links, geometry);
            return geometry;
        }

        OptionalModelLibrary.Model ThingModel(Thing thing, Graphic graphic) => _objectPresentation.ThingModel(thing, graphic);

        float GroundThingSurfaceHeight(Vector3 position) => _objectPresentation.GroundThingSurfaceHeight(position);

        int OptionalModelLinks(Thing thing, Graphic graphic, OptionalModelLibrary.Model model) => _objectPresentation.OptionalModelLinks(thing, graphic, model);

        static Color OptionalModelTint(Thing thing, Graphic graphic, Rot4 rotation)
        {
            var source = graphic?.MatAt(rotation, thing);
            return source != null && source.HasProperty("_Color") ? source.GetColor("_Color") : thing.DrawColor;
        }

        static float OptionalModelScale(Thing thing, Graphic graphic)
        {
            if (thing is Plant plant)
            {
                var properties = plant.def.plant;
                float size = properties.IsTree && !properties.isStump
                    ? properties.visualSizeRange.max * Mathf.Lerp(.35f, 1, plant.Growth) *
                        ObjectPresentation.TreeModelVariation(plant).y
                    : properties.visualSizeRange.LerpThroughRange(plant.Growth);
                return Mathf.Max(.001f, plant.def.graphicData.drawSize.x * size) *
                    Rim3DSettings.Current.TreeScaleFor(plant.def);
            }
            if (thing is Pawn pawn && pawn.RaceProps.Animal && graphic != null)
                return Mathf.Max(.001f, Mathf.Max(graphic.drawSize.x, graphic.drawSize.y));
            return 1;
        }

        bool TryDrawOptionalModel(Thing thing, Graphic graphic, Vector3 center,
            Dictionary<Material, Geometry> modelGeometry = null, OptionalModelLibrary.Model selectedModel = null) => _objectPresentation.TryDrawOptionalModel(thing, graphic, center, modelGeometry, selectedModel);

        sealed class BatteryBarView
        {
            public GameObject Root;
            public Transform Fill;
            public Vector3 Anchor;
            public float Charge = -1;
        }

        readonly Dictionary<CompPowerBattery, BatteryBarView> batteryBars = new Dictionary<CompPowerBattery, BatteryBarView>();
        readonly List<CompPowerBattery> removedBatteryBars = new List<CompPowerBattery>();
        Mesh batteryBarMesh;
        Material batteryBarFilled, batteryBarEmpty;

        void RegisterBatteryBar(CompPowerBattery battery, Vector3 anchor) => _objectPresentation.RegisterBatteryBar(battery, anchor);

        Transform BatteryBarPart(Transform parent, Material material) => _objectPresentation.BatteryBarPart(parent, material);

        void UpdateBatteryBars() => _objectPresentation.UpdateBatteryBars();

        static readonly AccessTools.FieldRef<CompPowerPlantWind, float> windSpinPosition =
            AccessTools.FieldRefAccess<CompPowerPlantWind, float>("spinPosition");
        readonly Dictionary<CompPowerPlantWind, GameObject> windTurbineBlades = new Dictionary<CompPowerPlantWind, GameObject>();
        readonly List<CompPowerPlantWind> removedWindTurbines = new List<CompPowerPlantWind>();
        Material windBladeMaterial;

        void CreateWindTurbineBlades(ThingWithComps turbine, float heightOffset) => _objectPresentation.CreateWindTurbineBlades(turbine, heightOffset);

        void UpdateWindTurbineBlades() => _objectPresentation.UpdateWindTurbineBlades();

        readonly Dictionary<Building_TurretGun, GameObject> optionalTurretTops = new Dictionary<Building_TurretGun, GameObject>();
        readonly List<Building_TurretGun> removedOptionalTurrets = new List<Building_TurretGun>();

        void UpdateOptionalTurrets() => _objectPresentation.UpdateOptionalTurrets();

        sealed partial class ObjectPresentation
        {
            internal static Vector2 TreeModelVariation(Plant plant)
            {
                uint seed = unchecked((uint)plant.thingIDNumber);
                seed ^= seed >> 16;
                seed = unchecked(seed * 0x7feb352du);
                seed ^= seed >> 15;
                seed = unchecked(seed * 0x846ca68bu);
                seed ^= seed >> 16;
                return new Vector2((seed & 65535u) * (360f / 65536f),
                    .92f + (seed >> 16) * (.16f / 65535f));
            }

            static Quaternion OptionalModelRotation(Thing thing)
            {
                if (thing is Plant plant && plant.def.plant.IsTree && !plant.def.plant.isStump)
                    return Quaternion.Euler(0, thing.Rotation.AsAngle + TreeModelVariation(plant).x, 0);
                return thing.Rotation.AsQuat;
            }

            readonly Material[] _optionalFaceMaterials = new Material[5];

            readonly Vector2[] _optionalTextureSizes = new Vector2[5];

            bool _lastAnimalPlantModels = true;
            sealed class ModelChunk
            {
                internal int Level;
                internal bool RebuildOnLevelChange;
                internal Dictionary<Material, Geometry>[] Geometry;
                internal GameObject[] Roots;
            }

            readonly Dictionary<Vector2Int, ModelChunk> _modelChunks = new Dictionary<Vector2Int, ModelChunk>();
            float _nextModelLodUpdate;
            readonly Dictionary<Material, Material> _distantModelMaterials = new Dictionary<Material, Material>();
            readonly Dictionary<Building_Grave, TextMesh> _graveInscriptions = new Dictionary<Building_Grave, TextMesh>();
            readonly List<Building_Grave> _removedGraveInscriptions = new List<Building_Grave>();
            Font _graveInscriptionFont;
            Material _graveInscriptionMaterial;
            float _nextGraveInscriptionUpdate;

            void RegisterGraveInscription(Building_Grave grave, Vector3 center, float heightOffset)
            {
                if (!(grave.ContainedThing is Corpse)) return;
                if (_graveInscriptions.TryGetValue(grave, out var previous) && previous != null)
                    UnityEngine.Object.Destroy(previous.gameObject);
                if (_graveInscriptionFont == null) _graveInscriptionFont = Text.CurFontStyle.font;
                if (_graveInscriptionFont == null) return;
                if (_graveInscriptionMaterial == null)
                    _graveInscriptionMaterial = _view.NewMaterial(_graveInscriptionFont.material.mainTexture, new Color(.12f, .1f, .08f), false);
                var obj = new GameObject("RimVision grave inscription") { layer = Layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(_view.geometryParent ?? _view.root.transform, false);
                obj.transform.localPosition = center + grave.Rotation.AsQuat * new Vector3(0, .32f + heightOffset, .7165f);
                var text = obj.AddComponent<TextMesh>();
                obj.SetActive(ModelDetail(ChunkKey(center)) < 3);
                text.font = _graveInscriptionFont;
                text.fontSize = 64;
                text.characterSize = .1f;
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                text.richText = false;
                var renderer = obj.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = _graveInscriptionMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                _graveInscriptions[grave] = text;
                _nextGraveInscriptionUpdate = 0;
            }

            internal void UpdateGraveInscriptions()
            {
                if (Time.unscaledTime < _nextGraveInscriptionUpdate) return;
                _nextGraveInscriptionUpdate = Time.unscaledTime + .25f;
                foreach (var entry in _graveInscriptions)
                {
                    var grave = entry.Key;
                    var text = entry.Value;
                    if (text == null || !grave.Spawned || grave.Map != _view.map || !(grave.ContainedThing is Corpse corpse))
                    {
                        if (text != null) UnityEngine.Object.Destroy(text.gameObject);
                        _removedGraveInscriptions.Add(grave);
                        continue;
                    }
                    var name = corpse.InnerPawn?.Name?.ToStringFull ?? corpse.InnerPawn?.LabelShortCap.ToString() ?? "";
                    bool visible = ModelDetail(ChunkKey(grave.TrueCenter())) < 3;
                    if (text.text == name)
                    {
                        text.gameObject.SetActive(visible);
                        continue;
                    }
                    text.gameObject.SetActive(true);
                    text.transform.localRotation = Quaternion.identity;
                    text.transform.localScale = Vector3.one;
                    text.text = name;
                    var size = text.GetComponent<MeshRenderer>().bounds.size;
                    float scale = Mathf.Min(.17f / Mathf.Max(.001f, size.x), .05f / Mathf.Max(.001f, size.y));
                    text.transform.localScale = Vector3.one * scale;
                    text.transform.localRotation = grave.Rotation.AsQuat;
                    text.gameObject.SetActive(visible);
                }
                foreach (var grave in _removedGraveInscriptions) _graveInscriptions.Remove(grave);
                _removedGraveInscriptions.Clear();
                if (_graveInscriptionMaterial != null && _graveInscriptionFont != null)
                    _graveInscriptionMaterial.mainTexture = _graveInscriptionFont.material.mainTexture;
            }

            int ModelDetail(Vector2Int key)
            {
                if (_modelChunks.TryGetValue(key, out var chunk)) return chunk.Level;
                int level = DesiredModelDetail(key, -1);
                _modelChunks.Add(key, new ModelChunk { Level = level });
                return level;
            }

            internal void InvalidateModelChunk(Vector2Int key)
            {
                if (!_modelChunks.TryGetValue(key, out var chunk)) return;
                chunk.Geometry = null;
                chunk.Roots = null;
                chunk.RebuildOnLevelChange = false;
            }

            internal void FinishModelChunk(Vector2Int key)
            {
                if (!_modelChunks.TryGetValue(key, out var chunk) || chunk.Geometry == null) return;
                if (chunk.Roots == null) chunk.Roots = new GameObject[4];
                for (int level = 0; level < chunk.Roots.Length; level++)
                {
                    if (chunk.Geometry[level] == null) continue;
                    var obj = new GameObject("RimVision model LOD " + level) { layer = Layer, hideFlags = HideFlags.DontSave };
                    obj.transform.SetParent(_view.geometryParent, false);
                    chunk.Roots[level] = obj;
                    foreach (var group in chunk.Geometry[level])
                        _view.CreateMesh(group.Key, group.Value, "Model chunk part").transform.SetParent(obj.transform, false);
                    obj.SetActive(level == chunk.Level);
                }
                chunk.Geometry = null;
            }

            int DesiredModelDetail(Vector2Int key, int previous)
            {
                var position = _view.CameraMapPosition;
                var bounds = new Bounds(new Vector3((key.x + .5f) * ChunkSize, 0, (key.y + .5f) * ChunkSize),
                    new Vector3(ChunkSize, 0, ChunkSize));
                return ModelDetailAtDistance(Mathf.Sqrt(bounds.SqrDistance(position)), previous);
            }

            internal int ModelDetailAtDistance(float distance, int previous)
            {
                if (_view.camera != null && _view.camera.orthographic) return 0;
                var settings = Rim3DSettings.Current;
                float margin = previous < 0 ? 0 : 6;
                if (settings.DistantModelCards && distance > settings.ModelCardDistance + (previous == 3 ? -margin : margin)) return 3;
                return 0;
            }

            internal void ClearModelDetail()
            {
                _graveInscriptions.Clear();
                _removedGraveInscriptions.Clear();
                _graveInscriptionFont = null;
                _graveInscriptionMaterial = null;
                _nextGraveInscriptionUpdate = 0;
                _modelChunks.Clear();
                _distantModelMaterials.Clear();
                _nextModelLodUpdate = 0;
            }

            internal void UpdateModelDetail()
            {
                if (Time.unscaledTime < _nextModelLodUpdate) return;
                _nextModelLodUpdate = Time.unscaledTime + .25f;
                foreach (var key in _view.chunkRoots.Keys)
                {
                    if (!_modelChunks.TryGetValue(key, out var chunk)) continue;
                    int level = DesiredModelDetail(key, chunk.Level);
                    if (level == chunk.Level) continue;
                    if (chunk.Roots != null)
                    {
                        chunk.Roots[chunk.Level].SetActive(false);
                        chunk.Roots[level].SetActive(true);
                    }
                    chunk.Level = level;
                    if (chunk.RebuildOnLevelChange) _view.dirtyChunks.Add(key);
                }
            }


            internal void UpdateAnimalPlantModelSettings()
            {
                bool enabled = Rim3DSettings.Current.AnimalPlantModels;
                if (_lastAnimalPlantModels == enabled) return;
                _lastAnimalPlantModels = enabled;
                foreach (var key in _view.chunkRoots.Keys) _view.dirtyChunks.Add(key);
                foreach (var entry in _view.pawnViews)
                {
                    if (!entry.Key.RaceProps.Animal) continue;
                    entry.Value.AnimalModelChecked = false;
                    _view.dirtyPawnAppearances.Add(entry.Key);
                }
            }

            internal OptionalModelLibrary.Model ConstructionModel(BuildableDef def, string path = null)
            {
                if (!(def is ThingDef thingDef) || thingDef.IsDoor || IsGroundSpot(thingDef) || IsConduit(thingDef)) return null;
                if (_view.optionalModels == null) _view.optionalModels = OptionalModelLibrary.Load();
                return _view.optionalModels.Find(thingDef.defName, path ?? thingDef.graphicData?.texPath)
                    ?? _view.optionalModels.Find(thingDef.defName, thingDef.graphicData?.texPath);
            }

            internal int ConstructionLinks(OptionalModelLibrary.Model model, ThingDef def, IntVec3 position, Rot4 rotation)
            {
                if (!model.HasLinks) return 0;
                int links = 0;
                var turn = Quaternion.Euler(0, rotation.AsAngle, 0);
                for (int d = 0; d < 4; d++)
                {
                    var local = new Rot4(d).FacingCell;
                    var offset = turn * new Vector3(local.x, 0, local.z);
                    var cell = position + new IntVec3(Mathf.RoundToInt(offset.x), 0, Mathf.RoundToInt(offset.z));
                    if (!cell.InBounds(_view.map)) continue;
                    foreach (var neighbor in _view.map.thingGrid.ThingsListAt(cell))
                        if (neighbor.def == def || neighbor.def.entityDefToBuild == def) { links |= 1 << d; break; }
                }
                return links;
            }

            internal OptionalModelLibrary.Model ThingModel(Thing thing, Graphic graphic)
            {
                if (_view.optionalModels == null) _view.optionalModels = OptionalModelLibrary.Load();
                var imageGraphic = graphic is Graphic_Linked linked ? linked.SubGraphic : graphic;
                var model = _view.optionalModels.Find(thing.def.defName, graphic.path);
                if (imageGraphic is Graphic_Random)
                {
                    // Random graphics keep a folder path; the chosen texture identifies the wreck variant.
                    var selectedTexture = imageGraphic.MatAt(thing.Rotation, thing)?.mainTexture;
                    if (selectedTexture != null)
                    {
                        var variant = _view.optionalModels.Find(thing.def.defName, selectedTexture.name);
                        if (variant != null && (model == null || variant.LoadOrder > model.LoadOrder ||
                            (variant.LoadOrder == model.LoadOrder && variant.PathFilter.Length > model.PathFilter.Length)))
                            model = variant;
                    }
                }
                return model;
            }

            internal float GroundThingSurfaceHeight(Vector3 position)
            {
                float height = 0;
                var cell = position.ToIntVec3();
                if (!cell.InBounds(_view.map)) return height;
                foreach (var thing in _view.map.thingGrid.ThingsListAt(cell))
                {
                    if (!(thing is Building) || thing is Building_Door || IsGroundSpot(thing.def) || IsConduit(thing.def)) continue;
                    var graphic = thing.Graphic;
                    if (graphic == null) continue;
                    var model = _view.ThingModel(thing, graphic);
                    if (model == null)
                    {
                        height = Mathf.Max(height, _view.GeneratedBuildingSurfaceHeight(thing, graphic, position));
                        continue;
                    }
                    var imageGraphic = graphic is Graphic_Linked linked ? linked.SubGraphic : graphic;
                    bool texturesAvailable = true;
                    for (int direction = 0; direction < 5; direction++)
                    {
                        if ((model.TextureMask & (1 << direction)) == 0) continue;
                        var source = imageGraphic.MatAt(direction == 4 ? thing.Rotation : new Rot4(direction), thing);
                        if (source != null && source.mainTexture != null) continue;
                        texturesAvailable = false;
                        break;
                    }
                    if (!texturesAvailable) continue;
                    var local = Quaternion.Inverse(thing.Rotation.AsQuat) * (position - thing.TrueCenter());
                    local.x += thing.def.size.x * .5f;
                    local.z += thing.def.size.z * .5f;
                    height = Mathf.Max(height, model.SurfaceHeight(local, _view.OptionalModelLinks(thing, graphic, model)));
                }
                return height;
            }

            internal int OptionalModelLinks(Thing thing, Graphic graphic, OptionalModelLibrary.Model model)
            {
                var turn = thing.Rotation.AsQuat;
                int links = 0;
                if (model.HasLinks && thing.Spawned)
                {
                    var linkGraphic = graphic as Graphic_Linked;
                    bool razorWire = thing.def.defName == "AncientRazorWire";
                    for (int d = 0; d < 4; d++)
                    {
                        var local = new Rot4(d).FacingCell;
                        var offset = turn * new Vector3(local.x, 0, local.z);
                        var cell = thing.Position + new IntVec3(Mathf.RoundToInt(offset.x), 0, Mathf.RoundToInt(offset.z));
                        if (!cell.InBounds(_view.map)) continue;
                        if (razorWire)
                        {
                            // Physical wire sections connect even if the graphic is wrapped or unlinked.
                            foreach (var neighbor in _view.map.thingGrid.ThingsListAt(cell))
                                if (neighbor.def == thing.def) { links |= 1 << d; break; }
                        }
                        else if (linkGraphic != null && linkGraphic.ShouldLinkWith(cell, thing)) links |= 1 << d;
                    }
                }
                return links;
            }

            internal bool TryRaycastOptionalModel(Thing thing, Ray ray, out float distance)
            {
                distance = float.PositiveInfinity;
                bool construction = thing is Blueprint || thing is Frame;
                var def = construction ? thing.def.entityDefToBuild as ThingDef : thing.def;
                if (def == null || def.IsDoor ||
                    IsConduit(def) || !construction && def.defName == "Vent" ||
                    def.category == ThingCategory.Plant && !Rim3DSettings.Current.AnimalPlantModels ||
                    !construction && def.defName == "NatureShrine_Large" &&
                    thing.Stuff?.stuffProps?.categories?.Contains(StuffCategoryDefOf.Woody) == true) return false;
                var graphic = construction ? _view.ConstructionGraphic(def, thing.Stuff, thing.StyleDef) : thing.Graphic;
                if (graphic == null || construction && IsConstructionWall(def) && graphic is Graphic_Linked) return false;
                var model = construction ? ConstructionModel(def, graphic.path) : ThingModel(thing, graphic);
                if (model == null || IsGroundSpot(def) && model.GroundCutout == null) return false;
                var imageGraphic = graphic is Graphic_Linked linked ? linked.SubGraphic : graphic;
                if (thing.def.category == ThingCategory.Item && imageGraphic is Graphic_StackCount stack)
                    imageGraphic = stack.SubGraphicForStackCount(1, thing.def);
                for (int direction = 0; direction < 5; direction++)
                {
                    if ((model.TextureMask & (1 << direction)) == 0) continue;
                    var source = imageGraphic.MatAt(direction == 4 ? thing.Rotation : new Rot4(direction), thing);
                    if (source == null || source.mainTexture == null || source.mainTexture == BaseContent.BadTex) return false;
                }
                var center = thing.TrueCenter();
                center.y = thing.def.category == ThingCategory.Item
                    ? GroundThingSurfaceHeight(center) + GroundItemHeightOffset(thing) : 0;
                if (construction) center.y = .035f;
                int detail = !construction && !model.HasTurretTop && thing.def.defName != "WindTurbine"
                    ? ModelDetail(ChunkKey(center)) : 0;
                if (detail == 3) return false;
                var renderModel = model;
                float scale = construction ? 1 : OptionalModelScale(thing, imageGraphic);
                var origin = new Vector3(def.size.x * .5f, -model.HeightOffset, def.size.z * .5f);
                var inverse = Quaternion.Inverse(OptionalModelRotation(thing));
                var bodyRay = new Ray(inverse * (ray.origin - center) / scale + origin, inverse * ray.direction);
                var topRay = bodyRay;
                if (thing is Building_TurretGun turret && _view.optionalTurretTops.TryGetValue(turret, out var top))
                {
                    inverse = Quaternion.Inverse(top.transform.localRotation);
                    topRay = new Ray(inverse * (ray.origin - top.transform.localPosition) / scale + origin,
                        inverse * ray.direction);
                }
                int links = construction ? ConstructionLinks(model, def, thing.Position, thing.Rotation)
                    : OptionalModelLinks(thing, graphic, model);
                foreach (var face in renderModel.Faces)
                {
                    if ((links & face.RequiredLinks) != face.RequiredLinks || (links & face.ForbiddenLinks) != 0) continue;
                    var localRay = face.TurretTop && thing is Building_TurretGun ? topRay : bodyRay;
                    var bounds = new Bounds((face.Min + face.Max) * .5f, face.Max - face.Min);
                    bounds.Expand(.0001f);
                    if (!bounds.IntersectRay(localRay)) continue;
                    var a = renderModel.Vertices[face.Indices[0]];
                    for (int i = 1; i < face.Indices.Length - 1; i++)
                    {
                        var edge1 = renderModel.Vertices[face.Indices[i]] - a;
                        var edge2 = renderModel.Vertices[face.Indices[i + 1]] - a;
                        var cross = Vector3.Cross(localRay.direction, edge2);
                        float determinant = Vector3.Dot(edge1, cross);
                        if (Mathf.Abs(determinant) < .00000001f) continue;
                        var offset = localRay.origin - a;
                        float u = Vector3.Dot(offset, cross) / determinant;
                        if (u < 0 || u > 1) continue;
                        var offsetCross = Vector3.Cross(offset, edge1);
                        float v = Vector3.Dot(localRay.direction, offsetCross) / determinant;
                        if (v < 0 || u + v > 1) continue;
                        float hit = Vector3.Dot(edge2, offsetCross) / determinant * scale;
                        if (hit >= 0 && hit < distance) distance = hit;
                    }
                }
                return true;
            }

            internal bool TryDrawOptionalModel(Thing thing, Graphic graphic, Vector3 center,
                Dictionary<Material, Geometry> modelGeometry = null, OptionalModelLibrary.Model selectedModel = null)
            {
                if (thing.def.category == ThingCategory.Plant && !Rim3DSettings.Current.AnimalPlantModels)
                {
                    _view.ClearModelFire(thing);
                    _view._gameLighting.RemoveModelLight(thing.thingIDNumber);
                    return false;
                }
                var imageGraphic = graphic is Graphic_Linked linked ? linked.SubGraphic : graphic;
                var model = selectedModel ?? _view.ThingModel(thing, graphic);
                if (model == null)
                {
                    _view.ClearModelFire(thing);
                    _view._gameLighting.RemoveModelLight(thing.thingIDNumber);
                    return false;
                }
                if (thing is Plant plant && plant.def.plant.IsTree && model.PathFilter.Length == 0 &&
                    imageGraphic == plant.def.plant.immatureGraphic)
                    imageGraphic = plant.def.graphicData.GraphicColoredFor(plant);
                if (thing.def.category == ThingCategory.Item && imageGraphic is Graphic_StackCount stack)
                    imageGraphic = stack.SubGraphicForStackCount(1, thing.def);
                // Resolve every required texture before changing a batch: missing assets use the old renderer.
                for (int direction = 0; direction < 5; direction++)
                {
                    if ((model.TextureMask & (1 << direction)) == 0) continue;
                    var rotation = direction == 4 ? modelGeometry != null ? Rot4.North : thing.Rotation : new Rot4(direction);
                    var source = imageGraphic.MatAt(rotation, thing);
                    if (source == null || source.mainTexture == null || source.mainTexture == BaseContent.BadTex) return false;
                    _optionalFaceMaterials[direction] = source;
                    // MatAt already resolves directional fallbacks, styles, stuff tint and masks.
                    // Keep its UV transform, including any packed atlas region, in ConvertMaterial.
                    _optionalTextureSizes[direction] = model.TextureSize(imageGraphic, source);
                }

                if (modelGeometry == null && !model.HasTurretTop && thing.def.defName != "WindTurbine")
                {
                    var key = ChunkKey(center);
                    ModelDetail(key);
                    var chunk = _modelChunks[key];
                    var cardSource = imageGraphic.MatAt(thing.Rotation, thing);
                    if (cardSource == null || cardSource.mainTexture == null || cardSource.mainTexture == BaseContent.BadTex)
                    {
                        chunk.RebuildOnLevelChange = true;
                        return AppendOptionalModel(thing, graphic, imageGraphic, model, center, null, chunk.Level, null);
                    }
                    if (chunk.Geometry == null)
                    {
                        chunk.Geometry = new Dictionary<Material, Geometry>[4];
                        chunk.Geometry[0] = new Dictionary<Material, Geometry>();
                        chunk.Geometry[3] = new Dictionary<Material, Geometry>();
                    }
                    AppendOptionalModel(thing, graphic, imageGraphic, model, center, null, 0, chunk.Geometry[0]);
                    AppendOptionalModel(thing, graphic, imageGraphic, model, center, null, 3, chunk.Geometry[3], false);
                    return true;
                }
                return AppendOptionalModel(thing, graphic, imageGraphic, model, center, modelGeometry, 0, null);
            }

            bool AppendOptionalModel(Thing thing, Graphic graphic, Graphic imageGraphic, OptionalModelLibrary.Model model,
                Vector3 center, Dictionary<Material, Geometry> modelGeometry, int detail, Dictionary<Material, Geometry> chunkGeometry,
                bool updatePresentation = true)
            {
                var renderModel = model;
                var renderFaces = detail == 3 ? Array.Empty<OptionalModelLibrary.Face>() : renderModel.Faces;
                if (detail == 3)
                {
                    var source = imageGraphic.MatAt(thing.Rotation, thing);
                    if (source == null || source.mainTexture == null || source.mainTexture == BaseContent.BadTex) return false;
                    if (!_distantModelMaterials.TryGetValue(source, out var material))
                    {
                        material = new Material(_view.ConvertMaterial(source, false)) { hideFlags = HideFlags.DontSave };
                        material.SetFloat("_Billboard", 1);
                        _view.materials.Add(material);
                        _distantModelMaterials.Add(source, material);
                    }
                    float scale = OptionalModelScale(thing, imageGraphic);
                    float height = Mathf.Max(.1f, model.Height * scale);
                    float width = height * imageGraphic.drawSize.x / Mathf.Max(.1f, imageGraphic.drawSize.y);
                    if (!(thing is Plant))
                    {
                        height = Mathf.Max(height, imageGraphic.drawSize.y);
                        width = Mathf.Max(width, imageGraphic.drawSize.x);
                    }
                    var geometry = OptionalModelBatch(material, center, chunkGeometry);
                    geometry.Upright(center - Vector3.up * (height * _view.BottomPadding(material)), width, height, false);
                    geometry.SetPlantWind(geometry.Vertices.Count - 4, center, WorldGeometry.PlantFlexibility(thing.def));
                }
                int links = _view.OptionalModelLinks(thing, graphic, model);
                var turn = modelGeometry == null ? OptionalModelRotation(thing) : Quaternion.identity;
                float modelScale = OptionalModelScale(thing, imageGraphic);
                var origin = new Vector3(thing.def.size.x * .5f, 0, thing.def.size.z * .5f);
                if (model.GroundCutout != null && modelGeometry == null && updatePresentation)
                {
                    if (_view._groundCutoutMaterial == null)
                    {
                        _view._groundCutoutMaterial = _view.NewMaterial(Texture2D.whiteTexture, Color.white, true);
                        _view._groundCutoutMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry - 1;
                        _view._groundCutoutMaterial.SetFloat("_Ground", 1);
                        _view._groundCutoutMaterial.SetFloat("_ZWrite", 0);
                        _view._groundCutoutMaterial.SetInt("_ColorMask", 0);
                        _view._groundCutoutMaterial.SetInt("_StencilWriteMask", GroundCutoutStencil);
                    }
                    var cutout = _view.Batch(_view._groundCutoutMaterial, center);
                    int start = cutout.Vertices.Count;
                    foreach (int index in model.GroundCutout)
                    {
                        var vertex = model.Vertices[index] - origin;
                        vertex.y = 0;
                        cutout.Vertices.Add(center + turn * (vertex * modelScale));
                        cutout.Normals.Add(Vector3.up);
                        cutout.CardCenters.Add(Vector3.zero);
                        cutout.Uvs.Add(Vector2.zero);
                    }
                    for (int v = 1; v < model.GroundCutout.Length - 1; v++)
                    {
                        cutout.Indices.Add(start); cutout.Indices.Add(start + v); cutout.Indices.Add(start + v + 1);
                    }
                }
                var woodFence = thing.def.defName == "Fence" && thing.Stuff != null &&
                    thing.Stuff.defName.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0
                    ? _view.WoodFenceMaterial(thing) : null;
                var turret = thing as Building_TurretGun;
                bool createTop = turret != null && model.HasTurretTop && !_view.optionalTurretTops.ContainsKey(turret);
                var topGroups = createTop ? new Dictionary<Material, Geometry>() : null;
                var bodyBounds = new Bounds();
                var topBounds = new Bounds();
                bool hasBodyBounds = false, hasTopBounds = false;
                foreach (var face in renderFaces)
                {
                    if ((links & face.RequiredLinks) != face.RequiredLinks || (links & face.ForbiddenLinks) != 0) continue;
                    bool movingTop = turret != null && face.TurretTop;
                    if (movingTop && !createTop) continue;
                    foreach (int index in face.Indices)
                    {
                        var vertex = (renderModel.Vertices[index] - origin + Vector3.up * model.HeightOffset) * modelScale;
                        if (movingTop)
                        {
                            if (!hasTopBounds) { topBounds = new Bounds(vertex, Vector3.zero); hasTopBounds = true; }
                            else topBounds.Encapsulate(vertex);
                        }
                        else
                        {
                            if (!hasBodyBounds) { bodyBounds = new Bounds(vertex, Vector3.zero); hasBodyBounds = true; }
                            else bodyBounds.Encapsulate(vertex);
                        }
                    }
                }
                bodyBounds.center = center + turn * bodyBounds.center;
                Color? customTint = null;
                foreach (var face in renderFaces)
                {
                    if ((links & face.RequiredLinks) != face.RequiredLinks || (links & face.ForbiddenLinks) != 0) continue;
                    bool movingTop = turret != null && face.TurretTop;
                    if (movingTop && !createTop) continue;
                    Material material;
                    if (woodFence != null && face.CustomTexture == null) material = woodFence;
                    else if (face.CustomTexture != null)
                    {
                        var color = Color.white;
                        if (face.TintWithThing)
                        {
                            if (!customTint.HasValue)
                                customTint = OptionalModelTint(thing, imageGraphic, modelGeometry != null ? Rot4.North : thing.Rotation);
                            color = customTint.Value;
                        }
                        var key = (face.CustomTexture, color, face.TwoSided);
                        if (!_view._optionalCustomMaterials.TryGetValue(key, out material))
                        {
                            material = _view.NewMaterial(face.CustomTexture, color, false);
                            material.SetFloat("_Cull", face.TwoSided ? 0 : 2);
                            _view._optionalCustomMaterials.Add(key, material);
                        }
                    }
                    else if (face.Solid)
                    {
                        var color = face.TintWithThing ? face.Color * thing.DrawColor : face.Color;
                        color.a = 1;
                        if (!_view.optionalSolidMaterials.TryGetValue(color, out material))
                        {
                            material = _view.NewMaterial(Texture2D.whiteTexture, color, true);
                            material.SetFloat("_Cull", 2);
                            _view.optionalSolidMaterials.Add(color, material);
                        }
                    }
                    else
                    {
                        var source = _optionalFaceMaterials[face.Texture];
                        var cache = face.TwoSided ? _view.optionalTwoSidedMaterials : _view.optionalModelMaterials;
                        if (!cache.TryGetValue(source, out material))
                        {
                            material = new Material(_view.ConvertMaterial(source, false)) { hideFlags = HideFlags.DontSave };
                            material.SetFloat("_Cull", face.TwoSided ? 0 : 2);
                            _view.materials.Add(material);
                            cache.Add(source, material);
                        }
                    }
                    Geometry geometry;
                    if (movingTop)
                    {
                        if (!topGroups.TryGetValue(material, out geometry))
                            topGroups.Add(material, geometry = new Geometry());
                    }
                    else if (modelGeometry != null)
                    {
                        if (!modelGeometry.TryGetValue(material, out geometry))
                            modelGeometry.Add(material, geometry = new Geometry());
                    }
                    else geometry = OptionalModelBatch(material, center, chunkGeometry);
                    int start = geometry.Vertices.Count;
                    var size = face.UvTextureSize.x > 0 ? face.UvTextureSize : face.CustomTexture != null
                        ? new Vector2(face.CustomTexture.width, face.CustomTexture.height) : _optionalTextureSizes[face.Texture];
                    for (int v = 0; v < face.Indices.Length; v++)
                    {
                        var vertex = renderModel.Vertices[face.Indices[v]] - origin;
                        vertex.y += model.HeightOffset;
                        vertex *= modelScale;
                        geometry.Vertices.Add(movingTop ? vertex : center + turn * vertex);
                        var normal = face.Normals == null ? face.Normal : face.Normals[v];
                        geometry.Normals.Add(movingTop ? normal : turn * normal);
                        geometry.CardCenters.Add(Vector3.zero);
                        if (woodFence != null && face.CustomTexture == null)
                        {
                            // Metre-based UVs keep the grain scale consistent with gate boards.
                            var localNormal = face.Normal;
                            geometry.Uvs.Add(Mathf.Abs(localNormal.y) > .5f ? new Vector2(vertex.x, vertex.z) :
                                Mathf.Abs(localNormal.x) > .5f ? new Vector2(vertex.z, vertex.y) : new Vector2(vertex.x, vertex.y));
                            continue;
                        }
                        var uv = face.Uvs[v];
                        geometry.Uvs.Add(face.PixelUvs
                            ? face.CustomTexture != null ? new Vector2(uv.x / size.x, 1 - uv.y / size.y)
                                : new Vector2(Mathf.Clamp01(uv.x / size.x), Mathf.Clamp01(1 - uv.y / size.y)) : uv);
                    }
                    for (int v = 1; v < face.Indices.Length - 1; v++)
                    {
                        geometry.Indices.Add(start); geometry.Indices.Add(start + v); geometry.Indices.Add(start + v + 1);
                    }
                    geometry.SetOutlineBounds(start, movingTop ? topBounds : bodyBounds, face.OutlineOrientation);
                    if (modelGeometry == null)
                        geometry.SetPlantWind(start, center, WorldGeometry.PlantFlexibility(thing.def));
                }
                if (modelGeometry != null) return true;
                if (!updatePresentation) return true;
                if ((detail < 3 || chunkGeometry != null) && thing.def.defName == "Grave" && thing is Building_Grave grave)
                    RegisterGraveInscription(grave, center, model.HeightOffset);
                if (createTop)
                {
                    var parent = _view.geometryParent;
                    var top = new GameObject("RimVision turret weapon") { layer = Layer, hideFlags = HideFlags.DontSave };
                    top.transform.SetParent(_view.root.transform, false);
                    _view.geometryParent = top.transform;
                    foreach (var group in topGroups) _view.CreateMesh(group.Key, group.Value, "Turret weapon part");
                    _view.geometryParent = parent;
                    _view.optionalTurretTops.Add(turret, top);
                }
                if (thing.def.defName == "WindTurbine" && thing is ThingWithComps turbine)
                    _view.CreateWindTurbineBlades(turbine, model.HeightOffset);
                if (model.HasBatteryBar && thing.TryGetComp<CompPowerBattery>() is CompPowerBattery battery)
                    _view.RegisterBatteryBar(battery, model.BatteryBar - origin + Vector3.up * model.HeightOffset);
                if (model.FireAnchor != null)
                    _view.TrackModelFire(thing, model.FireAnchor, center + turn * ((model.FireAnchor.Position - origin + Vector3.up * model.HeightOffset) * modelScale));
                else _view.ClearModelFire(thing);
                if (model.LightAnchor != null)
                    _view.RegisterModelLight(thing, model.LightAnchor, center + turn * ((model.LightAnchor.Position - origin + Vector3.up * model.HeightOffset) * modelScale));
                else _view._gameLighting.RemoveModelLight(thing.thingIDNumber);
                return true;
            }

            Geometry OptionalModelBatch(Material material, Vector3 center, Dictionary<Material, Geometry> chunkGeometry)
            {
                if (chunkGeometry == null) return _view.Batch(material, center);
                if (!chunkGeometry.TryGetValue(material, out var geometry))
                    chunkGeometry.Add(material, geometry = new Geometry());
                return geometry;
            }

            internal void RegisterBatteryBar(CompPowerBattery battery, Vector3 anchor)
            {
                if (_view.batteryBars.TryGetValue(battery, out var existing)) { existing.Anchor = anchor; return; }
                if (_view.batteryBarMesh == null)
                {
                    var geometry = new Geometry();
                    geometry.Quad(new Vector3(-.5f, 0, -.5f), new Vector3(-.5f, 0, .5f),
                        new Vector3(.5f, 0, .5f), new Vector3(.5f, 0, -.5f), Vector3.up,
                        Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
                    _view.batteryBarMesh = new Mesh { name = "RimVision battery bar", hideFlags = HideFlags.DontSave };
                    _view.batteryBarMesh.SetVertices(geometry.Vertices);
                    _view.batteryBarMesh.SetNormals(geometry.Normals);
                    _view.batteryBarMesh.SetUVs(0, geometry.Uvs);
                    _view.batteryBarMesh.SetTriangles(geometry.Indices, 0);
                    _view.batteryBarMesh.RecalculateBounds();
                    _view.meshes.Add(_view.batteryBarMesh);
                    _view.batteryBarFilled = _view.NewMaterial(Texture2D.whiteTexture, new Color(.9f, .85f, .2f), true);
                    _view.batteryBarEmpty = _view.NewMaterial(Texture2D.whiteTexture, new Color(.3f, .3f, .3f), true);
                }
                var obj = new GameObject("RimVision battery charge") { layer = Layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(_view.root.transform, false);
                var background = _view.BatteryBarPart(obj.transform, _view.batteryBarEmpty);
                background.localScale = new Vector3(.55f, 1, 1.45f);
                var fill = _view.BatteryBarPart(obj.transform, _view.batteryBarFilled);
                _view.batteryBars.Add(battery, new BatteryBarView { Root = obj, Fill = fill, Anchor = anchor });
            }

            internal Transform BatteryBarPart(Transform parent, Material material)
            {
                var obj = new GameObject("Charge surface") { layer = Layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(parent, false);
                obj.AddComponent<MeshFilter>().sharedMesh = _view.batteryBarMesh;
                var renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                _view.RegisterOutlineRenderer(renderer);
                return obj.transform;
            }

            internal void UpdateBatteryBars()
            {
                _view.removedBatteryBars.Clear();
                foreach (var entry in _view.batteryBars)
                {
                    var thing = entry.Key.parent;
                    var view = entry.Value;
                    if (thing.Destroyed || !thing.Spawned || thing.Map != _view.map)
                    {
                        UnityEngine.Object.Destroy(view.Root);
                        _view.removedBatteryBars.Add(entry.Key);
                        continue;
                    }
                    bool visible = !_view.map.fogGrid.IsFogged(thing.Position);
                    if (view.Root.activeSelf != visible) view.Root.SetActive(visible);
                    if (!visible) continue;
                    var turn = Quaternion.Euler(0, thing.Rotation.AsAngle, 0);
                    var center = thing.TrueCenter(); center.y = 0;
                    view.Root.transform.localPosition = center + turn * view.Anchor;
                    view.Root.transform.localRotation = turn;
                    float charge = Mathf.Clamp01(entry.Key.StoredEnergyPct);
                    if (charge == view.Charge) continue;
                    view.Charge = charge;
                    view.Fill.gameObject.SetActive(charge > 0);
                    // Match the vanilla bar: 1.3 cells long, filling from one end.
                    view.Fill.localScale = new Vector3(.4f, 1, 1.3f * charge);
                    view.Fill.localPosition = new Vector3(0, .003f, -.65f + .65f * charge);
                }
                foreach (var battery in _view.removedBatteryBars) _view.batteryBars.Remove(battery);
            }

            internal void CreateWindTurbineBlades(ThingWithComps turbine, float heightOffset)
            {
                var wind = turbine.GetComp<CompPowerPlantWind>();
                if (wind == null || _view.windTurbineBlades.ContainsKey(wind)) return;
                if (_view.windBladeMaterial == null)
                {
                    var texture = ContentFinder<Texture2D>.Get("Things/Building/Power/WindTurbine/WindTurbineBlades", false);
                    if (texture == null) return;
                    _view.windBladeMaterial = _view.NewMaterial(texture, Color.white, false);
                    _view.windBladeMaterial.SetFloat("_Cull", 0);
                }
                // The vanilla blades are drawn by CompPowerPlantWind.PostDraw, outside
                // the body's graphic. Keep this rotor out of the stationary chunk batch.
                var blade = new Geometry();
                blade.Quad(new Vector3(-3.3f, -.5f, 0), new Vector3(3.3f, -.5f, 0),
                    new Vector3(3.3f, .5f, 0), new Vector3(-3.3f, .5f, 0), Vector3.forward,
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
                var geometry = new Geometry();
                // The texture contains one blade. Repeat it around the hub; the tiny
                // depth separation prevents coplanar surfaces flickering at the axle.
                for (int i = 0; i < 4; i++)
                    geometry.Append(blade, Vector3.forward * (i * .001f), Quaternion.AngleAxis(i * 90f, Vector3.forward));
                var parent = _view.geometryParent;
                var rotor = new GameObject("RimVision wind turbine rotor") { layer = Layer, hideFlags = HideFlags.DontSave };
                rotor.transform.SetParent(_view.root.transform, false);
                try
                {
                    _view.geometryParent = rotor.transform;
                    _view.CreateMesh(_view.windBladeMaterial, geometry, "Wind turbine blades");
                }
                finally { _view.geometryParent = parent; }
                var turn = Quaternion.Euler(0, turbine.Rotation.AsAngle, 0);
                var center = turbine.TrueCenter(); center.y = 0;
                rotor.transform.localPosition = center + turn * new Vector3(0, 4f + heightOffset, 1.03f);
                rotor.transform.localRotation = turn * Quaternion.AngleAxis(windSpinPosition(wind) * Mathf.Rad2Deg, Vector3.forward);
                _view.windTurbineBlades.Add(wind, rotor);
            }

            internal void UpdateWindTurbineBlades()
            {
                _view.removedWindTurbines.Clear();
                foreach (var entry in _view.windTurbineBlades)
                {
                    var turbine = entry.Key.parent;
                    var rotor = entry.Value;
                    if (turbine.Destroyed || !turbine.Spawned || turbine.Map != _view.map)
                    {
                        _view.RemoveMovingShadows(rotor.transform);
                        foreach (var filter in rotor.GetComponentsInChildren<MeshFilter>())
                            if (_view.meshes.Remove(filter.sharedMesh)) UnityEngine.Object.Destroy(filter.sharedMesh);
                        UnityEngine.Object.Destroy(rotor);
                        _view.removedWindTurbines.Add(entry.Key);
                        continue;
                    }
                    bool visible = !_view.map.fogGrid.IsFogged(turbine.Position);
                    if (rotor.activeSelf != visible) rotor.SetActive(visible);
                    if (!visible) continue;
                    // Read the game's phase directly: wind, blockages, breakdowns,
                    // pause and simulation speed all follow the original component.
                    rotor.transform.localRotation = Quaternion.Euler(0, turbine.Rotation.AsAngle, 0) *
                        Quaternion.AngleAxis(windSpinPosition(entry.Key) * Mathf.Rad2Deg, Vector3.forward);
                }
                foreach (var wind in _view.removedWindTurbines) _view.windTurbineBlades.Remove(wind);
            }

            internal void UpdateOptionalTurrets()
            {
                _view.removedOptionalTurrets.Clear();
                foreach (var entry in _view.optionalTurretTops)
                {
                    var turret = entry.Key;
                    var obj = entry.Value;
                    if (turret.Destroyed || !turret.Spawned || turret.Map != _view.map)
                    {
                        _view.RemoveMovingShadows(obj.transform);
                        foreach (var filter in obj.GetComponentsInChildren<MeshFilter>())
                            if (_view.meshes.Remove(filter.sharedMesh)) UnityEngine.Object.Destroy(filter.sharedMesh);
                        UnityEngine.Object.Destroy(obj);
                        _view.removedOptionalTurrets.Add(turret);
                        continue;
                    }
                    obj.SetActive(!_view.map.fogGrid.IsFogged(turret.Position));
                    var position = turret.TrueCenter(); position.y = 0;
                    obj.transform.localPosition = position;
                    obj.transform.localRotation = Quaternion.Euler(0, turret.Top.CurRotation, 0);
                }
                foreach (var turret in _view.removedOptionalTurrets) _view.optionalTurretTops.Remove(turret);
            }

        }
    }

    // Optional data stays outside Unity Assets. No dependency on the source mod's assembly or Defs.
    internal sealed class OptionalModelLibrary
    {
        internal sealed class Model
        {
            readonly bool _useBaseTextureSize;
            readonly Dictionary<Material, Vector2> _textureSizes = new Dictionary<Material, Vector2>();

            public Model(bool useBaseTextureSize) => _useBaseTextureSize = useBaseTextureSize;

            public Vector2 TextureSize(Graphic graphic, Material source)
            {
                if (_textureSizes.TryGetValue(source, out var size)) return size;
                size = new Vector2(
                    Mathf.Max(1, source.mainTexture.width * Mathf.Abs(source.mainTextureScale.x)),
                    Mathf.Max(1, source.mainTexture.height * Mathf.Abs(source.mainTextureScale.y)));
                if (_useBaseTextureSize && !string.IsNullOrEmpty(graphic.path))
                {
                    string folder = graphic is Graphic_Random ? graphic.path : Path.GetDirectoryName(graphic.path);
                    string path = Path.Combine(folder ?? "", source.mainTexture.name).Replace('\\', '/');
                    var original = BaseTexture(path);
                    if (original != null) size = new Vector2(original.width, original.height);
                }
                _textureSizes.Add(source, size);
                return size;
            }

            static Texture2D BaseTexture(string path)
            {
                var texture = Resources.Load<Texture2D>(GenFilePaths.ContentPath<Texture2D>() + path);
                if (texture != null) return texture;
                foreach (var mod in LoadedModManager.RunningMods)
                {
                    if (!mod.IsOfficialMod) continue;
                    texture = mod.GetContentHolder<Texture2D>().Get(path);
                    if (texture != null) return texture;
                    string assetPath = "Assets/Data/" + mod.FolderName + "/" + GenFilePaths.ContentPath<Texture2D>() + path;
                    foreach (var bundle in mod.assetBundles.loadedAssetBundles)
                        foreach (var extension in ModAssetBundlesHandler.TextureExtensions)
                        {
                            texture = bundle.LoadAsset<Texture2D>(assetPath + extension);
                            if (texture != null) return texture;
                        }
                }
                return null;
            }

            public int LoadOrder;
            public string PathFilter;
            public float HeightOffset;
            public Vector3[] Vertices;
            public int[] GroundCutout;
            public Face[] Faces;
            public int TextureMask;
            public bool HasLinks, HasTurretTop;
            public bool HasBatteryBar;
            public Vector3 BatteryBar;
            public EffectAnchor FireAnchor, LightAnchor;
            public float Height;
            public readonly Dictionary<int, Geometry> Outlines = new Dictionary<int, Geometry>();

            readonly Dictionary<(Vector2, int), float> _surfaceHeights = new Dictionary<(Vector2, int), float>();

            public float SurfaceHeight(Vector3 position, int links)
            {
                var key = (new Vector2(position.x, position.z), links);
                if (_surfaceHeights.TryGetValue(key, out var height)) return height;
                height = 0;
                foreach (var face in Faces)
                {
                    if (face.Normal.y < .5f || face.TurretTop ||
                        (links & face.RequiredLinks) != face.RequiredLinks || (links & face.ForbiddenLinks) != 0 ||
                        position.x < face.Min.x - .001f || position.x > face.Max.x + .001f ||
                        position.z < face.Min.z - .001f || position.z > face.Max.z + .001f) continue;
                    var a = Vertices[face.Indices[0]];
                    for (int i = 1; i < face.Indices.Length - 1; i++)
                    {
                        var b = Vertices[face.Indices[i]] - a;
                        var c = Vertices[face.Indices[i + 1]] - a;
                        float determinant = b.x * c.z - c.x * b.z;
                        if (Mathf.Abs(determinant) < 1e-7f) continue;
                        var offset = position - a;
                        float u = (offset.x * c.z - c.x * offset.z) / determinant;
                        float v = (b.x * offset.z - offset.x * b.z) / determinant;
                        if (u < -.001f || v < -.001f || u + v > 1.001f) continue;
                        height = Mathf.Max(height, a.y + b.y * u + c.y * v + HeightOffset);
                    }
                }
                _surfaceHeights[key] = height;
                return height;
            }
        }
        internal sealed class EffectAnchor
        {
            public Vector3 Position;
            public float Size = 1, Radius = 8, Intensity = 1;
            public Color Color = Color.white;
            public bool Always;
        }

        internal sealed class Face
        {
            public int[] Indices;
            public int Texture, RequiredLinks, ForbiddenLinks;
            public bool TwoSided, PixelUvs, TurretTop;
            public bool Solid, TintWithThing;
            internal float OutlineOrientation;
            public Texture2D CustomTexture;
            public Color Color;
            public Vector3 Normal, Min, Max;
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public Vector2 UvTextureSize;
        }
        readonly Dictionary<string, List<Model>> models = new Dictionary<string, List<Model>>(StringComparer.Ordinal);

        public Model Find(string defName, string path)
        {
            if (!models.TryGetValue(defName, out var variants)) return null;
            Model match = null;
            foreach (var model in variants)
            {
                if (model.PathFilter.Length != 0 &&
                    (path == null || path.IndexOf(model.PathFilter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (match == null || model.LoadOrder > match.LoadOrder ||
                    (model.LoadOrder == match.LoadOrder && model.PathFilter.Length > match.PathFilter.Length)) match = model;
            }
            return match;
        }

        public static OptionalModelLibrary Load()
        {
            var library = new OptionalModelLibrary();
            int loadOrder = 0;
            bool ownModLoaded = false;
            foreach (var mod in LoadedModManager.RunningMods)
            {
                library.LoadMod(mod.RootDir, loadOrder++);
                ownModLoaded |= string.Equals(mod.RootDir, ModAssets.RootDir, StringComparison.OrdinalIgnoreCase);
            }
            // The Unity editor can bootstrap RimVision without a registered content pack.
            if (!ownModLoaded && !string.IsNullOrEmpty(ModAssets.RootDir)) library.LoadMod(ModAssets.RootDir, -1);
            return library;
        }

        void LoadMod(string root, int loadOrder)
        {
            if (string.IsNullOrEmpty(root)) return;
            LoadFolder(Path.Combine(root, "OptionalContent", "RimWorld3DVR", "Models"), loadOrder,
                string.Equals(root, ModAssets.RootDir, StringComparison.OrdinalIgnoreCase));
            LoadFolder(Path.Combine(root, "OptionalContent", "RimVision", "Models"), loadOrder, false);
        }

        void LoadFolder(string folder, int loadOrder, bool useBaseTextureSize)
        {
            if (!Directory.Exists(folder)) return;
            try
            {
                var files = Directory.GetFiles(folder, "*.xml", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                int skipped = 0;
                foreach (var file in files)
                {
                    try
                    {
                        var doc = new XmlDocument { XmlResolver = null };
                        using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                        foreach (XmlNode node in doc.SelectNodes("/Defs/*"))
                        {
                            string thing = Text(node, "thingDef");
                            if (thing.Length == 0 || DefDatabase<ThingDef>.GetNamedSilentFail(thing) == null) continue;
                            try
                            {
                                var model = ReadModel(node, useBaseTextureSize);
                                model.LoadOrder = loadOrder;
                                if (!models.TryGetValue(thing, out var variants)) models.Add(thing, variants = new List<Model>());
                                // Replace only the same variant; unrelated path filters remain available.
                                int existing = variants.FindIndex(candidate => string.Equals(candidate.PathFilter, model.PathFilter, StringComparison.OrdinalIgnoreCase)
                                    && candidate.LoadOrder <= loadOrder);
                                if (existing >= 0) variants[existing] = model;
                                else variants.Add(model);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning("[RimVision] Could not read model for " + thing + " in " + file + ": " + ex.Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Log.Warning("[RimVision] Could not read model file " + file + ": " + ex.Message);
                    }
                }
                if (skipped != 0) Log.Warning("[RimVision] Optional model files skipped in " + folder + ": " + skipped + ". Procedural geometry remains available.");
            }
            catch (Exception ex) { Log.Warning("[RimVision] Could not read optional models in " + folder + ": " + ex.Message); }
        }

        static Model ReadModel(XmlNode node, bool useBaseTextureSize)
        {
            var model = new Model(useBaseTextureSize) { PathFilter = Text(node, "graphicPathContains"), HeightOffset = Number(Text(node, "heightOffset", "0")) };
            foreach (XmlNode anchor in node.SelectNodes("anchors/li"))
            {
                if (Text(anchor, "name") == "batteryBar" && !model.HasBatteryBar)
                {
                    model.HasBatteryBar = true;
                    model.BatteryBar = new Vector3(Number(Text(anchor, "x", "0")),
                        Number(Text(anchor, "height", "0")), Number(Text(anchor, "z", "0")));
                }
                string name = Text(anchor, "name");
                if (name != "fire" && name != "light") continue;
                var effect = new EffectAnchor
                {
                    Position = new Vector3(Number(Text(anchor, "x", "0")), Number(Text(anchor, "height", "0")), Number(Text(anchor, "z", "0"))),
                    Size = Number(Text(anchor, "size", "1")), Radius = Number(Text(anchor, "radius", "8")),
                    Intensity = Number(Text(anchor, "intensity", "1")), Always = Text(anchor, "always") == "true"
                };
                var color = Text(anchor, "color", "1,1,1").Split(',');
                if (color.Length != 3) throw new FormatException("Invalid effect color");
                effect.Color = new Color(Number(color[0]), Number(color[1]), Number(color[2]), 1);
                if (effect.Size < .05f || effect.Size > 5 || effect.Radius < .1f || effect.Radius > 32 || effect.Intensity < 0 || effect.Intensity > 8 ||
                    effect.Color.r < 0 || effect.Color.r > 1 || effect.Color.g < 0 || effect.Color.g > 1 || effect.Color.b < 0 || effect.Color.b > 1)
                    throw new FormatException("Invalid effect parameters");
                if (name == "fire")
                {
                    if (model.FireAnchor != null) throw new FormatException("Duplicate fire anchor");
                    model.FireAnchor = effect;
                }
                else
                {
                    if (model.LightAnchor != null) throw new FormatException("Duplicate light anchor");
                    model.LightAnchor = effect;
                }
            }
            var vertices = new List<Vector3>();
            foreach (XmlNode vertex in node.SelectNodes("vertices/li"))
                vertices.Add(new Vector3(Number(Text(vertex, "x", "0")), Number(Text(vertex, "height", "0")), Number(Text(vertex, "z", "0"))));
            model.Vertices = vertices.ToArray();
            string groundCutout = Text(node, "groundCutout");
            if (groundCutout.Length != 0)
            {
                var indices = groundCutout.Split(',');
                if (indices.Length < 3) throw new FormatException("Too few ground cutout vertices");
                model.GroundCutout = new int[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    int index = int.Parse(indices[i], CultureInfo.InvariantCulture);
                    if (index < 0 || index >= vertices.Count) throw new FormatException("Invalid ground cutout vertex index");
                    model.GroundCutout[i] = index;
                }
            }
            foreach (var vertex in model.Vertices) model.Height = Mathf.Max(model.Height, vertex.y + model.HeightOffset);
            var faces = new List<Face>();
            foreach (XmlNode nodeFace in node.SelectNodes("faces/li"))
            {
                string[] indices = Text(nodeFace, "indices").Split(',');
                if (indices.Length < 3) throw new FormatException("Too few face vertices");
                var face = new Face { Indices = new int[indices.Length], TwoSided = Text(nodeFace, "twoSided", "true") == "true",
                    TurretTop = Text(nodeFace, "turretTop") == "true",
                    RequiredLinks = Links(Text(nodeFace, "requiredLinks")), ForbiddenLinks = Links(Text(nodeFace, "forbiddenLinks")) };
                string direction = Text(nodeFace, "texture", "Thing");
                face.Texture = direction == "North" ? 0 : direction == "East" ? 1 : direction == "South" ? 2 : direction == "West" ? 3 : 4;
                string solidColor = Text(nodeFace, "solidColor");
                string uvTextureSize = Text(nodeFace, "uvTextureSize");
                if (uvTextureSize.Length != 0)
                {
                    var dimensions = uvTextureSize.Split(',');
                    if (dimensions.Length != 2) throw new FormatException("Invalid UV texture size");
                    face.UvTextureSize = new Vector2(Number(dimensions[0]), Number(dimensions[1]));
                    if (face.UvTextureSize.x <= 0 || face.UvTextureSize.y <= 0) throw new FormatException("Invalid UV texture size");
                }
                string texturePath = Text(nodeFace, "texturePath");
                if (texturePath.Length != 0)
                {
                    if (solidColor.Length != 0) throw new FormatException("Custom texture and solid color cannot be combined");
                    face.CustomTexture = ContentFinder<Texture2D>.Get(texturePath, false);
                    if (face.CustomTexture == null) throw new FormatException("Missing custom texture: " + texturePath);
                    face.TintWithThing = Text(nodeFace, "tintWithThing") == "true";
                }
                if (solidColor.Length != 0)
                {
                    var channels = solidColor.Split(',');
                    if (channels.Length != 3) throw new FormatException("Invalid solid color");
                    face.Solid = true;
                    face.Color = new Color(Number(channels[0]), Number(channels[1]), Number(channels[2]), 1);
                    face.TintWithThing = Text(nodeFace, "tintWithThing") == "true";
                }
                for (int i = 0; i < indices.Length; i++)
                {
                    face.Indices[i] = int.Parse(indices[i], CultureInfo.InvariantCulture);
                    if (face.Indices[i] < 0 || face.Indices[i] >= vertices.Count) throw new FormatException("Invalid vertex index");
                }
                Vector3 min = vertices[face.Indices[0]], max = min, normal = Vector3.zero;
                for (int i = 0; i < indices.Length; i++)
                {
                    var a = vertices[face.Indices[i]];
                    var b = vertices[face.Indices[(i + 1) % indices.Length]];
                    min = Vector3.Min(min, a); max = Vector3.Max(max, a);
                    normal.x += (a.y - b.y) * (a.z + b.z);
                    normal.y += (a.z - b.z) * (a.x + b.x);
                    normal.z += (a.x - b.x) * (a.y + b.y);
                }
                float normalLengthSquared = normal.sqrMagnitude;
                if (normalLengthSquared <= 1e-16f) continue;
                face.Normal = normal / Mathf.Sqrt(normalLengthSquared);
                string cornerNormals = Text(nodeFace, "normals");
                if (cornerNormals.Length != 0)
                {
                    var normals = cornerNormals.Split(';');
                    if (normals.Length != face.Indices.Length) throw new FormatException("Normal count differs from face");
                    face.Normals = new Vector3[normals.Length];
                    for (int i = 0; i < normals.Length; i++)
                    {
                        var channels = normals[i].Split(',');
                        if (channels.Length != 3) throw new FormatException("Invalid corner normal");
                        var vertexNormal = new Vector3(Number(channels[0]), Number(channels[1]), Number(channels[2]));
                        float lengthSquared = vertexNormal.sqrMagnitude;
                        if (lengthSquared <= 1e-16f || float.IsInfinity(lengthSquared)) throw new FormatException("Invalid corner normal");
                        face.Normals[i] = vertexNormal / Mathf.Sqrt(lengthSquared);
                    }
                }
                face.Min = min; face.Max = max;
                face.Uvs = ReadUvs(nodeFace, face, vertices, min, max);
                if (!face.Solid && face.CustomTexture == null) model.TextureMask |= 1 << face.Texture;
                model.HasLinks |= (face.RequiredLinks | face.ForbiddenLinks) != 0;
                model.HasTurretTop |= face.TurretTop;
                faces.Add(face);
            }
            if (faces.Count == 0) throw new FormatException("No valid faces");
            model.Faces = faces.ToArray();
            AssignOutlineGeometry(model);
            return model;
        }

        static void AssignOutlineGeometry(Model model)
        {
            var vertices = new Dictionary<Vector3, int>();
            var vertexIds = new int[model.Vertices.Length];
            for (int i = 0; i < vertexIds.Length; i++)
            {
                if (!vertices.TryGetValue(model.Vertices[i], out int id))
                {
                    id = vertices.Count;
                    vertices.Add(model.Vertices[i], id);
                }
                vertexIds[i] = id;
            }
            var parents = new int[model.Faces.Length];
            var ranks = new byte[parents.Length];
            var reversed = new bool[parents.Length];
            for (int i = 0; i < parents.Length; i++) parents[i] = i;
            int Root(int face)
            {
                if (parents[face] == face) return face;
                int parent = parents[face];
                parents[face] = Root(parent);
                reversed[face] ^= reversed[parent];
                return parents[face];
            }
            var edges = new Dictionary<(int, int, bool), (int Face, bool Forward)>();
            for (int i = 0; i < model.Faces.Length; i++)
            {
                var face = model.Faces[i];
                for (int j = 0; j < face.Indices.Length; j++)
                {
                    int a = vertexIds[face.Indices[j]], b = vertexIds[face.Indices[(j + 1) % face.Indices.Length]];
                    if (a == b) continue;
                    var edge = (Math.Min(a, b), Math.Max(a, b), face.TurretTop);
                    if (edges.TryGetValue(edge, out var neighbor))
                    {
                        int root = Root(i), neighborRoot = Root(neighbor.Face);
                        if (root == neighborRoot) continue;
                        bool reverse = reversed[i] ^ reversed[neighbor.Face] ^ ((a < b) == neighbor.Forward);
                        if (ranks[root] > ranks[neighborRoot])
                        {
                            parents[neighborRoot] = root;
                            reversed[neighborRoot] = reverse;
                        }
                        else
                        {
                            parents[root] = neighborRoot;
                            reversed[root] = reverse;
                            if (ranks[root] == ranks[neighborRoot]) ranks[neighborRoot]++;
                        }
                    }
                    else edges.Add(edge, (i, a < b));
                }
            }
            var volumes = new bool[model.Faces.Length];
            var bounds = new Bounds[model.Faces.Length];
            for (int i = 0; i < model.Faces.Length; i++)
            {
                int root = Root(i);
                if (Mathf.Abs(Vector3.Dot(model.Faces[root].Normal, model.Faces[i].Normal)) < .9999f)
                    volumes[root] = true;
                if (i == root) bounds[root] = new Bounds((model.Faces[i].Min + model.Faces[i].Max) * .5f,
                    model.Faces[i].Max - model.Faces[i].Min);
            }
            for (int i = 0; i < model.Faces.Length; i++)
            {
                int root = Root(i);
                bounds[root].Encapsulate(model.Faces[i].Min);
                bounds[root].Encapsulate(model.Faces[i].Max);
            }
            var signedVolumes = new double[model.Faces.Length];
            for (int i = 0; i < model.Faces.Length; i++)
            {
                int root = Root(i);
                if (!volumes[root]) continue;
                var indices = model.Faces[i].Indices;
                var center = bounds[root].center;
                var a = model.Vertices[indices[0]] - center;
                for (int j = 1; j < indices.Length - 1; j++)
                {
                    var b = model.Vertices[indices[j]] - center;
                    var c = model.Vertices[indices[j + 1]] - center;
                    signedVolumes[root] += Vector3.Dot(a, Vector3.Cross(b, c)) * (reversed[i] ? -1d : 1d);
                }
            }
            for (int i = 0; i < model.Faces.Length; i++)
            {
                int root = Root(i);
                model.Faces[i].OutlineOrientation = !volumes[root] ? 0 : (reversed[i] ^ (signedVolumes[root] < 0) ? -1 : 1);
            }
        }

        static Vector2[] ReadUvs(XmlNode node, Face face, List<Vector3> vertices, Vector3 min, Vector3 max)
        {
            var uvs = new Vector2[face.Indices.Length];
            bool flipU = Text(node, "flipU") == "true", flipV = Text(node, "flipV") == "true";
            string points = Text(node, "uvPoints"), pixels = Text(node, "uvPixels");
            if (points.Length != 0)
            {
                string[] pairs = points.Split(';');
                if (pairs.Length != uvs.Length) throw new FormatException("UV count differs from face");
                Vector2 low = new Vector2(float.MaxValue, float.MaxValue), high = -low;
                for (int i = 0; i < uvs.Length; i++)
                {
                    var pair = pairs[i].Split(',');
                    if (pair.Length != 2) throw new FormatException("Invalid UV pair");
                    uvs[i] = new Vector2(Number(pair[0]), Number(pair[1]));
                    low = Vector2.Min(low, uvs[i]); high = Vector2.Max(high, uvs[i]);
                }
                for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(flipU ? low.x + high.x - uvs[i].x : uvs[i].x,
                    flipV ? low.y + high.y - uvs[i].y : uvs[i].y);
                face.PixelUvs = true;
                return uvs;
            }
            Vector4 rect = new Vector4(0, 0, 1, 1);
            if (pixels.Length != 0)
            {
                var parts = pixels.Split(',');
                if (parts.Length != 4) throw new FormatException("Invalid UV rectangle");
                float x0 = Number(parts[0]), y0 = Number(parts[1]), x1 = Number(parts[2]), y1 = Number(parts[3]);
                if (x0 != x1 && y0 != y1)
                {
                    rect = new Vector4(Mathf.Min(x0, x1), Mathf.Max(y0, y1), Mathf.Max(x0, x1), Mathf.Min(y0, y1));
                    face.PixelUvs = true;
                }
            }
            var normal = face.Normal;
            float ax = Mathf.Abs(normal.x), ay = Mathf.Abs(normal.y), az = Mathf.Abs(normal.z);
            for (int i = 0; i < uvs.Length; i++)
            {
                var p = vertices[face.Indices[i]];
                float u, v;
                if (ay >= ax && ay >= az) { u = Mathf.InverseLerp(min.x, max.x, p.x); v = Mathf.InverseLerp(min.z, max.z, p.z); }
                else if (az >= ax) { u = Mathf.InverseLerp(min.x, max.x, p.x); v = Mathf.InverseLerp(min.y, max.y, p.y); }
                else { u = Mathf.InverseLerp(min.z, max.z, p.z); v = Mathf.InverseLerp(min.y, max.y, p.y); }
                if (flipU) u = 1 - u;
                if (flipV) v = 1 - v;
                uvs[i] = new Vector2(Mathf.Lerp(rect.x, rect.z, u), Mathf.Lerp(rect.y, rect.w, v));
            }
            return uvs;
        }

        static string Text(XmlNode node, string name, string fallback = "") => node[name]?.InnerText.Trim() ?? fallback;
        static float Number(string value)
        {
            float number = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (float.IsNaN(number) || float.IsInfinity(number)) throw new FormatException("Non-finite coordinate");
            return number;
        }
        static int Links(string value)
        {
            int bits = 0;
            foreach (var piece in value.Split(','))
            {
                switch (piece.Trim()) { case "North": bits |= 1; break; case "East": bits |= 2; break;
                    case "South": bits |= 4; break; case "West": bits |= 8; break; }
            }
            return bits;
        }
    }
}

