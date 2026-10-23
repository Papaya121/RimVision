using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly Dictionary<TerrainDef, Material> _terrainEdgeMaterials = new Dictionary<TerrainDef, Material>();

        static readonly Vector2[] TerrainEdgeVertices =
        {
            new Vector2(.5f, 0), new Vector2(0, 0), new Vector2(0, .5f), new Vector2(0, 1),
            new Vector2(.5f, 1), new Vector2(1, 1), new Vector2(1, .5f), new Vector2(1, 0)
        };

        void ConfigureGroundPollution(Material material, TerrainDef terrain) => _worldGeometry.ConfigureGroundPollution(material, terrain);

        void BuildGroundTransitions(int x0, int z0, int x1, int z1) => _worldGeometry.BuildGroundTransitions(x0, z0, x1, z1);

        sealed partial class WorldGeometry
        {
            const float GroundThickness = .35f;

            readonly Dictionary<TerrainDef, Material> _groundSideMaterials = new Dictionary<TerrainDef, Material>();

            readonly HashSet<TerrainDef> _terrainEdgeDefs = new HashSet<TerrainDef>();

            readonly TerrainDef[] _terrainNeighbors = new TerrainDef[8];

            readonly bool[] _terrainEdgeAlpha = new bool[8];

            void BuildGroundSides(Material[,] cells, int x0, int z0, int x1, int z1)
            {
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (cells[x, z] == null || _view.map.fogGrid.IsFogged(cell)) continue;
                    var terrain = _view.map.terrainGrid.TerrainAt(cell);
                    if (terrain == null || terrain.dontRender || IsWaterTerrain(terrain)) continue;
                    bool south = GroundSideOpen(cell + IntVec3.South);
                    bool north = GroundSideOpen(cell + IntVec3.North);
                    bool west = GroundSideOpen(cell + IntVec3.West);
                    bool east = GroundSideOpen(cell + IntVec3.East);
                    if (!south && !north && !west && !east) continue;
                    if (!_groundSideMaterials.TryGetValue(terrain, out var material))
                    {
                        material = NewMaterial(terrain.DrawMatSingle.mainTexture, terrain.DrawColor, true);
                        material.SetInt("_Cull", (int)CullMode.Back);
                        _groundSideMaterials.Add(terrain, material);
                    }
                    var geometry = Batch(material, new Vector3(x, 0, z));
                    if (south) BuildGroundSide(geometry, new Vector3(x, 0, z), new Vector3(x + 1, 0, z));
                    if (north) BuildGroundSide(geometry, new Vector3(x + 1, 0, z + 1), new Vector3(x, 0, z + 1));
                    if (west) BuildGroundSide(geometry, new Vector3(x, 0, z + 1), new Vector3(x, 0, z));
                    if (east) BuildGroundSide(geometry, new Vector3(x + 1, 0, z), new Vector3(x + 1, 0, z + 1));
                }
            }

            bool GroundSideOpen(IntVec3 cell)
            {
                if (!cell.InBounds(_view.map) || !_view.DrawSpatialFloor(cell)) return true;
                if (_view.map.fogGrid.IsFogged(cell)) return false;
                var terrain = _view.map.terrainGrid.TerrainAt(cell);
                return terrain == null || terrain.dontRender;
            }

            static void BuildGroundSide(Geometry geometry, Vector3 a, Vector3 b)
            {
                int start = geometry.Vertices.Count;
                var depth = Vector3.down * GroundThickness;
                float u0 = (a.x + a.z) / 8f, u1 = (b.x + b.z) / 8f;
                geometry.Quad(a + depth, a, b, b + depth, Vector3.Cross(Vector3.up, b - a),
                    new Vector2(u0, -GroundThickness / 8f), new Vector2(u0, 0),
                    new Vector2(u1, 0), new Vector2(u1, -GroundThickness / 8f));
                geometry.SetOutlineBounds(start, new Bounds());
            }

            internal void ConfigureGroundPollution(Material material, TerrainDef terrain)
            {
                if (!ModsConfig.BiotechActive || terrain.graphicPolluted == null || terrain.graphicPolluted == BaseContent.BadGraphic) return;
                var polluted = terrain.DrawMatPolluted;
                material.SetFloat("_Pollution", 1);
                material.SetTexture("_PollutedTex", polluted.mainTexture);
                var overlay = polluted.HasProperty(ShaderPropertyIDs.BurnTex) ? polluted.GetTexture(ShaderPropertyIDs.BurnTex) : null;
                material.SetTexture("_PollutionTex", overlay != null ? overlay : Texture2D.blackTexture);
                material.SetColor("_PollutionColor", overlay != null ? terrain.pollutionColor : Color.clear);
                material.SetColor("_PollutionTint", terrain.pollutionTintColor);
                material.SetVector("_PollutionScale", terrain.pollutionOverlayScale);
                material.SetVector("_PollutionScroll", terrain.pollutionOverlayScrollSpeed);
                string shaderName = polluted.shader.name.Replace(" ", "");
                material.SetFloat("_PollutionBlend", shaderName.IndexOf("SoftLight", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 2 :
                    shaderName.IndexOf("LinearAdd", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0);
            }

            internal void BuildGroundTransitions(int x0, int z0, int x1, int z1)
            {
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!_view.DrawSpatialFloor(cell) || _view.map.fogGrid.IsFogged(cell) || _view.map.terrainGrid.FoundationAt(cell) != null) continue;
                    var terrain = _view.map.terrainGrid.TerrainAt(cell);
                    if (terrain == null || terrain.dontRender || IsWaterTerrain(terrain)) continue;
                    _terrainEdgeDefs.Clear();
                    for (int i = 0; i < 8; i++)
                    {
                        var neighbor = cell + GenAdj.AdjacentCellsAroundBottom[i];
                        _terrainNeighbors[i] = null;
                        if (!neighbor.InBounds(_view.map) || _view.map.fogGrid.IsFogged(neighbor) || _view.map.terrainGrid.FoundationAt(neighbor) != null) continue;
                        var edifice = neighbor.GetEdifice(_view.map);
                        if (edifice != null && edifice.def.coversFloor) continue;
                        var adjacent = _view.map.terrainGrid.TerrainAt(neighbor);
                        _terrainNeighbors[i] = adjacent;
                        if (adjacent != null && !adjacent.dontRender && adjacent != terrain && !IsWaterTerrain(adjacent) &&
                            adjacent.edgeType != TerrainDef.TerrainEdgeType.Hard && adjacent.renderPrecedence >= terrain.renderPrecedence)
                            _terrainEdgeDefs.Add(adjacent);
                    }
                    foreach (var adjacent in _terrainEdgeDefs)
                    {
                        if (!_view._terrainEdgeMaterials.TryGetValue(adjacent, out var material))
                        {
                            if (!_view.terrainMaterials.TryGetValue(adjacent, out var ground))
                            {
                                ground = _view.NewTerrainMaterial(adjacent);
                                _view.terrainMaterials.Add(adjacent, ground);
                            }
                            material = new Material(ground) { hideFlags = HideFlags.DontSave, renderQueue = 2502 + Mathf.Clamp(adjacent.renderPrecedence, 0, 400) };
                            material.SetFloat("_TerrainEdge", 1);
                            material.SetFloat("_TerrainDepthBias", -1);
                            material.SetFloat("_TerrainRough", adjacent.edgeType == TerrainDef.TerrainEdgeType.FadeRough ? 1 : 0);
                            material.SetTexture("_AlphaAddTex", TexGame.AlphaAddTex);
                            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                            material.SetFloat("_ZWrite", 0);
                            _view.materials.Add(material);
                            _view._terrainEdgeMaterials.Add(adjacent, material);
                        }
                        for (int i = 0; i < 8; i++) _terrainEdgeAlpha[i] = false;
                        for (int i = 0; i < 8; i++)
                        {
                            if (_terrainNeighbors[i] != adjacent) continue;
                            _terrainEdgeAlpha[i] = true;
                            if (i % 2 != 0) continue;
                            _terrainEdgeAlpha[(i + 7) % 8] = true;
                            _terrainEdgeAlpha[(i + 1) % 8] = true;
                        }
                        var geometry = _view.Batch(material, new Vector3(x, 0, z));
                        int start = geometry.Vertices.Count;
                        for (int i = 0; i < 9; i++)
                        {
                            var offset = i == 8 ? new Vector2(.5f, .5f) : TerrainEdgeVertices[i];
                            var position = new Vector3(x + offset.x, 0, z + offset.y);
                            geometry.Vertices.Add(position);
                            geometry.Normals.Add(Vector3.up);
                            geometry.Uvs.Add(new Vector2(position.x / 8f, position.z / 8f));
                            geometry.CardCenters.Add(Vector3.zero);
                            geometry.Colors.Add(new Color(1, 1, 1, i < 8 && _terrainEdgeAlpha[i] ? 1 : 0));
                        }
                        for (int i = 0; i < 8; i++)
                        {
                            geometry.Indices.Add(start + i);
                            geometry.Indices.Add(start + (i + 1) % 8);
                            geometry.Indices.Add(start + 8);
                        }
                    }
                }
            }

        }
    }
}
