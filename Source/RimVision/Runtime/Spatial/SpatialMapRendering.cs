using System;
using System.Collections.Generic;
using System.Diagnostics;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    internal sealed class SpatialMapRendering : IDisposable
    {
        sealed class Layer
        {
            internal Map Map;
            internal MapView Scene;
            internal float Offset;
        }

        const int MaximumLayers = 16;
        MapView _active;
        readonly RimVisionSpatialProvider _provider;
        readonly List<Map> _requested = new List<Map>();
        readonly List<Layer> _layers = new List<Layer>();
        Map _priorityMap;
        int _revision;
        int _buildFrame = -1;
        bool _initialized;
        bool _disposed;

        internal SpatialMapRendering(MapView active, RimVisionSpatialProvider provider)
        { _active = active; _provider = provider; }

        internal void Warmup()
        {
            if (_disposed || Current.Game == null) return;
            foreach (var map in _requested)
            {
                if (map == null || map == _active.SpatialMap || !Current.Game.Maps.Contains(map) ||
                    RimVisionSpatial.Resolve(map) != _provider) continue;
                var layer = Find(map);
                if (layer == null)
                {
                    if (_layers.Count >= MaximumLayers) break;
                    layer = new Layer { Map = map, Scene = new MapView(map, true) };
                    _layers.Add(layer);
                }
                layer.Scene.Build();
                layer.Scene.PrepareSpatialActivation();
            }
            RefreshOffsets();
        }

        internal void Update()
        {
            if (_disposed || Current.Game == null || _buildFrame == Time.frameCount) return;
            _buildFrame = Time.frameCount;
            long deadline = Stopwatch.GetTimestamp() + Math.Max(1, Stopwatch.Frequency / 333);
            Map activeMap = _active.SpatialMap;
            int revision = _provider.Revision(activeMap);
            if (!_initialized || revision != _revision)
            {
                if (_initialized)
                {
                    _active.InvalidateSpatialGeometry();
                    foreach (var layer in _layers) layer.Scene.InvalidateSpatialGeometry();
                }
                _revision = revision;
                _initialized = true;
                _requested.Clear();
                _provider.CollectLayers(activeMap, _requested);
                if (_priorityMap != null && !_requested.Contains(_priorityMap)) _requested.Insert(0, _priorityMap);
                for (int i = _layers.Count - 1; i >= 0; i--)
                    if (!_requested.Contains(_layers[i].Map) || !Current.Game.Maps.Contains(_layers[i].Map)) Remove(i);
                RefreshOffsets();
            }
            for (int i = _layers.Count - 1; i >= 0; i--)
                if (!Current.Game.Maps.Contains(_layers[i].Map)) Remove(i);
            foreach (var map in _requested)
            {
                if (map == null || map == activeMap || !Current.Game.Maps.Contains(map) ||
                    RimVisionSpatial.Resolve(map) != _provider || Find(map) != null) continue;
                if (_layers.Count >= MaximumLayers) break;
                _layers.Add(new Layer { Map = map, Scene = new MapView(map, true) });
                RefreshOffsets();
                break;
            }
            if (Stopwatch.GetTimestamp() >= deadline) return;
            int steps = 0;
            do
            {
                Layer pending = null;
                foreach (var layer in _layers)
                    if (!layer.Scene.IsBuilt && (pending == null || layer.Map == _priorityMap ||
                        pending.Map != _priorityMap && Mathf.Abs(layer.Offset) < Mathf.Abs(pending.Offset))) pending = layer;
                if (pending == null) break;
                pending.Scene.AdvanceBuild();
                if (pending.Scene.IsBuilt) pending.Scene.PrepareSpatialActivation();
                steps++;
            }
            while (steps < 64 && Stopwatch.GetTimestamp() < deadline);
        }

        void RefreshOffsets()
        {
            float activeHeight = _provider.Elevation(_active.SpatialMap);
            if (!RimVisionSpatial.Finite(activeHeight)) throw new InvalidOperationException("Invalid active map elevation.");
            foreach (var layer in _layers)
            {
                layer.Offset = _provider.Elevation(layer.Map) - activeHeight;
                if (!RimVisionSpatial.Finite(layer.Offset)) throw new InvalidOperationException("Invalid map layer elevation.");
            }
            _layers.Sort((a, b) => a.Map.uniqueID.CompareTo(b.Map.uniqueID));
        }

        Layer Find(Map map)
        {
            foreach (var layer in _layers) if (layer.Map == map) return layer;
            return null;
        }

        internal bool Prepare(Map destination)
        {
            if (_disposed || !RimVisionSpatial.Connected(_provider, _active.SpatialMap, destination)) return false;
            if (destination == _active.SpatialMap) return true;
            _priorityMap = destination;
            var layer = Find(destination);
            if (layer == null)
            {
                if (_layers.Count >= MaximumLayers)
                {
                    int farthest = 0;
                    for (int i = 1; i < _layers.Count; i++)
                        if (Mathf.Abs(_layers[i].Offset) > Mathf.Abs(_layers[farthest].Offset)) farthest = i;
                    Remove(farthest);
                }
                layer = new Layer { Map = destination, Scene = new MapView(destination, true),
                    Offset = _provider.Elevation(destination) - _provider.Elevation(_active.SpatialMap) };
                _layers.Add(layer);
            }
            return layer.Scene.IsBuilt;
        }

        internal bool TryActivate(Map destination, out MapView scene)
        {
            scene = null;
            if (!Prepare(destination)) return false;
            var layer = Find(destination);
            if (layer == null) return false;
            var previous = _active;
            scene = layer.Scene;
            Vector3 origin = previous.VisualLayerRoot.position + Vector3.up * layer.Offset;
            scene.PrepareSpatialActivation();
            _layers.Remove(layer);
            previous.DemoteSpatialScene();
            _active = scene;
            _layers.Add(new Layer { Map = previous.SpatialMap, Scene = previous });
            _priorityMap = null;
            scene.ActivateSpatialScene(previous, this, origin);
            RefreshOffsets();
            _initialized = false;
            return true;
        }

        internal MapView Scene(Map map) => map == _active.SpatialMap ? _active :
            Find(map) is Layer layer && layer.Scene.IsBuilt ? layer.Scene : null;

        internal float? Offset(Map map) => map == _active.SpatialMap ? 0f : Find(map) is Layer layer && layer.Scene.IsBuilt ? layer.Offset : (float?)null;
        internal Transform Root(Map map) => map == _active.SpatialMap ? _active.VisualLayerRoot :
            Find(map) is Layer layer && layer.Scene.IsBuilt ? layer.Scene.VisualLayerRoot : null;

        internal void Render(Camera camera, RenderTexture target)
        {
            if (_layers.Count == 0 || _disposed) return;
            GameObject activeRoot = _active.VisualLayerRoot.gameObject;
            bool wasActive = activeRoot.activeSelf;
            activeRoot.SetActive(false);
            try
            {
                foreach (var layer in _layers)
                    if (layer.Scene.IsBuilt)
                        layer.Scene.RenderVisualLayer(camera, target, activeRoot.transform.position + Vector3.up * layer.Offset);
            }
            finally { if (activeRoot != null) activeRoot.SetActive(wasActive); }
        }

        internal bool TryPick(Ray ray, float maximumDistance, Pawn excludedPawn, ref RimVisionSpatialHit hit) =>
            TryPick(ray, maximumDistance, excludedPawn, ref hit, RimVisionPickFilter.All);

        internal bool TryPick(Ray ray, float maximumDistance, Pawn excludedPawn, ref RimVisionSpatialHit hit, RimVisionPickFilter filter)
        {
            bool found = false;
            foreach (var layer in _layers)
            {
                if (!layer.Scene.IsBuilt) continue;
                var localRay = new Ray(ray.origin - Vector3.up * layer.Offset, ray.direction);
                if (!layer.Scene.TryPickSpatial(localRay, maximumDistance, excludedPawn, out var candidate, filter)) continue;
                maximumDistance = candidate.Distance;
                hit = new RimVisionSpatialHit(candidate.Map, candidate.Thing, candidate.Cell, ray.GetPoint(candidate.Distance), candidate.Distance);
                found = true;
            }
            return found;
        }

        void Remove(int index)
        {
            _layers[index].Scene.Dispose();
            _layers.RemoveAt(index);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = _layers.Count - 1; i >= 0; i--) Remove(i);
            _requested.Clear();
            _priorityMap = null;
        }
    }
}
