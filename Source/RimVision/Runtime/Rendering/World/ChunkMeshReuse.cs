using System.Collections.Generic;
using UnityEngine;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {

        void CollectChunkMeshes(GameObject oldRoot) => _worldGeometry.CollectChunkMeshes(oldRoot);
        GameObject TakeChunkMesh(Material material) => _worldGeometry.TakeChunkMesh(material);
        void FinishChunkReuse() => _worldGeometry.FinishChunkReuse();

        sealed partial class WorldGeometry
        {
            readonly Dictionary<Material, Queue<GameObject>> _chunkMeshReuse = new Dictionary<Material, Queue<GameObject>>();

            GameObject _chunkReuseRoot;

            bool _reusingChunk;

            Transform _chunkReuseDestination;

            internal void CollectChunkMeshes(GameObject oldRoot)
            {
                if (_chunkReuseRoot == null)
                {
                    _chunkReuseRoot = new GameObject("RimVision inactive mesh reuse");
                    _chunkReuseRoot.transform.SetParent(_view.root.transform, false);
                    _chunkReuseRoot.SetActive(false);
                }
                _reusingChunk = true;
                for (int i = oldRoot.transform.childCount - 1; i >= 0; i--)
                {
                    var child = oldRoot.transform.GetChild(i);
                    var filter = child.GetComponent<MeshFilter>();
                    var renderer = child.GetComponent<MeshRenderer>();
                    if (filter == null || renderer == null || filter.sharedMesh == null) continue;
                    if (!_chunkMeshReuse.TryGetValue(renderer.sharedMaterial, out var queue))
                        _chunkMeshReuse.Add(renderer.sharedMaterial, queue = new Queue<GameObject>());
                    child.SetParent(_chunkReuseRoot.transform, false);
                    queue.Enqueue(child.gameObject);
                }
            }

            internal GameObject TakeChunkMesh(Material material)
            {
                if (!_reusingChunk || _view.geometryParent != _chunkReuseDestination || !_chunkMeshReuse.TryGetValue(material, out var queue) || queue.Count == 0) return null;
                return queue.Dequeue();
            }

            internal void FinishChunkReuse()
            {
                // Reused objects have already moved out; only discarded meshes remain here.
                if (_chunkReuseRoot != null) _view.RemoveMovingShadows(_chunkReuseRoot.transform);
                foreach (var queue in _chunkMeshReuse.Values)
                    while (queue.Count != 0)
                    {
                        var obj = queue.Dequeue();
                        var mesh = obj.GetComponent<MeshFilter>().sharedMesh;
                        _view.meshes.Remove(mesh);
                        UnityEngine.Object.Destroy(mesh);
                        UnityEngine.Object.Destroy(obj);
                    }
                _chunkMeshReuse.Clear();
                _reusingChunk = false; _chunkReuseDestination = null;
            }

        }
    }
}

