using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly Dictionary<Material, Material> filthMaterials = new Dictionary<Material, Material>();

        Material FilthMaterial(Material source) => _worldGeometry.FilthMaterial(source);

        void DrawFilth(Thing thing) => _worldGeometry.DrawFilth(thing);

        sealed partial class WorldGeometry
        {
            internal Material FilthMaterial(Material source)
            {
                if (_view.filthMaterials.TryGetValue(source, out var material)) return material;
                material = new Material(_view.ConvertMaterial(source, false))
                { hideFlags = HideFlags.DontSave, renderQueue = (int)RenderQueue.Transparent - 10 };
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0);
                // The existing opacity path preserves soft texture edges and the def's alpha.
                // Disable cutout so faint dirt is not discarded by the sprite threshold.
                material.SetFloat("_Opaque", 1);
                material.SetFloat("_Ghost", Mathf.Max(.0001f, material.color.a));
                material.SetFloat("_FlatItem", 1);
                material.SetInt("_StencilWriteMask", 0);
                material.SetInt("_StencilComp", (int)CompareFunction.Equal);
                material.SetInt("_StencilReadMask", GroundCutoutStencil);
                _view.materials.Add(material);
                _view.filthMaterials.Add(source, material);
                return material;
            }

            internal void DrawFilth(Thing thing)
            {
                var graphic = thing.Graphic;
                if (graphic == null) return;
                var center = thing.TrueCenter();
                _view.geometryParent = _view.ChunkRoot(ChunkKey(center)).transform;
                var filth = thing as Filth;
                bool cluster = graphic is Graphic_Cluster;
                bool tight = graphic is Graphic_ClusterTight;
                int count = tight ? Mathf.Max(1, filth?.drawInstances?.Count ?? 0)
                    : cluster ? (filth != null ? filth.thickness : 3) : 1;
                float spread = tight ? .2f : .45f;
                float sizeVariance = tight ? .15f : .2f;
                // Match vanilla cluster variation without advancing simulation randomness.
                Rand.PushState(thing.Position.GetHashCode());
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var source = cluster ? graphic.MatSingle : graphic.MatAt(thing.Rotation, thing);
                        var position = center + graphic.DrawOffset(thing.Rotation);
                        if (tight && filth?.drawInstances != null && i < filth.drawInstances.Count)
                            position = filth.drawInstances[i].drawPos;
                        var size = graphic.drawSize;
                        float angle = graphic.ShouldDrawRotated ? thing.Rotation.AsAngle : 0;
                        if (cluster)
                        {
                            position += new Vector3(Rand.Range(-spread, spread), 0, Rand.Range(-spread, spread));
                            size.x *= Rand.Range(1 - sizeVariance, 1 + sizeVariance);
                            size.y *= Rand.Range(1 - sizeVariance, 1 + sizeVariance);
                            angle = Rand.RangeInclusive(0, 360);
                        }
                        if (filth?.drawInstances != null && filth.drawInstances.Count == count)
                        {
                            angle = filth.drawInstances[i].rotation;
                        }
                        bool flip = cluster && Rand.Value < .5f;
                        if (source == null || source.mainTexture == null || source.mainTexture == BaseContent.BadTex) continue;
                        // Keep every splat in its owner's batch, including across chunk borders.
                        position.y = .018f + (thing.thingIDNumber & 15) * .0002f + i * .0001f;
                        _view.Batch(_view.FilthMaterial(source), center).RotatedFloorCard(position,
                            flip ? -size.x : size.x, size.y, angle);
                    }
                }
                finally { Rand.PopState(); }
            }

        }
    }
}
