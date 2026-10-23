using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly FireRenderer _fireRenderer;

        void TrackFireThing(Thing thing) => _fireRenderer.TrackFireThing(thing);

        void TrackModelFire(Thing thing, OptionalModelLibrary.EffectAnchor anchor, Vector3 position) =>
            _fireRenderer.TrackModelFire(thing, anchor, position);

        void ClearModelFire(Thing thing) => _fireRenderer.ClearModelFire(thing);

        void UpdateFireVisuals() => _fireRenderer.UpdateFireVisuals();

        void DisposeFireVisuals() => _fireRenderer?.DisposeFireVisuals();

        sealed class FireRenderer
        {
            readonly MapView _view;

            internal FireRenderer(MapView view)
            {
                _view = view;
            }

            sealed class FireVisual
            {
                public GameObject Object;
                public CompFireOverlayBase Overlay;
                public CompRefuelable Fuel;
                public float FloorTorchHeight;
                public OptionalModelLibrary.EffectAnchor Anchor;
                public Vector3 AnchorPosition;
                public CompPowerTrader Power;
                public bool HasGameLight;
            }

            readonly Dictionary<Thing, FireVisual> _fireVisuals = new Dictionary<Thing, FireVisual>();
            readonly List<Thing> _removedFires = new List<Thing>();
            Mesh _fireMesh;
            Material _fireMaterial, _darkFireMaterial;
            static readonly int _fireTicksId = Shader.PropertyToID("_Rim3DFireTicks");
            static readonly int _firePhaseId = Shader.PropertyToID("_Phase");

            internal void TrackFireThing(Thing thing)
            {
                if (_fireVisuals.ContainsKey(thing)) return;
                var overlay = thing.TryGetComp<CompFireOverlayBase>();
                if (!(thing is Fire) && overlay == null) return;
                _fireVisuals.Add(thing, new FireVisual
                {
                    Overlay = overlay,
                    Fuel = thing.TryGetComp<CompRefuelable>()
                });
            }

            internal void TrackModelFire(Thing thing, OptionalModelLibrary.EffectAnchor anchor, Vector3 position)
            {
                if (!_fireVisuals.TryGetValue(thing, out var visual))
                {
                    visual = new FireVisual { Overlay = thing.TryGetComp<CompFireOverlayBase>(), Fuel = thing.TryGetComp<CompRefuelable>() };
                    _fireVisuals.Add(thing, visual);
                }
                visual.Anchor = anchor;
                visual.AnchorPosition = position;
                visual.Power = thing.TryGetComp<CompPowerTrader>();
                visual.HasGameLight = thing.TryGetComp<CompGlower>() != null;
            }

            internal void ClearModelFire(Thing thing)
            {
                if (!_fireVisuals.TryGetValue(thing, out var visual) || visual.Anchor == null) return;
                if (visual.Object != null) UnityEngine.Object.Destroy(visual.Object);
                visual.Object = null;
                visual.Anchor = null;
                visual.Power = null;
                if (visual.Overlay == null && !(thing is Fire)) _fireVisuals.Remove(thing);
            }

            Material GetFireMaterial(bool dark)
            {
                var material = dark ? _darkFireMaterial : _fireMaterial;
                if (material != null) return material;
                var shader = ModAssets.LoadShader("Rim3D/Fire");
                if (shader == null) return null;
                string path = dark ? "textures/things/special/darklight/DarkFire" : "textures/things/special/fire/Fire";
                var a = ModAssets.LoadTexture(path + "A");
                var b = ModAssets.LoadTexture(path + "B");
                var c = ModAssets.LoadTexture(path + "C");
                if (a == null) return null;
                material = new Material(shader) { hideFlags = HideFlags.DontSave };
                material.SetTexture("_MainTex", a);
                material.SetTexture("_FrameB", b != null ? b : a);
                material.SetTexture("_FrameC", c != null ? c : a);
                _view.materials.Add(material);
                if (dark) _darkFireMaterial = material; else _fireMaterial = material;
                return material;
            }

            internal void UpdateFireVisuals()
            {
                // Only registered fires are visited; no full-map scan per frame.
                Shader.SetGlobalFloat(_fireTicksId, Find.TickManager.TicksGame);
                _removedFires.Clear();
                foreach (var entry in _fireVisuals)
                {
                    var thing = entry.Key;
                    var visual = entry.Value;
                    if (thing.Destroyed || !thing.Spawned || thing.Map != _view.map)
                    {
                        if (visual.Object != null) UnityEngine.Object.Destroy(visual.Object);
                        _removedFires.Add(thing);
                        continue;
                    }
                    var fire = thing as Fire;
                    bool floorTorch = visual.Overlay != null &&
                        visual.Anchor == null &&
                        (thing.def.defName == "TorchLamp" || thing.def.defName == "DarktorchFungus");
                    var cell = fire != null && fire.parent != null ? fire.parent.Position : thing.Position;
                    // Building fires can be cell fires rather than attached Things.
                    var burningBuilding = fire == null ? null : fire.parent as Building ??
                        (fire.parent == null && cell.InBounds(_view.map) ? cell.GetEdifice(_view.map) : null);
                    bool visible = Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.FireEffects) && cell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(cell) &&
                        (visual.Anchor != null && visual.Anchor.Always ||
                         (visual.Fuel == null || visual.Fuel.HasFuel) && (visual.Anchor == null ||
                          (visual.Power == null || visual.Power.PowerOn) && (!visual.HasGameLight || _view._gameLighting.HasGlowingThing(thing.thingIDNumber))));
                    if (!visible)
                    {
                        if (visual.Object != null) visual.Object.SetActive(false);
                        continue;
                    }
                    if (visual.Object == null)
                    {
                        var material = GetFireMaterial(visual.Overlay is CompDarklightOverlay);
                        if (material == null) continue;
                        if (_fireMesh == null)
                        {
                            _fireMesh = new Mesh { name = "RimVision shared flame", hideFlags = HideFlags.DontSave };
                            _fireMesh.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0),
                                new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0) };
                            _fireMesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                            _fireMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                            // The shader rotates the card towards the camera, including overhead views.
                            _fireMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1.6f);
                            _view.meshes.Add(_fireMesh);
                        }
                        visual.Object = new GameObject("RimVision flame") { layer = Layer, hideFlags = HideFlags.DontSave };
                        visual.Object.transform.SetParent(_view.root.transform, false);
                        visual.Object.AddComponent<MeshFilter>().sharedMesh = _fireMesh;
                        var renderer = visual.Object.AddComponent<MeshRenderer>();
                        renderer.sharedMaterial = material;
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                        renderer.receiveShadows = false;
                        var properties = new MaterialPropertyBlock();
                        properties.SetFloat(_firePhaseId, (thing.thingIDNumber & 1023) * .37f);
                        renderer.SetPropertyBlock(properties);
                        if (floorTorch)
                        {
                            var model = _view.optionalModels?.Find(thing.def.defName, thing.Graphic.path);
                            // Cache the head height, excluding the transparent top of the model card.
                            visual.FloorTorchHeight = thing.def.defName == "DarktorchFungus" ? 1.08f :
                                model != null ? model.Height - .25f : .65f;
                        }
                    }
                    float size = fire != null ? Mathf.Clamp(fire.fireSize / 1.2f, .08f, 1.2f)
                        : visual.Anchor != null ? visual.Anchor.Size : Mathf.Max(.05f, visual.Overlay.FireSize);
                    var position = thing.DrawPos;
                    position.y = .025f;
                    if (fire != null && fire.parent is Pawn burningPawn) position.y = burningPawn.Downed ? .15f : .45f;
                    else if (burningBuilding != null)
                    {
                        position = burningBuilding.TrueCenter();
                        position.y = .65f;
                    }
                    if (visual.Anchor != null) position = visual.AnchorPosition;
                    else if (visual.Overlay != null)
                    {
                        if (IsWallTorch(thing)) position = WallTorchFirePosition(thing);
                        else if (floorTorch)
                        {
                            // The flat-game Z offset represents sprite height, not a world-space displacement.
                            position = thing.TrueCenter();
                            position.y = visual.FloorTorchHeight;
                        }
                        else if (thing.def.defName == "Burnbong" || thing.def.defName == "Pyre")
                        {
                            // Vanilla Z offsets encode sprite height; keep the fire in the actual bowl/log stack.
                            position = thing.TrueCenter();
                            position.y = thing.def.defName == "Burnbong" ? .72f : .56f;
                        }
                        else
                        {
                            var offset = visual.Overlay.Props.DrawOffsetForRot(thing.Rotation);
                            position.x += offset.x; position.z += offset.z;
                            // Raise small brazier flames above the furniture surface.
                            if (thing is Building && thing.def.defName != "Campfire") position.y += .65f;
                        }
                    }
                    float height = size * (visual.Overlay != null || visual.Anchor != null ? .9f : 1.65f); if (cell.InBounds(_view.map) && _view.map.roofGrid.Roofed(cell)) height = Mathf.Min(height, Mathf.Max(.12f, 1.90f - position.y));
                    // Match the billboard's up axis so its base stays on the torch as the camera tilts.
                    position += (floorTorch || visual.Anchor != null ? _view.camera.transform.up : Vector3.up) * (height * .5f);
                    if (burningBuilding != null) position = FireOutsideBuilding(burningBuilding, position);
                    visual.Object.transform.localPosition = position;
                    visual.Object.transform.localScale = new Vector3(size * 1.3f, height, 1);
                    visual.Object.SetActive(true);
                }
                foreach (var thing in _removedFires) _fireVisuals.Remove(thing);
            }

            Vector3 FireOutsideBuilding(Building building, Vector3 position)
            {
                float buildingHeight = building is Building_Door ? 2f :
                    building.def.Fillage == FillCategory.Full || building.def.mineable ? 2f : .65f;
                var center = building.TrueCenter();
                center.y = buildingHeight * .5f;
                var size = building.RotatedSize;
                var half = new Vector3(size.x * .5f, buildingHeight * .5f, size.z * .5f);
                if (building is Building_Door door)
                {
                    // Include the sliding panels at their current opening fraction.
                    float slide = door.def.size.x * .5f * Mathf.SmoothStep(0, 1, DoorOpenFraction(door));
                    if (door.Rotation.AsInt % 2 == 0) half.x += slide; else half.z += slide;
                }
                // The billboard lies in the camera plane. Move that entire plane past
                // the building's nearest supporting plane, so even a small flame at
                // eye level cannot be buried in the wall. Depth testing remains on.
                var outward = -_view.camera.transform.forward;
                float extent = Mathf.Abs(outward.x) * half.x + Mathf.Abs(outward.y) * half.y +
                    Mathf.Abs(outward.z) * half.z;
                float offset = extent + .04f - Vector3.Dot(position - center, outward);
                return position + outward * Mathf.Max(0, offset);
            }

            internal void DisposeFireVisuals()
            {
                _fireVisuals.Clear(); _removedFires.Clear();
                _fireMesh = null; _fireMaterial = null; _darkFireMaterial = null;
            }
        }
    }
}
