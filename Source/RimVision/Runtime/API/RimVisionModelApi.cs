using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    public struct RimVisionModelFrame
    {
        public RimVisionPose Pose { get; set; }
        public Vector3 Scale { get; set; }
        public bool Visible { get; set; }
        public bool DrawDefault { get; set; }
        public Bounds? PickingBounds { get; set; }
        internal bool Valid => Pose.Valid && RimVisionPawnAnimationFrame.ValidScale(Scale) &&
            (!PickingBounds.HasValue || RimVisionControl.Finite(PickingBounds.Value.center) && RimVisionControl.Finite(PickingBounds.Value.size) &&
            PickingBounds.Value.size.x > 0 && PickingBounds.Value.size.y > 0 && PickingBounds.Value.size.z > 0);
    }
    public sealed class RimVisionModelContext
    {
        readonly List<UnityEngine.Object> _resources = new List<UnityEngine.Object>();
        readonly Func<Texture, Color, Material> _material;
        public Thing Thing { get; }
        public Map Map { get; }
        public GameObject Root { get; }
        public Pawn Observer { get; internal set; }
        public RimVisionPose DefaultPose { get; internal set; }
        public bool IsOpen { get; private set; } = true;
        internal RimVisionModelContext(Thing thing, GameObject root, Func<Texture, Color, Material> material)
        { Thing = thing; Map = thing.Map; Root = root; _material = material; }
        public T OwnResource<T>(T resource) where T : UnityEngine.Object
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(RimVisionModelContext));
            if (!(resource is Mesh || resource is Material || resource is Texture)) throw new ArgumentException("Only owned meshes, materials and textures are accepted.");
            if (!_resources.Contains(resource)) _resources.Add(resource);
            return resource;
        }
        public Material CreateSurfaceMaterial(Texture texture, Color color) => OwnResource(_material(texture, color));
        internal void Close()
        {
            if (!IsOpen) return;
            IsOpen = false; if (Root != null) { Root.SetActive(false); UnityEngine.Object.Destroy(Root); }
            foreach (var resource in _resources)
            {
                if (resource == null) continue;
                if (resource is RenderTexture target) { if (RenderTexture.active == target) RenderTexture.active = null; target.Release(); }
                if (resource != null) UnityEngine.Object.Destroy(resource);
            }
            _resources.Clear();
        }
    }
    public abstract class RimVisionModelProvider
    {
        public abstract bool Handles(Thing thing);
        public virtual bool HideDefault => true;
        public abstract void Create(RimVisionModelContext context);
        public virtual void Update(RimVisionModelContext context, ref RimVisionModelFrame frame) { }
        public virtual void Dispose(RimVisionModelContext context) { }
    }
    public static class RimVisionModels
    {
        internal static readonly ApiRegistry<RimVisionModelProvider> Providers = new ApiRegistry<RimVisionModelProvider>("models");
        public static IDisposable RegisterProvider(string id, RimVisionModelProvider provider, int priority = 0) =>
            Providers.Add(id, provider ?? throw new ArgumentNullException(nameof(provider)), priority);
    }
}

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed partial class ObjectPresentation
        {
            sealed class ApiModel
            {
                internal API.ApiRegistry<API.RimVisionModelProvider>.Entry Entry;
                internal API.RimVisionModelContext Context;
                internal bool DrawDefault;
                internal Bounds? PickingBounds;
            }
            readonly Dictionary<Thing, ApiModel> _apiModels = new Dictionary<Thing, ApiModel>();
            readonly List<Thing> _apiModelWork = new List<Thing>();
            readonly HashSet<Thing> _failedApiModels = new HashSet<Thing>();
            int _apiModelRevision = -1;

            API.RimVisionPose DefaultApiModelPose(Thing thing)
            {
                var center = thing.TrueCenter();
                center.y = thing.def.category == ThingCategory.Item ? _view.GroundThingSurfaceHeight(center) + GroundItemHeightOffset(thing) : 0;
                return new API.RimVisionPose(center, Quaternion.Euler(0, thing.Rotation.AsAngle, 0));
            }
            internal bool ReplaceApiModel(Thing thing)
            {
                if (_apiModelRevision < 0) _apiModelRevision = API.RimVisionModels.Providers.Revision;
                if (_view.simulationOnly || thing == null || !thing.Spawned || thing.Map != _view.map || _view.map.fogGrid.IsFogged(thing.Position) ||
                    !_view.DrawSpatialThing(thing) || _failedApiModels.Contains(thing)) return false;
                if (_apiModels.TryGetValue(thing, out var existing)) return !existing.DrawDefault;
                if (API.RimVisionModels.Providers.Entries.Length == 0) return false;
                Rand.PushState();
                try
                {
                    foreach (var entry in API.RimVisionModels.Providers.Entries)
                    {
                        if (!entry.Active) continue;
                        API.RimVisionModelContext context = null;
                        try
                        {
                            if (!entry.Value.Handles(thing)) continue;
                            var root = new GameObject("RimVision model " + entry.Id) { hideFlags = HideFlags.DontSave, layer = _view.root.layer };
                            root.transform.SetParent(_view.root.transform, false);
                            var defaultPose = DefaultApiModelPose(thing);
                            root.transform.localPosition = defaultPose.Position; root.transform.localRotation = defaultPose.Rotation;
                            context = new API.RimVisionModelContext(thing, root, _view.CreateExtensionSurfaceMaterial);
                            context.DefaultPose = defaultPose;
                            context.Observer = _view.ControlledPawn;
                            entry.Value.Create(context);
                            if (root == null) throw new InvalidOperationException("Model creation destroyed its root.");
                            foreach (var child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = _view.root.layer;
                            var model = new ApiModel { Entry = entry, Context = context, DrawDefault = !entry.Value.HideDefault };
                            _apiModels.Add(thing, model);
                            return !model.DrawDefault;
                        }
                        catch (Exception error)
                        {
                            API.RimVisionDiagnostics.ReportFault("models", entry.Id, error, true);
                            if (context != null)
                            {
                                try { entry.Value.Dispose(context); }
                                catch (Exception cleanup) { API.RimVisionDiagnostics.ReportFault("models.dispose", entry.Id, cleanup); }
                                finally { context.Close(); }
                            }
                            _failedApiModels.Add(thing); return false;
                        }
                    }
                }
                finally { Rand.PopState(); }
                return false;
            }

            internal void UpdateApiModels()
            {
                if (_view.simulationOnly) return;
                if (_apiModels.Count == 0 && API.RimVisionModels.Providers.Entries.Length == 0) { _apiModelRevision = API.RimVisionModels.Providers.Revision; return; }
                if (_apiModelRevision != API.RimVisionModels.Providers.Revision)
                {
                    _apiModelRevision = API.RimVisionModels.Providers.Revision;
                    _failedApiModels.Clear();
                    DisposeApiModels();
                    foreach (var thing in _view.map.listerThings.AllThings)
                        if (!(thing is Pawn) && ReplaceApiModel(thing)) _view.OnThingChanged(_view.map, thing);
                }
                if (API.RimVisionModels.Providers.Entries.Length > 0)
                    foreach (var pawn in _view.map.mapPawns.AllPawnsSpawned) ReplaceApiModel(pawn);
                if (_apiModels.Count == 0) return;
                _apiModelWork.Clear(); _apiModelWork.AddRange(_apiModels.Keys);
                foreach (var thing in _apiModelWork)
                {
                    var model = _apiModels[thing];
                    if (model.Context.Root == null || !model.Entry.Active || !thing.Spawned || thing.Destroyed || thing.Map != _view.map)
                    { RemoveApiModel(thing, model); continue; }
                    var context = model.Context;
                    var pose = DefaultApiModelPose(thing);
                    var scale = Vector3.one;
                    bool visible = !_view.map.fogGrid.IsFogged(thing.Position) && _view.DrawSpatialThing(thing);
                    Transform pawnCard = null;
                    var bodyPawn = thing as Pawn ?? (thing as Corpse)?.InnerPawn;
                    if (bodyPawn != null && _view.pawnCards.TryGetValue(bodyPawn, out pawnCard))
                    { pose = new API.RimVisionPose(pawnCard.localPosition, pawnCard.localRotation); scale = pawnCard.localScale; visible &= pawnCard.gameObject.activeSelf; }
                    context.Observer = _view.ControlledPawn; context.DefaultPose = pose;
                    var frame = new API.RimVisionModelFrame { Pose = pose, Scale = scale, Visible = visible, DrawDefault = model.DrawDefault, PickingBounds = model.PickingBounds };
                    Rand.PushState();
                    try
                    {
                        model.Entry.Value.Update(context, ref frame);
                        if (!frame.Valid) throw new InvalidOperationException("Invalid model frame.");
                        context.Root.transform.localPosition = frame.Pose.Position;
                        context.Root.transform.localRotation = frame.Pose.Rotation.normalized;
                        context.Root.transform.localScale = frame.Scale;
                        context.Root.SetActive(visible && frame.Visible);
                        model.PickingBounds = frame.PickingBounds;
                        if (frame.DrawDefault != model.DrawDefault)
                        { model.DrawDefault = frame.DrawDefault; if (!(thing is Pawn)) _view.OnThingChanged(_view.map, thing); }
                        if (pawnCard != null && !model.DrawDefault) pawnCard.gameObject.SetActive(false);
                    }
                    catch (Exception error)
                    { API.RimVisionDiagnostics.ReportFault("models", model.Entry.Id, error, true); _failedApiModels.Add(thing); RemoveApiModel(thing, model); }
                    finally { Rand.PopState(); }
                }
                _apiModelWork.Clear();
            }
            internal bool TryRaycastApiModel(Thing thing, Ray ray, out float distance, out bool handled)
            {
                distance = 0; handled = false;
                if (!_apiModels.TryGetValue(thing, out var model) || !model.PickingBounds.HasValue || model.Context.Root == null) return false;
                handled = true;
                if (!model.Context.Root.activeSelf) return false;
                var transform = model.Context.Root.transform;
                var inverse = Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale).inverse;
                var direction = inverse.MultiplyVector(ray.direction);
                float factor = direction.magnitude;
                if (factor < .000001f || !API.RimVisionControl.Finite(factor)) return false;
                if (!model.PickingBounds.Value.IntersectRay(new Ray(inverse.MultiplyPoint3x4(ray.origin), direction / factor), out float local)) return false;
                distance = local / factor; return true;
            }
            void RemoveApiModel(Thing thing, ApiModel model)
            {
                _apiModels.Remove(thing);
                Rand.PushState();
                try { model.Entry.Value.Dispose(model.Context); }
                catch (Exception error) { API.RimVisionDiagnostics.ReportFault("models.dispose", model.Entry.Id, error); }
                finally { model.Context.Close(); Rand.PopState(); }
                if (!_view._apiDisposed && thing.Spawned && thing.Map == _view.map && !(thing is Pawn)) _view.OnThingChanged(_view.map, thing);
            }
            internal void DisposeApiModels()
            {
                _apiModelWork.Clear(); _apiModelWork.AddRange(_apiModels.Keys);
                foreach (var thing in _apiModelWork) RemoveApiModel(thing, _apiModels[thing]);
                _apiModelWork.Clear();
            }
        }
    }
}
