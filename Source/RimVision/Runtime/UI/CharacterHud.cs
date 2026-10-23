using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        // Keep the character facing the player while the first-person camera turns.
        // RimWorld owns and invalidates this portrait when apparel or wounds change.
        static readonly Vector2 CharacterHudSize = new Vector2(160, 200);
        const float CharacterHudZoom = 1.2f;

        sealed class CompassMarker
        {
            public IntVec3 Cell;
            public string Name;
            public int Icon;
        }

        static readonly ConditionalWeakTable<Map, List<CompassMarker>> _compassMarkers =
            new ConditionalWeakTable<Map, List<CompassMarker>>();
        static Texture2D[] _compassIcons;
        static Texture2D[] CompassIcons => _compassIcons ?? (_compassIcons = new[]
        {
            TexCommand.GatherSpotActive, TexCommand.Attack, TexCommand.Draft, TexCommand.ForbidOn,
            TexCommand.DesirePower, TexCommand.Install, TexCommand.SelectShelf, TexCommand.OpenLinkedQuestTex
        });


        readonly Dictionary<Pawn, PawnDirection> _compassHeads = new Dictionary<Pawn, PawnDirection>();

        sealed class CompassMarkerWindow : Window
        {
            readonly MapView _owner;
            readonly List<CompassMarker> _markers;
            readonly CompassMarker _marker;
            readonly bool _existing;
            string _name;
            int _icon;

            public override Vector2 InitialSize => new Vector2(440f, 290f);

            public CompassMarkerWindow(MapView owner, List<CompassMarker> markers, CompassMarker marker, bool existing)
            {
                _owner = owner;
                _markers = markers;
                _marker = marker;
                _existing = existing;
                _name = marker.Name;
                _icon = marker.Icon;
                layer = WindowLayer.Dialog;
                forcePause = true;
                preventCameraMotion = true;
                absorbInputAroundWindow = true;
                closeOnAccept = false;
                doCloseX = true;
            }

            public override void WindowUpdate()
            {
                base.WindowUpdate();
                if (!Rim3DSettings.Current.CompassEnabled || _owner.camera == null || Find.CurrentMap != _owner.map) Close(false);
            }

            public override void DoWindowContents(Rect rect)
            {
                if (!Rim3DSettings.Current.CompassEnabled) { Close(false); return; }
                Widgets.Label(new Rect(0, 0, rect.width - 30f, 28f), "Rim3D_CompassEdit".Translate());
                _name = Widgets.TextField(new Rect(0, 36f, rect.width, 30f), _name, 40);
                Widgets.Label(new Rect(0, 76f, rect.width, 24f), "Rim3D_CompassChooseIcon".Translate());
                for (int i = 0; i < CompassIcons.Length; i++)
                {
                    var button = new Rect(i * 48f, 106f, 42f, 42f);
                    Widgets.DrawBoxSolid(button, i == _icon ? new Color(.2f, .45f, .6f) : new Color(.12f, .14f, .17f));
                    if (Widgets.ButtonImage(button.ContractedBy(5f), CompassIcons[i])) _icon = i;
                }
                float buttonWidth = (rect.width - 16f) / 3f;
                float y = rect.height - 36f;
                if (Widgets.ButtonText(new Rect(0, y, buttonWidth, 32f), "Rim3D_CompassSave".Translate()))
                {
                    _marker.Name = _name.Trim().Length == 0 ? "Rim3D_CompassMarker".Translate().ToString() : _name.Trim();
                    _marker.Icon = _icon;
                    if (!_existing) _markers.Add(_marker);
                    Close(false);
                }
                if (_existing && Widgets.ButtonText(new Rect(buttonWidth + 8f, y, buttonWidth, 32f),
                    "Rim3D_CompassDelete".Translate()))
                {
                    _markers.Remove(_marker);
                    Close(false);
                }
                if (Widgets.ButtonText(new Rect((buttonWidth + 8f) * 2f, y, buttonWidth, 32f),
                    "Rim3D_CompassCancel".Translate())) Close(false);
            }
        }

        bool CompassInputAvailable => Rim3DSettings.Current.CompassEnabled && !TextInputFocused && !ExternalInputBlocked && Application.isFocused &&
            !eventCamera && !cameraFlight && !FallPresentationActive && !constructionMenu && !actionMenu &&
            !statusMenu && buildTool == null && zoneTool == null && (!FirstPerson || CanCaptureMouse);

        void UpdateCompassInput()
        {
            if (!CompassInputAvailable || !ViewBindings.Pressed(ViewAction.Compass)) return;
            var markers = _compassMarkers.GetOrCreateValue(map);
            var aimedMarker = _hudPresentation.AimedCompassMarker();
            if (aimedMarker != null)
            {
                ReleaseMouse();
                Find.WindowStack.Add(new CompassMarkerWindow(this, markers, aimedMarker, true));
                return;
            }
            var ray = new Ray(CameraMapPosition, camera.transform.forward);
            if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float hit))
            {
                Messages.Message("Rim3D_CompassAimGround".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            var cell = ray.GetPoint(hit).ToIntVec3();
            if (!cell.InBounds(map) || map.fogGrid.IsFogged(cell))
            {
                Messages.Message("Rim3D_CellUnavailable".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            for (int i = 0; i < markers.Count; i++)
                if ((markers[i].Cell.ToVector3Shifted() - cell.ToVector3Shifted()).sqrMagnitude <= 9f)
                {
                    ReleaseMouse();
                    Find.WindowStack.Add(new CompassMarkerWindow(this, markers, markers[i], true));
                    return;
                }
            if (markers.Count >= 32)
            {
                Messages.Message("Rim3D_CompassMarkerLimit".TranslateBindings(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            ReleaseMouse();
            Find.WindowStack.Add(new CompassMarkerWindow(this, markers, new CompassMarker
                { Cell = cell, Name = "Rim3D_CompassMarker".Translate().ToString() }, false));
        }

        void DrawCompassHud(Rect viewport) => _hudPresentation.DrawCompassHud(viewport);

        bool CompassPawnAvailable(Pawn pawn) => _hudPresentation.CompassPawnAvailable(pawn);

        bool CompassPawnVisible(Pawn pawn) => _hudPresentation.CompassPawnVisible(pawn);

        void DrawCompassMarker(Rect bar, float heading, Vector3 position, string icon, Color color,
            string name, ref string label, ref float nearestAngle, ref float nearestDistance,
            Texture2D texture = null, Pawn portraitPawn = null) => _hudPresentation.DrawCompassMarker(bar, heading, position, icon, color, name, ref label, ref nearestAngle, ref nearestDistance, texture, portraitPawn);

        RenderTexture CompassHeadTexture(Pawn pawn) => _hudPresentation.CompassHeadTexture(pawn);

        public void DrawResourceHud(Rect viewport) => _hudPresentation.DrawResourceHud(viewport);

        public void DrawTimeHud(Rect viewport) => _hudPresentation.DrawTimeHud(viewport);

        public void DrawTopHudMessages(Rect viewport) => _hudPresentation.DrawTopHudMessages(viewport);

        void DrawTopHudMessage(ref Rect row, string message, float alpha = 1f) => _hudPresentation.DrawTopHudMessage(ref row, message, alpha);

        public void DrawNeedsHud(Rect viewport) => _hudPresentation.DrawNeedsHud(viewport);

        public void DrawCharacterHud(Rect viewport) => _hudPresentation.DrawCharacterHud(viewport);

        void DrawCharacterHudItem(Rect portrait, Thing thing, Vector2 offset, float size, float angle) => _hudPresentation.DrawCharacterHudItem(portrait, thing, offset, size, angle);

        sealed partial class HudPresentation
        {
            GUIStyle _characterNameStyle;
            GUIStyle _characterWeaponStyle;
            GUIStyle _characterVitalsStyle;

            int _displayedTimeMinute = -1;

            string _displayedTime = "";

            GUIStyle _timeHudStyle;

            string _displayedTemperature = "";

            string _temperatureHudValue = "";

            float _nextTemperatureHudUpdate;

            TemperatureDisplayMode _displayedTemperatureMode;

            object _displayedTemperatureLanguage;

            float _temperatureHudWidth;

            GUIStyle _hudMessageStyle;

            readonly GUIContent _hudMessageContent = new GUIContent();

            readonly List<Need> _hudNeeds = new List<Need>();

            readonly List<Pawn> _compassPawns = new List<Pawn>();

            readonly List<string> _compassPawnLabels = new List<string>();

            readonly string[] _compassDirections = new string[4];

            Vector3 _compassHome;

            bool _compassHasHome;

            float _nextCompassUpdate;

            float _nextCompassHomeUpdate;

            object _compassLanguage;

            GUIStyle _compassStyle;

            string _compassHomeLabel;

            Vector2 _compassViewportSize;

            static void DrawNavigationPanel(Rect rect)
            {
                GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    new Color(.12f, .16f, .19f, .58f), 0f, 8f);
            }

            bool ProjectCompassMarker(CompassMarker marker, out Vector3 point)
            {
                point = Vector3.zero;
                if (!marker.Cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(marker.Cell)) return false;
                var position = marker.Cell.ToVector3Shifted();
                var offset = position - _view.CameraMapPosition;
                offset.y = 0f;
                if (offset.sqrMagnitude > 4f) return false;
                point = _view.camera.WorldToViewportPoint(_view.root.transform.position +
                    position + Vector3.up * .75f);
                return point.z > _view.camera.nearClipPlane && point.x > 0f && point.x < 1f &&
                    point.y > 0f && point.y < 1f;
            }

            internal CompassMarker AimedCompassMarker()
            {
                if (!_view.CompassInputAvailable || _compassViewportSize.x <= 0f || _compassViewportSize.y <= 0f) return null;
                CompassMarker nearest = null;
                float nearestDistance = float.MaxValue;
                var markers = _compassMarkers.GetOrCreateValue(_view.map);
                for (int i = 0; i < markers.Count; i++)
                {
                    if (!ProjectCompassMarker(markers[i], out var point)) continue;
                    float x = (point.x - .5f) * _compassViewportSize.x;
                    float y = (point.y - .5f) * _compassViewportSize.y;
                    float distance = x * x + y * y;
                    if (Mathf.Abs(x) > 20f || Mathf.Abs(y) > 20f || distance >= nearestDistance) continue;
                    nearest = markers[i];
                    nearestDistance = distance;
                }
                return nearest;
            }

            void DrawWorldCompassMarkers(Rect viewport)
            {
                _compassViewportSize = viewport.size;
                var markers = _compassMarkers.GetOrCreateValue(_view.map);
                var aimed = AimedCompassMarker();
                for (int i = 0; i < markers.Count; i++)
                {
                    var marker = markers[i];
                    if (!ProjectCompassMarker(marker, out var point)) continue;
                    float x = viewport.x + point.x * viewport.width;
                    float y = viewport.y + (1f - point.y) * viewport.height;
                    bool hovered = marker == aimed;
                    var iconRect = new Rect(x - 20f, y - 20f, 40f, 40f);
                    Widgets.DrawBoxSolid(iconRect, hovered ? new Color(.12f, .4f, .55f, .9f) : new Color(.06f, .08f, .11f, .7f));
                    var previousColor = GUI.color;
                    GUI.color = new Color(.4f, .8f, 1f);
                    GUI.DrawTexture(iconRect.ContractedBy(5f), CompassIcons[marker.Icon], ScaleMode.ScaleToFit, true);
                    GUI.color = previousColor;
                    _view.Badge(new Rect(x - 130f, y + 22f, 260f, 22f), marker.Name, _compassStyle);
                }
                if (aimed != null && ProjectCompassMarker(aimed, out var aimedPoint))
                {
                    float x = viewport.x + aimedPoint.x * viewport.width;
                    float y = viewport.y + (1f - aimedPoint.y) * viewport.height;
                    var hintRect = new Rect(Mathf.Clamp(x - 110f, viewport.x, Mathf.Max(viewport.x, viewport.xMax - 220f)),
                        Mathf.Clamp(y + 46f, viewport.y, Mathf.Max(viewport.y, viewport.yMax - 26f)), 220f, 26f);
                    Widgets.DrawBoxSolid(hintRect, new Color(.06f, .08f, .11f, .95f));
                    _view.Badge(hintRect, "Rim3D_CompassEditHint".TranslateBindings().ToString(), _compassStyle);
                }
            }

            internal void DrawCompassHud(Rect viewport)
            {
                if (!Rim3DSettings.Current.CompassEnabled) return;
                if (_compassStyle == null)
                    _compassStyle = new GUIStyle(_view.hudStyle) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
                DrawWorldCompassMarkers(viewport);
                if (_compassLanguage != LanguageDatabase.activeLanguage || _compassHomeLabel == null)
                {
                    _compassLanguage = LanguageDatabase.activeLanguage;
                    _compassHomeLabel = "Rim3D_CompassHome".Translate().ToString();
                    for (int i = 0; i < 4; i++)
                        _compassDirections[i] = ("Rim3D_CompassDirection" + i).Translate().ToString();
                }
                if (Time.unscaledTime >= _nextCompassUpdate)
                {
                    _nextCompassUpdate = Time.unscaledTime + .5f;
                    if (Time.unscaledTime >= _nextCompassHomeUpdate)
                    {
                        _nextCompassHomeUpdate = Time.unscaledTime + 1f;
                        _compassHome = Vector3.zero;
                        int count = 0;
                        foreach (var cell in _view.map.areaManager.Home.ActiveCells)
                        {
                            if (_view.map.fogGrid.IsFogged(cell)) continue;
                            _compassHome += cell.ToVector3Shifted();
                            count++;
                        }
                        _compassHasHome = count > 0;
                        if (_compassHasHome) _compassHome /= count;
                    }
                    _compassPawns.Clear();
                    _compassPawnLabels.Clear();
                    foreach (var pawn in _view.map.mapPawns.AllPawnsSpawned)
                        if (_view.CompassPawnVisible(pawn))
                        {
                            _compassPawns.Add(pawn);
                            _compassPawnLabels.Add(pawn.LabelShort);
                        }
                }
                float width = Mathf.Min(440f, Mathf.Max(120f, viewport.width - 368f));
                var bar = new Rect(viewport.center.x - width * .5f, viewport.y + 42f, width, 32f);
                ReportHudBounds(API.RimVisionHudElement.Compass, new Rect(bar.x, bar.y, bar.width, bar.height + 20));
                DrawNavigationPanel(bar);
                float heading = _view.camera.transform.localEulerAngles.y;
                for (int bearing = 0; bearing < 360; bearing += 5)
                {
                    float delta = Mathf.DeltaAngle(heading, bearing);
                    if (Mathf.Abs(delta) > 86f) continue;
                    float x = bar.center.x + delta / 180f * bar.width;
                    float alpha = Mathf.Lerp(.12f, .65f, 1f - Mathf.Abs(delta) / 90f);
                    if (bearing % 90 == 0)
                        _view.Badge(new Rect(x - 12f, bar.y, 24f, 20f), _compassDirections[bearing / 90], _compassStyle,
                            Mathf.Min(1f, alpha * 2f));
                    else
                    {
                        float tickHeight = bearing % 15 == 0 ? 10f : 5f;
                        Widgets.DrawBoxSolid(new Rect(x, bar.y + 7f, 1f, tickHeight), new Color(1f, 1f, 1f, alpha));
                    }
                }
                for (int i = 0; i < 4; i++)
                    Widgets.DrawBoxSolid(new Rect(bar.center.x - 3f + i, bar.y + i, 7f - i * 2f, 1f), Color.white);
                string label = null;
                float nearestAngle = 12f;
                float nearestDistance = 0;
                if (_compassHasHome)
                    _view.DrawCompassMarker(bar, heading, _compassHome, "H", new Color(.5f, 1f, .6f),
                        _compassHomeLabel, ref label, ref nearestAngle, ref nearestDistance);
                var markers = _compassMarkers.GetOrCreateValue(_view.map);
                for (int i = 0; i < markers.Count; i++)
                    _view.DrawCompassMarker(bar, heading, markers[i].Cell.ToVector3Shifted(), "+", new Color(.4f, .8f, 1f),
                        markers[i].Name, ref label, ref nearestAngle, ref nearestDistance, CompassIcons[markers[i].Icon]);
                for (int i = 0; i < _compassPawns.Count; i++)
                {
                    var pawn = _compassPawns[i];
                    if (!_view.CompassPawnAvailable(pawn)) continue;
                    bool own = pawn.Faction == Faction.OfPlayer;
                    bool hostile = pawn.HostileTo(Faction.OfPlayer);
                    var color = own ? new Color(.5f, 1f, .6f) : hostile ? new Color(1f, .3f, .25f) : new Color(.9f, .85f, .6f);
                    _view.DrawCompassMarker(bar, heading, pawn.DrawPos, hostile ? "!" : "+", color,
                        _compassPawnLabels[i], ref label, ref nearestAngle, ref nearestDistance,
                        own ? null : hostile ? TexCommand.Attack : TexCommand.GatherSpotActive, own ? pawn : null);
                }
                if (label != null)
                    _view.Badge(new Rect(bar.x, bar.yMax, bar.width, 20f), label + " · " +
                        Mathf.RoundToInt(nearestDistance), _compassStyle);
            }

            internal bool CompassPawnAvailable(Pawn pawn) => pawn != _view.ControlledPawn && pawn.Spawned && pawn.Map == _view.map &&
                !pawn.Dead && !pawn.IsHiddenFromPlayer() && !_view.map.fogGrid.IsFogged(pawn.Position);

            internal bool CompassPawnVisible(Pawn pawn)
            {
                if (!_view.CompassPawnAvailable(pawn)) return false;
                if (pawn.Faction == Faction.OfPlayer) return true;
                var lordJob = pawn.GetLord()?.LordJob;
                bool hostile = pawn.HostileTo(Faction.OfPlayer);
                if (hostile)
                {
                    if (pawn.Downed) return false;
                    if (lordJob is LordJob_AssaultColony || lordJob is LordJob_StageThenAttack || lordJob is LordJob_Siege)
                        return true;
                    if (pawn.stances?.curStance is Stance_Busy busy && !busy.neverAimWeapon &&
                        busy.focusTarg.Thing?.Faction == Faction.OfPlayer) return true;
                    if ((pawn.CurJobDef == JobDefOf.AttackMelee || pawn.CurJobDef == JobDefOf.AttackStatic) &&
                        pawn.CurJob.targetA.Thing?.Faction == Faction.OfPlayer) return true;
                }
                else
                {
                    if (!pawn.RaceProps.Humanlike) return false;
                    if (lordJob is LordJob_TradeWithColony || lordJob is LordJob_VisitColony ||
                        lordJob is LordJob_TravelAndExit || lordJob is LordJob_AssistColony) return true;
                }
                var observer = _view.CameraMapPosition.ToIntVec3();
                return observer.InBounds(_view.map) && (pawn.Position - observer).LengthHorizontalSquared <= 2025 &&
                    GenSight.LineOfSight(observer, pawn.Position, _view.map);
            }

            internal void DrawCompassMarker(Rect bar, float heading, Vector3 position, string icon, Color color,
                string name, ref string label, ref float nearestAngle, ref float nearestDistance,
                Texture2D texture = null, Pawn portraitPawn = null)
            {
                var offset = position - _view.CameraMapPosition;
                offset.y = 0;
                float delta = Mathf.DeltaAngle(heading, Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg);
                if (Mathf.Abs(delta) > 86f) return;
                float x = bar.center.x + delta / 180f * bar.width;
                if (portraitPawn != null)
                {
                    var portraitRect = new Rect(x - 11f, bar.y + 10f, 22f, 22f);
                    if (portraitPawn.RaceProps.Humanlike)
                        GUI.DrawTexture(portraitRect, _view.CompassHeadTexture(portraitPawn), ScaleMode.ScaleToFit, true);
                    else Widgets.ThingIcon(portraitRect, portraitPawn);
                }
                else if (texture != null)
                {
                    var previousColor = GUI.color;
                    GUI.color = color;
                    GUI.DrawTexture(new Rect(x - 9f, bar.y + 12f, 18f, 18f), texture, ScaleMode.ScaleToFit, true);
                    GUI.color = previousColor;
                }
                else if (icon == "H")
                {
                    Widgets.DrawBoxSolid(new Rect(x - 2f, bar.y + 17f, 4f, 2f), color);
                    Widgets.DrawBoxSolid(new Rect(x - 4f, bar.y + 19f, 8f, 2f), color);
                    Widgets.DrawBoxSolid(new Rect(x - 7f, bar.y + 21f, 14f, 2f), color);
                    Widgets.DrawBoxSolid(new Rect(x - 5f, bar.y + 23f, 10f, 7f), color);
                    Widgets.DrawBoxSolid(new Rect(x - 1f, bar.y + 25f, 3f, 5f), new Color(.06f, .08f, .11f));
                }
                else
                {
                    var previousColor = GUI.color;
                    GUI.color = color;
                    GUI.Label(new Rect(x - 8f, bar.y + 10f, 16f, 22f), icon, _compassStyle);
                    GUI.color = previousColor;
                }
                if (Mathf.Abs(delta) >= nearestAngle) return;
                nearestAngle = Mathf.Abs(delta);
                nearestDistance = offset.magnitude;
                label = name;
            }

            internal RenderTexture CompassHeadTexture(Pawn pawn)
            {
                if (!_view._compassHeads.TryGetValue(pawn, out var head))
                {
                    head = new PawnDirection
                    {
                        Texture = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32)
                        {
                            hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
                            wrapMode = TextureWrapMode.Clamp, useMipMap = false
                        }
                    };
                    _view.pawnTextures.Add(head.Texture);
                    head.Texture.Create();
                    _view._compassHeads.Add(pawn, head);
                }
                if (!head.Valid)
                {
                    pawn.Drawer.renderer.EnsureGraphicsInitialized();
                    CapturePawnAppearance(pawn, head.Texture, Rot4.South, 1f, true);
                    head.Valid = true;
                }
                return head.Texture;
            }

            ResourceReadout _resourceReadout;

            Vector2 _resourceHudScroll;

            internal void DrawResourceHud(Rect viewport)
            {
                if (!Rim3DSettings.Current.ResourceHud) return;
                if (!_view.FirstPerson || Find.CurrentMap != _view.map || Event.current.type == EventType.Layout) return;
                bool interactive = ViewBindings.Held(ViewAction.Cursor);
                if (!interactive && (Event.current.isMouse || Event.current.type == EventType.ScrollWheel)) return;

                var color = GUI.color;
                var font = Text.Font;
                var anchor = Text.Anchor;
                bool wrap = Text.WordWrap;
                try
                {
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Text.WordWrap = false;
                    var amounts = _view.map.resourceCounter.AllCountedAmounts;
                    int rows = 0;
                    float countWidth = 0f;
                    foreach (var entry in amounts)
                    {
                        if (entry.Value <= 0 && !entry.Key.resourceReadoutAlwaysShow) continue;
                        rows++;
                        countWidth = Mathf.Max(countWidth, Text.CalcSize(entry.Value.ToStringCached()).x);
                    }
                    float availableHeight = viewport.height - 280f;
                    if (rows == 0 || availableHeight < 36f) return;

                    if (_resourceReadout == null) _resourceReadout = new ResourceReadout();
                    float height = Mathf.Min(rows * 24f + 16f, availableHeight);
                    float scrollbarWidth = rows * 24f + 16f > height ? 16f : 0f;
                    float width = Mathf.Min(Mathf.Max(80f, 34f + countWidth + 16f) + scrollbarWidth,
                        viewport.width - 24f);
                    var panel = new Rect(viewport.xMax - width - 12f, viewport.y + 12f, width, height);
                    ReportHudBounds(API.RimVisionHudElement.Resources, panel);
                    var list = panel.ContractedBy(6f);
                    var content = new Rect(0f, 0f, list.width - scrollbarWidth, rows * 24f + 4f);
                    _resourceHudScroll.y = Mathf.Clamp(_resourceHudScroll.y, 0f,
                        Mathf.Max(0f, content.height - list.height));
                    DrawNavigationPanel(panel);
                    Widgets.BeginScrollView(list, ref _resourceHudScroll, content);
                    try
                    {
                        float y = 0f;
                        foreach (var entry in amounts)
                        {
                            if (entry.Value <= 0 && !entry.Key.resourceReadoutAlwaysShow) continue;
                            if (y + 27f >= _resourceHudScroll.y && y <= _resourceHudScroll.y + list.height)
                                _resourceReadout.DrawResourceSimple(new Rect(0f, y, content.width, 24f), entry.Key);
                            y += 24f;
                        }
                    }
                    finally { Widgets.EndScrollView(); }
                }
                finally
                {
                    GUI.color = color;
                    Text.Font = font;
                    Text.Anchor = anchor;
                    Text.WordWrap = wrap;
                }
            }

            internal void DrawTimeHud(Rect viewport)
            {
                if (Event.current.type != EventType.Repaint) return;

                int minute = Mathf.Clamp(Mathf.FloorToInt(GenLocalDate.DayPercent(_view.map) * 1440f), 0, 1439);
                if (minute != _displayedTimeMinute)
                {
                    _displayedTimeMinute = minute;
                    _displayedTime = (minute / 60).ToString("00") + ":" + (minute % 60).ToString("00");
                }
                if (_timeHudStyle == null)
                    _timeHudStyle = new GUIStyle(_view.hudStyle) { alignment = TextAnchor.MiddleCenter, fontSize = 14 };

                if (_displayedTemperature.Length == 0 || Time.unscaledTime >= _nextTemperatureHudUpdate ||
                    _displayedTemperatureMode != Prefs.TemperatureMode ||
                    _displayedTemperatureLanguage != LanguageDatabase.activeLanguage)
                {
                    _nextTemperatureHudUpdate = Time.unscaledTime + 1f;
                    _displayedTemperatureMode = Prefs.TemperatureMode;
                    _displayedTemperatureLanguage = LanguageDatabase.activeLanguage;
                    var pawn = _view.FirstPerson ? _view.ControlledPawn : null;
                    var room = pawn != null && pawn.Spawned && pawn.Map == _view.map ? pawn.Position.GetRoom(_view.map) : null;
                    string label = room != null && !room.PsychologicallyOutdoors
                        ? (room.OpenRoofCount == 0 ? "Indoors".Translate().ToString()
                            : "IndoorsUnroofed".Translate() + " (" + room.OpenRoofCount + ")")
                        : "Outdoors".Translate().CapitalizeFirst().ToString();
                    float temperature = room != null ? room.Temperature : _view.map.mapTemperature.OutdoorTemp;
                    _temperatureHudValue = temperature.ToStringTemperature("F0");
                    _displayedTemperature = label + ": " + _temperatureHudValue;
                    _temperatureHudWidth = Mathf.Max(52f, _timeHudStyle.CalcSize(new GUIContent(_temperatureHudValue)).x + 20f);
                }
                float width = 76f + _temperatureHudWidth;
                var panel = new Rect(viewport.center.x - width * .5f, viewport.y + 16f, width, 26f);
                ReportHudBounds(API.RimVisionHudElement.Time, panel);
                DrawNavigationPanel(panel);
                _view.Badge(new Rect(panel.x + 4f, panel.y, 68f, panel.height), _displayedTime, _timeHudStyle);
                Widgets.DrawBoxSolid(new Rect(panel.x + 76f, panel.y + 7f, 1f, 12f), new Color(1f, 1f, 1f, .22f));
                var previousColor = GUI.color;
                GUI.color = new Color(.3f, .87f, .9f);
                GUI.Label(new Rect(panel.x + 77f, panel.y, _temperatureHudWidth - 1f, panel.height),
                    _temperatureHudValue, _timeHudStyle);
                GUI.color = previousColor;
                TooltipHandler.TipRegion(panel, _displayedTemperature);
                _view.DrawHudElement(API.RimVisionHudElement.Compass, viewport);
            }

            internal void DrawTopHudMessages(Rect viewport)
            {
                if (Event.current.type != EventType.Repaint) return;
                if (_hudMessageStyle == null)
                    _hudMessageStyle = new GUIStyle(_view.hudStyle) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                float width = Mathf.Min(600f, Mathf.Max(120f, viewport.width - 368f));
                var row = new Rect(viewport.center.x - width * .5f,
                    viewport.y + 100f, width, 24f);
                if (_view._constructionPlansHidden)
                    _view.DrawTopHudMessage(ref row, "Rim3D_PlansHidden".TranslateBindings().ToString());
                if (!_view.FirstPerson)
                {
                    if (!_view.Status.NullOrEmpty()) _view.DrawTopHudMessage(ref row, _view.Status);
                    return;
                }
                if (_view.controller.InputBlocked)
                    _view.DrawTopHudMessage(ref row, "Rim3D_ObservationOnly".Translate(_view.ControlledPawn.InMentalState
                        ? _view.ControlledPawn.MentalStateDef.LabelCap.ToString() : "Rim3D_Incapacitated".Translate().ToString()).ToString());
                else
                {
                    if (_view.ControlledPawn.Drafted)
                        _view.DrawTopHudMessage(ref row, "Rim3D_CombatHud".Translate().ToString());
                    if (Time.unscaledTime < _messageUntil && !_view.actionMessage.NullOrEmpty() &&
                        _view.actionMessage != _view.ActionReport(_view.ControlledPawn) &&
                        (_bottomControlsText.NullOrEmpty() || _bottomControlsText.IndexOf(_view.actionMessage, System.StringComparison.Ordinal) < 0))
                        _view.DrawTopHudMessage(ref row, _view.actionMessage);
                }
            }

            internal void DrawTopHudMessage(ref Rect row, string message, float alpha = 1f)
            {
                _hudMessageContent.text = message;
                row.height = Mathf.Max(24f, _hudMessageStyle.CalcHeight(_hudMessageContent, row.width));
                _view.Badge(row, message, _hudMessageStyle, alpha);
                row.y = row.yMax + 4f;
            }

            internal void DrawNeedsHud(Rect viewport)
            {
                var pawn = _view.ControlledPawn;
                if (!_view.FirstPerson || pawn?.needs == null || _view.statusMenu || _view.constructionMenu || _view.actionMenu ||
                    Event.current.type != EventType.Repaint) return;

                var food = pawn.needs.food;
                var rest = pawn.needs.rest;
                _hudNeeds.Clear();
                if (food != null) _hudNeeds.Add(food);
                if (rest != null) _hudNeeds.Add(rest);
                var preferences = Current.Game?.GetComponent<NeedsHudPreferences>();
                var allNeeds = pawn.needs.AllNeeds;
                for (int i = 0; i < allNeeds.Count; i++)
                {
                    var need = allNeeds[i];
                    if (need != food && need != rest && (need.ShowOnNeedList || need == pawn.needs.mood) &&
                        preferences?.ShowAdditionalHudNeed(need.def) == true) _hudNeeds.Add(need);
                }
                int rows = _hudNeeds.Count;
                if (rows == 0) return;

                float scale = Mathf.Min(1f, viewport.height / 600f);
                float width = Mathf.Min(220f, viewport.width - 32f);
                float panelY = viewport.y + 24f + 200f * scale;
                if (Rim3DSettings.Current.CharacterHud)
                    panelY = CharacterWeaponRect(viewport, out _).yMax + 8f;
                float rowHeight = Mathf.Min(50f, Mathf.Max(1f, (viewport.yMax - panelY - 28f) / rows));
                var panel = new Rect(viewport.x + 16f, panelY,
                    width, rows * rowHeight + 12f);
                ReportHudBounds(API.RimVisionHudElement.Needs, panel);
                var color = GUI.color;
                var font = Text.Font;
                var anchor = Text.Anchor;
                bool wrap = Text.WordWrap;
                try
                {
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.WordWrap = false;
                    DrawNavigationPanel(panel);
                    var row = new Rect(panel.x, panel.y + 6f, panel.width, rowHeight);
                    // Use the same bars, thresholds, arrows and localized labels as the needs tab.
                    bool tooltip = ViewBindings.Held(ViewAction.Cursor);
                    for (int i = 0; i < _hudNeeds.Count; i++)
                    {
                        _hudNeeds[i].DrawOnGUI(row, int.MaxValue, 8f, doTooltip: tooltip);
                        row.y += rowHeight;
                    }
                }
                finally
                {
                    GUI.color = color;
                    Text.Font = font;
                    Text.Anchor = anchor;
                    Text.WordWrap = wrap;
                }
            }

            Rect CharacterWeaponRect(Rect viewport, out string label)
            {
                var weapon = _view.ControlledPawn.equipment?.Primary;
                label = weapon != null ? weapon.LabelCap.ToString() : "Rim3D_HudNoWeapon".Translate().ToString();
                if (_characterWeaponStyle == null)
                    _characterWeaponStyle = new GUIStyle(_view.hudStyle) { wordWrap = true };
                float width = Mathf.Min(220f, viewport.width - 32f);
                float height = Mathf.Max(48f, _characterWeaponStyle.CalcHeight(new GUIContent(label), Mathf.Max(1f, width - 46f)));
                return new Rect(viewport.x + 16f,
                    viewport.y + 36f + 200f * Mathf.Min(1f, viewport.height / 600f), width, height);
            }

            Rect CharacterPortraitRect(Rect viewport)
            {
                float scale = Mathf.Min(1f, viewport.height / 600f);
                return new Rect(viewport.x + 16, viewport.y + 16, 160 * scale, 200 * scale);
            }

            internal void DrawCharacterHud(Rect viewport)
            {
                if (!_view.FirstPerson || _view.ControlledPawn == null) return;
                _view.DrawHudElement(API.RimVisionHudElement.Colonists, viewport);
                _view.DrawHudElement(API.RimVisionHudElement.Portrait, viewport);
                _view.DrawHudElement(API.RimVisionHudElement.Health, viewport);
                _view.DrawHudElement(API.RimVisionHudElement.Pain, viewport);
                _view.DrawHudElement(API.RimVisionHudElement.Stamina, viewport);
                _view.DrawHudElement(API.RimVisionHudElement.Weapon, viewport);
            }

            void DrawCharacterPortrait(Rect viewport)
            {
                var pawn = _view.ControlledPawn;
                if (!_view.FirstPerson || pawn == null || Event.current.type != EventType.Repaint) return;
                var rect = CharacterPortraitRect(viewport);
                ReportHudBounds(API.RimVisionHudElement.Portrait, rect);
                var previousColor = GUI.color;
                GUI.color = Color.white;
                try
                {
                    var portrait = PortraitsCache.Get(pawn, CharacterHudSize, Rot4.South,
                        new Vector3(0, 0, .1f), CharacterHudZoom, false, false);
                    GUI.DrawTexture(rect, portrait, ScaleMode.StretchToFill, true);
                    if (_characterNameStyle == null)
                        _characterNameStyle = new GUIStyle(_view.hudStyle) { alignment = TextAnchor.MiddleCenter };
                    _view.Badge(new Rect(rect.x, rect.y, rect.width, 24f), pawn.LabelShort, _characterNameStyle);

                    var weapon = pawn.equipment?.Primary;
                    if (PawnIsProne(pawn)) return;
                    bool aiming = pawn.stances?.curStance is Stance_Busy busy &&
                        (!busy.neverAimWeapon || busy.verb?.IsMeleeAttack == true) && busy.focusTarg.IsValid;
                    if (weapon != null && pawn.CurJob?.def.neverShowWeapon != true &&
                        (aiming || pawn.Drafted || PawnRenderUtility.CarryWeaponOpenly(pawn)))
                    {
                        float factor = pawn.ageTracker.CurLifeStage.equipmentDrawDistanceFactor;
                        float angle = aiming ? 0 : 53 + weapon.def.equippedAngleOffset;
                        _view.DrawCharacterHudItem(rect, weapon, new Vector2(0, -.22f * factor),
                            WeaponSpriteScale * Mathf.Max(weapon.Graphic.drawSize.x, weapon.Graphic.drawSize.y), angle);
                    }

                    var carried = pawn.carryTracker?.CarriedThing;
                    if (carried != null && (!pawn.Drafted || HeldHumanPawn(carried) != null))
                        _view.DrawCharacterHudItem(rect, carried, new Vector2(.28f, -.1f), .55f, HeldHumanPawn(carried) != null ? 90 : 0);
                }
                finally { GUI.color = previousColor; }
            }

            void DrawCharacterWeaponPanel(Rect viewport)
            {
                var pawn = _view.ControlledPawn;
                if (!_view.FirstPerson || pawn == null || Event.current.type != EventType.Repaint) return;
                var previousColor = GUI.color;
                GUI.color = Color.white;
                try
                {
                    var weapon = pawn.equipment?.Primary;
                    var weaponRect = CharacterWeaponRect(viewport, out var weaponLabel);
                    DrawNavigationPanel(weaponRect);
                    var weaponIcon = new Rect(weaponRect.x, weaponRect.y, 40f, 40f);
                    if (weapon != null) _view.DrawHeldThingIcon(weaponIcon, weapon);
                    else GUI.DrawTexture(weaponIcon, TexCommand.AttackMelee, ScaleMode.ScaleToFit, true);
                    float labelWidth = Mathf.Max(1f, weaponRect.width - 46f);
                    _view.Badge(new Rect(weaponRect.x + 46f, weaponRect.y, labelWidth,
                        weaponRect.height),
                        weaponLabel, _characterWeaponStyle);

                    ReportHudBounds(API.RimVisionHudElement.Weapon, weaponRect);
                }
                finally { GUI.color = previousColor; }
            }

            void DrawCharacterVitals(Rect portrait, Rect weaponPanel, API.RimVisionHudElement element)
            {
                float scale = Mathf.Max(.85f, portrait.height / CharacterHudSize.y);
                var panel = new Rect(portrait.xMax - 12f * scale, portrait.center.y - 70f * scale,
                    64f * scale, 66f * scale);
                if (_characterVitalsStyle == null)
                    _characterVitalsStyle = new GUIStyle(_view.hudStyle)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontStyle = FontStyle.Bold,
                        wordWrap = false
                    };
                _characterVitalsStyle.fontSize = Mathf.RoundToInt(18f * scale);
                float x = panel.center.x;
                if (element == API.RimVisionHudElement.Health)
                {
                    ReportHudBounds(element, panel);
                    DrawNavigationPanel(panel);
                    float y = panel.y + 8f * scale;
                    var healthColor = new Color(1f, .42f, .42f);
                    Widgets.DrawBoxSolid(new Rect(x - 4f * scale, y, 8f * scale, 26f * scale), healthColor);
                    Widgets.DrawBoxSolid(new Rect(x - 13f * scale, y + 9f * scale, 26f * scale, 8f * scale), healthColor);
                    _view.Badge(new Rect(panel.x, y + 28f * scale, panel.width, 24f * scale),
                        Mathf.RoundToInt(_view.health * 100f) + "%", _characterVitalsStyle);

                }
                if (element == API.RimVisionHudElement.Pain)
                {
                    panel.y += 74f * scale;
                    DrawNavigationPanel(panel);
                    float y = panel.y + 8f * scale;
                    var painIcon = ModAssets.LoadTexture("textures/RimVision/Custom/Pain");
                    if (painIcon != null)
                        GUI.DrawTexture(new Rect(x - 13f * scale, y, 26f * scale, 26f * scale),
                            painIcon, ScaleMode.ScaleToFit, true);
                    _view.Badge(new Rect(panel.x, y + 28f * scale, panel.width, 24f * scale),
                        Mathf.RoundToInt(_view.pain * 100f) + "%", _characterVitalsStyle);

                    ReportHudBounds(element, panel);
                }
                if (element == API.RimVisionHudElement.Stamina)
                {
                    var stamina = new Rect(weaponPanel.x, portrait.yMax + 2f, weaponPanel.width, 6f);
                    ReportHudBounds(element, stamina);
                    Widgets.DrawBoxSolid(stamina, new Color(.06f, .08f, .12f, .85f));
                    stamina.width *= Mathf.Clamp01(_view.controller.Stamina);
                    Widgets.DrawBoxSolid(stamina, Color.Lerp(new Color(1f, .65f, .25f),
                        new Color(.45f, .8f, .75f), _view.controller.Stamina));
                }
            }

            void DrawCharacterPawnList(Rect viewport, Rect portrait)
            {
                var pawns = _view.SpatialPawns();
                if (pawns.Count == 0) return;
                var bar = Find.ColonistBar;
                if (bar.Scale <= 0f) return;
                float gap = 4f;
                float height = (portrait.height - gap * 2f) / 3f;
                float nativeHeight = ColonistBarColonistDrawer.PawnTextureSize.y * bar.Scale + 8f;
                float scale = height / nativeHeight * 1.15f;
                float width = (bar.Size.x + bar.SpaceBetweenColonistsHorizontal) * (height / (nativeHeight + 8f));
                float left = Mathf.Max(portrait.xMax + 72f * Mathf.Max(.85f, portrait.height / CharacterHudSize.y), viewport.x + Mathf.Min(220f, viewport.width - 32f) + 16f) + 8f;
                ReportHudBounds(API.RimVisionHudElement.Colonists, new Rect(left, portrait.y,
                    ((pawns.Count + 2) / 3) * (width + gap) - gap, portrait.height));
                for (int i = 0; i < pawns.Count; i++)
                {
                    var rect = new Rect(left + (i / 3) * (width + gap),
                        portrait.y + (i % 3) * (height + gap), width, height);
                    bool selected = pawns[i] == _view.ControlledPawn;
                    var current = Event.current;
                    if (Rim3DSettings.Current.PortraitPawnSwitch && current.type == EventType.MouseDown && current.button == 0 &&
                        (current.alt || ViewBindings.Held(ViewAction.Cursor)) && rect.Contains(current.mousePosition))
                    {
                        current.Use();
                        if (!selected && !_view.FallPresentationActive && !Rim3DMultiplayer.PawnLocked &&
                            PawnControl.CanAcquire(pawns[i]) &&
                            (!Rim3DMultiplayer.Active || Rim3DMultiplayer.AvailableForLocal?.Invoke(pawns[i]) != false))
                            _view.EnterFirstPerson(pawns[i]);
                        return;
                    }
                    if (current.type != EventType.Repaint) continue;
                    var matrix = GUI.matrix;
                    var font = Text.Font;
                    try
                    {
                        GUI.matrix = matrix * Matrix4x4.TRS(new Vector3(rect.x, rect.y - 14f * (portrait.height / CharacterHudSize.y), 0f),
                            Quaternion.identity, new Vector3(scale, scale, 1f));
                        float top = (ColonistBarColonistDrawer.PawnTextureSize.y - ColonistBar.BaseSize.y) * bar.Scale + 2f;
                        var card = new Rect((width / scale - bar.Size.x) * .5f, top, bar.Size.x, bar.Size.y);
                        bar.drawer.DrawColonist(card, pawns[i], API.RimVisionControl.MapFor(pawns[i]), false, false);
                    }
                    finally
                    {
                        GUI.matrix = matrix;
                        Text.Font = font;
                    }
                    if (selected)
                    {
                        GUI.color = new Color(.35f, .9f, 1f);
                        Widgets.DrawBox(rect, 2);
                        GUI.color = Color.white;
                    }
                }
            }

            internal void DrawCharacterHudItem(Rect portrait, Thing thing, Vector2 offset, float size, float angle)
            {
                float pixelsPerCell = portrait.height * CharacterHudZoom * .5f;
                var center = portrait.center + new Vector2(offset.x, .1f - offset.y) * pixelsPerCell;
                float extent = pixelsPerCell * size;
                var rect = new Rect(center.x - extent * .5f, center.y - extent * .5f, extent, extent);
                var matrix = GUI.matrix;
                try
                {
                    GUIUtility.RotateAroundPivot(angle, center);
                    _view.DrawHeldThingIcon(rect, thing);
                }
                finally { GUI.matrix = matrix; }
            }

        }
    }
}
