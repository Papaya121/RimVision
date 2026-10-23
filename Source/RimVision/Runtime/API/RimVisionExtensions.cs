using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    public abstract class RimVisionExtension : IDisposable
    {
        public virtual bool RetainOnMapTransfer => false;
        public virtual void Open(RimVisionView view) { }
        public virtual void StateChanged(RimVisionView view, RimVisionViewState previous) { }
        public virtual void Update(RimVisionView view) { }
        public virtual void BeforeRender(RimVisionView view) { }
        public virtual void DrawHud(RimVisionView view, Rect viewport) { }
        public virtual void PresentationEvent(RimVisionView view, RimVisionViewEvent notification) { }
        public virtual void Lifecycle(RimVisionView view, RimVisionLifecycleEvent notification) { }
        public virtual void AnimationEvent(RimVisionView view, RimVisionAnimationEvent notification) { }
        public virtual void JobChanged(RimVisionView view, RimVisionJobEvent notification) { }
        public virtual void ActionCompleted(RimVisionView view, RimVisionActionRequest request) { }
        public virtual void Dispose() { }
    }

    public sealed partial class RimVisionView
    {
        Func<RimVisionViewState> _state;
        Func<string, Thing, IntVec3, int, bool> _request;
        Action<IntVec3> _focus;
        Action<Pawn> _refreshPawn;
        Action<Thing> _refreshThing;
        Transform _parent;
        Camera _camera;
        readonly List<UnityEngine.Object> _resources = new List<UnityEngine.Object>();
        readonly List<GameObject> _layerObjects = new List<GameObject>();
        SpatialViewBindings _spatial;
        GameObject _root;
        readonly int[] _inputBlocks = new int[5];
        internal string ExtensionId { get; set; }
        public bool IsOpen => _state != null;
        public RimVisionViewState State => IsOpen ? _state() : throw new ObjectDisposedException(nameof(RimVisionView));
        internal RimVisionInputChannels BlockedChannels
        {
            get
            {
                var result = RimVisionInputChannels.None;
                if (IsOpen) for (int i = 0; i < _inputBlocks.Length; i++) if (_inputBlocks[i] > 0) result |= (RimVisionInputChannels)(1 << i);
                return result;
            }
        }

        internal RimVisionView(Func<RimVisionViewState> state, Func<string, Thing, IntVec3, int, bool> request,
            Action<IntVec3> focus, Action<Pawn> refreshPawn, Action<Thing> refreshThing, Transform parent, Camera camera,
            SpatialViewBindings spatial, ExtensionViewOperations operations)
        {
            _state = state; _request = request; _focus = focus; _refreshPawn = refreshPawn;
            _refreshThing = refreshThing; _parent = parent; _camera = camera;
            _spatial = spatial; _operations = operations;
        }

        public bool RequestAction(string id, Thing target, IntVec3 cell, int value = 0) =>
            IsOpen && _request(id, target, cell, value);

        public bool RequestLayerAction(string id, Map targetMap, Thing target, IntVec3 cell, int value = 0) =>
            IsOpen && _spatial.Request(id, targetMap, target, cell, value);

        public float? LayerOffset(Map map) => IsOpen ? _spatial.Offset(map) : null;

        public bool PrepareLayer(Map map) => IsOpen && _spatial.Prepare(map);

        public bool TryPick(Ray ray, float maximumDistance, out RimVisionSpatialHit hit)
        {
            hit = default;
            return IsOpen && _spatial.Pick(ray, maximumDistance, out hit);
        }

        public bool TryProject(Map map, Vector3 mapPosition, Rect viewport, out Vector2 position)
        {
            position = default;
            var offset = LayerOffset(map);
            return offset.HasValue && TryProject(mapPosition + Vector3.up * offset.Value, viewport, out position);
        }

        public GameObject CreateLayerObject(Map map, string name)
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(RimVisionView));
            var parent = _spatial.Root(map);
            if (parent == null) throw new ArgumentException("Map is not a rendered layer of this view.", nameof(map));
            var result = new GameObject(name) { hideFlags = HideFlags.DontSave, layer = parent.gameObject.layer };
            result.transform.SetParent(parent, false);
            _layerObjects.Add(result);
            return result;
        }

        public bool RefreshPawn(Pawn pawn)
        {
            if (!IsOpen || pawn?.Spawned != true || pawn.Map != State.Map) return false;
            _refreshPawn(pawn);
            return true;
        }

        public bool RefreshThing(Thing thing)
        {
            if (!IsOpen || thing?.Spawned != true || thing.Map != State.Map) return false;
            _refreshThing(thing);
            return true;
        }

        public bool FocusCell(IntVec3 cell)
        {
            if (!IsOpen || !cell.InBounds(State.Map) || State.FirstPerson) return false;
            _focus(cell);
            return true;
        }

        public bool TryProject(Vector3 mapPosition, Rect viewport, out Vector2 position)
        {
            position = default;
            if (!IsOpen) return false;
            var point = _camera.WorldToViewportPoint(_parent.TransformPoint(mapPosition));
            position = new Vector2(viewport.x + point.x * viewport.width, viewport.yMax - point.y * viewport.height);
            return point.z > 0 && point.x >= 0 && point.x <= 1 && point.y >= 0 && point.y <= 1;
        }

        public GameObject CreateSceneObject(string name)
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(RimVisionView));
            if (_root == null)
            {
                _root = new GameObject("RimVision API extension") { hideFlags = HideFlags.DontSave, layer = _parent.gameObject.layer };
                _root.transform.SetParent(_parent, false);
            }
            var result = new GameObject(name) { hideFlags = HideFlags.DontSave, layer = _root.layer };
            result.transform.SetParent(_root.transform, false);
            return result;
        }

        public T OwnResource<T>(T resource) where T : UnityEngine.Object
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(RimVisionView));
            if (resource == null || !(resource is Mesh || resource is Material || resource is Texture))
                throw new ArgumentException("Only newly created meshes, materials and textures may be owned.", nameof(resource));
            if (!_resources.Contains(resource)) _resources.Add(resource);
            return resource;
        }

        public Material CreateSurfaceMaterial(Texture texture, Color color)
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(RimVisionView));
            return OwnResource(_spatial.SurfaceMaterial(texture, color));
        }

        public IDisposable BlockInput() => BlockInput(RimVisionInputChannels.All);

        public IDisposable BlockInput(RimVisionInputChannels channels)
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(RimVisionView));
            if (channels == RimVisionInputChannels.None || (channels & ~RimVisionInputChannels.All) != 0) throw new ArgumentOutOfRangeException(nameof(channels));
            for (int i = 0; i < _inputBlocks.Length; i++) if (((int)channels & (1 << i)) != 0) _inputBlocks[i]++;
            return new InputBlock(this, channels);
        }

        sealed class InputBlock : IDisposable
        {
            RimVisionView _view;
            readonly RimVisionInputChannels _channels;
            internal InputBlock(RimVisionView view, RimVisionInputChannels channels) { _view = view; _channels = channels; }
            public void Dispose()
            {
                if (_view == null) return;
                for (int i = 0; i < _view._inputBlocks.Length; i++) if (((int)_channels & (1 << i)) != 0) _view._inputBlocks[i]--;
                _view = null;
            }
        }

        internal void Close()
        {
            if (!IsOpen) return;
            CloseEffects();
            _state = null; _request = null; _focus = null; _refreshPawn = null; _refreshThing = null;
            _spatial = default; _operations = default;
            _attachments.Clear();
            if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
            foreach (var item in _layerObjects)
                if (item != null) { item.SetActive(false); UnityEngine.Object.Destroy(item); }
            _layerObjects.Clear();
            foreach (var resource in _resources)
            {
                if (resource == null) continue;
                if (resource is RenderTexture texture)
                {
                    if (RenderTexture.active == texture) RenderTexture.active = null;
                    texture.Release();
                }
                UnityEngine.Object.Destroy(resource);
            }
            _resources.Clear();
        }

        internal void Rebind(Func<RimVisionViewState> state, Func<string, Thing, IntVec3, int, bool> request,
            Action<IntVec3> focus, Action<Pawn> refreshPawn, Action<Thing> refreshThing, Transform parent, Camera camera,
            SpatialViewBindings spatial, ExtensionViewOperations operations)
        {
            _state = state; _request = request; _focus = focus; _refreshPawn = refreshPawn;
            _refreshThing = refreshThing; _parent = parent; _camera = camera; _spatial = spatial; _operations = operations;
            if (_root != null) _root.transform.SetParent(parent, false);
        }
    }

    internal sealed class RimVisionExtensionSession : IDisposable
    {
        sealed class Instance
        {
            internal ApiRegistry<Func<RimVisionExtension>>.Entry Registration;
            internal RimVisionExtension Extension;
            internal RimVisionView View;
            internal RimVisionViewState Previous;
            internal bool Closing;
        }

        readonly List<Instance> _instances = new List<Instance>();
        bool _disposed;
        internal bool InputBlocked => BlockedChannels == RimVisionInputChannels.All;
        internal RimVisionInputChannels BlockedChannels
        {
            get
            {
                var result = RimVisionInputChannels.None;
                foreach (var instance in _instances) result |= instance.View.BlockedChannels;
                return result;
            }
        }

        internal RimVisionInputBlockInfo[] GetInputBlocks()
        {
            var result = new List<RimVisionInputBlockInfo>();
            foreach (var instance in _instances)
                if (instance.View.IsOpen && instance.View.BlockedChannels != RimVisionInputChannels.None)
                    result.Add(new RimVisionInputBlockInfo(instance.Registration.Id, instance.View.BlockedChannels));
            return result.ToArray();
        }

        internal RimVisionExtensionSession(Func<RimVisionViewState> state,
            Func<string, Thing, IntVec3, int, bool> request, Action<IntVec3> focus,
            Action<Pawn> refreshPawn, Action<Thing> refreshThing, Transform parent, Camera camera, SpatialViewBindings spatial, ExtensionViewOperations operations)
        {
            foreach (var registration in RimVisionApi.Extensions.Entries)
            {
                if (!registration.Active) continue;
                var instance = new Instance { Registration = registration,
                    View = new RimVisionView(state, request, focus, refreshPawn, refreshThing, parent, camera, spatial, operations) { ExtensionId = registration.Id }, Previous = state() };
                _instances.Add(instance);
                Invoke(instance, 0, default);
            }
        }

        readonly Queue<RimVisionAnimationEvent> _animationEvents = new Queue<RimVisionAnimationEvent>();
        internal void NotifyAnimation(RimVisionAnimationEvent notification) { if (!_disposed && _animationEvents.Count < 256) _animationEvents.Enqueue(notification); }
        readonly Queue<RimVisionViewEvent> _events = new Queue<RimVisionViewEvent>();
        internal void Notify(RimVisionViewEvent notification) { if (!_disposed) _events.Enqueue(notification); }
        internal void JobChanged(RimVisionJobEvent notification)
        {
            foreach (var instance in _instances)
                if (instance.View.IsOpen && instance.Registration.Active) Invoke(instance, 8, default, job: notification);
        }
        internal void Complete(RimVisionActionRequest request)
        {
            foreach (var instance in _instances)
                if (instance.View.IsOpen && instance.Registration.Active) Invoke(instance, 5, default, default, request);
        }
        internal void Update()
        {
            InvokeAll(1, default);
            int animationCount = _animationEvents.Count;
            while (animationCount-- > 0 && _animationEvents.Count > 0)
            {
                var animation = _animationEvents.Dequeue();
                foreach (var instance in _instances)
                    if (instance.View.IsOpen && instance.Registration.Active) Invoke(instance, 7, default, animation: animation);
            }
            int count = _events.Count;
            while (count-- > 0 && _events.Count > 0)
            {
                var notification = _events.Dequeue();
                foreach (var instance in _instances)
                    if (instance.View.IsOpen && instance.Registration.Active) Invoke(instance, 4, default, notification);
            }
        }
        internal void BeforeRender() => InvokeAll(2, default);
        internal void DrawHud(Rect viewport) => InvokeAll(3, viewport);

        internal void Rebind(Func<RimVisionViewState> state, Func<string, Thing, IntVec3, int, bool> request,
            Action<IntVec3> focus, Action<Pawn> refreshPawn, Action<Thing> refreshThing, Transform parent, Camera camera,
            SpatialViewBindings spatial, ExtensionViewOperations operations)
        {
            foreach (var instance in _instances)
            {
                if (!instance.Registration.Active) { Close(instance, RimVisionViewCloseReason.RegistrationRemoved); continue; }
                bool retain = false;
                Rand.PushState();
                try { retain = instance.View.IsOpen && instance.Extension.RetainOnMapTransfer; }
                catch (Exception error)
                {
                    Log.Error("[RimVision API] Extension transfer failed: " + instance.Registration.Id + "\n" + error);
                    Close(instance);
                    continue;
                }
                finally { Rand.PopState(); }
                if (retain)
                    instance.View.Rebind(state, request, focus, refreshPawn, refreshThing, parent, camera, spatial, operations);
                else
                {
                    Close(instance, RimVisionViewCloseReason.MapChanged);
                    instance.Closing = false;
                    instance.View = new RimVisionView(state, request, focus, refreshPawn, refreshThing, parent, camera, spatial, operations) { ExtensionId = instance.Registration.Id };
                    instance.Previous = state();
                    Invoke(instance, 0, default);
                }
            }
            foreach (var registration in RimVisionApi.Extensions.Entries)
            {
                if (!registration.Active) continue;
                bool exists = false;
                foreach (var instance in _instances)
                    if (instance.Registration == registration) { exists = true; break; }
                if (exists) continue;
                var added = new Instance { Registration = registration,
                    View = new RimVisionView(state, request, focus, refreshPawn, refreshThing, parent, camera, spatial, operations) { ExtensionId = registration.Id }, Previous = state() };
                _instances.Add(added);
                Invoke(added, 0, default);
            }
        }

        void InvokeAll(int phase, Rect viewport)
        {
            if (_disposed) return;
            foreach (var instance in _instances)
            {
                if (!instance.Registration.Active) { Close(instance, RimVisionViewCloseReason.RegistrationRemoved); continue; }
                if (instance.View.IsOpen) Invoke(instance, phase, viewport);
            }
        }

        static void Invoke(Instance instance, int phase, Rect viewport, RimVisionViewEvent notification = default,
            RimVisionActionRequest request = null, RimVisionLifecycleEvent lifecycle = default, RimVisionAnimationEvent animation = default, RimVisionJobEvent job = default)
        {
            bool drawing = phase == 3;
            var color = drawing ? GUI.color : default;
            bool enabled = drawing && GUI.enabled;
            var matrix = drawing ? GUI.matrix : default;
            var font = Text.Font;
            var anchor = Text.Anchor;
            bool wrap = Text.WordWrap;
            Rand.PushState();
            try
            {
                switch (phase)
                {
                    case 0:
                        instance.Extension = instance.Registration.Value() ?? throw new InvalidOperationException("Extension factory returned null.");
                        instance.Extension.Open(instance.View);
                        instance.Extension.Lifecycle(instance.View, new RimVisionLifecycleEvent(RimVisionLifecycleKind.Opened));
                        break;
                    case 1:
                        var state = instance.View.State;
                        var previous = instance.Previous;
                        instance.Previous = state;
                        if (state.Map != previous.Map || state.Pawn != previous.Pawn || state.FirstPerson != previous.FirstPerson ||
                            state.CanControl != previous.CanControl || state.MortarAiming != previous.MortarAiming)
                            instance.Extension.StateChanged(instance.View, previous);
                        NotifyChanges(instance, state, previous);
                        instance.Extension.Update(instance.View);
                        break;
                    case 2: instance.View.UpdateAttachments(); instance.View.UpdateEffects(); instance.Extension.BeforeRender(instance.View); break;
                    case 3: instance.Extension.DrawHud(instance.View, viewport); break;
                    case 4: instance.Extension.PresentationEvent(instance.View, notification); break;
                    case 5: instance.Extension.ActionCompleted(instance.View, request); break;
                    case 6: instance.Extension.Lifecycle(instance.View, lifecycle); break;
                    case 7: instance.Extension.AnimationEvent(instance.View, animation); break;
                    case 8: instance.Extension.JobChanged(instance.View, job); break;
                }
            }
            catch (Exception error)
            {
                Log.Error("[RimVision API] Extension disabled for this view: " + instance.Registration.Id + "\n" + error);
                RimVisionDiagnostics.ReportFault("extensions", instance.Registration.Id, error, true);
                Close(instance, RimVisionViewCloseReason.CallbackFailed);
            }
            finally
            {
                Rand.PopState();
                if (drawing) { GUI.color = color; GUI.enabled = enabled; GUI.matrix = matrix; }
                Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap;
            }
        }

        static void NotifyChanges(Instance instance, RimVisionViewState state, RimVisionViewState previous)
        {
            if (state.Map != previous.Map) instance.Extension.Lifecycle(instance.View, new RimVisionLifecycleEvent(RimVisionLifecycleKind.MapChanged, map: previous.Map, pawn: previous.Pawn));
            if (state.Pawn != previous.Pawn || state.FirstPerson != previous.FirstPerson || state.CanControl != previous.CanControl)
            {
                if (previous.FirstPerson && previous.CanControl) instance.Extension.Lifecycle(instance.View, new RimVisionLifecycleEvent(RimVisionLifecycleKind.ControlReleased, pawn: previous.Pawn));
                if (state.FirstPerson && state.CanControl) instance.Extension.Lifecycle(instance.View, new RimVisionLifecycleEvent(RimVisionLifecycleKind.ControlAcquired, pawn: previous.Pawn));
            }
            if (state.Carrier != previous.Carrier) instance.Extension.Lifecycle(instance.View, new RimVisionLifecycleEvent(RimVisionLifecycleKind.CarrierChanged, pawn: previous.Pawn));
            if (state.Paused != previous.Paused) instance.Extension.Lifecycle(instance.View, new RimVisionLifecycleEvent(state.Paused ? RimVisionLifecycleKind.Paused : RimVisionLifecycleKind.Resumed));
            if (state.Pawn != previous.Pawn) return;
            int tick = state.Map != null && Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
                ? Rim3DMultiplayer.MapTicks(state.Map) : Find.TickManager.TicksGame;
            if (state.Weapon != previous.Weapon)
                instance.Extension.AnimationEvent(instance.View, new RimVisionAnimationEvent(state.Pawn, state.Weapon, RimVisionAnimationEventKind.Equip, RimVisionAnimationPhase.Complete, 1, 0));
            if (state.Weapon != previous.Weapon)
                instance.Extension.PresentationEvent(instance.View, new RimVisionViewEvent(RimVisionViewEventKind.WeaponChanged, state.Pawn, state.Weapon, tick));
            if (state.HeldItem != previous.HeldItem)
                instance.Extension.PresentationEvent(instance.View, new RimVisionViewEvent(RimVisionViewEventKind.HeldItemChanged, state.Pawn, state.HeldItem, tick));
            if ((state.AimBlend > .01f) != (previous.AimBlend > .01f))
                instance.Extension.PresentationEvent(instance.View, new RimVisionViewEvent(state.AimBlend > .01f ? RimVisionViewEventKind.AimStarted : RimVisionViewEventKind.AimEnded, state.Pawn, state.Weapon, tick));
            if (state.Sprinting != previous.Sprinting || state.Sneaking != previous.Sneaking || state.Moving != previous.Moving)
                instance.Extension.PresentationEvent(instance.View, new RimVisionViewEvent(RimVisionViewEventKind.MovementChanged, state.Pawn, state.Weapon, tick));
            if (state.LastMeleeAttackTick != previous.LastMeleeAttackTick && state.LastMeleeAttackTick >= 0)
                instance.Extension.PresentationEvent(instance.View, new RimVisionViewEvent(RimVisionViewEventKind.Melee, state.Pawn, state.Weapon, state.LastMeleeAttackTick));
            if (state.BodyVisible != previous.BodyVisible || state.Orthographic != previous.Orthographic || state.CameraTransition != previous.CameraTransition)
                instance.Extension.PresentationEvent(instance.View, new RimVisionViewEvent(RimVisionViewEventKind.CameraChanged, state.Pawn, state.Weapon, tick));
        }

        static void Close(Instance instance, RimVisionViewCloseReason reason = RimVisionViewCloseReason.ViewClosed)
        {
            if (!instance.View.IsOpen || instance.Closing) return;
            instance.Closing = true;
            Rand.PushState();
            try
            {
                try { instance.Extension?.Lifecycle(instance.View, new RimVisionLifecycleEvent(RimVisionLifecycleKind.Closing, reason)); }
                catch (Exception error) { RimVisionDiagnostics.ReportFault("extensions.lifecycle", instance.Registration.Id, error); }
                instance.Extension?.Dispose();
            }
            catch (Exception error) { Log.Error("[RimVision API] Extension cleanup failed: " + instance.Registration.Id + "\n" + error); }
            finally
            {
                try { instance.View.Close(); }
                catch (Exception error) { Log.Error("[RimVision API] Resource cleanup failed: " + instance.Registration.Id + "\n" + error); }
                finally { Rand.PopState(); }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = _instances.Count - 1; i >= 0; i--)
                Close(_instances[i], Current.Game?.Maps.Contains(_instances[i].Previous.Map) == true ? RimVisionViewCloseReason.ViewClosed : RimVisionViewCloseReason.MapRemoved);
            _instances.Clear();
            _events.Clear(); _animationEvents.Clear();
        }
    }
}
