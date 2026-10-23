#if UNITY_EDITOR
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        struct EditorBuildingName
        {
            public Thing Building;
            public GUIContent Content;
            public float Height;
            public Vector2 Size;
        }

        readonly List<EditorBuildingName> editorBuildingNames = new List<EditorBuildingName>();
        bool editorBuildingNamesDirty = true;
        LoadedLanguage editorBuildingNamesLanguage;
        GUIStyle editorBuildingNameStyle;

        public void DrawEditorBuildingNames(Rect viewport)
        {
            if (Rim3DPreview.Instance == null || !Rim3DPreview.Instance.ShowBuildingNames ||
                Event.current.type != EventType.Repaint) return;
            if (editorBuildingNameStyle == null)
                editorBuildingNameStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    richText = false
                };
            if (editorBuildingNamesDirty || editorBuildingNamesLanguage != LanguageDatabase.activeLanguage)
            {
                editorBuildingNames.Clear();
                foreach (var building in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                {
                    if (!building.Spawned || building.Destroyed) continue;
                    var content = new GUIContent(building.LabelCap.ToString());
                    float width = Mathf.Clamp(editorBuildingNameStyle.CalcSize(content).x + 12f, 40f, 260f);
                    editorBuildingNames.Add(new EditorBuildingName
                    {
                        Building = building,
                        Content = content,
                        Height = ConstructionHeight(building.def) + .25f,
                        Size = new Vector2(width, editorBuildingNameStyle.CalcHeight(content, width) + 4f)
                    });
                }
                editorBuildingNamesDirty = false;
                editorBuildingNamesLanguage = LanguageDatabase.activeLanguage;
            }

            var previousColor = GUI.color;
            GUI.BeginGroup(viewport);
            try
            {
                GUI.color = Color.white;
                editorBuildingNameStyle.normal.textColor = Color.white;
                foreach (var entry in editorBuildingNames)
                {
                    var building = entry.Building;
                    if (!building.Spawned || building.Destroyed || map.fogGrid.IsFogged(building.Position)) continue;
                    var position = building.TrueCenter();
                    position.y = entry.Height;
                    var projected = camera.WorldToViewportPoint(root.transform.position + position);
                    if (projected.z <= camera.nearClipPlane || projected.z > camera.farClipPlane ||
                        projected.x < 0 || projected.x > 1 || projected.y < 0 || projected.y > 1) continue;
                    var rect = new Rect(projected.x * viewport.width - entry.Size.x * .5f,
                        (1f - projected.y) * viewport.height - entry.Size.y, entry.Size.x, entry.Size.y);
                    Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, .65f));
                    GUI.Label(rect, entry.Content, editorBuildingNameStyle);
                }
            }
            finally
            {
                GUI.EndGroup();
                GUI.color = previousColor;
            }
        }
    }
}
#endif
