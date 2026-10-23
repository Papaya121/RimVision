using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    internal sealed class AmbientOcclusion
    {
        static readonly int _aoTexture = Shader.PropertyToID("_Rim3DAOTexture");
        static readonly int _rawTexture = Shader.PropertyToID("_Rim3DAORaw");
        static readonly int _texelSize = Shader.PropertyToID("_Rim3DAOTexelSize");
        static readonly int _depthTexelSize = Shader.PropertyToID("_Rim3DAODepthTexelSize");
        static readonly int _inverseProjection = Shader.PropertyToID("_Rim3DAOInverseProjection");
        static readonly int _projection = Shader.PropertyToID("_Rim3DAOProjection");
        static readonly int _intensity = Shader.PropertyToID("_Rim3DAOIntensity");
        readonly Camera _camera;
        readonly Material _material;
        CommandBuffer _draw;
        RenderTexture _occlusion;
        RenderTexture _output;
        bool _failed;
        bool _availabilityLogged;
        bool _diagnosed;
        internal RenderTexture Output => _output;

        internal AmbientOcclusion(Camera camera)
        {
            _camera = camera;
            var shader = ModAssets.LoadShader("Rim3D/AmbientOcclusion");
            if (shader != null && shader.isSupported)
                _material = new Material(shader) { hideFlags = HideFlags.DontSave };
        }

        internal bool Configure(bool enabled, float intensity)
        {
            enabled &= intensity > 0;
            bool requested = enabled;
            bool r8Supported = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8);
            enabled &= !_failed && _material != null && r8Supported;
            if (requested && !enabled && !_availabilityLogged)
            {
                _availabilityLogged = true;
                Log.Warning("[RimVision AO] Unavailable: shader=" + (_material != null) +
                    ", R8=" + r8Supported + ", previousFailure=" + _failed);
            }
            _camera.depthTextureMode = enabled ? DepthTextureMode.Depth : DepthTextureMode.None;
            if (!enabled)
            {
                ReleaseResources();
                return false;
            }
            try
            {
                var target = _camera.targetTexture;
                int width = Mathf.Max(1, target.width / 2);
                int height = Mathf.Max(1, target.height / 2);
                if (_output == null || !_output.IsCreated() || _output.width != target.width ||
                    _output.height != target.height || _output.antiAliasing != target.antiAliasing ||
                    _occlusion == null || !_occlusion.IsCreated())
                {
                    ReleaseResources();
                    _output = new RenderTexture(target.width, target.height, 24, RenderTextureFormat.ARGB32)
                        { hideFlags = HideFlags.DontSave, antiAliasing = target.antiAliasing };
                    _occlusion = new RenderTexture(target.width, target.height, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
                        { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    if (!_output.Create() || !_occlusion.Create())
                        throw new System.InvalidOperationException("Cannot create AO textures.");
                    _draw = new CommandBuffer { name = "RimVision ambient occlusion" };
                    _draw.GetTemporaryRT(_rawTexture, width, height, 0, FilterMode.Bilinear,
                        RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
                    _draw.Blit(BuiltinRenderTextureType.None, _rawTexture, _material, 0);
                    _draw.Blit(_rawTexture, _occlusion, _material, 1);
                    _draw.ReleaseTemporaryRT(_rawTexture);
                    _draw.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    _camera.AddCommandBuffer(CameraEvent.AfterDepthTexture, _draw);
                    _material.SetTexture(_aoTexture, _occlusion);
                }
                var projection = GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true);
                _material.SetFloat(_intensity, intensity);
                _material.SetMatrix(_inverseProjection, projection.inverse);
                _material.SetVector(_projection, new Vector4(Mathf.Abs(projection.m00), Mathf.Abs(projection.m11),
                    _camera.farClipPlane, _camera.orthographic ? 1f : 0f));
                _material.SetVector(_texelSize, new Vector4(1f / width, 1f / height, width, height));
                _material.SetVector(_depthTexelSize, new Vector4(1f / target.width, 1f / target.height,
                    target.width, target.height));
                return true;
            }
            catch (System.Exception exception)
            {
                Disable(exception);
                return false;
            }
        }

        internal void Render(RenderTexture target, bool diagnose)
        {
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(target, _output, _material, 2);
                if (diagnose && !_diagnosed) Diagnose(target);
            }
            catch (System.Exception exception)
            {
                Disable(exception);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        void Diagnose(RenderTexture target)
        {
            _diagnosed = true;
            var previous = RenderTexture.active;
            Texture2D pixels = null;
            try
            {
                pixels = new Texture2D(_occlusion.width, _occlusion.height, TextureFormat.RGBA32, false, true);
                RenderTexture.active = _occlusion;
                pixels.ReadPixels(new Rect(0, 0, _occlusion.width, _occlusion.height), 0, 0);
                var colors = pixels.GetPixels32();
                byte minimum = 255, maximum = 0;
                int shaded = 0;
                foreach (var color in colors)
                {
                    if (color.r < minimum) minimum = color.r;
                    if (color.r > maximum) maximum = color.r;
                    if (color.r < 250) shaded++;
                }
                Log.Message("[RimVision AO] Camera depth pass: source=" + target.width + "x" + target.height +
                    ", MSAA=" + target.antiAliasing + ", AO range=" + minimum + ".." + maximum +
                    ", shadedPixels=" + shaded + "/" + colors.Length);
            }
            catch (System.Exception exception)
            {
                Log.Warning("[RimVision AO] Output diagnostic failed: " + exception);
            }
            finally
            {
                RenderTexture.active = previous;
                if (pixels != null) Object.Destroy(pixels);
            }
        }

        void Disable(System.Exception exception)
        {
            _failed = true;
            _camera.depthTextureMode = DepthTextureMode.None;
            ReleaseResources();
            Log.Warning("[RimVision] Ambient occlusion disabled after rendering error: " + exception);
        }

        void ReleaseResources()
        {
            if (_draw != null)
            {
                _camera.RemoveCommandBuffer(CameraEvent.AfterDepthTexture, _draw);
                _draw.Release();
                _draw = null;
            }
            if (_material != null) _material.SetTexture(_aoTexture, null);
            if (_occlusion != null)
            {
                if (RenderTexture.active == _occlusion) RenderTexture.active = null;
                _occlusion.Release();
                Object.Destroy(_occlusion);
                _occlusion = null;
            }
            if (_output != null)
            {
                if (RenderTexture.active == _output) RenderTexture.active = null;
                _output.Release();
                Object.Destroy(_output);
                _output = null;
            }
        }

        internal void Dispose()
        {
            ReleaseResources();
            if (_material != null) Object.Destroy(_material);
        }
    }
}
