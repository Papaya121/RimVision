using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Recovery.Rim3D.API
{
    public readonly struct RimVisionEffectContext
    {
        public float Elapsed { get; }
        public float Duration { get; }
        public Map Map { get; }
        public bool Paused { get; }
        public float Progress => Duration > 0 ? Mathf.Clamp01(Elapsed / Duration) : 0;
        public RimVisionViewState State { get; }
        internal RimVisionEffectContext(float elapsed, float duration, RimVisionViewState state, Map map, bool paused)
        { Elapsed = elapsed; Duration = duration; State = state; Map = map; Paused = paused; }
    }

    public sealed class RimVisionEffect : IDisposable
    {
        GameObject _root;
        readonly List<UnityEngine.Object> _resources = new List<UnityEngine.Object>();
        readonly Func<Texture, Color, Material> _material;
        readonly Action<GameObject, RimVisionEffectContext> _update;
        Action<GameObject> _release;
        readonly Map _map;
        readonly Pawn _pawn;
        readonly Vector3 _position;
        readonly bool _attached, _pause;
        readonly float _duration;
        float _elapsed, _lastTime;
        public GameObject Root => _root;
        public bool IsAlive => _root != null;
        internal RimVisionEffect(GameObject root, Map map, Vector3 position, bool attached, float duration, bool pause,
            Action<GameObject, RimVisionEffectContext> update, Action<GameObject> release, Func<Texture, Color, Material> material, Pawn pawn = null)
        {
            _root = root; _map = map; _pawn = pawn; _position = position; _attached = attached; _duration = duration;
            _material = material; _pause = pause; _update = update; _release = release; _lastTime = Time.unscaledTime;
        }
        public T OwnResource<T>(T resource) where T : UnityEngine.Object
        {
            if (!IsAlive) throw new ObjectDisposedException(nameof(RimVisionEffect));
            if (!(resource is Mesh || resource is Material || resource is Texture)) throw new ArgumentException("Only owned meshes, materials and textures are accepted.");
            if (!_resources.Contains(resource)) _resources.Add(resource);
            return resource;
        }
        public Material CreateSurfaceMaterial(Texture texture, Color color)
        {
            if (!IsAlive) throw new ObjectDisposedException(nameof(RimVisionEffect));
            return OwnResource(_material(texture, color));
        }
        internal void Update(RimVisionViewState state)
        {
            if (_root == null) return;
            var map = _pawn != null ? RimVisionControl.MapFor(_pawn) : _map;
            bool paused = map == null || (Rim3DMultiplayer.Active ? (Rim3DMultiplayer.MinigameTickRate?.Invoke(map) ?? 0) <= 0 : Find.TickManager.Paused);
            float now = Time.unscaledTime;
            if (!_pause || !paused) _elapsed += Mathf.Max(0, now - _lastTime);
            _lastTime = now;
            if (_duration > 0 && _elapsed >= _duration || map == null || Current.Game?.Maps.Contains(map) != true)
            { Dispose(); return; }
            if (!_attached) _root.SetActive(_position.ToIntVec3().InBounds(_map) && !_map.fogGrid.IsFogged(_position.ToIntVec3()));
            if (_root.activeInHierarchy) _update?.Invoke(_root, new RimVisionEffectContext(_elapsed, _duration, state, map, paused));
        }
        public void Dispose()
        {
            var root = _root; _root = null;
            var release = _release; _release = null;
            try
            {
                if (!ReferenceEquals(root, null)) release?.Invoke(root);
                if (root != null) { root.SetActive(false); UnityEngine.Object.Destroy(root); }
            }
            finally
            {
                foreach (var resource in _resources)
                {
                    if (resource == null) continue;
                    if (resource is RenderTexture target) { if (RenderTexture.active == target) RenderTexture.active = null; target.Release(); }
                    UnityEngine.Object.Destroy(resource);
                }
                _resources.Clear();
            }
        }
    }

    public sealed class RimVisionSound : IDisposable
    {
        Sustainer _sound;
        readonly Map _map;
        readonly IntVec3 _cell;
        internal RimVisionSound(Sustainer sound, Map map, IntVec3 cell) { _sound = sound; _map = map; _cell = cell; }
        public bool IsAlive => _sound != null && !_sound.Ended;
        internal void Update()
        {
            if (!IsAlive) return;
            if (Current.Game?.Maps.Contains(_map) != true || !_cell.InBounds(_map) || _map.fogGrid.IsFogged(_cell)) { Dispose(); return; }
            _sound.Maintain();
        }
        public void Dispose() { var sound = _sound; _sound = null; if (sound != null && !sound.Ended) sound.End(); }
    }

    public sealed partial class RimVisionView
    {
        readonly List<RimVisionEffect> _effects = new List<RimVisionEffect>();
        readonly List<RimVisionSound> _sounds = new List<RimVisionSound>();
        public RimVisionEffect CreateEffect(Map map, RimVisionPose pose, float duration,
            Action<GameObject, RimVisionEffectContext> update = null, bool pauseWithGame = true)
        {
            ValidateEffect(pose, duration);
            var root = CreateLayerObject(map, "RimVision API effect");
            root.transform.localPosition = pose.Position; root.transform.localRotation = pose.Rotation.normalized;
            var effect = new RimVisionEffect(root, map, pose.Position, false, duration, pauseWithGame, update, item => _layerObjects.Remove(item), _spatial.SurfaceMaterial);
            _effects.Add(effect); return effect;
        }
        public RimVisionEffect CreatePawnEffect(Pawn pawn, RimVisionAttachment socket, RimVisionPose offset, float duration,
            Action<GameObject, RimVisionEffectContext> update = null, bool pauseWithGame = true)
        {
            ValidateEffect(offset, duration);
            var root = CreatePawnAttachment(pawn, socket, "RimVision attached effect", offset);
            var effect = new RimVisionEffect(root, RimVisionControl.MapFor(pawn), Vector3.zero, true, duration, pauseWithGame, update, null, _spatial.SurfaceMaterial, pawn);
            _effects.Add(effect); return effect;
        }
        void ValidateEffect(RimVisionPose pose, float duration)
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(RimVisionView));
            if (!pose.Valid || !RimVisionControl.Finite(duration) || duration < 0) throw new ArgumentException("Invalid effect pose or duration.");
            if (_effects.Count >= 512) throw new InvalidOperationException("Effect limit reached.");
        }
        public bool PlaySound(SoundDef sound, Map map, IntVec3 cell)
        {
            if (!CanPlaySound(sound, map, cell)) return false;
            Rand.PushState();
            try { sound.PlayOneShot(new TargetInfo(cell, map)); return true; }
            finally { Rand.PopState(); }
        }
        public RimVisionSound StartSound(SoundDef sound, Map map, IntVec3 cell)
        {
            if (!CanPlaySound(sound, map, cell) || _sounds.Count >= 128) return null;
            Rand.PushState();
            try
            {
                var sustainer = sound.TrySpawnSustainer(SoundInfo.InMap(new TargetInfo(cell, map), MaintenanceType.PerFrame));
                if (sustainer == null) return null;
                var result = new RimVisionSound(sustainer, map, cell); _sounds.Add(result); return result;
            }
            finally { Rand.PopState(); }
        }
        bool CanPlaySound(SoundDef sound, Map map, IntVec3 cell) => IsOpen && sound != null && map != null &&
            LayerOffset(map).HasValue && cell.InBounds(map) && !map.fogGrid.IsFogged(cell);
        internal void UpdateEffects()
        {
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                var effect = _effects[i];
                try { effect.Update(State); }
                catch (Exception error) { RimVisionDiagnostics.ReportFault("effects", ExtensionId ?? "view", error, true); effect.Dispose(); }
                if (!effect.IsAlive) { effect.Dispose(); _effects.RemoveAt(i); }
            }
            for (int i = _sounds.Count - 1; i >= 0; i--)
            {
                var sound = _sounds[i];
                try { sound.Update(); }
                catch (Exception error) { RimVisionDiagnostics.ReportFault("sounds", ExtensionId ?? "view", error, true); sound.Dispose(); }
                if (!sound.IsAlive) { sound.Dispose(); _sounds.RemoveAt(i); }
            }
        }
        void CloseEffects()
        {
            foreach (var effect in _effects)
                try { effect.Dispose(); } catch (Exception error) { RimVisionDiagnostics.ReportFault("effects.dispose", ExtensionId ?? "view", error); }
            _effects.Clear();
            foreach (var sound in _sounds)
                try { sound.Dispose(); } catch (Exception error) { RimVisionDiagnostics.ReportFault("sounds.dispose", ExtensionId ?? "view", error); }
            _sounds.Clear();
        }
    }
}
