using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly OutlineRenderer _outlines;

        void RegisterOutlineRenderer(MeshRenderer renderer) => _outlines.RegisterOutlineRenderer(renderer);

        void UpdateVanillaOutlines() => _outlines.UpdateVanillaOutlines(Target);

        void DisposeVanillaOutlines() => _outlines?.DisposeVanillaOutlines();

        sealed class OutlineRenderer
        {
            static readonly int _viewId = Shader.PropertyToID("_Rim3DOutlineView");
            static readonly int _projectionId = Shader.PropertyToID("_Rim3DOutlineProjection");
            static readonly int _cameraPositionId = Shader.PropertyToID("_Rim3DOutlineCameraPosition");
            static readonly int _positionCorrectionId = Shader.PropertyToID("_Rim3DOutlinePositionCorrection");
            static readonly int _widthId = Shader.PropertyToID("_Rim3DOutlineWidth");
            static readonly int _worldPerPixelId = Shader.PropertyToID("_Rim3DOutlineWorldPerPixel");
            static readonly int _nearId = Shader.PropertyToID("_Rim3DOutlineNear");
            readonly Camera _camera;
            readonly Transform _root;
            readonly Shader _surfaceShader;
            readonly List<OutlineSubject> _renderers = new List<OutlineSubject>();
            readonly HashSet<MeshRenderer> _registeredRenderers = new HashSet<MeshRenderer>();
            readonly Dictionary<Material, Material> _materials = new Dictionary<Material, Material>();
            readonly HashSet<Material> _updatedMaterials = new HashSet<Material>();
            readonly Dictionary<Material, bool> _eligibleMaterials = new Dictionary<Material, bool>();
            readonly Plane[] _planes = new Plane[6];
            CommandBuffer _draw;
            Shader _outlineShader;
            bool _unavailable;
            int _mode = -1;
            readonly Material[] _firstPersonMaterials = new Material[2];

            readonly struct OutlineSubject
            {
                internal readonly MeshRenderer Renderer;
                internal readonly MeshFilter Filter;

                internal OutlineSubject(MeshRenderer renderer, MeshFilter filter)
                {
                    Renderer = renderer;
                    Filter = filter;
                }
            }

            internal OutlineRenderer(Camera camera, Transform root, Shader surfaceShader)
            {
                _camera = camera;
                _root = root;
                _surfaceShader = surfaceShader;
            }

            internal void RegisterOutlineRenderer(MeshRenderer renderer)
            {
                if (renderer.sharedMaterial == null || renderer.sharedMaterial.shader != _surfaceShader) return;
                if (_registeredRenderers.Contains(renderer)) return;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord2)) return;
                _registeredRenderers.Add(renderer);
                _renderers.Add(new OutlineSubject(renderer, filter));
            }

            internal void UnregisterOutlineRenderer(MeshRenderer renderer)
            {
                if (!_registeredRenderers.Remove(renderer)) return;
                for (int i = _renderers.Count - 1; i >= 0; i--)
                    if (_renderers[i].Renderer == renderer)
                    {
                        _renderers.RemoveAt(i);
                        return;
                    }
            }

            internal void ClearRegistrations()
            {
                _renderers.Clear();
                _registeredRenderers.Clear();
            }

            internal void UpdateVanillaOutlines(RenderTexture target)
            {
                var settings = Rim3DSettings.Current;
                if (!settings.VanillaOutlines || _mode != -1 && _mode != settings.OutlineType)
                {
                    DisposeWorldOutlines();
                    _unavailable = false;
                }
                if (!settings.VanillaOutlines || target == null || _unavailable) return;
                if (_draw == null)
                {
                    _mode = settings.OutlineType;
                    _outlineShader = ModAssets.LoadShader(_mode == 0 ? "Rim3D/OutlineDepth" : "Rim3D/OutlineHull");
                    if (_outlineShader == null || !_outlineShader.isSupported)
                    {
                        _unavailable = true;
                        Log.Warning("[RimVision] Outlines are unavailable. Rebuild the RimVision shader bundle and check shader support.");
                        return;
                    }
                    _draw = new CommandBuffer { name = "RimVision object outlines" };
                    _camera.AddCommandBuffer(CameraEvent.AfterSkybox, _draw);
                }

                _draw.Clear();
                float width = (_mode == 0 ? settings.ScreenOutlineWidth : settings.HullOutlineWidth) * settings.RenderScale;
                if (width <= 0) return;
                var position = _camera.transform.position;
                var projection = GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true);
                float worldPerPixel = 2f / (target.height * Mathf.Abs(projection.m11));
                _draw.SetGlobalMatrix(_viewId, _camera.worldToCameraMatrix);
                _draw.SetGlobalMatrix(_projectionId, projection);
                _draw.SetGlobalVector(_cameraPositionId, position);
                _draw.SetGlobalVector(_positionCorrectionId, _camera.transform.localPosition - (position - _root.position));
                _draw.SetGlobalFloat(_widthId, width);
                _draw.SetGlobalFloat(_worldPerPixelId, worldPerPixel);
                _draw.SetGlobalFloat(_nearId, _camera.nearClipPlane);
                GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
                _updatedMaterials.Clear();
                _eligibleMaterials.Clear();

                for (int i = _renderers.Count - 1; i >= 0; i--)
                {
                    var subject = _renderers[i];
                    var renderer = subject.Renderer;
                    if (renderer == null)
                    {
                        _registeredRenderers.Remove(renderer);
                        _renderers.RemoveAt(i);
                        continue;
                    }
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    var source = renderer.sharedMaterial;
                    if (source == null) continue;
                    if (!_eligibleMaterials.TryGetValue(source, out bool eligible))
                    {
                        eligible = source.shader == _surfaceShader && source.GetInt("_ColorMask") != 0
                            && source.GetFloat("_Ground") < .5f && source.GetFloat("_Billboard") < .5f
                            && source.GetFloat("_Foliage") < .5f && source.GetFloat("_Unlit") < .5f
                            && source.GetFloat("_FlatItem") < .5f && source.GetFloat("_Ghost") <= 0;
                        _eligibleMaterials.Add(source, eligible);
                    }
                    if (!eligible) continue;
                    var bounds = renderer.bounds;
                    if (bounds.SqrDistance(position) > 2025) continue;
                    bounds.Expand(width * worldPerPixel * 12f);
                    if (!GeometryUtility.TestPlanesAABB(_planes, bounds)) continue;
                    if (subject.Filter == null || subject.Filter.sharedMesh == null
                        || !subject.Filter.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord2)) continue;
                    if (!_materials.TryGetValue(source, out var material))
                    {
                        material = new Material(_outlineShader) { hideFlags = HideFlags.DontSave };
                        _materials.Add(source, material);
                    }
                    if (_updatedMaterials.Add(source)) material.CopyPropertiesFromMaterial(source);
                    _draw.DrawRenderer(renderer, material, 0, 0);
                }
            }

            internal void DrawFirstPersonOutline(CommandBuffer draw, Mesh mesh, Matrix4x4 matrix, Matrix4x4 projection, int targetHeight)
            {
                var settings = Rim3DSettings.Current;
                if (!settings.VanillaOutlines || targetHeight <= 0) return;
                int mode = settings.OutlineType == 0 ? 0 : 1;
                float width = (mode == 0 ? settings.ScreenOutlineWidth : settings.HullOutlineWidth) * settings.RenderScale;
                if (width <= 0) return;
                var material = _firstPersonMaterials[mode];
                if (material == null)
                {
                    var shader = ModAssets.LoadShader(mode == 0 ? "Rim3D/OutlineDepth" : "Rim3D/OutlineHull");
                    if (shader == null || !shader.isSupported) return;
                    material = new Material(shader) { hideFlags = HideFlags.DontSave };
                    material.SetFloat("_Rim3DOutlineFirstPerson", 1);
                    material.SetMatrix(_viewId, Matrix4x4.Scale(new Vector3(1, 1, -1)));
                    material.SetVector(_cameraPositionId, Vector4.zero);
                    material.SetVector(_positionCorrectionId, Vector4.zero);
                    _firstPersonMaterials[mode] = material;
                }
                material.SetMatrix(_projectionId, projection);
                material.SetFloat(_widthId, width);
                material.SetFloat(_worldPerPixelId, 2f / (targetHeight * Mathf.Abs(projection.m11)));
                material.SetFloat(_nearId, _camera.nearClipPlane);
                draw.DrawMesh(mesh, matrix, material, 0, 0);
            }

            internal void DisposeVanillaOutlines()
            {
                DisposeWorldOutlines();
                for (int i = 0; i < _firstPersonMaterials.Length; i++)
                {
                    if (_firstPersonMaterials[i] != null) Object.Destroy(_firstPersonMaterials[i]);
                    _firstPersonMaterials[i] = null;
                }
            }

            void DisposeWorldOutlines()
            {
                if (_draw != null)
                {
                    if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.AfterSkybox, _draw);
                    _draw.Release();
                    _draw = null;
                }
                foreach (var material in _materials.Values) Object.Destroy(material);
                _materials.Clear();
                _updatedMaterials.Clear();
                _eligibleMaterials.Clear();
                _outlineShader = null;
                _mode = -1;
            }
        }
    }
}
