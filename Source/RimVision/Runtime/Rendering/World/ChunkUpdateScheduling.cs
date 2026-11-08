using UnityEngine;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed partial class WorldGeometry
        {
            readonly Plane[] _geometryUpdatePlanes = new Plane[6];
            float _nextBackgroundChunkUpdate;
            int _backgroundChunkCursor = -1;

            bool TryTakeDirtyChunk(out Vector2Int key)
            {
                key = default;
                bool found = false;
                bool visible = false;
                bool backgroundAfterCursor = false;
                int backgroundOrder = 0;
                Vector2Int background = default;
                foreach (var candidate in _view.dirtyChunks)
                {
                    int order = candidate.y * _view.map.Size.x + candidate.x;
                    bool afterCursor = order > _backgroundChunkCursor;
                    if (!found || afterCursor && !backgroundAfterCursor ||
                        afterCursor == backgroundAfterCursor && order < backgroundOrder)
                    {
                        background = candidate;
                        backgroundOrder = order;
                        backgroundAfterCursor = afterCursor;
                    }
                    found = true;
                    var bounds = new Bounds(_view.root.transform.position +
                        new Vector3((candidate.x + .5f) * ChunkSize, 16, (candidate.y + .5f) * ChunkSize),
                        new Vector3(ChunkSize + 16, 64, ChunkSize + 16));
                    if (Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.GroundShadows))
                        bounds.Encapsulate(ProjectedShadowBounds(bounds));
                    if (!GeometryUtility.TestPlanesAABB(_geometryUpdatePlanes, bounds)) continue;
                    key = candidate;
                    visible = true;
                    break;
                }
                if (!visible)
                {
                    if (!found || Time.unscaledTime < _nextBackgroundChunkUpdate) return false;
                    _nextBackgroundChunkUpdate = Time.unscaledTime + .5f;
                    _backgroundChunkCursor = backgroundOrder;
                    key = background;
                }
                _view.dirtyChunks.Remove(key);
                return true;
            }
        }
    }
}
