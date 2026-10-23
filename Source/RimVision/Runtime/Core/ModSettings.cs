using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class Rim3DSettings : ModSettings
    {
        static readonly string[] _tabs = { "Graphics", "World", "Effects", "Interface", "Controls", "Minigames" };
        public static Rim3DSettings Current { get; internal set; } = new Rim3DSettings();
        public bool VisualEffects = true, WeatherParticles = true, WeatherFog = true;
        public bool GroundSnow = true, FireEffects = true, GroundShadows = true, AmbientOcclusion = true;
        float _ambientOcclusionIntensity = 1;
        internal float AmbientOcclusionIntensity => _ambientOcclusionIntensity;
        public bool CameraBob = true, DamageShake = true, StatusEffects = true;
        float _walkCameraBobIntensity = 1, _runCameraBobIntensity = 1;
        internal float WalkCameraBobIntensity => _walkCameraBobIntensity;
        internal float RunCameraBobIntensity => _runCameraBobIntensity;
        public bool ScarPainEffects;
        public float HungerEffectIntensity = 1, SleepEffectIntensity = 1, PainEffectIntensity = 1;
        public bool FirstPersonItems = true, CharacterHud = true, NeedsHud = true;
        bool _portraitPawnSwitch = true;
        internal bool PortraitPawnSwitch => _portraitPawnSwitch;
        bool _hideInterface;
        internal bool HideInterface
        {
            get
            {
#if UNITY_EDITOR
                if (Rim3DPreview.Instance != null && Rim3DPreview.Instance.HideInterfaceForScreenshots) return true;
#endif
                return _hideInterface;
            }
        }
        internal void ToggleInterfaceVisibility()
        {
            _hideInterface = !HideInterface;
#if UNITY_EDITOR
            if (Rim3DPreview.Instance != null) Rim3DPreview.Instance.HideInterfaceForScreenshots = false;
#endif
        }
        bool _resourceHud = true;
        internal bool ResourceHud => _resourceHud;
        bool _compass = true;
        public bool Compass => _compass;
        internal bool CompassEnabled => _compass && (!Rim3DMultiplayer.Active ||
            Rim3DMultiplayer.CompassDisabled?.Invoke() != true);
        bool _firstPersonHands = true;
        internal bool FirstPersonHands => _firstPersonHands;
        HashSet<string> _additionalHudNeeds = new HashSet<string>();
        internal HashSet<string> CopyAdditionalHudNeeds() => new HashSet<string>(_additionalHudNeeds);
        public bool Crosshair = true, CombatFeedback = true, Heartbeat = true;
        public bool ZoneOutlines = true;
        public bool VanillaOutlines;
        int _outlineType;
        float _screenOutlineWidth = 1.25f, _hullOutlineWidth = 1.25f;
        internal int OutlineType => _outlineType;
        internal float ScreenOutlineWidth => _screenOutlineWidth;
        internal float HullOutlineWidth => _hullOutlineWidth;
        bool _autoGenerateBuildings = true;
        internal bool AutoGenerateBuildings => _autoGenerateBuildings;
        bool _animalPlantModels = true;
        internal bool AnimalPlantModels => _animalPlantModels;
        bool _legacyTreeScale;
        internal float TreeScale => _legacyTreeScale ? 1f : 2f;
        internal float TreeScaleFor(ThingDef def) => def.plant.IsTree && !def.plant.isStump &&
            def.defName.IndexOf("Cactus", System.StringComparison.OrdinalIgnoreCase) < 0 ? TreeScale : 1f;
        public bool BottomControls = true;
        public bool AntiAliasing = true, InvertMouseY;
        bool _modelLods = true, _distantModelCards = true;
        float _modelLodDistance = 48, _modelCardDistance = 112;
        internal bool DistantModelCards => _distantModelCards;
        internal float ModelCardDistance => _modelCardDistance;
        bool _simpleWater;
        internal bool SimpleWater => _simpleWater;
        bool _plantWind = true;
        internal bool PlantWind => _plantWind;
        bool _neighborMaps;
        internal bool NeighborMaps => _neighborMaps;
        public bool AccelerateActions = true;
        bool _minigames = true, _minigameExperience = true;
        HashSet<WorkMinigameKind> _disabledMinigames = new HashSet<WorkMinigameKind>();
        Dictionary<WorkMinigameKind, float> _minigameMultipliers = new Dictionary<WorkMinigameKind, float>();
        internal bool Minigames => _minigames;
        internal bool MinigameEnabled(WorkMinigameKind kind) => _minigames && !_disabledMinigames.Contains(kind);
        public float MinigameRewardMultiplier(WorkMinigameKind kind) =>
            _minigameMultipliers.TryGetValue(kind, out float value) ? WorkMinigames.ClampRewardMultiplier(value) : 1;
        public bool MinigameWorkBonus => _minigameExperience;
        public bool MinigameExperience => MinigameWorkBonus;
        internal bool MinigameWorkBonusEnabled => Rim3DMultiplayer.Active
            ? Rim3DMultiplayer.MinigameRewardsAllowed?.Invoke() == true : _minigameExperience;
        bool _lockClientPawns;
        public bool LockClientPawns => _lockClientPawns;
        bool _blockAutomaticPawnSwitch;
        public bool BlockAutomaticPawnSwitch => _blockAutomaticPawnSwitch;
        public bool DisablePlantSlowdown;
        bool _shootWhileRunning;
        public bool ShootWhileRunning => _shootWhileRunning;
        internal bool ShootWhileRunningEnabled => Rim3DMultiplayer.Active
            ? Rim3DMultiplayer.ShootWhileRunningAllowed?.Invoke() == true : _shootWhileRunning;
        public float FieldOfView = 75, MouseSensitivity = 1, RenderScale = 1;
        public float NightBrightness = 1;
        public int MaxPawnTextureResolution = 1024;
        int _mountainSimplification = 2;
        internal int MountainSimplification => _mountainSimplification;
        internal bool EffectsEnabled(bool option) => VisualEffects && option;

        public override void ExposeData()
        {
            base.ExposeData();
            ExposeBindings();
            Scribe_Values.Look(ref VisualEffects, "visualEffects", true);
            Scribe_Values.Look(ref WeatherParticles, "weatherParticles", true);
            Scribe_Values.Look(ref WeatherFog, "weatherFog", true);
            Scribe_Values.Look(ref _plantWind, "plantWind", true);
            Scribe_Values.Look(ref GroundSnow, "groundSnow", true);
            Scribe_Values.Look(ref FireEffects, "fireEffects", true);
            Scribe_Values.Look(ref GroundShadows, "groundShadows", true);
            Scribe_Values.Look(ref AmbientOcclusion, "ambientOcclusion", true);
            Scribe_Values.Look(ref _ambientOcclusionIntensity, "ambientOcclusionIntensity", 1);
            Scribe_Values.Look(ref CameraBob, "cameraBob", true);
            Scribe_Values.Look(ref _walkCameraBobIntensity, "walkCameraBobIntensity", 1);
            Scribe_Values.Look(ref _runCameraBobIntensity, "runCameraBobIntensity", 1);
            Scribe_Values.Look(ref DamageShake, "damageShake", true);
            Scribe_Values.Look(ref StatusEffects, "statusEffects", true);
            Scribe_Values.Look(ref ScarPainEffects, "scarPainEffects", false);
            Scribe_Values.Look(ref HungerEffectIntensity, "hungerEffectIntensity", 1);
            Scribe_Values.Look(ref SleepEffectIntensity, "sleepEffectIntensity", 1);
            Scribe_Values.Look(ref PainEffectIntensity, "painEffectIntensity", 1);
            Scribe_Values.Look(ref FirstPersonItems, "firstPersonItems", true);
            Scribe_Values.Look(ref _firstPersonHands, "firstPersonHands", true);
            Scribe_Values.Look(ref _compass, "compass", true);
            Scribe_Values.Look(ref CharacterHud, "characterHud", true);
            Scribe_Values.Look(ref _portraitPawnSwitch, "portraitPawnSwitch", true);
            Scribe_Values.Look(ref _hideInterface, "hideInterface", false);
            Scribe_Values.Look(ref NeedsHud, "needsHud", true);
            Scribe_Values.Look(ref _resourceHud, "resourceHud", true);
            Scribe_Collections.Look(ref _additionalHudNeeds, "additionalHudNeeds", LookMode.Value);
            if (_additionalHudNeeds == null) _additionalHudNeeds = new HashSet<string>();
            Scribe_Values.Look(ref BottomControls, "bottomControls", true);
            Scribe_Values.Look(ref ZoneOutlines, "zoneOutlines", true);
            Scribe_Values.Look(ref VanillaOutlines, "vanillaOutlines", false);
            VanillaOutlines = false;
            Scribe_Values.Look(ref _outlineType, "outlineType", 0);
            Scribe_Values.Look(ref _screenOutlineWidth, "screenOutlineWidth", 1.25f);
            Scribe_Values.Look(ref _hullOutlineWidth, "hullOutlineWidth", 1.25f);
            Scribe_Values.Look(ref _autoGenerateBuildings, "autoGenerateBuildings", true);
            Scribe_Values.Look(ref _animalPlantModels, "animalPlantModels", true);
            Scribe_Values.Look(ref _legacyTreeScale, "legacyTreeScale", false);
            Scribe_Values.Look(ref Crosshair, "crosshair", true);
            Scribe_Values.Look(ref CombatFeedback, "combatFeedback", true);
            Scribe_Values.Look(ref Heartbeat, "heartbeat", true);
            Scribe_Values.Look(ref AntiAliasing, "antiAliasing", true);
            Scribe_Values.Look(ref _modelLods, "modelLods", true);
            Scribe_Values.Look(ref _distantModelCards, "distantModelCards", true);
            Scribe_Values.Look(ref _modelLodDistance, "modelLodDistance", 48);
            Scribe_Values.Look(ref _modelCardDistance, "modelCardDistance", 112);
            _modelLodDistance = Mathf.Clamp(_modelLodDistance, 24, 96);
            _modelCardDistance = Mathf.Clamp(_modelCardDistance, 96, 240);
            Scribe_Values.Look(ref _simpleWater, "simpleWater", false);
            Scribe_Values.Look(ref _neighborMaps, "neighborMaps", false);
            Scribe_Values.Look(ref InvertMouseY, "invertMouseY", false);
            Scribe_Values.Look(ref AccelerateActions, "accelerateActions", true);
            Scribe_Values.Look(ref _minigames, "minigames", true);
            Scribe_Values.Look(ref _minigameExperience, "minigameExperience", true);
            Scribe_Collections.Look(ref _disabledMinigames, "disabledMinigames", LookMode.Value);
            Scribe_Collections.Look(ref _minigameMultipliers, "minigameRewardMultipliers", LookMode.Value, LookMode.Value);
            if (_disabledMinigames == null) _disabledMinigames = new HashSet<WorkMinigameKind>();
            if (_minigameMultipliers == null) _minigameMultipliers = new Dictionary<WorkMinigameKind, float>();
            Scribe_Values.Look(ref _lockClientPawns, "lockClientPawns", false);
            Scribe_Values.Look(ref _blockAutomaticPawnSwitch, "blockAutomaticPawnSwitch", false);
            Scribe_Values.Look(ref DisablePlantSlowdown, "disablePlantSlowdown", false);
            Scribe_Values.Look(ref _shootWhileRunning, "shootWhileRunning", false);
            Scribe_Values.Look(ref FieldOfView, "fieldOfView", 75);
            Scribe_Values.Look(ref MouseSensitivity, "mouseSensitivity", 1);
            Scribe_Values.Look(ref RenderScale, "renderScale", 1);
            Scribe_Values.Look(ref NightBrightness, "nightBrightness", 1);
            Scribe_Values.Look(ref MaxPawnTextureResolution, "maxPawnTextureResolution", 1024);
            Scribe_Values.Look(ref _mountainSimplification, "mountainSimplification", 2);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) ClampValues();
        }

        void ClampValues()
        {
            FieldOfView = Clamp(FieldOfView, 50, 110, 75);
            MouseSensitivity = Clamp(MouseSensitivity, .2f, 3, 1);
            RenderScale = Clamp(RenderScale, .5f, 1.5f, 1);
            NightBrightness = Clamp(NightBrightness, 1, 4, 1);
            _ambientOcclusionIntensity = Clamp(_ambientOcclusionIntensity, 0, 2, 1);
            _walkCameraBobIntensity = Clamp(_walkCameraBobIntensity, 0, 5, 1);
            _runCameraBobIntensity = Clamp(_runCameraBobIntensity, 0, 5, 1);
            HungerEffectIntensity = Clamp(HungerEffectIntensity, 0, 1, 1);
            SleepEffectIntensity = Clamp(SleepEffectIntensity, 0, 1, 1);
            PainEffectIntensity = Clamp(PainEffectIntensity, 0, 1, 1);
            MaxPawnTextureResolution = Mathf.Clamp(MaxPawnTextureResolution, 128, 4096);
            _mountainSimplification = Mathf.Clamp(_mountainSimplification, 0, 3);
            _outlineType = Mathf.Clamp(_outlineType, 0, 1);
            _screenOutlineWidth = Clamp(_screenOutlineWidth, .5f, 8, 1.25f);
            _hullOutlineWidth = Clamp(_hullOutlineWidth, .5f, 8, 1.25f);
        }

        static float Clamp(float value, float min, float max, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, min, max);

        internal void Reset()
        {
            ResetBindings();
            _plantWind = true;
            _ambientOcclusionIntensity = 1;
            VisualEffects = WeatherParticles = WeatherFog = GroundSnow = FireEffects = GroundShadows = AmbientOcclusion = true;
            CameraBob = DamageShake = StatusEffects = FirstPersonItems = CharacterHud = NeedsHud = true;
            _portraitPawnSwitch = true;
            _compass = true;
            _hideInterface = false;
            _resourceHud = true;
            _firstPersonHands = true;
            _additionalHudNeeds.Clear();
            _walkCameraBobIntensity = _runCameraBobIntensity = 1;
            Crosshair = CombatFeedback = Heartbeat = AntiAliasing = true;
            _modelLods = _distantModelCards = true;
            _modelLodDistance = 48;
            _modelCardDistance = 112;
            _simpleWater = false;
            _neighborMaps = false;
            ZoneOutlines = true;
            VanillaOutlines = false;
            _outlineType = 0;
            _screenOutlineWidth = _hullOutlineWidth = 1.25f;
            _autoGenerateBuildings = true;
            _animalPlantModels = true;
            _legacyTreeScale = false;
            BottomControls = true;
            InvertMouseY = false;
            ScarPainEffects = false;
            HungerEffectIntensity = SleepEffectIntensity = PainEffectIntensity = 1;
            AccelerateActions = true;
            _minigames = _minigameExperience = true;
            _disabledMinigames.Clear();
            _minigameMultipliers.Clear();
            _lockClientPawns = false;
            _blockAutomaticPawnSwitch = false;
            DisablePlantSlowdown = false;
            _shootWhileRunning = false;
            FieldOfView = 75; MouseSensitivity = RenderScale = 1;
            NightBrightness = 1;
            MaxPawnTextureResolution = 1024;
            _mountainSimplification = 2;
        }

        readonly Vector2[] _scroll = new Vector2[_tabs.Length];
        readonly float[] _contentHeight = { 600, 600, 600, 600, 600, 600 };
        int _selectedTab;

        internal void Draw(Rect rect)
        {
            var font = Text.Font;
            var anchor = Text.Anchor;
            var color = GUI.color;
            try
            {
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                var hint = "Rim3D_SettingsApplyHint".Translate().ToString();
                float hintHeight = Text.CalcHeight(hint, rect.width);
                GUI.color = new Color(.72f, .78f, .82f);
                var hintRect = new Rect(rect.x, rect.y, rect.width, hintHeight);
                Widgets.Label(hintRect, hint);
                GUI.color = color;
                TooltipHandler.TipRegion(hintRect, "Rim3D_SettingsHint".Translate());

                int columns = Mathf.Clamp(Mathf.FloorToInt((rect.width + 6) / 146), 1, _tabs.Length);
                float tabWidth = (rect.width - (columns - 1) * 6) / columns;
                float tabY = hintRect.yMax + 12;
                for (int i = 0; i < _tabs.Length; i++)
                {
                    var button = new Rect(rect.x + i % columns * (tabWidth + 6),
                        tabY + i / columns * 40, tabWidth, 34);
                    if (_selectedTab == i)
                        Widgets.DrawBoxSolid(button.ExpandedBy(2), new Color(.3f, .6f, .65f));
                    if (Widgets.ButtonText(button, ("Rim3D_SettingsTab" + _tabs[i]).Translate()))
                        _selectedTab = i;
                }

                float bodyY = tabY + ((_tabs.Length + columns - 1) / columns) * 40 + 8;
                var body = new Rect(rect.x, bodyY, rect.width, Mathf.Max(1, rect.yMax - bodyY - 50));
                Widgets.DrawBoxSolid(body, new Color(.07f, .09f, .12f, .55f));
                var viewport = body.ContractedBy(10);
                var content = new Rect(0, 0, Mathf.Max(100, viewport.width - 20), _contentHeight[_selectedTab]);
                Widgets.BeginScrollView(viewport, ref _scroll[_selectedTab], content);
                var list = new Listing_Standard { maxOneColumn = true };
                list.Begin(content);
                switch (_selectedTab)
                {
                    case 0: DrawGraphics(list); break;
                    case 1: DrawWorld(list); break;
                    case 2: DrawEffects(list); break;
                    case 3: DrawInterface(list); break;
                    case 4: DrawControls(list); break;
                    case 5: DrawMinigames(list); break;
                }
                _contentHeight[_selectedTab] = list.CurHeight + 12;
                list.End();
                Widgets.EndScrollView();

                if (Widgets.ButtonText(new Rect(rect.xMax - Mathf.Min(300, rect.width), rect.yMax - 34,
                    Mathf.Min(300, rect.width), 34), "Rim3D_SettingsReset".Translate())) Reset();
            }
            finally
            {
                Text.Font = font;
                Text.Anchor = anchor;
                GUI.color = color;
            }
        }

        void DrawGraphics(Listing_Standard list)
        {
            Section(list, "Quality");
            Check(list, "AntiAliasing", ref AntiAliasing);
            Check(list, "AmbientOcclusion", ref AmbientOcclusion);
            list.Label("Rim3D_SettingAmbientOcclusionIntensity".Translate(Mathf.RoundToInt(_ambientOcclusionIntensity * 100)));
            _ambientOcclusionIntensity = Mathf.Round(list.Slider(_ambientOcclusionIntensity, 0, 2) * 20) / 20;
            Check(list, "SimpleWater", ref _simpleWater);
            Hint(list, "SimpleWater");
            Check(list, "PlantWind", ref _plantWind);
            Hint(list, "PlantWind");
            list.Label("Rim3D_SettingRenderScale".Translate(Mathf.RoundToInt(RenderScale * 100)));
            RenderScale = Mathf.Round(list.Slider(RenderScale, .5f, 1.5f) * 20) / 20;
            list.Label("Rim3D_SettingMaxPawnTextureResolution".Translate(MaxPawnTextureResolution));
            MaxPawnTextureResolution = 1 << Mathf.RoundToInt(list.Slider(
                Mathf.Log(MaxPawnTextureResolution, 2), 7, 12));
            Hint(list, "MaxPawnTextureResolution");
            Check(list, "DistantModelCards", ref _distantModelCards);
            list.Label("Rim3D_SettingModelCardDistance".Translate(Mathf.RoundToInt(_modelCardDistance)));
            _modelCardDistance = Mathf.Round(list.Slider(_modelCardDistance, 96, 240));
            Section(list, "Mountains");
            list.Label("Rim3D_SettingMountainSimplification".Translate(
                ("Rim3D_MountainSimplification" + _mountainSimplification).Translate()));
            _mountainSimplification = Mathf.RoundToInt(list.Slider(_mountainSimplification, 0, 3));
            Hint(list, "MountainSimplification");
            Section(list, "Lighting");
            list.Label("Rim3D_SettingNightBrightness".Translate(Mathf.RoundToInt(NightBrightness * 100)));
            NightBrightness = Mathf.Round(list.Slider(NightBrightness, 1, 4) * 20) / 20;
            Hint(list, "NightBrightness");
        }

        void DrawWorld(Listing_Standard list)
        {
            Check(list, "NeighborMaps", ref _neighborMaps);
            Hint(list, "NeighborMaps");
            Section(list, "Models");
            Check(list, "AutoGenerateBuildings", ref _autoGenerateBuildings);
            Hint(list, "AutoGenerateBuildings");
            if (list.ButtonText("Rim3D_SettingAnimalPlantModels".Translate(_animalPlantModels ? "3D" : "2D")))
                _animalPlantModels = !_animalPlantModels;
            Hint(list, "AnimalPlantModels");
            Check(list, "LegacyTreeScale", ref _legacyTreeScale);
            Hint(list, "LegacyTreeScale");
        }

        void DrawEffects(Listing_Standard list)
        {
            Check(list, "VisualEffects", ref VisualEffects);
            Hint(list, "VisualEffects");
            Section(list, "Weather");
            Check(list, "WeatherParticles", ref WeatherParticles);
            Check(list, "WeatherFog", ref WeatherFog);
            Check(list, "GroundSnow", ref GroundSnow);
            Check(list, "FireEffects", ref FireEffects);
            Check(list, "GroundShadows", ref GroundShadows);
            Section(list, "CameraEffects");
            Check(list, "CameraBob", ref CameraBob);
            list.Label("Rim3D_SettingWalkCameraBobIntensity".Translate(Mathf.RoundToInt(_walkCameraBobIntensity * 100)));
            _walkCameraBobIntensity = Mathf.Round(list.Slider(_walkCameraBobIntensity, 0, 5) * 20) / 20;
            list.Label("Rim3D_SettingRunCameraBobIntensity".Translate(Mathf.RoundToInt(_runCameraBobIntensity * 100)));
            _runCameraBobIntensity = Mathf.Round(list.Slider(_runCameraBobIntensity, 0, 5) * 20) / 20;
            Check(list, "DamageShake", ref DamageShake);
            Section(list, "Status");
            Check(list, "StatusEffects", ref StatusEffects);
            list.Label("Rim3D_SettingHungerEffectIntensity".Translate(Mathf.RoundToInt(HungerEffectIntensity * 100)));
            HungerEffectIntensity = Mathf.Round(list.Slider(HungerEffectIntensity, 0, 1) * 20) / 20;
            list.Label("Rim3D_SettingSleepEffectIntensity".Translate(Mathf.RoundToInt(SleepEffectIntensity * 100)));
            SleepEffectIntensity = Mathf.Round(list.Slider(SleepEffectIntensity, 0, 1) * 20) / 20;
            list.Label("Rim3D_SettingPainEffectIntensity".Translate(Mathf.RoundToInt(PainEffectIntensity * 100)));
            PainEffectIntensity = Mathf.Round(list.Slider(PainEffectIntensity, 0, 1) * 20) / 20;
            Check(list, "ScarPainEffects", ref ScarPainEffects);
            Check(list, "Heartbeat", ref Heartbeat);
        }

        void DrawInterface(Listing_Standard list)
        {
            Section(list, "Hud");
            Check(list, "HideInterface", ref _hideInterface);
            Hint(list, "HideInterface");
            Check(list, "CharacterHud", ref CharacterHud);
            Check(list, "PortraitPawnSwitch", ref _portraitPawnSwitch);
            Check(list, "Compass", ref _compass);
            Hint(list, "Compass");
            Check(list, "NeedsHud", ref NeedsHud);
            Check(list, "ResourceHud", ref _resourceHud);
            Check(list, "BottomControls", ref BottomControls);
            Check(list, "ZoneOutlines", ref ZoneOutlines);
            Section(list, "FirstPerson");
            Check(list, "FirstPersonItems", ref FirstPersonItems);
            Check(list, "FirstPersonHands", ref _firstPersonHands);
            Check(list, "Crosshair", ref Crosshair);
            Check(list, "CombatFeedback", ref CombatFeedback);
        }

        void DrawControls(Listing_Standard list)
        {
            Section(list, "Camera");
            list.Label("Rim3D_SettingFieldOfView".Translate(Mathf.RoundToInt(FieldOfView)));
            FieldOfView = Mathf.Round(list.Slider(FieldOfView, 50, 110));
            list.Label("Rim3D_SettingMouseSensitivity".Translate(MouseSensitivity.ToString("0.00")));
            MouseSensitivity = list.Slider(MouseSensitivity, .2f, 3);
            Check(list, "InvertMouseY", ref InvertMouseY);
            Section(list, "Actions");
            bool enabled = GUI.enabled;
            bool canChangeLock = !Rim3DMultiplayer.Active || Rim3DMultiplayer.IsHost?.Invoke() == true;
            bool pawnLock = canChangeLock ? _lockClientPawns : Rim3DMultiplayer.PawnLockEnabled?.Invoke() == true;
            GUI.enabled = enabled && canChangeLock;
            Check(list, "LockClientPawns", ref pawnLock);
            if (canChangeLock) _lockClientPawns = pawnLock;
            GUI.enabled = enabled;
            Hint(list, "LockClientPawns");
            bool shootWhileRunning = canChangeLock ? _shootWhileRunning : ShootWhileRunningEnabled;
            GUI.enabled = enabled && canChangeLock;
            Check(list, "ShootWhileRunning", ref shootWhileRunning);
            if (canChangeLock) _shootWhileRunning = shootWhileRunning;
            GUI.enabled = enabled;
            Hint(list, "ShootWhileRunning");
            Check(list, "BlockAutomaticPawnSwitch", ref _blockAutomaticPawnSwitch);
            Hint(list, "BlockAutomaticPawnSwitch");
            Check(list, "AccelerateActions", ref AccelerateActions);
            Check(list, "DisablePlantSlowdown", ref DisablePlantSlowdown);
            DrawBindings(list);
        }

        void DrawMinigames(Listing_Standard list)
        {
            Check(list, "Minigames", ref _minigames);
            Hint(list, "MinigameLocal");
            bool enabled = GUI.enabled;
            bool canChangeRewards = !Rim3DMultiplayer.Active || Rim3DMultiplayer.IsHost?.Invoke() == true;
            try
            {
                GUI.enabled = enabled && canChangeRewards;
                bool rewards = canChangeRewards ? _minigameExperience : MinigameWorkBonusEnabled;
                Check(list, "MinigameExperience", ref rewards);
                if (canChangeRewards) _minigameExperience = rewards;
                GUI.enabled = enabled;
                Hint(list, "MinigameHostRewards");
                Hint(list, "MinigameExperience");
                foreach (var kind in WorkMinigames.Kinds)
                {
                    list.GapLine();
                    bool play = !_disabledMinigames.Contains(kind);
                    list.CheckboxLabeled(("Rim3D_MinigameOption" + kind).Translate(), ref play);
                    if (play) _disabledMinigames.Remove(kind);
                    else _disabledMinigames.Add(kind);
                    GUI.enabled = enabled && canChangeRewards;
                    float multiplier = canChangeRewards ? MinigameRewardMultiplier(kind) : WorkMinigames.RewardMultiplier(kind);
                    list.Label("Rim3D_SettingMinigameMultiplier".Translate(multiplier.ToString("0.00")));
                    float value = Mathf.Round(list.Slider(multiplier, 0, 5) * 20) / 20;
                    if (canChangeRewards && value != multiplier) _minigameMultipliers[kind] = value;
                    GUI.enabled = enabled;
                }
            }
            finally { GUI.enabled = enabled; }
        }

        static void Section(Listing_Standard list, string key)
        {
            list.Gap(12);
            var color = GUI.color;
            GUI.color = new Color(.55f, .8f, .82f);
            list.Label(("Rim3D_SettingsSection" + key).Translate());
            GUI.color = color;
            list.GapLine(8);
        }

        static void Hint(Listing_Standard list, string key)
        {
            var font = Text.Font;
            var color = GUI.color;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(.72f, .78f, .82f);
            list.Label(("Rim3D_Setting" + key + "Hint").Translate());
            GUI.color = color;
            Text.Font = font;
            list.Gap();
        }

        static void Check(Listing_Standard list, string key, ref bool value) =>
            list.CheckboxLabeled(("Rim3D_Setting" + key).TranslateBindings(), ref value);
    }
}
