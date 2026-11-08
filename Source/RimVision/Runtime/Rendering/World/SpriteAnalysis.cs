using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed partial class WorldGeometry
        {
            internal sealed class SpriteAnalysis
            {
                internal RenderTexture Sample;
                internal AsyncGPUReadbackRequest Request;
                internal bool Ready;
                internal float Padding, VisibleHeight = 1;
                internal byte[] Mask;
                internal uint UpdateCount;
                internal int Width, Height;
                internal float Alpha;
            }

            readonly Dictionary<(Texture2D, Vector2, Vector2, float), SpriteAnalysis> _staticSpriteAnalyses =
                new Dictionary<(Texture2D, Vector2, Vector2, float), SpriteAnalysis>();
            readonly List<SpriteAnalysis> _pendingSpriteAnalyses = new List<SpriteAnalysis>();
            bool _spriteAnalysesDisposed, _asyncSpriteAnalysisFailed;

            internal bool CanQueueSpriteAnalysis => _pendingSpriteAnalyses.Count < 8;

            internal SpriteAnalysis BeginSpriteAnalysis(Texture texture, Vector2 scale, Vector2 offset, float alpha, bool asynchronous)
            {
                var analysis = new SpriteAnalysis
                {
                    Width = Mathf.Clamp(texture.width, 1, 256), Height = Mathf.Clamp(texture.height, 1, 512), Alpha = alpha
                };
                if (_spriteAnalysesDisposed || _view.simulationOnly) { analysis.Ready = true; return analysis; }
                try
                {
                    analysis.Sample = RenderTexture.GetTemporary(analysis.Width, analysis.Height, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(texture, analysis.Sample, scale, offset);
                    if (asynchronous && SystemInfo.supportsAsyncGPUReadback && !_asyncSpriteAnalysisFailed)
                    {
                        _pendingSpriteAnalyses.Add(analysis);
                        try
                        {
                            analysis.Request = AsyncGPUReadback.Request(analysis.Sample, 0, TextureFormat.RGBA32,
                                request => CompleteSpriteReadback(analysis, request));
                            return analysis;
                        }
                        catch (Exception error)
                        {
                            _pendingSpriteAnalyses.Remove(analysis);
                            _asyncSpriteAnalysisFailed = true;
                            Log.Warning("RimVision asynchronous sprite analysis unavailable: " + error.Message);
                        }
                    }
                    ReadSpriteSynchronously(analysis);
                }
                catch (Exception error)
                {
                    analysis.Ready = true;
                    ReleaseSpriteSample(analysis);
                    Log.Warning("RimVision sprite analysis: " + error.Message);
                }
                return analysis;
            }

            internal bool CompleteSpriteAnalysis(SpriteAnalysis analysis, bool immediate)
            {
                if (!analysis.Ready && immediate)
                {
                    analysis.Request.WaitForCompletion();
                    if (!analysis.Ready) CompleteSpriteReadback(analysis, analysis.Request);
                }
                return analysis.Ready;
            }

            void CompleteSpriteReadback(SpriteAnalysis analysis, AsyncGPUReadbackRequest request)
            {
                if (analysis.Ready) return;
                if (!_spriteAnalysesDisposed && request.hasError)
                {
                    _asyncSpriteAnalysisFailed = true;
                    ReadSpriteSynchronously(analysis);
                    return;
                }
                try
                {
                    if (!_spriteAnalysesDisposed) AnalyzeSpritePixels(analysis, request.GetData<Color32>());
                }
                catch (Exception error)
                {
                    _asyncSpriteAnalysisFailed = true;
                    Log.Warning("RimVision sprite readback: " + error.Message);
                    ReadSpriteSynchronously(analysis);
                }
                finally
                {
                    analysis.Ready = true;
                    ReleaseSpriteSample(analysis);
                    _pendingSpriteAnalyses.Remove(analysis);
                }
            }

            void ReadSpriteSynchronously(SpriteAnalysis analysis)
            {
                var previous = RenderTexture.active;
                Texture2D pixels = null;
                try
                {
                    RenderTexture.active = analysis.Sample;
                    pixels = new Texture2D(analysis.Width, analysis.Height, TextureFormat.RGBA32, false);
                    pixels.ReadPixels(new Rect(0, 0, analysis.Width, analysis.Height), 0, 0, false);
                    AnalyzeSpritePixels(analysis, pixels.GetRawTextureData<Color32>());
                }
                catch (Exception error) { Log.Warning("RimVision sprite analysis: " + error.Message); }
                finally
                {
                    RenderTexture.active = previous;
                    if (pixels != null) UnityEngine.Object.Destroy(pixels);
                    analysis.Ready = true;
                    ReleaseSpriteSample(analysis);
                    _pendingSpriteAnalyses.Remove(analysis);
                }
            }

            static void AnalyzeSpritePixels(SpriteAnalysis analysis, NativeArray<Color32> colors)
            {
                int width = analysis.Width, height = analysis.Height;
                var mask = new byte[64 * 80];
                for (int y = 0; y < 80; y++)
                for (int x = 0; x < 64; x++)
                    mask[y * 64 + x] = colors[Mathf.Min(height - 1, y * height / 80) * width + Mathf.Min(width - 1, x * width / 64)].a;
                analysis.Mask = mask;
                int first = -1, last = -1;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (colors[y * width + x].a / 255f * analysis.Alpha < .25f) continue;
                    if (first < 0) first = y;
                    last = y;
                    break;
                }
                if (first >= 0)
                {
                    analysis.Padding = first / (float)height;
                    analysis.VisibleHeight = (last + 1) / (float)height - analysis.Padding;
                }
            }

            internal void ApplySpriteAnalysis(Material material, SpriteAnalysis analysis)
            {
                _view.bottomPadding[material] = analysis.Padding;
                _view.visibleSpriteHeights[material] = analysis.VisibleHeight;
                if (analysis.Mask != null) _view.spriteMasks[material] = analysis.Mask;
                else _view.spriteMasks.Remove(material);
            }

            internal SpriteAnalysis MaterialSpriteAnalysis(Material material)
            {
                var texture = material.mainTexture;
                if (texture == null) return null;
                var source = texture as Texture2D;
                var key = (source, material.mainTextureScale, material.mainTextureOffset, material.color.a);
                if (source != null && _staticSpriteAnalyses.TryGetValue(key, out var cached) && cached.UpdateCount == source.updateCount)
                    return cached;
                var analysis = BeginSpriteAnalysis(texture, material.mainTextureScale, material.mainTextureOffset, material.color.a, false);
                if (source != null)
                {
                    analysis.UpdateCount = source.updateCount;
                    _staticSpriteAnalyses[key] = analysis;
                }
                return analysis;
            }

            static void ReleaseSpriteSample(SpriteAnalysis analysis)
            {
                if (analysis.Sample == null) return;
                RenderTexture.ReleaseTemporary(analysis.Sample);
                analysis.Sample = null;
            }

            internal void ClearSpriteAnalyses()
            {
                _spriteAnalysesDisposed = true;
                var pending = _pendingSpriteAnalyses.ToArray();
                foreach (var analysis in pending)
                {
                    try
                    {
                        if (!analysis.Request.done) analysis.Request.WaitForCompletion();
                    }
                    catch (Exception error) { Log.Warning("RimVision sprite readback cleanup: " + error.Message); }
                    if (analysis.Request.done)
                    {
                        analysis.Ready = true;
                        ReleaseSpriteSample(analysis);
                    }
                }
                _pendingSpriteAnalyses.Clear();
                _staticSpriteAnalyses.Clear();
            }
        }
    }
}
