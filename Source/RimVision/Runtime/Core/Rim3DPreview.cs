using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    // Independent 3D rendering; pawn movement runs through the original game simulation.
    [DefaultExecutionOrder(-10000)]
    public sealed class Rim3DPreview : MonoBehaviour
    {
        public static Rim3DPreview Instance { get; private set; }
        public static Action RequestGameInputFocus;
#if UNITY_EDITOR
        [Header("Editor sandbox")]
        [Tooltip("Temporarily unlock all research and show all build-menu items. Does not change saved research progress.")]
        public bool UnlockAllResearchAndBuildings;
        [Tooltip("Place finished buildings and floors immediately, without resources or construction work.")]
        public bool InstantConstruction;
        [Tooltip("Show names above buildings in the 3D view.")]
        public bool ShowBuildingNames;
        [Tooltip("Hide all preview text and interface and fill the Game view for screenshots.")]
        public bool HideInterfaceForScreenshots;
#endif
        public PreviewWindow ActiveWindow { get; private set; }
        Map jumpMap;
        IntVec3 jumpCell;
        bool openingPreview;

        public void QueueCameraJump(Map map, IntVec3 cell)
        {
            if (ActiveWindow?.IsOpen != true || ActiveWindow.View == null ||
                map == null || ActiveWindow.Map != map || !cell.InBounds(map)) return;
            jumpMap = map;
            jumpCell = cell;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Bootstrap()
        {
            if (Instance != null) return;
            var host = new GameObject("RimVision preview controller");
            DontDestroyOnLoad(host);
            Instance = host.AddComponent<Rim3DPreview>();
#if UNITY_EDITOR
            Instance.UnlockAllResearchAndBuildings = UnityEditor.EditorPrefs.GetBool("Rim3D.EditorUnlockAll", false);
            Instance.InstantConstruction = UnityEditor.EditorPrefs.GetBool("Rim3D.EditorInstantConstruction", false);
            Instance.ShowBuildingNames = UnityEditor.EditorPrefs.GetBool("Rim3D.EditorBuildingNames", false);
#endif
        }

        void Update()
        {
            if (ActiveWindow != null && (Find.WindowStack == null || !ActiveWindow.IsOpen))
            {
                ActiveWindow.DisposeView();
                ActiveWindow = null;
            }
            
            if (ActiveWindow?.IsOpen == true && Current.ProgramState == ProgramState.Playing &&
                !LongEventHandler.ShouldWaitForEvent && ActiveWindow.View?.ControlledPawn is Pawn pawn &&
                API.RimVisionControl.MapFor(pawn) is Map pawnMap && pawnMap != ActiveWindow.Map)
            {
                if (ActiveWindow.TransferToMap(pawnMap)) Current.Game.CurrentMap = pawnMap;
            }

            if (ActiveWindow?.IsOpen == true && Rim3DMultiplayer.PawnLocked &&
                Current.ProgramState == ProgramState.Playing && Current.Game.Maps.Contains(ActiveWindow.Map) &&
                Find.CurrentMap != ActiveWindow.Map) Current.Game.CurrentMap = ActiveWindow.Map;

            if (ActiveWindow == null && Rim3DMultiplayer.PawnLocked && Current.ProgramState == ProgramState.Playing) Toggle();

            if (ActiveWindow?.IsOpen == true && ActiveWindow.View?.FirstPerson == true &&
                ActiveWindow.View.ControlledPawn?.Map == ActiveWindow.Map && Find.CurrentMap != ActiveWindow.Map &&
                ActiveWindow.View.HasSpatialMap(Find.CurrentMap)) Current.Game.CurrentMap = ActiveWindow.Map;

            if (ActiveWindow?.IsOpen == true && Find.CurrentMap != ActiveWindow.Map &&
                ActiveWindow.View?.HasSpatialMap(Find.CurrentMap) == true && !ActiveWindow.View.FirstPerson)
                ActiveWindow.TransferToMap(Find.CurrentMap);

            if (ActiveWindow != null && (Current.ProgramState != ProgramState.Playing ||
                Find.CurrentMap != ActiveWindow.Map && ActiveWindow.View?.HasSpatialMap(Find.CurrentMap) != true &&
                (ActiveWindow.View?.ControlledPawn?.Spawned != false || ActiveWindow.View.ControlledPawn.Dead)))
                ActiveWindow.Close(false);

            if (ActiveWindow?.View?.CloseAfterCameraReturn == true && !Rim3DMultiplayer.PawnLocked)
            {
                ActiveWindow.Close(false);
                ActiveWindow = null;
            }

            if (jumpMap != null)
            {
                var targetMap = jumpMap;
                jumpMap = null;
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap != targetMap) return;
                // Never open or reopen 3D for a vanilla camera jump from 2D.
                if (ActiveWindow?.IsOpen != true || ActiveWindow.Map != targetMap || ActiveWindow.View == null) return;
                if (ActiveWindow?.View?.FallPresentationActive == true) return;
                ActiveWindow.View.FlyTo(jumpCell);
            }
        }

        void LateUpdate()
        {
            if (ActiveWindow?.IsOpen == true) ActiveWindow.View?.RefreshMouseCapture();
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused) ActiveWindow?.View?.ReleaseMouse();
        }

        public void Toggle()
        {
            if (openingPreview) return;
            if (!ModsConfig.IsActive("papaya.rimvision")) return;
            if (ActiveWindow?.View?.FallPresentationActive == true) return;
            if (ActiveWindow != null && ActiveWindow.IsOpen)
            {
                if (Rim3DMultiplayer.PawnLocked) return;
                ActiveWindow.Close(false); ActiveWindow = null; return;
            }
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent) return;
            if (Find.WindowStack.WindowsForcePause || Find.WindowStack.WindowsPreventCameraMotion) return;
            var window = new PreviewWindow(Find.CurrentMap, Find.Selector.SingleSelectedThing as Pawn);
            var game = Current.Game;
            openingPreview = true;
            // Unity objects and the first render must be prepared on the main thread.
            // The synchronous long event displays RimWorld's loading screen first.
            LongEventHandler.QueueLongEvent(() =>
            {
                if (Current.Game != game || Current.ProgramState != ProgramState.Playing || Find.CurrentMap != window.Map)
                { openingPreview = false; return; }
                window.PrepareView();
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try
                    {
                        if (Instance != this || Current.Game != game || Current.ProgramState != ProgramState.Playing ||
                            Find.CurrentMap != window.Map)
                        { window.DisposeView(); return; }
                        Find.WindowStack.Add(window);
                        ActiveWindow = window;
                    }
                    catch (Exception error) { FailedToOpen(error); }
                    finally { openingPreview = false; }
                });
            }, "Rim3D_LoadingView", false, FailedToOpen);

            void FailedToOpen(Exception error)
            {
                openingPreview = false;
                if (Find.WindowStack != null && window.IsOpen) window.Close(false);
                else window.DisposeView();
                Log.Error("RimVision preview: " + error);
            }
        }

        void OnDestroy()
        {
            if (ActiveWindow != null) ActiveWindow.DisposeView();
            if (Instance == this) Instance = null;
        }
    }

    public sealed class PreviewWindow : Window
    {
        public Map Map { get; private set; }
        readonly Pawn preferredPawn;
        readonly bool overview;
        readonly Vector3 overviewStart;
        readonly Game game;
        TimeSpeed previousSpeed;
        bool speedOverridden;
        bool initialPerspectivePending;
        public MapView View { get; private set; }
        bool NativeViewOpen => WorldRendererUtility.WorldSelected || Find.MainTabsRoot.OpenTab == MainButtonDefOf.Architect;
        public override Vector2 InitialSize => new Vector2(UI.screenWidth, UI.screenHeight);
        protected override float Margin => 0;
        public PreviewWindow(Map map, Pawn preferredPawn = null, bool overview = false, Vector3 overviewStart = default)
        {
            Map = map;
            game = Current.Game;
            this.preferredPawn = preferredPawn;
            this.overview = overview;
            this.overviewStart = overviewStart;
            forcePause = true;
            preventCameraMotion = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
            closeOnCancel = false;
            forceCatchAcceptAndCancelEventEvenIfUnfocused = true;
            doWindowBackground = false;
            drawShadow = false;
            soundAppear = null;
            soundClose = null;
            preventSave = false;
            layer = WindowLayer.GameUI;
        }
        public void PrepareView()
        {
            if (View != null) return;
            View = new MapView(Map);
            View.Build();
            View.WarmupSpatialMaps();
            if (overview) View.SetOverviewStart(overviewStart);
            else if (preferredPawn != null && preferredPawn.Spawned && preferredPawn.Map == Map)
                View.SetOverviewStart(preferredPawn.DrawPos);
            // Allocate the target and warm up geometry, shaders and effects under loading.
            View.Render(Mathf.RoundToInt(UI.screenWidth * Prefs.UIScale),
                Mathf.RoundToInt(Mathf.Max(1, UI.screenHeight) * Prefs.UIScale), true);
        }
        Pawn pendingSpatialPawn;

        internal bool TransferToMap(Map destination)
        {
            if (destination == null || Current.Game != game || !game.Maps.Contains(destination)) return false;
            if (destination == Map) return true;
            var previous = View;
            if (previous.HasSpatialMap(destination))
            {
                if (!previous.TryActivateSpatialMap(destination, out var cached)) return false;
                View = cached;
                Map = destination;
                initialPerspectivePending = false;
                game.CurrentMap = destination;
                return true;
            }
            var replacement = new MapView(destination);
            previous.TransferControlTo(replacement);
            View = replacement;
            Map = destination;
            initialPerspectivePending = false;
            previous.Dispose();
            replacement.Build();
            replacement.SetOverviewStart(replacement.Controller.Position);
            return true;
        }

        internal bool SelectSpatialPawn(Pawn pawn, bool smooth)
        {
            if (API.RimVisionControl.MapFor(pawn) == null || !PawnControl.CanAcquire(pawn) ||
                Rim3DMultiplayer.Active && Rim3DMultiplayer.AvailableForLocal?.Invoke(pawn) == false ||
                Rim3DMultiplayer.PawnLocked && Rim3DMultiplayer.PawnAllowed?.Invoke(pawn) == false ||
                API.RimVisionControl.MapFor(pawn) != Map && !View.HasSpatialMap(API.RimVisionControl.MapFor(pawn))) return false;
            if (!TransferToMap(API.RimVisionControl.MapFor(pawn))) { pendingSpatialPawn = pawn; return true; }
            pendingSpatialPawn = null;
            return View.EnterFirstPerson(pawn, smooth);
        }

        public override void PostOpen()
        {
            base.PostOpen();
            PrepareView();
            initialPerspectivePending = !overview;
            // Failed pawn acquisition leaves the overview camera active. Clear
            // Inspect and focus input in that mode too, or it blocks WASD.
            Find.Selector.ClearSelection();
            Find.MainTabsRoot.EscapeCurrentTab(false);
            Rim3DPreview.RequestGameInputFocus?.Invoke();
            UpdateMode();
        }
        public override void WindowUpdate()
        {
            base.WindowUpdate();
            bool nativeViewOpen = NativeViewOpen;
            preventCameraMotion = absorbInputAroundWindow = forceCatchAcceptAndCancelEventEvenIfUnfocused = !nativeViewOpen;
            windowRect = nativeViewOpen ? Rect.zero : new Rect(0, 0, UI.screenWidth, UI.screenHeight);
            if (pendingSpatialPawn != null)
            {
                if (API.RimVisionControl.MapFor(pendingSpatialPawn) == null || !PawnControl.CanAcquire(pendingSpatialPawn) ||
                    !SelectSpatialPawn(pendingSpatialPawn, false)) pendingSpatialPawn = null;
            }
            if (LongEventHandler.ShouldWaitForEvent || View?.ControlledPawn is Pawn transitioningPawn &&
                (API.RimVisionControl.MapFor(transitioningPawn) == null && !transitioningPawn.Dead || API.RimVisionControl.MapFor(transitioningPawn) is Map transitionMap && transitionMap != Map)) return;
            if (initialPerspectivePending)
            {
                initialPerspectivePending = false;
                View?.EnterFirstPerson(preferredPawn);
            }
            View?.UpdateCamera();
            UpdateMode();
            View?.UpdateMinigame();
            View?.UpdateApiExtensions();
        }
        void UpdateMode()
        {
            bool firstPerson = View != null && View.FirstPerson;
            forcePause = !firstPerson || View.StatusMenuOpen;
            if (firstPerson)
            {
                if (!speedOverridden) { previousSpeed = Find.TickManager.CurTimeSpeed; speedOverridden = true; }
                Find.TickManager.CurTimeSpeed = View.PausedByPlayer ? TimeSpeed.Paused :
                    View.AcceleratingSleep || View.AcceleratingAction ? TimeSpeed.Superfast : TimeSpeed.Normal;
            }
            else RestoreSpeed();
        }
        void RestoreSpeed()
        {
            if (speedOverridden && Current.Game == game) Find.TickManager.CurTimeSpeed = previousSpeed;
            speedOverridden = false;
        }
        public override void WindowOnGUI()
        {
            if (NativeViewOpen) return;
            base.WindowOnGUI();
        }
        public override void DoWindowContents(Rect rect)
        {
            if (View == null) return;
            windowRect = new Rect(0, 0, UI.screenWidth, UI.screenHeight);
            bool hideInterface = Rim3DSettings.Current.HideInterface;
            bool showBottomControls = !hideInterface && !View.MortarAiming && Rim3DSettings.Current.BottomControls;
            var viewport = new Rect(0, 0, rect.width, Mathf.Max(1, rect.height));
            View.BeginApiHud();
            View.Render(Mathf.RoundToInt(viewport.width * Prefs.UIScale), Mathf.RoundToInt(viewport.height * Prefs.UIScale));
            GUI.DrawTexture(viewport, View.DisplayTarget, ScaleMode.StretchToFill, false);
            if (View.MortarAiming)
            {
                View.DrawHudElement(API.RimVisionHudElement.Mortar, viewport);
                if (!hideInterface && Rim3DSettings.Current.BottomControls) View.DrawHudElement(API.RimVisionHudElement.Help, rect);
                return;
            }
            if (hideInterface) return;
            View.DrawHudElement(API.RimVisionHudElement.Scope, viewport);
            if (View.FallPresentationActive)
            {
                View.DrawHudElement(API.RimVisionHudElement.Fall, rect);
                return;
            }
            View.DrawHudElement(API.RimVisionHudElement.Activity, viewport);
#if UNITY_EDITOR
            View.DrawEditorBuildingNames(viewport);
#endif
            View.DrawHudElement(API.RimVisionHudElement.Immersion, viewport);
            View.DrawHudElement(API.RimVisionHudElement.Time, viewport);
            View.DrawHudElement(API.RimVisionHudElement.Messages, viewport);
            View.DrawHudElement(API.RimVisionHudElement.Character, viewport, Rim3DSettings.Current.CharacterHud);
            View.DrawHudElement(API.RimVisionHudElement.Needs, viewport, Rim3DSettings.Current.NeedsHud);
            View.DrawHudElement(API.RimVisionHudElement.Acceleration, viewport);
            View.DrawHudElement(API.RimVisionHudElement.Minigame, viewport);
            View.DrawHudElement(API.RimVisionHudElement.Controls, viewport);
            View.DrawHudElement(API.RimVisionHudElement.Interaction, viewport);
            View.DrawHudElement(API.RimVisionHudElement.Resources, viewport);
            if (showBottomControls)
                View.DrawHudElement(API.RimVisionHudElement.Help, rect);
            if (View.FirstPerson && !View.Controller.InputBlocked)
            {
                View.DrawHudElement(API.RimVisionHudElement.Crosshair, viewport,
                    Rim3DSettings.Current.Crosshair && View.CanCaptureMouse && View.SniperScopeOpacity <= 0);
                if (Rim3DSettings.Current.CombatFeedback) View.DrawHudElement(API.RimVisionHudElement.CombatFeedback, viewport);
            }
            View.DrawHudElement(API.RimVisionHudElement.Letters, viewport);
            View.DrawHudElement(API.RimVisionHudElement.Extensions, viewport);
        }
        public override void Close(bool doCloseSound = true)
        {
            if (Rim3DMultiplayer.PawnLocked && Current.Game == game &&
                Current.ProgramState == ProgramState.Playing && game.Maps.Contains(Map)) return;
            base.Close(doCloseSound);
        }
        public override void PostClose() { DisposeView(); base.PostClose(); }
        public override void OnCancelKeyPressed()
        {
            if (WorldRendererUtility.WorldSelected) return;
            if (View?.FallPresentationActive == true) return;
            if (View != null && View.CloseFPVMenu()) return;
            View?.OpenGameMenu();
            Event.current?.Use();
        }
        public void DisposeView() { View?.Dispose(); View = null; RestoreSpeed(); }
    }

    public sealed partial class MapView : IDisposable
    {
        const int Layer = 29;
        const int ChunkSize = 16;
        const float PawnVisualScale = 1.75f;
        readonly Map map;
        readonly GameObject root;
        readonly Camera camera;
        readonly Shader shader;
        readonly Shader groundShadowShader;
        readonly AmbientOcclusion _ambientOcclusion;
        readonly Dictionary<Material, Material> groundShadowMaterials = new Dictionary<Material, Material>();
        readonly Light sunlight;
        readonly Material skyMaterial;
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly List<Material> materials = new List<Material>();
        readonly List<RenderTexture> pawnTextures = new List<RenderTexture>();


        readonly Dictionary<Pawn, Transform> pawnCards = new Dictionary<Pawn, Transform>();
        readonly Dictionary<Building_Door, Transform> doors = new Dictionary<Building_Door, Transform>();
        PawnControl controller = new PawnControl();
        readonly Dictionary<Vector2Int, Dictionary<Material, Geometry>> batches = new Dictionary<Vector2Int, Dictionary<Material, Geometry>>();
        readonly Dictionary<Renderer, Renderer> movingShadows = new Dictionary<Renderer, Renderer>();
        readonly List<Renderer> projectedShadows = new List<Renderer>();
        bool shadowsEnabled = true;
        readonly List<Renderer> removedShadowSources = new List<Renderer>();
        readonly Dictionary<Vector2Int, GameObject> chunkRoots = new Dictionary<Vector2Int, GameObject>();
        readonly Dictionary<TerrainDef, Material> terrainMaterials = new Dictionary<TerrainDef, Material>();
        NeighborMapRendering _neighborMapRendering;

        Texture2D roofMask;
        byte[,] _mountainRoofTypes;
        Vector3[,] _mountainVertices;
        ThingDef[,] _mountainRockTypes;
        readonly Dictionary<ThingDef, Material> _mountainRockMaterials = new Dictionary<ThingDef, Material>();
        readonly Dictionary<TerrainDef, ThingDef> _mountainTerrainRocks = new Dictionary<TerrainDef, ThingDef>();
        readonly Dictionary<Material, Material> _naturalRockMaterials = new Dictionary<Material, Material>();
        readonly Dictionary<Material, Material> _mountainExteriorMaterials = new Dictionary<Material, Material>();
        Material[,] _mountainSurfaceMaterials;
        readonly List<MountainLod> _mountainLods = new List<MountainLod>();
        float _nextMountainLodUpdate;
        readonly Dictionary<Vector2Int, Bounds> _mountainOccluders = new Dictionary<Vector2Int, Bounds>();

        readonly HashSet<Vector2Int> dirtyChunks = new HashSet<Vector2Int>();


        Material[,] terrainScratch;
        Material hiddenGround;
        Transform geometryParent;
        Vector3 home;
        bool eventCamera, returningFromEvent, returnTo2D;
        Pawn returnPawn;
        Vector3 returnFocus;
        float returnYaw, returnPitch, returnDistance;
        int returnCompletedFrame = -1;
        public bool CloseAfterCameraReturn { get; private set; }
        public string InputStatus => "focus=" + Application.isFocused + ", captured=" + mouseCaptured +
            ", lock=" + Cursor.lockState + ", visible=" + Cursor.visible + ", tab=" + Find.MainTabsRoot.OpenTab?.defName +
            ", pauseWindow=" + Find.WindowStack.WindowsForcePause + ", captureFrame=" + mouseCaptureFrame + ", frame=" + Time.frameCount +
            ", yaw=" + yaw + ", pitch=" + pitch;
        public RenderTexture Target { get; private set; }
        internal RenderTexture DisplayTarget => _ambientOcclusion?.Output ?? Target;
        public int CubeCount { get; private set; }
        public int TerrainCount { get; private set; }
        public int PlantCount { get; private set; }
        public int PawnCount => pawnCards.Count;
        public int MeshCount => meshes.Count;
        public Vector3 Focus => focus;
        public bool FirstPerson => falling || controller.Observing;
        public Pawn ControlledPawn => controller.Pawn;
        public PawnControl Controller => controller;
        public string Status => controller.Status;
        public Vector3 CameraMapPosition => camera.transform.localPosition;

        public MapView(Map map) : this(map, false) { }

        internal MapView(Map map, bool visualLayer)
        {
            _worldOverlays = new WorldOverlays(this);
            _heldThingPresentation = new HeldThingPresentation(this);
            _objectPresentation = new ObjectPresentation(this);
            _worldGeometry = new WorldGeometry(this);
            _synchronizedActions = new SynchronizedActions(this);
            _zoneActions = new ZoneActions(this);
            _combatActions = new CombatActions(this);
            _ingestActions = new IngestActions(this);
            _arrestActions = new ArrestActions(this);
            _medicalActions = new MedicalActions(this);
            _workActions = new WorkActions(this);
            _carryActions = new CarryActions(this);
            _constructionActions = new ConstructionActions(this);
            _viewTargeting = new ViewTargeting(this);
            _actionMenus = new ActionMenus(this);
            _hudPresentation = new HudPresentation(this);
            this.map = map;
            _visualLayer = visualLayer;
            controller.ItemNotice = SayFloatingItem;
            // With editor domain/scene reload disabled, an older hidden preview can
            // survive Play/recompilation at the same coordinates as the new map.
            // Hide it immediately, before the new camera can see both generations.
            if (!_visualLayer)
            foreach (var stale in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (stale.parent != null || stale.name != "RimVision map preview" || stale.gameObject.layer != Layer) continue;
                stale.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(stale.gameObject);
            }
            shader = ModAssets.LoadShader("Rim3D/PreviewSurface");
            groundShadowShader = ModAssets.LoadShader("Rim3D/GroundShadow");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("RimVision surface shader is unavailable.");
            root = new GameObject(_visualLayer ? "RimVision attached map layer" : "RimVision map preview") { hideFlags = HideFlags.DontSave };
            root.layer = Layer;
            // Keep the clone outside all original cameras, including pawn portrait cameras.
            root.transform.position = new Vector3(0, -10000, 0);
            var cameraObject = new GameObject("RimVision preview camera") { hideFlags = HideFlags.DontSave };
            cameraObject.transform.SetParent(root.transform, false);
            camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 1 << Layer;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(.39f, .53f, .65f);
            camera.fieldOfView = 55;
            camera.nearClipPlane = .15f;
            camera.farClipPlane = Mathf.Max(map.Size.x, map.Size.z) * 2 + 100;
            camera.allowHDR = false;
            camera.allowMSAA = true;
            camera.renderingPath = RenderingPath.Forward;
            camera.depthTextureMode = DepthTextureMode.None;
            if (!_visualLayer) _ambientOcclusion = new AmbientOcclusion(camera);
            var skyShader = ModAssets.LoadShader("Rim3D/PreviewSky");
            if (skyShader != null && skyShader.isSupported)
            {
                skyMaterial = new Material(skyShader) { hideFlags = HideFlags.DontSave };
                materials.Add(skyMaterial);
                cameraObject.AddComponent<Skybox>().material = skyMaterial;
            }
            var sunObject = new GameObject("RimVision afternoon sun") { layer = Layer, hideFlags = HideFlags.DontSave };
            sunObject.transform.SetParent(root.transform, false);
            sunObject.transform.localRotation = Quaternion.Euler(38, -38, 0);
            sunlight = sunObject.AddComponent<Light>();
            sunlight.type = LightType.Directional;
            sunlight.color = new Color(1f, .88f, .70f);
            sunlight.cullingMask = 1 << Layer;
            sunlight.shadows = LightShadows.None;
            sunlight.enabled = false;
            if (!_visualLayer)
            {
                _weatherParticles = new WeatherParticlesRenderer(root.transform, Layer, meshes, materials);
                _lightningRenderer = new LightningRenderer(root.transform, Layer, materials, camera);
            }
            _fireRenderer = new FireRenderer(this);
            _combatEffects = new CombatEffectsRenderer(this);
            _firstPersonWeaponRenderer = new FirstPersonWeaponRenderer(this);
            _outlines = new OutlineRenderer(camera, root.transform, shader);
            if (!_visualLayer) _neighborMapRendering = new NeighborMapRendering(map, root.transform, shader, Layer);
            if (_visualLayer) root.SetActive(false);
        }

        System.Collections.Generic.IEnumerator<object> _buildSteps;
        bool _built;
        internal bool IsBuilt => _built;

        public void Build()
        {
            while (!_built && AdvanceBuild()) { }
        }

        internal bool AdvanceBuild()
        {
            if (_built || _apiDisposed) return false;
            if (_buildSteps == null) _buildSteps = BuildSteps();
            if (_buildSteps.MoveNext()) return true;
            _buildSteps.Dispose();
            _buildSteps = null;
            return false;
        }

        System.Collections.Generic.IEnumerator<object> BuildSteps()
        {
            _spatialProvider = API.RimVisionSpatial.Resolve(map);
            _lastAutoGenerateBuildings = Rim3DSettings.Current.AutoGenerateBuildings;
            home = new Vector3(map.Size.x * .5f, 0, map.Size.z * .5f);
            var colonists = map.mapPawns.FreeColonistsSpawned;
            if (colonists.Count > 0)
            {
                home = Vector3.zero;
                foreach (var pawn in colonists) home += new Vector3(pawn.Position.x + .5f, 0, pawn.Position.z + .5f);
                home /= colonists.Count;
            }
            _viewCamera.SetFocus(home);
            _viewInput.ResetMousePosition();
            var hidden = NewMaterial(Texture2D.whiteTexture, new Color(.11f, .13f, .15f), true);
            hidden.SetFloat("_Ground", 1);
            hiddenGround = hidden;
            var terrainCells = terrainScratch = new Material[map.Size.x, map.Size.z];
            ModHooks.Rim3DMeshChanged += OnMapMeshChanged;
            map.GetComponent<RoofStyles>().Changed += MarkNeighbors;
            ModHooks.Rim3DThingChanged += OnThingChanged;
            ModHooks.Rim3DPawnChanged += OnPawnChanged;
            if (!_visualLayer) LightningStrikePatch.Struck += OnLightningStrike;
            for (int z = 0; z < map.Size.z; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    Material material = hidden;
                    if (!map.fogGrid.IsFogged(cell))
                    {
                        var terrain = map.terrainGrid.TerrainAt(cell);
                        if (terrain == null || terrain.dontRender) continue;
                        if (!terrainMaterials.TryGetValue(terrain, out material))
                        {
                            material = NewTerrainMaterial(terrain);
                            terrainMaterials.Add(terrain, material);
                        }
                    }
                    terrainCells[x, z] = material;
                    TerrainCount++;
                }
                yield return null;
            }
            for (int z = 0; z < map.Size.z; z += ChunkSize)
            for (int x = 0; x < map.Size.x; x += ChunkSize)
            {
                var key = new Vector2Int(x / ChunkSize, z / ChunkSize);
                BuildTerrain(terrainCells, key);
                BuildRoofs(key);
                FlushBuildBatches();
                yield return null;
            }
            var things = new List<Thing>(map.listerThings.AllThings).ToArray();
            for (int i = 0; i < things.Length; i++)
            {
                var thing = things[i];
                if (thing.Spawned && thing.Map == map)
                {
                    TrackActivityThing(thing);
                    TrackFireThing(thing);
                    CountThing(thing, ChunkKey(thing.TrueCenter()));
                    DrawStaticThing(thing);
                }
                if ((i & 63) == 63) yield return null;
            }
            var pendingChunks = new List<Vector2Int>(batches.Keys);
            foreach (var key in pendingChunks)
            {
                geometryParent = ChunkRoot(key).transform;
                foreach (var entry in batches[key])
                    CreateMesh(entry.Key, entry.Value, "Map chunk " + key.x + "," + key.y);
                batches.Remove(key);
                geometryParent = null;
                yield return null;
            }
            foreach (var key in new List<Vector2Int>(chunkRoots.Keys))
            {
                geometryParent = chunkRoots[key].transform;
                try { _objectPresentation.FinishModelChunk(key); }
                finally { geometryParent = null; }
                _worldGeometry.RebuildWallDamage(key);
                yield return null;
            }
            foreach (var pawn in map.mapPawns.AllPawnsSpawned) OnPawnChanged(pawn);
            foreach (var thing in map.listerThings.AllThings)
                if (thing is Corpse corpse && corpse.InnerPawn != null) OnPawnChanged(corpse.InnerPawn);
            UpdatePawnAppearances();
            _built = true;
            if (!_visualLayer) PositionCamera();
            OpenApiExtensions();
        }

        void FlushBuildBatches()
        {
            foreach (var chunk in batches)
            {
                geometryParent = ChunkRoot(chunk.Key).transform;
                foreach (var entry in chunk.Value)
                    CreateMesh(entry.Key, entry.Value, "Map chunk " + chunk.Key.x + "," + chunk.Key.y);
            }
            batches.Clear();
            geometryParent = null;
        }

        static Vector2Int ChunkKey(Vector3 position) => new Vector2Int(
            Mathf.FloorToInt(position.x / ChunkSize), Mathf.FloorToInt(position.z / ChunkSize));
        GameObject ChunkRoot(Vector2Int key) => _worldGeometry.ChunkRoot(key);

        void RemoveMovingShadows(Transform owner) => _worldGeometry.RemoveMovingShadows(owner);

        void UpdateProjectedShadowBounds(Renderer source, Renderer shadow) => _worldGeometry.UpdateProjectedShadowBounds(source, shadow);

        static class ViewShaderIds
        {
            public static readonly int Ambient = Shader.PropertyToID("_Rim3DAmbientColor");
            public static readonly int SunColor = Shader.PropertyToID("_Rim3DSunColor");
            public static readonly int SunDirection = Shader.PropertyToID("_Rim3DSunDirection");
            public static readonly int RoofMask = Shader.PropertyToID("_Rim3DRoofMask");
            public static readonly int GameLightMap = Shader.PropertyToID("_Rim3DGameLightMap");
            public static readonly int ModelLightPositions = Shader.PropertyToID("_Rim3DModelLightPositions");
            public static readonly int GroundY = Shader.PropertyToID("_Rim3DMapGroundY");
            public static readonly int MapSize = Shader.PropertyToID("_Rim3DRoofMapSize");
            public static readonly int RoofHeight = Shader.PropertyToID("_Rim3DRoofHeight");
            public static readonly int FogColor = Shader.PropertyToID("_Rim3DFogColor");
            public static readonly int ViewRight = Shader.PropertyToID("_Rim3DViewRight");
            public static readonly int CameraPositionCorrection = Shader.PropertyToID("_Rim3DCameraPositionCorrection");
            public static readonly int ShadowOpacity = Shader.PropertyToID("_Rim3DShadowOpacity");
        }

        void SetViewShaderGlobals(float daylight, Color ambient, Color horizon, Vector3 viewRight)
        {
            // Private shader names leave the original game's materials untouched.
            // Shared view data is submitted once, regardless of material count.
            Shader.SetGlobalColor(ViewShaderIds.Ambient, ambient);
            Shader.SetGlobalColor(ViewShaderIds.SunColor, sunlight.color * sunlight.intensity);
            Shader.SetGlobalVector(ViewShaderIds.SunDirection, -sunlight.transform.forward);
            Shader.SetGlobalTexture(ViewShaderIds.RoofMask, roofMask);
            _gameLighting.SetShaderGlobals(ViewShaderIds.GameLightMap, ViewShaderIds.ModelLightPositions);
            _shorelineMask.SetShaderGlobals();
            _groundSnow.SetShaderGlobals();
            Shader.SetGlobalFloat(ViewShaderIds.GroundY, root.transform.position.y);
            Shader.SetGlobalVector(ViewShaderIds.MapSize, new Vector4(map.Size.x, map.Size.z, 0, 0));
            Shader.SetGlobalFloat(ViewShaderIds.RoofHeight, root.transform.position.y + SpatialRoofHeight);
            Shader.SetGlobalColor(ViewShaderIds.FogColor, horizon);
            Shader.SetGlobalVector(ViewShaderIds.ViewRight, viewRight);
            Shader.SetGlobalVector(ViewShaderIds.CameraPositionCorrection,
                CameraMapPosition - (camera.transform.position - root.transform.position));
            Shader.SetGlobalFloat(ViewShaderIds.ShadowOpacity, Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.GroundShadows) ? biomeShadowStrength * daylight * weatherDirectSun : 0);
        }
        void MarkCell(IntVec3 cell)
        {
            if (!cell.InBounds(map)) return;
            var key = new Vector2Int(cell.x / ChunkSize, cell.z / ChunkSize);
            dirtyChunks.Add(key);
            if (_mountainOccluders.Remove(key)) _nextMountainLodUpdate = 0;
        }
        void MarkNeighbors(IntVec3 cell)
        {
            MarkCell(cell);
            MarkCell(cell + IntVec3.North); MarkCell(cell + IntVec3.South);
            MarkCell(cell + IntVec3.East); MarkCell(cell + IntVec3.West);
            MarkCell(cell + new IntVec3(-1, 0, -1)); MarkCell(cell + new IntVec3(1, 0, -1));
            MarkCell(cell + new IntVec3(-1, 0, 1)); MarkCell(cell + new IntVec3(1, 0, 1));
        }
        void OnMapMeshChanged(Map changedMap, IntVec3 cell, ulong flags)
        {
            if (changedMap != map)
            {
                if (_spatialProvider?.SurfaceDependsOn(map, changedMap) == true &&
                    (flags & ((ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Roofs |
                        (ulong)MapMeshFlagDefOf.FogOfWar | (ulong)MapMeshFlagDefOf.Buildings)) != 0)
                {
                    _mountainVertices = null;
                    MarkNeighbors(cell);
                }
                return;
            }
            if ((flags & ((ulong)MapMeshFlagDefOf.Snow | (ulong)MapMeshFlagDefOf.Terrain |
                (ulong)MapMeshFlagDefOf.FogOfWar)) != 0 && cell.InBounds(map))
                _groundSnow.MarkDirty(new Vector2Int(cell.x / ChunkSize, cell.z / ChunkSize));
            if ((flags & ((ulong)MapMeshFlagDefOf.GroundGlow | (ulong)MapMeshFlagDefOf.FogOfWar)) != 0 && cell.InBounds(map))
                _gameLighting.MarkDirty(cell);
            ulong relevant = (ulong)MapMeshFlagDefOf.Things | (ulong)MapMeshFlagDefOf.Buildings |
                (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.FogOfWar;
            if ((flags & relevant) != 0) MarkNeighbors(cell);
            if ((flags & (ulong)MapMeshFlagDefOf.BuildingsDamage) != 0) _worldGeometry.MarkWallDamage(cell);
            if ((flags & (ulong)MapMeshFlagDefOf.Roofs) != 0 && cell.InBounds(map) && roofMask != null)
            {
                // GlowGrid also marks the roof layer dirty. A lamp switch must not
                // rebuild thousands of unchanged ceiling meshes.
                var roof = map.roofGrid.RoofAt(cell);
                bool hasRoof = RoofVisible(cell, roof);
                if (hasRoof != (roofMask.GetPixel(cell.x, cell.z).r > .5f)) MarkNeighbors(cell);
                byte mountainType = DrawSpatialMountain(cell) ? MountainRoofType(roof) : (byte)0;
                if (_mountainRoofTypes != null && _mountainRoofTypes[cell.x, cell.z] != mountainType)
                {
                    _mountainRoofTypes[cell.x, cell.z] = mountainType;
                    _mountainVertices = null;
                    _mountainOccluders.Clear();
                    _nextMountainLodUpdate = 0;
                    foreach (var key in chunkRoots.Keys) dirtyChunks.Add(key);
                }
            }
            if ((flags & ((ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.FogOfWar)) != 0)
            {
                // Diagonal shore underlays also depend on this cell, including across chunks.
                MarkCell(cell + new IntVec3(-1, 0, -1)); MarkCell(cell + new IntVec3(1, 0, -1));
                MarkCell(cell + new IntVec3(-1, 0, 1)); MarkCell(cell + new IntVec3(1, 0, 1));
            }
        }
        void OnThingChanged(Map changedMap, Thing thing)
        {
            if (changedMap == map && (thing.Destroyed || !thing.Spawned)) _gameLighting.RemoveModelLight(thing.thingIDNumber);
            if (changedMap == map)
            {
#if UNITY_EDITOR
                if (thing.def.category == ThingCategory.Building) editorBuildingNamesDirty = true;
#endif
                TrackActivityThing(thing);
                TrackFireThing(thing);
                if (thing is Pawn pawn) OnPawnChanged(pawn);
                if (thing is Corpse corpse && corpse.InnerPawn != null) OnPawnChanged(corpse.InnerPawn);
            }
            if (changedMap != map || thing is Pawn || thing.def.graphicData == null) return;
            var category = thing.def.category;
            if (category != ThingCategory.Building && category != ThingCategory.Plant && category != ThingCategory.Item && category != ThingCategory.Filth &&
                !(thing is Blueprint) && !(thing is Frame)) return;
            dirtyChunks.Add(ChunkKey(thing.TrueCenter()));
            foreach (var cell in thing.OccupiedRect()) MarkNeighbors(cell);
        }
        void CountThing(Thing thing, Vector2Int key) => _worldGeometry.CountThing(thing, key);


        void UpdateWorldGeometry()
        {
            _objectPresentation.UpdateModelDetail();
            _worldGeometry.UpdateWorldGeometry();
        }
        void RebuildChunk(Vector2Int key) => _worldGeometry.RebuildChunk(key);
        internal static bool IsGroundSpot(ThingDef def)
        {
            // Ground markers use the game's floor layer and have no physical body.
            return def != null && def.defName != "Grave" && def.category == ThingCategory.Building &&
                (def.defName == "Ideogram" ||
                 def.altitudeLayer == AltitudeLayer.FloorEmplacement || def.altitudeLayer == AltitudeLayer.Floor);
        }

        internal static bool IsConduit(BuildableDef def)
        {
            return def is ThingDef thingDef && thingDef.building != null && thingDef.building.isPowerConduit;
        }

        int ConduitLinks(IntVec3 position, Graphic_Linked graphic = null, Thing thing = null) => _worldGeometry.ConduitLinks(position, graphic, thing);

        void DrawStaticThing(Thing thing) => _worldGeometry.DrawStaticThing(thing);

        void DrawFallbackBuilding(Graphic graphic, Material source, Vector3 center, Rot4 rotation) => _worldGeometry.DrawFallbackBuilding(graphic, source, center, rotation);

        readonly Dictionary<Mesh, Geometry> graphicSurfaceMeshes = new Dictionary<Mesh, Geometry>();
        void AppendGroundSpotSurface(Geometry target, Graphic graphic, ThingDef def, Rot4 rotation, Vector3 center, float height) => _worldGeometry.AppendGroundSpotSurface(target, graphic, def, rotation, center, height);

        void AppendGraphicSurface(Geometry target, Graphic graphic, Rot4 rotation, Vector3 center, float height) => _worldGeometry.AppendGraphicSurface(target, graphic, rotation, center, height);

        Material NewMaterial(Texture texture, Color color, bool opaque) => _worldGeometry.NewMaterial(texture, color, opaque);
        readonly Dictionary<Material, float> bottomPadding = new Dictionary<Material, float>();
        readonly Dictionary<Material, float> visibleSpriteHeights = new Dictionary<Material, float>();
        readonly Dictionary<Material, byte[]> spriteMasks = new Dictionary<Material, byte[]>();
        float BottomPadding(Material material) => _worldGeometry.BottomPadding(material);
        Material ConvertMaterial(Material source, bool opaque) => _worldGeometry.ConvertMaterial(source, opaque);


        Material LinkedBuildingMaterial(Material source) => _worldGeometry.LinkedBuildingMaterial(source);
        bool SolidNeighbor(IntVec3 cell) => _worldGeometry.SolidNeighbor(cell);

        Material ItemMaterial(Material source) => _worldGeometry.ItemMaterial(source);
        Material PlantMaterial(Material source) => _worldGeometry.PlantMaterial(source);
        static byte MountainRoofType(RoofDef roof)
        {
            return roof == null || !roof.isNatural ? (byte)0 : roof.isThickRoof ? (byte)2 : (byte)1;
        }
        void BuildMountainVertices() => _worldGeometry.BuildMountainVertices();
        void BuildMountainRockTypes() => _worldGeometry.BuildMountainRockTypes();
        void RegisterMountainTerrain(ThingDef rock, string suffix) => _worldGeometry.RegisterMountainTerrain(rock, suffix);
        Material MountainRockMaterial(IntVec3 cell, Material fallback) => _worldGeometry.MountainRockMaterial(cell, fallback);
        Material NaturalRockMaterial(Material source, bool resource) => _worldGeometry.NaturalRockMaterial(source, resource);
        static void MountainTriangle(Geometry geometry, Vector3 a, Vector3 b, Vector3 c)
        {
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.sqrMagnitude < 1e-10f) return;
            int start = geometry.Vertices.Count;
            var normal = cross.normalized;
            geometry.Vertices.Add(a); geometry.Vertices.Add(b); geometry.Vertices.Add(c);
            for (int i = 0; i < 3; i++)
            {
                geometry.Normals.Add(normal);
                geometry.CardCenters.Add(Vector3.zero);
                geometry.Uvs.Add(Vector2.zero);
            }
            geometry.Indices.Add(start); geometry.Indices.Add(start + 1); geometry.Indices.Add(start + 2);
        }
        static void MountainFace(Geometry geometry, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            MountainTriangle(geometry, a, b, c);
            MountainTriangle(geometry, a, c, d);
        }
        void BuildMountainRoof(IntVec3 cell, Material material) => _worldGeometry.BuildMountainRoof(cell, material);
        void BuildMountainRoofBoundary(Geometry geometry, IntVec3 cell) => _worldGeometry.BuildMountainRoofBoundary(geometry, cell);
        sealed class MountainLod
        {
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Mesh Detailed, Medium, Distant, Extreme;
            public Bounds Bounds;
            public int Level;
        }
        void ConfigureMountainLod(MeshFilter filter, MeshRenderer renderer, Material material) => _worldGeometry.ConfigureMountainLod(filter, renderer, material);
        Mesh BuildMountainLodMesh(Vector2Int key, Material material, int step, Mesh detailed) => _worldGeometry.BuildMountainLodMesh(key, material, step, detailed);
        void MountainLodEdge(Geometry geometry, Vector3 center, int x, int z, int dx, int dz, int length) => _worldGeometry.MountainLodEdge(geometry, center, x, z, dx, dz, length);
        void UpdateMountainLods() => _worldGeometry.UpdateMountainLods();
        void ReleaseMountainLods(Transform owner) => _worldGeometry.ReleaseMountainLods(owner);
        void DestroyMountainLodMesh(Mesh mesh) => _worldGeometry.DestroyMountainLodMesh(mesh);
        void BuildMountainOccluder(Vector2Int key) => _worldGeometry.BuildMountainOccluder(key);
        bool MountainHidden(Bounds bounds, Vector3 position) => _worldGeometry.MountainHidden(bounds, position);
        bool NaturalRoofAt(IntVec3 cell) => _worldGeometry.NaturalRoofAt(cell);
        bool VisibleNaturalRockAt(IntVec3 cell) => _worldGeometry.VisibleNaturalRockAt(cell);
        Vector3 NaturalRockVertex(int x, int z, int layer) => _worldGeometry.NaturalRockVertex(x, z, layer);
        void NaturalRockFace(Geometry geometry, int leftX, int leftZ, int rightX, int rightZ) => _worldGeometry.NaturalRockFace(geometry, leftX, leftZ, rightX, rightZ);
        void DrawNaturalRock(Geometry geometry, IntVec3 cell, int faces, bool roofed) => _worldGeometry.DrawNaturalRock(geometry, cell, faces, roofed);
        bool RoofVisible(IntVec3 cell, RoofDef roof) => _worldGeometry.RoofVisible(cell, roof);

        void BuildRoofs(Vector2Int? onlyChunk = null) => _worldGeometry.BuildRoofs(onlyChunk);
        void BuildTerrain(Material[,] cells, Vector2Int? onlyChunk = null, bool roofSlab = false) => _worldGeometry.BuildTerrain(cells, onlyChunk, roofSlab);
        Geometry Batch(Material material, Vector3 position) => _worldGeometry.Batch(material, position);
        GameObject CreateMesh(Material material, Geometry geometry, string name) => _worldGeometry.CreateMesh(material, geometry, name);

        public void UpdateCamera()
        {
            RefreshColonyTab();
            _viewInput.UpdateExtensions(this);
            UpdateSpatialMaps();
            UpdatePawnAppearances();
            UpdateWorldGeometry();
            if (UpdateFallTransition()) { _orders?.Close(); return; }
            if (UpdateMortarTargeting()) { _orders?.Close(); return; }
            if (eventCamera || cameraFlight || returnCompletedFrame == Time.frameCount)
            {
                _orders?.Close();
                PositionCamera();
                UpdateCombatEffects();
                UpdateDropPods();
                return;
            }
            bool typing = TextInputFocused || ExternalInputBlocked;
            if (_viewInput.TogglePlansRequested(typing)) ToggleConstructionPlans();
            if (_viewInput.TogglePerspectiveRequested(typing)) TogglePerspective();
            if (cameraFlight)
            {
                _orders?.Close();
                PositionCamera();
                UpdateCombatEffects();
                UpdateDropPods();
                return;
            }
            bool accelerationBlocked = TextInputFocused || WorldRendererUtility.WorldSelected || ColonyTabOpen || ExternalDialogInputBlocked && !_workMinigame.InputFocused;
            if (_viewInput.ToggleAccelerationRequested(this, accelerationBlocked))
                ToggleActionAcceleration();
            if (_viewInput.NextPawnRequested(this, typing)) NextPawn();
            if (_visualLayer) return;
            UpdateMouseCapture();
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            UpdateWeaponPresentation(dt);
            _viewInput.ReadMovement(typing, out float dx, out float dz);
            if (FirstPerson)
            {
                bool blocked = ApiInputBlocked(API.RimVisionInputChannels.Movement) || controller.InputBlocked || constructionMenu || actionMenu || statusMenu || ExternalInputBlocked || !Application.isFocused;
                if (!controller.InputBlocked) UpdateSleepControl();
                if (_visualLayer) return;
                controller.Sneaking = !blocked && _viewInput.SneakHeld;
                controller.Sprinting = !blocked && _viewInput.SprintHeld;
                var before = controller.Position;
                if (!Rim3DMultiplayer.Active)
                {
                    var aim = _viewCamera.HasFrame ? _viewCamera.AimRay.direction : Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
                    controller.SetNativeAxes(blocked ? 0 : NativeVerticalInput, -Mathf.Asin(Mathf.Clamp(aim.y, -1, 1)) * Mathf.Rad2Deg,
                        Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg);
                }
                controller.Move(blocked ? Vector2.zero : new Vector2(dx, dz), _viewCamera.HasFrame ? _viewCamera.Frame.MovementYaw : yaw);
                UpdateImmersion(before, dt);
            }
            else
            {
                if (controller.Pawn != null) controller.Dispose();
                var movement = ApiInputBlocked(API.RimVisionInputChannels.Movement) ? Vector3.zero : new Vector3(dx, 0, dz);
                if (movement.sqrMagnitude > 1) movement.Normalize();
                float speed = Mathf.Max(5, _viewCamera.Distance * .55f) * (_viewInput.SprintHeld ? 2 : 1);
                _viewCamera.PanOverview(movement, speed, dt, map);
            }
            _viewInput.UpdateLook(this, typing);
            if (!FirstPerson || _viewCamera.HasFrame && _viewCamera.Frame.AllowZoom)
            {
                _viewInput.UpdateOverviewZoom(this, typing);
            }
            PositionCamera();
            UpdateCompassInput();
            UpdateFPVActions();
            UpdateCombatEffects();
            UpdateDropPods();
        }
        public void RefreshMouseCapture() => UpdateMouseCapture();
        public bool CanCaptureMouse => (!_viewCamera.HasFrame || _viewCamera.Frame.CaptureMouse) && !ApiInputBlocked(API.RimVisionInputChannels.Look) && FirstPerson && !MortarAiming && !eventCamera && !cameraFlight && !controller.InputBlocked &&
            !FallPresentationActive && Application.isFocused && !constructionMenu && !actionMenu &&
            !statusMenu && !ExternalInputBlocked && !ViewBindings.Held(ViewAction.Cursor);
        public bool EnterFirstPerson(Pawn preferred = null, bool smooth = true)
        {
            if (FallPresentationActive) return false;
            if (preferred != null && API.RimVisionControl.MapFor(preferred) is Map preferredMap && preferredMap != map)
                return HasSpatialMap(preferredMap) && Rim3DPreview.Instance?.ActiveWindow?.View == this &&
                    Rim3DPreview.Instance.ActiveWindow.SelectSpatialPawn(preferred, smooth);
            Pawn pawn = preferred;
            if (Rim3DMultiplayer.PawnLocked && pawn != null &&
                Rim3DMultiplayer.PawnAllowed?.Invoke(pawn) == false) return false;
            if (!PawnControl.CanAcquire(pawn) || API.RimVisionControl.MapFor(pawn) != map)
            {
                pawn = null;
                foreach (var candidate in SpatialPawns())
                    if (PawnControl.CanAcquire(candidate) &&
                        (!Rim3DMultiplayer.Active || Rim3DMultiplayer.AvailableForLocal?.Invoke(candidate) != false) &&
                        (!Rim3DMultiplayer.PawnLocked || Rim3DMultiplayer.PawnAllowed?.Invoke(candidate) != false))
                    { pawn = candidate; break; }
            }
            bool selectingSleeper = PawnControl.IsSleeping(pawn);
            if (!controller.Acquire(pawn)) return false;
            _viewCamera.StopFlight();
            if (smooth) BeginPerspectiveFlight(1.6f);
            suppressSleepHandoff = selectingSleeper;
            ResetImmersion(pawn);
            constructionMenu = actionMenu = statusMenu = false;
            // RimWorld automatically reopens Inspect while a thing is selected.
            // The FPV HUD owns the controlled pawn's presentation instead.
            Find.Selector.ClearSelection();
            Find.MainTabsRoot.EscapeCurrentTab(false);
            Rim3DPreview.RequestGameInputFocus?.Invoke();
            _viewCamera.SetLook(pawn.Rotation.AsAngle, 5);
            if (controller.IsResting && pawn.CurrentBed() is Building_Bed sleepingBed) _viewCamera.SetYaw(sleepingBed.Rotation.AsAngle + 180);
            controlledPawnWasSleeping = PawnControl.IsSleeping(pawn);
            _viewCamera.InitializeSleepCamera(controller);
            sleepHandoffAt = -1;
            _viewCamera.ResetSleepEyelids();
            _viewInput.ResetMousePosition();
            UpdateMouseCapture();
            PositionCamera();
            return true;
        }
        internal void TransferControlTo(MapView destination)
        {
            destination.controller = controller;
            controller = new PawnControl();
            destination.controller.ItemNotice = destination.SayFloatingItem;
            if (!Rim3DMultiplayer.Active && destination.ControlledPawn != null) destination.controller.NotifyMapTransfer();
            destination._viewCamera.SetLook(yaw, pitch);
            if (destination.ControlledPawn != null) destination.ResetImmersion(destination.ControlledPawn);
        }

        public void SetOverviewStart(Vector3 position)
        {
            _viewCamera.SetFocus(new Vector3(Mathf.Clamp(position.x, 0, map.Size.x), 0,
                Mathf.Clamp(position.z, 0, map.Size.z)));
            PositionCamera();
        }

        void BeginPerspectiveFlight(float duration)
        {
            // Capture the rendered pose before changing the destination mode.
            _viewCamera.CaptureFlight(camera);
            _viewCamera.BeginFlight(Time.unscaledTime, duration);
            ReleaseMouse();
        }

        public bool HandleCameraReturnInput(Event current)
        {
            if (!eventCamera || current.type != EventType.KeyDown ||
                TextInputFocused || ExternalDialogInputBlocked) return false;
            if (!returningFromEvent) ReturnFromEvent();
            current.Use();
            return true;
        }

        void ReturnFromEvent()
        {
            // Capture the visible camera before reacquiring the pawn, which positions it immediately.
            var startPosition = CameraMapPosition;
            var startRotation = camera.transform.rotation;
            float startFov = camera.fieldOfView;
            _viewCamera.StopFlight();
            _viewCamera.RestoreView(returnFocus, returnYaw, returnPitch, returnDistance);
            if (returnPawn != null && returnPawn.Map == map && PawnControl.CanAcquire(returnPawn) &&
                EnterFirstPerson(returnPawn, false))
            {
                _viewCamera.SetLook(returnYaw, returnPitch);
            }
            else if (returnPawn != null)
            {
                _viewCamera.SetPitchAndDistance(48, 24);
            }
            ReleaseMouse();
            _viewCamera.SetFlightOrigin(startPosition, startRotation, startFov);
            _viewCamera.BeginFlight(Time.unscaledTime,
                Mathf.Clamp(.65f + Vector3.Distance(startPosition, returnFocus) * .008f, .65f, 1.6f));
            returningFromEvent = true;
            PositionCamera();
        }

        public void FlyTo(IntVec3 cell, bool closeOnReturn = false)
        {
            if (!cell.InBounds(map) || FallPresentationActive) return;
            // Additional event clicks keep the original return destination.
            if (!eventCamera)
            {
                returnPawn = FirstPerson ? ControlledPawn : null;
                returnFocus = focus;
                returnYaw = yaw;
                returnPitch = pitch;
                returnDistance = _viewCamera.Distance;
                returnTo2D = closeOnReturn;
            }
            eventCamera = true;
            returningFromEvent = false;
            CloseAfterCameraReturn = false;
            _viewCamera.CaptureFlight(camera);
            if (FirstPerson)
            {
                _viewCamera.SetGroundFocus(controller.Position);
                controller.Dispose();
            }
            CloseColonyTab();
            constructionMenu = actionMenu = statusMenu = false;
            ReleaseMouse();
            var startFocus = focus;
            _viewCamera.SetGroundFocus(cell.ToVector3Shifted());
            _viewCamera.SetPitchAndDistance(48, 24);
            _viewCamera.BeginFlight(Time.unscaledTime,
                Mathf.Clamp(.65f + Vector3.Distance(startFocus, focus) * .008f, .65f, 1.6f));
            PositionCamera();
        }

        public void TogglePerspective()
        {
            if (FallPresentationActive) return;
            if (Rim3DMultiplayer.PawnLocked && !eventCamera)
            {
                if (!FirstPerson) EnterFirstPerson();
                return;
            }
            if (eventCamera)
            {
                if (!returningFromEvent) ReturnFromEvent();
                return;
            }
            _viewCamera.StopFlight();
            if (FirstPerson)
            {
                BeginPerspectiveFlight(1.4f);
                var pos = controller.Position; pos.y = 0;
                _viewCamera.SetFocus(pos); controller.Dispose(); _viewCamera.SetPitchAndDistance(48, 24);
                ReleaseMouse();
            }
            else EnterFirstPerson();
            PositionCamera();
        }
        public void NextPawn()
        {
            if (FallPresentationActive || Rim3DMultiplayer.PawnLocked) return;
            var candidates = SpatialPawns();
            int current = candidates.IndexOf(ControlledPawn);
            for (int i = 1; i <= candidates.Count; i++)
            {
                var pawn = candidates[(current + i) % candidates.Count];
                if (PawnControl.CanAcquire(pawn) && EnterFirstPerson(pawn)) return;
            }
        }
        bool controlledPawnWasSleeping;
        bool suppressSleepHandoff;
        public bool AllPawnsSleeping { get; private set; }
        public bool AcceleratingSleep => AllPawnsSleeping ||
            (Rim3DSettings.Current.BlockAutomaticPawnSwitch || Rim3DMultiplayer.PawnLocked) && PawnControl.IsSleeping(ControlledPawn);
        float sleepHandoffAt = -1;

        void UpdateSleepControl()
        {
            var candidates = SpatialPawns();
            bool sleeping = PawnControl.IsSleeping(ControlledPawn);
            bool fellAsleep = sleeping && !controlledPawnWasSleeping && !suppressSleepHandoff;
            if (sleeping || !controller.IsResting) suppressSleepHandoff = false;
            controlledPawnWasSleeping = sleeping;
            if (fellAsleep) sleepHandoffAt = Time.unscaledTime + 1.8f;
            if (!sleeping || Rim3DSettings.Current.BlockAutomaticPawnSwitch || Rim3DMultiplayer.PawnLocked) sleepHandoffAt = -1;
            AllPawnsSleeping = false;
            int available = 0, asleep = 0;
            foreach (var pawn in candidates)
            {
                if (!PawnControl.Eligible(pawn)) continue;
                PawnControl.RetainSleepingPosture(pawn);
                available++;
                if (PawnControl.IsSleeping(pawn)) asleep++;
            }
            AllPawnsSleeping = available > 0 && available == asleep;
            if (sleepHandoffAt < 0 || Time.unscaledTime < sleepHandoffAt || AllPawnsSleeping) return;
            sleepHandoffAt = -1;
            int current = candidates.IndexOf(ControlledPawn);
            for (int i = 1; i < candidates.Count; i++)
            {
                var pawn = candidates[(current + i) % candidates.Count];
                if (PawnControl.Eligible(pawn) && !PawnControl.IsSleeping(pawn) && EnterFirstPerson(pawn)) return;
            }
        }

        void PositionCamera()
        {
            if (_viewCamera.CalculatePose(this, out var position, out var rotation) && returningFromEvent)
            {
                eventCamera = returningFromEvent = false;
                returnCompletedFrame = Time.frameCount;
                if (returnTo2D && !Rim3DMultiplayer.PawnLocked)
                {
                    Find.CameraDriver.JumpToCurrentMapLoc(returnFocus.ToIntVec3());
                    CloseAfterCameraReturn = true;
                }
                returnPawn = null;
            }
            camera.transform.localPosition = position;
            camera.transform.localRotation = rotation;
            UpdateAudioListener(position, rotation);
            UpdateHeldThings();
            UpdateSceneActors(position);
        }

        void UpdateSceneActors(Vector3 position)
        {
            pawnAppearanceWork.Clear();
            pawnAppearanceWork.AddRange(pawnCards.Keys);
            foreach (var pawn in pawnAppearanceWork)
            {
                var card = pawnCards[pawn];
                Thing body = pawn.Corpse != null ? (Thing)pawn.Corpse : pawn;
                bool defaultVisible = pawn != ControlledPawn || _viewCamera.HasFrame && _viewCamera.Frame.ShowBody || MortarAiming || cameraFlight && (position - controller.Position).sqrMagnitude > 4f;
                bool visible = body.Spawned && body.Map == map && !map.fogGrid.IsFogged(body.Position) && DrawSpatialThing(body) &&
                    (body is Corpse ? defaultVisible : API.RimVisionPresentation.DrawPawn(pawn, FirstPerson ? ControlledPawn : null, defaultVisible));
                card.gameObject.SetActive(visible);
                if (!visible)
                {
                    if (body.Spawned && body.Map == map && !map.fogGrid.IsFogged(body.Position)) PositionPawnCard(pawn, body, card);
                    continue;
                }
                UpdatePawnView(pawn, body);
                if (pawnCards.TryGetValue(pawn, out card)) PositionPawnCard(pawn, body, card);
            }
            pawnAppearanceWork.Clear();
            _objectPresentation.UpdateApiModels();
            foreach (var entry in doors)
            {
                entry.Value.gameObject.SetActive(entry.Key.Spawned);
                AnimateDoor(entry.Key, entry.Value);
            }
        }
        public void Render(int width, int height, bool warmup = false)
        {
            var settings = Rim3DSettings.Current;
            width = Mathf.RoundToInt(width * settings.RenderScale);
            height = Mathf.RoundToInt(height * settings.RenderScale);
            width = Mathf.Clamp(width, 64, 2560); height = Mathf.Clamp(height, 64, 1440);
            int samples = settings.AntiAliasing && SystemInfo.supportsMultisampledTextures != 0 ? 2 : 1;
            if (Target == null || Target.width != width || Target.height != height || Target.antiAliasing != samples)
            {
                if (Target != null) { Target.Release(); UnityEngine.Object.Destroy(Target); }
                Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave,
                    antiAliasing = samples };
                Target.Create(); camera.targetTexture = Target;
            }
            if (!warmup && Event.current != null && Event.current.type != EventType.Repaint) return;
            UpdateSpatialMaps();
            PrepareSceneRendering(out float daylight, out Color ambient, out Color horizon, out Vector3 viewRight);
            _apiExtensions?.BeforeRender();
            bool ambientOcclusion = !_visualLayer && _ambientOcclusion.Configure(settings.EffectsEnabled(settings.AmbientOcclusion),
                settings.AmbientOcclusionIntensity);
            camera.Render();
            if (_spatialMaps != null)
            {
                var weatherFog = Shader.GetGlobalVector(WeatherFogId);
                var plantWind = Shader.GetGlobalVector(PlantWindId);
                var indoorAmbient = Shader.GetGlobalColor(IndoorAmbientId);
                var indoorFog = Shader.GetGlobalColor(IndoorFogColorId);
                try { _spatialMaps.Render(camera, Target); }
                finally
                {
                    SetViewShaderGlobals(daylight, ambient, horizon, viewRight);
                    Shader.SetGlobalVector(WeatherFogId, weatherFog);
                    Shader.SetGlobalVector(PlantWindId, plantWind);
                    Shader.SetGlobalColor(IndoorAmbientId, indoorAmbient);
                    Shader.SetGlobalColor(IndoorFogColorId, indoorFog);
                }
            }
            if (ambientOcclusion) _ambientOcclusion.Render(Target, !warmup);
            _firstPersonWeaponRenderer.Render(DisplayTarget);
        }

        void PrepareSceneRendering(out float daylight, out Color ambient, out Color horizon, out Vector3 viewRight)
        {
            var settings = Rim3DSettings.Current;
            UpdateGameLighting();
            UpdateGroundSnow();
            bool showShadows = settings.EffectsEnabled(settings.GroundShadows);
            if (shadowsEnabled != showShadows)
            {
                shadowsEnabled = showShadows;
                for (int i = projectedShadows.Count - 1; i >= 0; i--)
                {
                    if (projectedShadows[i] == null) projectedShadows.RemoveAt(i);
                    else projectedShadows[i].enabled = showShadows;
                }
            }
            if (showShadows)
            {
                removedShadowSources.Clear();
                foreach (var entry in movingShadows)
                {
                    if (entry.Key == null || entry.Value == null) removedShadowSources.Add(entry.Key);
                    else UpdateProjectedShadowBounds(entry.Key, entry.Value);
                }
                foreach (var source in removedShadowSources) movingShadows.Remove(source);
                removedShadowSources.Clear();
            }
            // Camera.Render performs native frustum culling using these bounds.
            // Surface and shadow are separate renderers: a visible projection
            // survives even when its source is behind the camera. Avoid a second
            // managed traversal of every renderer on the map each repaint.
            UpdateBiomeLighting(out daylight, out ambient, out horizon, out var skySunDirection);
            if (_visualLayer) UpdateWeatherPresentation(ref ambient, ref horizon, daylight, false);
            else UpdateWeatherRendering(ref ambient, ref horizon, daylight);
            viewRight = camera.transform.right;
            viewRight.y = 0;
            viewRight.Normalize();
            SetViewShaderGlobals(daylight, ambient, horizon, viewRight);
            if (!_visualLayer) UpdateSkyRendering(horizon, skySunDirection);
            if (!_visualLayer) _neighborMapRendering?.Update();
            // Illumination comes from explicit shader uniforms; projected shadows
            // need neither a directional-light pass nor a realtime shadow map.
            if (!_visualLayer)
            {
                UpdateFirstPersonWeapon();
                UpdateDesignationIcons();
                UpdateZoneVisibility();
                _worldOverlays.UpdateInspectionOverlays();
                UpdatePowerHoverOverlay();
            }
            UpdateFireVisuals();
            UpdateOptionalTurrets();
            UpdateBatteryBars();
            _objectPresentation.UpdateGraveInscriptions();
            UpdateWindTurbineBlades();
            UpdateMountainLods();
            UpdateVanillaOutlines();
        }

        public void Dispose()
        {
            if (_apiDisposed) return;
            if (!simulationOnly && !_visualLayer) _viewInput.UpdateExtensions(this, true);
            CancelNativeCombatInput();
            _viewCamera.CloseApi(API.RimVisionCameraExitReason.ViewClosed);
            _apiDisposed = true;
            _objectPresentation.DisposeApiModels();
            _buildSteps?.Dispose();
            _buildSteps = null;
            geometryParent = null;
            _apiRequests.Clear();
            CloseApiRequests();
            ClearOverviewPreview();
            _overviewPlacements.Clear();
            _apiExtensions?.Dispose();
            _spatialMaps?.Dispose();
            if (_layerStencilClear != null)
            {
                camera.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeForwardOpaque, _layerStencilClear);
                _layerStencilClear.Release();
                _layerStencilClear = null;
            }
            _neighborMapRendering?.Dispose();
            _ambientOcclusion?.Dispose();
            _workMinigame.Dispose();
            DisposeAudioListener();
            ReleaseMouse();
            _mouseCapture.Dispose();
            _gameLighting.Dispose();
            if (roofMask != null) { UnityEngine.Object.Destroy(roofMask); roofMask = null; }
            _mountainVertices = null;
            _mountainRoofTypes = null;
            _mountainRockTypes = null;
            _mountainRockMaterials.Clear();
            _mountainTerrainRocks.Clear();
            _naturalRockMaterials.Clear();
            _mountainExteriorMaterials.Clear();
            _mountainSurfaceMaterials = null;
            _mountainLods.Clear();
            _mountainOccluders.Clear();
            ModHooks.Rim3DMeshChanged -= OnMapMeshChanged;
            map.GetComponent<RoofStyles>().Changed -= MarkNeighbors;
            ModHooks.Rim3DThingChanged -= OnThingChanged;
            ModHooks.Rim3DPawnChanged -= OnPawnChanged;
            movingShadows.Clear(); removedShadowSources.Clear(); projectedShadows.Clear();
            Shader.SetGlobalTexture(ViewShaderIds.RoofMask, null);
            Shader.SetGlobalTexture(ViewShaderIds.GameLightMap, null);
            dirtyPawnAppearances.Clear(); pronePawns.Clear(); pawnAppearanceWork.Clear(); pawnViews.Clear();
            _compassHeads.Clear();
            controller.Dispose();
            DisposeFPV();
            DisposeCombatEffects();
            DisposeFireVisuals();
            optionalTurretTops.Clear(); removedOptionalTurrets.Clear();
            batteryBars.Clear(); removedBatteryBars.Clear();
            batteryBarMesh = null; batteryBarFilled = null; batteryBarEmpty = null;
            windTurbineBlades.Clear(); removedWindTurbines.Clear(); windBladeMaterial = null;
            DisposeWeatherRendering();
            DisposeSkyRendering();
            DisposeGroundSnow();
            DisposeShoreline();
            _terrainEdgeMaterials.Clear();
            DisposeActivity();
#if UNITY_EDITOR
            editorBuildingNames.Clear();
#endif
            DisposeFirstPersonWeapon();
            DisposeVanillaOutlines();
            _outlines?.ClearRegistrations();
            _worldGeometry.ClearWallDamage();
            if (root != null) UnityEngine.Object.Destroy(root);
            if (Target != null) { if (RenderTexture.active == Target) RenderTexture.active = null; Target.Release(); UnityEngine.Object.Destroy(Target); Target = null; }
            foreach (var mesh in meshes) UnityEngine.Object.Destroy(mesh);
            foreach (var material in materials) UnityEngine.Object.Destroy(material);
            foreach (var copy in pawnTextures) { copy.Release(); UnityEngine.Object.Destroy(copy); }
            meshes.Clear(); materials.Clear(); pawnCards.Clear(); pawnTextures.Clear();
            batches.Clear();
            graphicSurfaceMeshes.Clear();
            _generatedBuildings.Clear();
            _generatedBuildingPixels.Clear();
            _generatedBuildingSideMaterial = null;
            optionalModels = null;
            optionalModelMaterials.Clear(); optionalTwoSidedMaterials.Clear(); optionalSolidMaterials.Clear();
            _optionalCustomMaterials.Clear();
            _objectPresentation.ClearModelDetail();
            woodFenceMaterials.Clear();
            DisposeWallTextures();
            bottomPadding.Clear();
            visibleSpriteHeights.Clear();
            spriteMasks.Clear();
            filthMaterials.Clear();
        }

        sealed partial class WorldGeometry
        {
            readonly Dictionary<Material, Material> _solidMaterials = new Dictionary<Material, Material>();

            readonly Dictionary<Material, Material> _cutoutMaterials = new Dictionary<Material, Material>();

            readonly Dictionary<RoofDef, Material> _roofMaterials = new Dictionary<RoofDef, Material>();

            readonly Dictionary<Material, Material> _roofUndersideMaterials = new Dictionary<Material, Material>();

            Vector3 _lastMountainCameraPosition;

            bool _lastMountainFirstPerson;

            int _lastMountainSimplification = -1;

            readonly Dictionary<Vector2Int, Vector2Int> _chunkCounts = new Dictionary<Vector2Int, Vector2Int>();

            readonly HashSet<Thing> _chunkThings = new HashSet<Thing>();

            internal GameObject ChunkRoot(Vector2Int key)
            {
                if (_view.chunkRoots.TryGetValue(key, out var obj)) return obj;
                obj = new GameObject("RimVision chunk " + key.x + "," + key.y) { layer = Layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(_view.root.transform, false);
                _view.chunkRoots.Add(key, obj);
                return obj;
            }

            internal void RemoveMovingShadows(Transform owner)
            {
                _view.removedShadowSources.Clear();
                foreach (var entry in _view.movingShadows)
                    if (entry.Key == null || entry.Value == null || entry.Key.transform.IsChildOf(owner) ||
                        entry.Value.transform.IsChildOf(owner)) _view.removedShadowSources.Add(entry.Key);
                foreach (var source in _view.removedShadowSources) _view.movingShadows.Remove(source);
                _view.removedShadowSources.Clear();
                for (int i = _view.projectedShadows.Count - 1; i >= 0; i--)
                    if (_view.projectedShadows[i] == null || _view.projectedShadows[i].transform.IsChildOf(owner))
                        _view.projectedShadows.RemoveAt(i);
            }

            internal void UpdateProjectedShadowBounds(Renderer source, Renderer shadow)
            {
                if (source == null || shadow == null || !source.gameObject.activeInHierarchy) return;
                var bounds = source.bounds;
                float groundY = _view.root.transform.position.y + .012f;
                var rays = _view.sunlight.transform.forward;
                var slope = new Vector2(rays.x, rays.z) / Mathf.Max(.1f, -rays.y);
                float low = Mathf.Max(0, bounds.min.y - groundY);
                float high = Mathf.Max(0, bounds.max.y - groundY);
                var min = new Vector3(bounds.min.x + Mathf.Min(slope.x * low, slope.x * high), groundY,
                    bounds.min.z + Mathf.Min(slope.y * low, slope.y * high));
                var max = new Vector3(bounds.max.x + Mathf.Max(slope.x * low, slope.x * high), groundY + .002f,
                    bounds.max.z + Mathf.Max(slope.y * low, slope.y * high));
                var world = new Bounds((min + max) * .5f, max - min);
                var inverse = shadow.transform.worldToLocalMatrix;
                var extent = world.extents;
                // Transform an AABB conservatively, including rotated/scaled held cards.
                var localExtent = new Vector3(
                    Mathf.Abs(inverse.m00) * extent.x + Mathf.Abs(inverse.m01) * extent.y + Mathf.Abs(inverse.m02) * extent.z,
                    Mathf.Abs(inverse.m10) * extent.x + Mathf.Abs(inverse.m11) * extent.y + Mathf.Abs(inverse.m12) * extent.z,
                    Mathf.Abs(inverse.m20) * extent.x + Mathf.Abs(inverse.m21) * extent.y + Mathf.Abs(inverse.m22) * extent.z);
                shadow.localBounds = new Bounds(inverse.MultiplyPoint3x4(world.center), localExtent * 2);
            }

            internal void CountThing(Thing thing, Vector2Int key)
            {
                if (!thing.Spawned || thing is Pawn || thing.def.graphicData == null || _view.map.fogGrid.IsFogged(thing.Position)) return;
                _chunkCounts.TryGetValue(key, out var count);
                if (thing.def.category == ThingCategory.Building) { count.x++; _view.CubeCount++; }
                if (thing.def.category == ThingCategory.Plant) { count.y++; _view.PlantCount++; }
                _chunkCounts[key] = count;
            }

            float _lastTreeScale = 2f;

            static int _geometryBudgetFrame = -1;
            static long _geometryBudgetTicks;

            internal void UpdateWorldGeometry()
            {
                _view.UpdateGeneratedBuildingSettings();
                UpdateWaterSettings();
                float treeScale = Rim3DSettings.Current.TreeScale;
                if (_lastTreeScale != treeScale)
                {
                    _lastTreeScale = treeScale;
                    foreach (var chunk in _view.chunkRoots.Keys) _view.dirtyChunks.Add(chunk);
                }
                // No polling: notifications coalesce into one rebuild per affected chunk.
                if (_view.dirtyChunks.Count == 0) { UpdateWallDamage(); return; }
                if (_geometryBudgetFrame != Time.frameCount)
                {
                    _geometryBudgetFrame = Time.frameCount;
                    _geometryBudgetTicks = 0;
                }
                if (_geometryBudgetTicks >= System.Diagnostics.Stopwatch.Frequency / 500) return;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                Vector2Int key;
                using (var iterator = _view.dirtyChunks.GetEnumerator()) { iterator.MoveNext(); key = iterator.Current; }
                _view.dirtyChunks.Remove(key);
                _view.RebuildChunk(key);
                UpdateWallDamage();
                _geometryBudgetTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            }

            internal void RebuildChunk(Vector2Int key)
            {
                ForgetWallDamage(key);
                _view._objectPresentation.InvalidateModelChunk(key);
                if (_view.chunkRoots.TryGetValue(key, out var oldRoot))
                {
                    _view.ReleaseMountainLods(oldRoot.transform);
                    _view.CollectChunkMeshes(oldRoot); var removedDoors = new List<Building_Door>();
                    foreach (var entry in _view.doors) if (entry.Value.IsChildOf(oldRoot.transform)) removedDoors.Add(entry.Key);
                    foreach (var door in removedDoors) _view.doors.Remove(door);
                    _view.RemoveMovingShadows(oldRoot.transform);
                    var oldMeshes = new HashSet<Mesh>();
                    foreach (var filter in oldRoot.GetComponentsInChildren<MeshFilter>(true)) oldMeshes.Add(filter.sharedMesh);
                    oldRoot.SetActive(false);
                    UnityEngine.Object.Destroy(oldRoot);
                    _view.chunkRoots.Remove(key);
                    foreach (var mesh in oldMeshes) { _view.meshes.Remove(mesh); UnityEngine.Object.Destroy(mesh); }
                }
                _view.geometryParent = _view.ChunkRoot(key).transform; _chunkReuseDestination = _view.geometryParent;
                try
                {
                    int startX = key.x * ChunkSize, startZ = key.y * ChunkSize;
                    for (int z = startZ; z < Mathf.Min(startZ + ChunkSize, _view.map.Size.z); z++)
                    for (int x = startX; x < Mathf.Min(startX + ChunkSize, _view.map.Size.x); x++)
                    {
                        var cell = new IntVec3(x,0,z);
                        Material material = _view.hiddenGround;
                        if (!_view.map.fogGrid.IsFogged(cell))
                        {
                            var terrain = _view.map.terrainGrid.TerrainAt(cell);
                            if (terrain == null || terrain.dontRender) { _view.terrainScratch[x,z] = null; continue; }
                            if (!_view.terrainMaterials.TryGetValue(terrain, out material))
                            {
                                material = _view.NewTerrainMaterial(terrain);
                                _view.terrainMaterials.Add(terrain, material);
                            }
                        }
                        _view.terrainScratch[x,z] = material;
                    }
                    _view.BuildTerrain(_view.terrainScratch, key);
                    _view.BuildRoofs(key);
                    if (_chunkCounts.TryGetValue(key, out var previousCount))
                    {
                        _view.CubeCount -= previousCount.x; _view.PlantCount -= previousCount.y;
                        _chunkCounts.Remove(key);
                    }
                    _chunkThings.Clear();
                    // Read only the affected cells, never the whole map's object list.
                    for (int z = startZ; z < Mathf.Min(startZ + ChunkSize, _view.map.Size.z); z++)
                    for (int x = startX; x < Mathf.Min(startX + ChunkSize, _view.map.Size.x); x++)
                    foreach (var thing in _view.map.thingGrid.ThingsListAt(new IntVec3(x, 0, z)))
                    {
                        if (thing is Pawn || !_chunkThings.Add(thing) || ChunkKey(thing.TrueCenter()) != key) continue;
                        _view.CountThing(thing, key);
                        _view.DrawStaticThing(thing);
                    }
                    _chunkThings.Clear();
                    _view.geometryParent = _view.ChunkRoot(key).transform; _chunkReuseDestination = _view.geometryParent;
                    if (_view.batches.TryGetValue(key, out var chunk))
                    {
                        foreach (var entry in chunk) _view.CreateMesh(entry.Key, entry.Value, "Map chunk " + key.x + "," + key.y);
                        _view.batches.Remove(key);
                    }
                    _view._objectPresentation.FinishModelChunk(key);
                }
                finally { _view.geometryParent = null; _view.FinishChunkReuse(); }
                RebuildWallDamage(key);
            }

            internal int ConduitLinks(IntVec3 position, Graphic_Linked graphic = null, Thing thing = null)
            {
                int links = 0;
                for (int d = 0; d < 4; d++)
                {
                    // Keep the native atlas direction order and transmitter linking rules.
                    var cell = position + GenAdj.CardinalDirections[d];
                    if (!cell.InBounds(_view.map)) continue;
                    bool connected = graphic != null && thing != null
                        ? graphic.ShouldLinkWith(cell, thing)
                        : _view.map.powerNetGrid.TransmittedPowerNetAt(cell) != null;
                    if (!connected && thing == null)
                        foreach (var neighbor in _view.map.thingGrid.ThingsListAt(cell))
                            if ((neighbor is Blueprint || neighbor is Frame) && IsConduit(neighbor.def.entityDefToBuild))
                            { connected = true; break; }
                    if (connected) links |= 1 << d;
                }
                return links;
            }

            internal void DrawStaticThing(Thing thing)
            {
                    if (!_view.DrawSpatialThing(thing)) return;
                    if (_view._objectPresentation.ReplaceApiModel(thing)) return;
                    if (thing is Corpse) return;
                    if (_view.workIngredientCards.ContainsKey(thing)) return;
                    if (thing is Skyfaller) return;
                    if (thing is Blueprint || thing is Frame) { _view.DrawConstructionPlan(thing); return; }
                    if (!thing.Spawned || _view.map.fogGrid.IsFogged(thing.Position) || thing is Pawn || thing.def.graphicData == null) return;
                    if (thing.def.category == ThingCategory.Filth) { _view.DrawFilth(thing); return; }
                    if (thing.def.category != ThingCategory.Building && thing.def.category != ThingCategory.Plant && thing.def.category != ThingCategory.Item) return;
                    bool conduit = IsConduit(thing.def);
                    if (conduit && (thing.def.defName == "HiddenConduit" || thing.def.graphicData.color.a <= 0)) return;
                    var graphic = thing.Graphic;
                    if (graphic == null) return;
                    var source = graphic is Graphic_Linked linked
                        ? MaterialAtlasPool.SubMaterialFromAtlas(linked.SubGraphic.MatSingleFor(thing),
                            (LinkDirections)(conduit ? _view.ConduitLinks(thing.Position, linked, thing) : 15))
                        : graphic.MatAt(thing.Rotation, thing);
                    if (source == null || source.mainTexture == null || source.mainTexture == BaseContent.BadTex) return;
                    var center = thing.TrueCenter();
                    center.y = thing.def.category == ThingCategory.Item
                        ? _view.GroundThingSurfaceHeight(center) + _view._objectPresentation.GroundItemHeightOffset(thing) : 0;
                    _view.geometryParent = _view.ChunkRoot(ChunkKey(center)).transform;
                    if (conduit)
                    {
                        // One ground quad; the atlas supplies straights, corners and junctions.
                        _view.Batch(_view.ConvertMaterial(source, false), center).FloorCard(center + Vector3.up * .018f, 1, 1);
                        return;
                    }
                    if (IsGroundSpot(thing.def))
                    {
                        var groundModel = _view.ThingModel(thing, graphic);
                        if (groundModel?.GroundCutout != null &&
                            _view.TryDrawOptionalModel(thing, graphic, center, selectedModel: groundModel)) return;
                        _view.AppendGraphicSurface(_view.Batch(_view.ItemMaterial(source), center), graphic, thing.Rotation, center, .025f);
                        return;
                    }
                    if (IsWallTorch(thing))
                    {
                        if (!_view.TryDrawOptionalModel(thing, graphic, center)) _view.DrawWallTorch(thing, center);
                        return;
                    }
                    if (thing.def.defName == "Vent") { _view.DrawVent(thing, source, center); return; }
                    if (IsDropPod(thing))
                    {
                        if (!_view.TryDrawOptionalModel(thing, graphic, center)) _view.DrawLandedPod(source, center);
                        return;
                    }
                    if (thing.def.defName == "NatureShrine_Large" &&
                        thing.Stuff?.stuffProps?.categories?.Contains(StuffCategoryDefOf.Woody) == true)
                    {
                        var treeMaterial = _view.PlantMaterial(source);
                        float treeHeight = Mathf.Clamp(graphic.drawSize.y * 2f, 3f, 7f);
                        _view.Batch(treeMaterial, center).Upright(center - Vector3.up * (treeHeight * _view.BottomPadding(treeMaterial)),
                            Mathf.Clamp(graphic.drawSize.x * 1.5f, 2f, 5f), treeHeight, false);
                        return;
                    }
                    if (!(thing is Building_Door) && _view.TryDrawOptionalModel(thing, graphic, center)) return;
                    if (thing.def.category == ThingCategory.Building)
                    {
                        if (thing is Building_Door door)
                        {
                            _view.CreateDoor(door, source, center);
                            return;
                        }
                        var size = thing.RotatedSize;
                        float height = thing.def.IsDoor ? 1.7f : thing.def.Fillage == FillCategory.Full || thing.def.mineable ? 2f : .65f;
                        if (graphic is Graphic_Linked linkedBuilding && (thing.def.Fillage == FillCategory.Full || thing.def.mineable))
                        {
                            var atlasSource = linkedBuilding.SubGraphic.MatSingleFor(thing);
                            int faces = 0;
                            if (!_view.SolidNeighbor(thing.Position + IntVec3.South)) faces |= 1;
                            if (!_view.SolidNeighbor(thing.Position + IntVec3.North)) faces |= 2;
                            if (!_view.SolidNeighbor(thing.Position + IntVec3.West)) faces |= 4;
                            if (!_view.SolidNeighbor(thing.Position + IntVec3.East)) faces |= 8;
                            var surfaceMaterial = thing.def.building != null && thing.def.building.isResourceRock
                                ? _view.ResourceRockMaterial(atlasSource) : _view.LinkedBuildingMaterial(atlasSource);
                            if (thing.def.building != null && thing.def.building.isNaturalRock &&
                                size.x == 1 && size.z == 1)
                            {
                                if (_view.map.fogGrid.IsFogged(thing.Position + IntVec3.South)) faces |= 1;
                                if (_view.map.fogGrid.IsFogged(thing.Position + IntVec3.North)) faces |= 2;
                                if (_view.map.fogGrid.IsFogged(thing.Position + IntVec3.West)) faces |= 4;
                                if (_view.map.fogGrid.IsFogged(thing.Position + IntVec3.East)) faces |= 8;
                                surfaceMaterial = _view.NaturalRockMaterial(surfaceMaterial, thing.def.building.isResourceRock);
                                _view.DrawNaturalRock(_view.Batch(surfaceMaterial, center), thing.Position, faces,
                                    _view.map.roofGrid.RoofAt(thing.Position)?.isNatural == true);
                                return;
                            }
                            if (thing.def.defName == "Wall" && size.x == 1 && size.z == 1)
                            {
                                var wallGeometry = _view.Batch(surfaceMaterial, center);
                                int wallStart = wallGeometry.Vertices.Count;
                                _view.DrawWall(wallGeometry, center, height, faces, thing.Position);
                                if (thing is Building wall) _view.DrawWallDamage(wall, wallGeometry, wallStart, center, height);
                                return;
                            }
                            _view.Batch(surfaceMaterial, center).Box(center,
                                new Vector3(size.x, height, size.z), new Rect(0,0,size.x,height),
                                new Rect(0,0,size.x,size.z), faces, false);
                        }
                        else
                        {
                            if (_view.TryDrawGeneratedBuilding(thing, graphic, source, center)) return;
                            if (HasFallbackBuildingBody(thing.def))
                                _view.DrawFallbackBuilding(graphic, source, center, thing.Rotation);
                            else
                                _view.AppendGraphicSurface(_view.Batch(_view.ItemMaterial(source), center), graphic, thing.Rotation, center, .025f);
                        }
                    }
                    else if (thing.def.category == ThingCategory.Plant)
                    {
                        var plant = thing as Plant;
                        float growth = plant == null ? 1 : Mathf.Lerp(.35f, 1, plant.Growth);
                        var size = graphic.drawSize * growth;
                        bool tree = thing.def.plant.IsTree && !thing.def.plant.isStump;
                        if (plant != null && !tree && thing.def.plant.maxMeshCount > 1)
                        {
                            DrawPlantCluster(plant, graphic, center);
                            return;
                        }
                        bool grass = thing.def.defName.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) >= 0;
                        float plantScale = grass ? 1.2f : 1.6f;
                        float width = tree ? Mathf.Clamp(size.x * 2.3f, 1.2f, 6f)
                            : Mathf.Clamp(size.x * (grass ? .45f : .8f), .12f, 1.4f) * plantScale;
                        float height = tree ? Mathf.Clamp(size.y * 3.5f, 2f, 9f)
                            : Mathf.Clamp(size.y * (grass ? .16f : .4f), .08f, grass ? .32f : .85f) * plantScale;
                        if (tree)
                        {
                            float treeScale = Rim3DSettings.Current.TreeScaleFor(thing.def);
                            width *= treeScale;
                            height *= treeScale;
                        }
                        if (thing.def.plant.isStump)
                        {
                            var stumpSize = graphic.drawSize * thing.def.plant.visualSizeRange.LerpThroughRange(plant?.Growth ?? 1f);
                            width = stumpSize.x;
                            height = stumpSize.y;
                        }
                        else if (!tree && !grass)
                        {
                            height *= .9f;
                            if (thing.def.defName == "Plant_Berry" || thing.def.defName == "Plant_Berry_Leafless")
                                height *= 2f;
                            else if (thing.def.defName == "Plant_Bush" || thing.def.defName == "Plant_Bush_Leafless")
                                height *= 1.5f;
                            // Preserve the original graphic proportions instead of stretching shrubs horizontally.
                            width = height * graphic.drawSize.x / Mathf.Max(graphic.drawSize.y, .001f);
                        }
                        var material = _view.PlantMaterial(source);
                        var geometry = _view.Batch(material, center);
                        geometry.Upright(center - Vector3.up * (height * _view.BottomPadding(material)), width, height, false);
                        geometry.SetPlantWind(geometry.Vertices.Count - 4, center, WorldGeometry.PlantFlexibility(thing.def));
                    }
                    else
                    {
                        var size = graphic.drawSize;
                        _view.DrawItemVolume(thing, source, center, Mathf.Min(size.x, 2), Mathf.Min(size.y, 2));
                    }
            }

            void DrawPlantCluster(Plant plant, Graphic graphic, Vector3 center)
            {
                var properties = plant.def.plant;
                int count = Mathf.Max(1, Mathf.CeilToInt(plant.Growth * properties.maxMeshCount));
                int gridSize = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(properties.maxMeshCount)));
                float spacing = 1f / gridSize;
                var size = graphic.drawSize * properties.visualSizeRange.LerpThroughRange(plant.Growth);
                Rand.PushState(plant.Position.GetHashCode());
                try
                {
                    var positions = PlantPosIndices.GetPositionIndices(plant);
                    for (int i = 0; i < count && i < positions.Length; i++)
                    {
                        int index = positions[i];
                        var position = center + new Vector3((index / gridSize + .5f) * spacing - .5f,
                            0, (index % gridSize + .5f) * spacing - .5f);
                        position += Gen.RandomHorizontalVector(spacing * .3f);
                        bool flip = Rand.Bool;
                        var source = graphic is Graphic_Random random && random.SubGraphicsCount > 0
                            ? random.SubGraphicAtIndex(Rand.Range(0, random.SubGraphicsCount)).MatSingle
                            : graphic.MatAt(plant.Rotation, plant);
                        if (source == null || source.mainTexture == null || source.mainTexture == BaseContent.BadTex) continue;
                        var material = _view.PlantMaterial(source);
                        var geometry = _view.Batch(material, center);
                        geometry.Upright(position - Vector3.up * (size.y * _view.BottomPadding(material)),
                            flip ? -size.x : size.x, size.y, false);
                        geometry.SetPlantWind(geometry.Vertices.Count - 4, position, WorldGeometry.PlantFlexibility(plant.def));
                    }
                }
                finally { Rand.PopState(); }
            }

            internal void DrawFallbackBuilding(Graphic graphic, Material source, Vector3 center, Rot4 rotation)
            {
                // Include the selected atlas region when preserving the sprite's pixel proportions.
                float pixelWidth = Mathf.Max(1, source.mainTexture.width * Mathf.Abs(source.mainTextureScale.x));
                float pixelHeight = Mathf.Max(1, source.mainTexture.height * Mathf.Abs(source.mainTextureScale.y));
                float scale = Mathf.Max(graphic.drawSize.x / pixelWidth, graphic.drawSize.y / pixelHeight);
                float width = pixelWidth * scale, height = pixelHeight * scale;
                var material = _view.ConvertMaterial(source, false);
                center += graphic.DrawOffset(rotation);
                center.y = -height * _view.BottomPadding(material);
                _view.Batch(material, center).DiagonalCards(center, width, height, rotation.AsAngle);
            }

            internal void AppendGroundSpotSurface(Geometry target, Graphic graphic, ThingDef def, Rot4 rotation, Vector3 center, float height)
            {
                int start = target.Vertices.Count;
                _view.AppendGraphicSurface(target, graphic, rotation, center, height);
                var size = GenAdj.OccupiedRect(IntVec3.Zero, rotation, def.Size);
                float halfX = size.Width * .5f - .004f, halfZ = size.Height * .5f - .004f;
                // Native marker graphics can oversize their footprint. Keep adjacent
                // markers disjoint, including rotated graphics and draw offsets.
                for (int i = start; i < target.Vertices.Count; i++)
                {
                    var vertex = target.Vertices[i];
                    vertex.x = Mathf.Clamp(vertex.x, center.x - halfX, center.x + halfX);
                    vertex.z = Mathf.Clamp(vertex.z, center.z - halfZ, center.z + halfZ);
                    target.Vertices[i] = vertex;
                }
            }

            internal void AppendGraphicSurface(Geometry target, Graphic graphic, Rot4 rotation, Vector3 center, float height)
            {
                var mesh = graphic.MeshAt(rotation);
                if (!_view.graphicSurfaceMeshes.TryGetValue(mesh, out var surface))
                {
                    // Cache copies of Unity mesh buffers; chunk rebuilds reuse them.
                    surface = new Geometry();
                    surface.Vertices.AddRange(mesh.vertices);
                    surface.Uvs.AddRange(mesh.uv);
                    surface.Indices.AddRange(mesh.triangles);
                    for (int i = 0; i < surface.Vertices.Count; i++)
                    {
                        surface.Normals.Add(Vector3.up);
                        surface.CardCenters.Add(Vector3.zero);
                    }
                    _view.graphicSurfaceMeshes.Add(mesh, surface);
                }
                var offset = graphic.DrawOffset(rotation); offset.y = height;
                // Native rotation includes flip compensation and fallback texture angles.
                target.Append(surface, center + offset, graphic.QuatFromRot(rotation));
            }

            internal Material NewMaterial(Texture texture, Color color, bool opaque)
            {
                var material = new Material(_view.shader) { hideFlags = HideFlags.DontSave, mainTexture = texture, color = color };
                material.SetFloat("_Cull", 0);
                material.SetFloat("_ZWrite", 1);
                material.SetInt("_ColorMask", 15);
                material.SetInt("_StencilComp", (int)CompareFunction.Always);
                material.SetInt("_StencilReadMask", 255);
                material.SetInt("_StencilWriteMask", 64);
                material.SetFloat("_Opaque", opaque ? 1 : 0);
                material.SetTexture("_MaskTex", Texture2D.blackTexture);
                _view.materials.Add(material);
                return material;
            }

            internal float BottomPadding(Material material)
            {
                if (_view.bottomPadding.TryGetValue(material, out var padding)) return padding;
                var texture = material.mainTexture;
                if (texture == null) return 0;
                // Imported textures need not be CPU-readable. Read a small GPU copy
                // once per material, including its atlas scale/offset, at view creation.
                int width = Mathf.Clamp(texture.width, 1, 256);
                int height = Mathf.Clamp(texture.height, 1, 512);
                var previous = RenderTexture.active;
                RenderTexture sample = null;
                Texture2D pixels = null;
                float visibleHeight = 1;
                try
                {
                    sample = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(texture, sample, material.mainTextureScale, material.mainTextureOffset);
                    RenderTexture.active = sample;
                    pixels = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    var colors = pixels.GetPixels32();
                    var mask = new byte[64 * 80];
                    for (int my = 0; my < 80; my++) for (int mx = 0; mx < 64; mx++)
                        mask[my * 64 + mx] = colors[Mathf.Min(height - 1, my * height / 80) * width + Mathf.Min(width - 1, mx * width / 64)].a;
                    _view.spriteMasks[material] = mask;
                    float alpha = material.color.a;
                    bool found = false;
                    for (int y = 0; y < height && !found; y++)
                        for (int x = 0; x < width; x++)
                            if (colors[y * width + x].a / 255f * alpha >= .25f)
                            {
                                // Lower edge of the first visible row touches the ground.
                                padding = y / (float)height;
                                found = true;
                                break;
                            }
                    if (found)
                    {
                        bool topFound = false;
                        for (int y = height - 1; y >= 0 && !topFound; y--)
                            for (int x = 0; x < width; x++)
                                if (colors[y * width + x].a / 255f * alpha >= .25f)
                                {
                                    visibleHeight = (y + 1) / (float)height - padding;
                                    topFound = true;
                                    break;
                                }
                    }
                }
                catch (Exception error)
                {
                    Log.Warning("RimVision ground alignment: " + error.Message);
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (sample != null) RenderTexture.ReleaseTemporary(sample);
                    if (pixels != null) UnityEngine.Object.Destroy(pixels);
                }
                _view.bottomPadding.Add(material, padding);
                _view.visibleSpriteHeights[material] = visibleHeight;
                return padding;
            }

            internal Material ConvertMaterial(Material source, bool opaque)
            {
                var cache = opaque ? _solidMaterials : _cutoutMaterials;
                if (cache.TryGetValue(source, out var result)) return result;
                result = _view.NewMaterial(source.mainTexture, source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white, opaque);
                result.mainTextureScale = source.mainTextureScale;
                result.mainTextureOffset = source.mainTextureOffset;
                if (source.HasProperty("_MaskTex") && source.GetTexture("_MaskTex") != null)
                {
                    result.SetTexture("_MaskTex", source.GetTexture("_MaskTex"));
                    result.SetFloat("_UseMask", 1);
                    if (source.HasProperty("_ColorTwo")) result.SetColor("_ColorTwo", source.GetColor("_ColorTwo"));
                }
                cache.Add(source, result);
                return result;
            }

            readonly Dictionary<Material, Material> _plantMaterials = new Dictionary<Material, Material>();

            readonly Dictionary<Material, Material> _linkedBuildingMaterials = new Dictionary<Material, Material>();

            internal Material LinkedBuildingMaterial(Material source)
            {
                if (_linkedBuildingMaterials.TryGetValue(source, out var result)) return result;
                result = new Material(_view.ConvertMaterial(source, true)) { hideFlags = HideFlags.DontSave };
                result.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
                // Atlas selection is baked into each face's UVs exactly once.
                result.mainTextureScale = Vector2.one;
                result.mainTextureOffset = Vector2.zero;
                string name = source.mainTexture.name;
                bool wood = name.IndexOf("Planks", StringComparison.OrdinalIgnoreCase) >= 0;
                string path = name.IndexOf("Smooth", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "textures/terrain/surfaces/SmoothStone"
                        : "textures/terrain/surfaces/RoughHewnRock";
                var surface = wood ? null : ModAssets.LoadTexture(path);
                if (surface != null) result.mainTexture = surface;
                result.SetFloat("_SurfaceTile", 1);
                result.SetFloat("_UseMask", 0);
                result.SetTexture("_MaskTex", Texture2D.blackTexture);
                if (wood) _view.ApplyWoodSurface(result);
                if (name.IndexOf("Bricks", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Isolate one repeatable strip; sampling the atlas directly leaks its
                    // black gaps into both the face and the texture's lower mip levels.
                    result.mainTexture = _view.WallBrickTexture(source.mainTexture);
                    result.SetVector("_SurfaceScale", new Vector4(1, 4, 0, 0));
                    result.SetFloat("_SurfaceRepeat", 1);
                }
                result.SetFloat("_WallSurface", 1);
                result.SetFloat("_WallBevel", 1);
                _view.materials.Add(result);
                _linkedBuildingMaterials.Add(source, result);
                return result;
            }

            internal bool SolidNeighbor(IntVec3 cell)
            {
                if (!cell.InBounds(_view.map)) return false;
                var building = cell.GetEdifice(_view.map);
                if (building == null || building is Building_Door ||
                    (building.def.Fillage != FillCategory.Full && !building.def.mineable)) return false;
                var graphic = building.Graphic;
                return graphic is Graphic_Linked && _view.ThingModel(building, graphic) == null;
            }

            readonly Dictionary<Material, Material> _itemMaterials = new Dictionary<Material, Material>();

            internal Material ItemMaterial(Material source)
            {
                if (_itemMaterials.TryGetValue(source, out var result)) return result;
                result = new Material(_view.ConvertMaterial(source, false)) { hideFlags = HideFlags.DontSave };
                result.SetFloat("_FlatItem", 1);
                _view.materials.Add(result);
                _itemMaterials.Add(source, result);
                return result;
            }

            internal Material PlantMaterial(Material source)
            {
                if (_plantMaterials.TryGetValue(source, out var result)) return result;
                result = new Material(_view.ConvertMaterial(source, false)) { hideFlags = HideFlags.DontSave };
                result.SetFloat("_Billboard", 1);
                result.SetFloat("_Foliage", 1);
                _view.materials.Add(result);
                _plantMaterials.Add(source, result);
                return result;
            }

            internal static float PlantFlexibility(ThingDef def)
            {
                if (def.category != ThingCategory.Plant || def.plant == null || def.plant.isStump) return 0;
                return def.plant.IsTree ? .075f : .45f;
            }

            internal void BuildMountainVertices()
            {
                int width = _view.map.Size.x, depth = _view.map.Size.z;
                if (_view._mountainRoofTypes == null) _view._mountainRoofTypes = new byte[width, depth];
                var distances = new float[width + 2, depth + 2];
                for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    byte type = _view.DrawSpatialMountain(cell) ? MountainRoofType(_view.map.roofGrid.RoofAt(cell)) : (byte)0;
                    _view._mountainRoofTypes[x, z] = type;
                    distances[x + 1, z + 1] = type == 0 ? 0 : 64;
                }
                _view.BuildMountainRockTypes();
                for (int z = 1; z <= depth; z++)
                for (int x = 1; x <= width; x++)
                {
                    float value = distances[x, z];
                    value = Mathf.Min(value, Mathf.Min(distances[x - 1, z], distances[x, z - 1]) + 1);
                    value = Mathf.Min(value, Mathf.Min(distances[x - 1, z - 1], distances[x + 1, z - 1]) + 1.414214f);
                    distances[x, z] = value;
                }
                for (int z = depth; z >= 1; z--)
                for (int x = width; x >= 1; x--)
                {
                    float value = distances[x, z];
                    value = Mathf.Min(value, Mathf.Min(distances[x + 1, z], distances[x, z + 1]) + 1);
                    value = Mathf.Min(value, Mathf.Min(distances[x + 1, z + 1], distances[x - 1, z + 1]) + 1.414214f);
                    distances[x, z] = value;
                }
                _view._mountainVertices = new Vector3[width + 1, depth + 1];
                for (int z = 0; z <= depth; z++)
                for (int x = 0; x <= width; x++)
                {
                    float inset = 0, thick = 0;
                    int count = 0;
                    var inward = Vector2.zero;
                    for (int dz = -1; dz <= 0; dz++)
                    for (int dx = -1; dx <= 0; dx++)
                    {
                        int cx = x + dx, cz = z + dz;
                        if (cx < 0 || cz < 0 || cx >= width || cz >= depth || _view._mountainRoofTypes[cx, cz] == 0) continue;
                        count++;
                        inset += distances[cx + 1, cz + 1];
                        thick += _view._mountainRoofTypes[cx, cz] == 2 ? 1 : 0;
                        inward += new Vector2(dx + .5f, dz + .5f);
                    }
                    if (count == 0) continue;
                    inset = Mathf.Max(0, inset / count - .5f);
                    if (count < 4) inset *= .35f;
                    float broad = Mathf.PerlinNoise(x * .043f + 17.3f, z * .043f + 39.7f);
                    float ridge = 1 - Mathf.Abs(2 * Mathf.PerlinNoise(x * .11f + 71.6f, z * .11f + 23.1f) - 1);
                    float detail = Mathf.PerlinNoise(x * .47f + 11.8f, z * .47f + 63.4f);
                    float height = 2.7f + Mathf.Pow(inset, .78f) * (1.1f + broad * 1.6f) *
                        Mathf.Lerp(.65f, 1, thick / count) + ridge * Mathf.Min(inset * .45f, 3.5f) + detail * .35f;
                    Vector2 offset = count < 4 ? inward.normalized * .22f : new Vector2(
                        Mathf.PerlinNoise(x * .73f + 5.2f, z * .73f + 9.6f) - .5f,
                        Mathf.PerlinNoise(x * .73f + 45.2f, z * .73f + 29.6f) - .5f) * .44f;
                    _view._mountainVertices[x, z] = new Vector3(x + offset.x, height, z + offset.y);
                }
            }

            internal void BuildMountainRockTypes()
            {
                int width = _view.map.Size.x, depth = _view.map.Size.z;
                if (_view._mountainRockTypes == null)
                {
                    _view._mountainRockTypes = new ThingDef[width, depth];
                    foreach (var rock in DefDatabase<ThingDef>.AllDefsListForReading)
                    {
                        if (rock.building == null || !rock.building.isNaturalRock || rock.building.isResourceRock ||
                            rock.graphicData == null) continue;
                        _view.RegisterMountainTerrain(rock, "_Rough");
                        _view.RegisterMountainTerrain(rock, "_RoughHewn");
                        _view.RegisterMountainTerrain(rock, "_Smooth");
                    }
                }
                var pending = new Queue<IntVec3>();
                for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    if (_view._mountainRoofTypes[x, z] == 0) { _view._mountainRockTypes[x, z] = null; continue; }
                    var cell = new IntVec3(x, 0, z);
                    var rock = cell.GetEdifice(_view.map)?.def;
                    if (rock?.building != null && rock.building.isNaturalRock && !rock.building.isResourceRock &&
                        rock.graphicData != null) _view._mountainRockTypes[x, z] = rock;
                    else
                    {
                        var terrain = _view.map.terrainGrid.TerrainAt(cell);
                        if (terrain != null && _view._mountainTerrainRocks.TryGetValue(terrain, out var terrainRock))
                            _view._mountainRockTypes[x, z] = terrainRock;
                    }
                    if (_view._mountainRockTypes[x, z] != null) pending.Enqueue(cell);
                }
                while (pending.Count != 0)
                {
                    var cell = pending.Dequeue();
                    for (int i = 0; i < GenAdj.CardinalDirections.Length; i++)
                    {
                        var neighbor = cell + GenAdj.CardinalDirections[i];
                        if (!neighbor.InBounds(_view.map) || _view._mountainRoofTypes[neighbor.x, neighbor.z] == 0 ||
                            _view._mountainRockTypes[neighbor.x, neighbor.z] != null) continue;
                        _view._mountainRockTypes[neighbor.x, neighbor.z] = _view._mountainRockTypes[cell.x, cell.z];
                        pending.Enqueue(neighbor);
                    }
                }
            }

            internal void RegisterMountainTerrain(ThingDef rock, string suffix)
            {
                var terrain = DefDatabase<TerrainDef>.GetNamedSilentFail(rock.defName + suffix);
                if (terrain != null) _view._mountainTerrainRocks[terrain] = rock;
            }

            internal Material MountainRockMaterial(IntVec3 cell, Material fallback)
            {
                var rock = _view._mountainRockTypes[cell.x, cell.z];
                if (rock == null) return fallback;
                if (_view._mountainRockMaterials.TryGetValue(rock, out var material)) return material;
                material = new Material(fallback) { hideFlags = HideFlags.DontSave, color = rock.graphicData.color };
                _view.materials.Add(material);
                _view._mountainRockMaterials.Add(rock, material);
                return material;
            }

            internal Material NaturalRockMaterial(Material source, bool resource)
            {
                if (_view._naturalRockMaterials.TryGetValue(source, out var material)) return material;
                material = new Material(source) { hideFlags = HideFlags.DontSave };
                if (!resource)
                    material.mainTexture = ModAssets.LoadTexture("textures/terrain/surfaces/RoughStone") ?? source.mainTexture;
                material.SetFloat("_Cull", (float)CullMode.Off);
                material.SetFloat("_WallBevel", 0);
                if (!resource) material.SetVector("_SurfaceScale", new Vector4(.25f, .25f, 0, 0));
                _view.materials.Add(material);
                _view._naturalRockMaterials.Add(source, material);
                return material;
            }

            internal void BuildMountainRoof(IntVec3 cell, Material material)
            {
                if (!_view._mountainExteriorMaterials.TryGetValue(material, out var exterior))
                {
                    exterior = new Material(material) { hideFlags = HideFlags.DontSave };
                    exterior.SetFloat("_Cull", (float)CullMode.Back);
                    _view.materials.Add(exterior);
                    _view._mountainExteriorMaterials.Add(material, exterior);
                }
                int x = cell.x, z = cell.z;
                var geometry = _view.Batch(exterior, new Vector3(x, 0, z));
                var a = _view._mountainVertices[x, z];
                var b = _view._mountainVertices[x, z + 1];
                var c = _view._mountainVertices[x + 1, z + 1];
                var d = _view._mountainVertices[x + 1, z];
                MountainFace(geometry, a, b, c, d);
                _view.BuildMountainRoofBoundary(geometry, cell);
            }

            internal void BuildMountainRoofBoundary(Geometry geometry, IntVec3 cell)
            {
                bool south = !_view.NaturalRoofAt(cell + IntVec3.South);
                bool north = !_view.NaturalRoofAt(cell + IntVec3.North);
                bool west = !_view.NaturalRoofAt(cell + IntVec3.West);
                bool east = !_view.NaturalRoofAt(cell + IntVec3.East);
                if (!south && !north && !west && !east) return;
                int x = cell.x, z = cell.z;
                var a = _view._mountainVertices[x, z];
                var b = _view._mountainVertices[x, z + 1];
                var c = _view._mountainVertices[x + 1, z + 1];
                var d = _view._mountainVertices[x + 1, z];
                var floorA = _view.NaturalRockVertex(x, z, 2);
                var floorB = _view.NaturalRockVertex(x, z + 1, 2);
                var floorC = _view.NaturalRockVertex(x + 1, z + 1, 2);
                var floorD = _view.NaturalRockVertex(x + 1, z, 2);
                if (south)
                    MountainFace(geometry, floorA, a, d, floorD);
                if (north)
                    MountainFace(geometry, floorC, c, b, floorB);
                if (west)
                    MountainFace(geometry, floorB, b, a, floorA);
                if (east)
                    MountainFace(geometry, floorD, d, c, floorC);
            }

            internal void ConfigureMountainLod(MeshFilter filter, MeshRenderer renderer, Material material)
            {
                Material source = null;
                foreach (var entry in _view._mountainExteriorMaterials)
                    if (entry.Value == material) { source = entry.Key; break; }
                if (source == null) return;
                var detailed = filter.sharedMesh;
                var key = ChunkKey(detailed.bounds.center);
                var lod = new MountainLod
                {
                    Filter = filter, Renderer = renderer, Detailed = detailed, Bounds = renderer.bounds,
                    Medium = _view.BuildMountainLodMesh(key, source, 4, detailed),
                    Distant = _view.BuildMountainLodMesh(key, source, 8, detailed),
                    Extreme = _view.BuildMountainLodMesh(key, source, 16, detailed)
                };
                if (lod.Distant != lod.Medium && lod.Distant.vertexCount >= lod.Medium.vertexCount)
                {
                    if (lod.Distant != detailed) _view.DestroyMountainLodMesh(lod.Distant);
                    lod.Distant = lod.Medium;
                }
                if (lod.Extreme != lod.Distant && lod.Extreme.vertexCount >= lod.Distant.vertexCount)
                {
                    if (lod.Extreme != detailed) _view.DestroyMountainLodMesh(lod.Extreme);
                    lod.Extreme = lod.Distant;
                }
                _view._mountainLods.Add(lod);
                _view._nextMountainLodUpdate = 0;
            }

            internal Mesh BuildMountainLodMesh(Vector2Int key, Material material, int step, Mesh detailed)
            {
                var geometry = new Geometry();
                int startX = key.x * ChunkSize, startZ = key.y * ChunkSize;
                int endX = Mathf.Min(startX + ChunkSize, _view.map.Size.x), endZ = Mathf.Min(startZ + ChunkSize, _view.map.Size.z);
                for (int z = startZ; z < endZ; z++)
                for (int x = startX; x < endX; x++)
                    if (_view._mountainSurfaceMaterials[x, z] == material)
                        _view.BuildMountainRoofBoundary(geometry, new IntVec3(x, 0, z));
                for (int z = startZ; z < endZ; z += step)
                for (int x = startX; x < endX; x += step)
                {
                    int right = Mathf.Min(x + step, endX), top = Mathf.Min(z + step, endZ);
                    bool merge = right - x >= 3 && top - z >= 3;
                    for (int row = z; row < top && merge; row++)
                    for (int column = x; column < right; column++)
                        if (_view._mountainSurfaceMaterials[column, row] != material) { merge = false; break; }
                    if (merge)
                    {
                        var center = _view._mountainVertices[(x + right) / 2, (z + top) / 2];
                        _view.MountainLodEdge(geometry, center, x, z, 0, 1, top - z);
                        _view.MountainLodEdge(geometry, center, x, top, 1, 0, right - x);
                        _view.MountainLodEdge(geometry, center, right, top, 0, -1, top - z);
                        _view.MountainLodEdge(geometry, center, right, z, -1, 0, right - x);
                    }
                    else
                    {
                        for (int row = z; row < top; row++)
                        for (int column = x; column < right; column++)
                            if (_view._mountainSurfaceMaterials[column, row] == material)
                                MountainFace(geometry, _view._mountainVertices[column, row], _view._mountainVertices[column, row + 1],
                                    _view._mountainVertices[column + 1, row + 1], _view._mountainVertices[column + 1, row]);
                    }
                }
                if (geometry.Indices.Count >= detailed.GetIndexCount(0)) return detailed;
                var mesh = new Mesh { name = "RimVision mountain LOD " + step, indexFormat = IndexFormat.UInt32,
                    hideFlags = HideFlags.DontSave };
                mesh.SetVertices(geometry.Vertices); mesh.SetNormals(geometry.Normals);
                mesh.SetUVs(0, geometry.Uvs); mesh.SetUVs(1, geometry.CardCenters);
                geometry.ApplyOutlineData(mesh);
                mesh.SetTriangles(geometry.Indices, 0);
                mesh.bounds = detailed.bounds;
                _view.meshes.Add(mesh);
                return mesh;
            }

            internal void MountainLodEdge(Geometry geometry, Vector3 center, int x, int z, int dx, int dz, int length)
            {
                for (int i = 0; i < length; i++)
                    MountainTriangle(geometry, center, _view._mountainVertices[x + dx * i, z + dz * i],
                        _view._mountainVertices[x + dx * (i + 1), z + dz * (i + 1)]);
            }

            internal void UpdateMountainLods()
            {
                var position = _view.camera.transform.position;
                bool firstPerson = _view.FirstPerson;
                int simplification = Rim3DSettings.Current.MountainSimplification;
                if (Time.unscaledTime < _view._nextMountainLodUpdate && firstPerson == _lastMountainFirstPerson &&
                    simplification == _lastMountainSimplification &&
                    (position - _lastMountainCameraPosition).sqrMagnitude < 4) return;
                _view._nextMountainLodUpdate = Time.unscaledTime + .2f;
                _lastMountainCameraPosition = position;
                _lastMountainFirstPerson = firstPerson;
                _lastMountainSimplification = simplification;
                foreach (var lod in _view._mountainLods)
                {
                    float distance = lod.Bounds.SqrDistance(position);
                    float scale = simplification == 3 ? .45f : simplification == 2 ? .75f : 1;
                    float near = (lod.Level == 0 ? 72 : 56) * scale;
                    float far = (lod.Level == 2 ? 112 : 144) * scale;
                    int level = simplification == 0 || distance < near * near ? 0 : distance < far * far ? 1 : 2;
                    var mesh = level == 0 ? lod.Detailed : level == 1
                        ? simplification == 1 ? lod.Medium : lod.Distant
                        : simplification == 1 ? lod.Distant : lod.Extreme;
                    if (lod.Filter.sharedMesh != mesh) lod.Filter.sharedMesh = mesh;
                    lod.Level = level;
                    lod.Renderer.enabled = !firstPerson || !_view.MountainHidden(lod.Bounds, position);
                }
            }

            internal void ReleaseMountainLods(Transform owner)
            {
                for (int i = _view._mountainLods.Count - 1; i >= 0; i--)
                {
                    var lod = _view._mountainLods[i];
                    if (!lod.Filter.transform.IsChildOf(owner)) continue;
                    lod.Filter.sharedMesh = lod.Detailed;
                    lod.Renderer.enabled = true;
                    if (lod.Medium != lod.Detailed) _view.DestroyMountainLodMesh(lod.Medium);
                    if (lod.Distant != lod.Detailed && lod.Distant != lod.Medium) _view.DestroyMountainLodMesh(lod.Distant);
                    if (lod.Extreme != lod.Detailed && lod.Extreme != lod.Medium && lod.Extreme != lod.Distant)
                        _view.DestroyMountainLodMesh(lod.Extreme);
                    _view._mountainLods.RemoveAt(i);
                }
            }

            internal void DestroyMountainLodMesh(Mesh mesh)
            {
                _view.meshes.Remove(mesh);
                UnityEngine.Object.Destroy(mesh);
            }

            internal void BuildMountainOccluder(Vector2Int key)
            {
                int x = key.x * ChunkSize, z = key.y * ChunkSize;
                int right = Mathf.Min(x + ChunkSize, _view.map.Size.x), top = Mathf.Min(z + ChunkSize, _view.map.Size.z);
                bool groundSolid = true;
                for (int row = z; row < top; row++)
                for (int column = x; column < right; column++)
                {
                    if (_view._mountainRoofTypes[column, row] == 0) { _view._mountainOccluders.Remove(key); return; }
                    if (!_view.VisibleNaturalRockAt(new IntVec3(column, 0, row))) groundSolid = false;
                }
                float height = float.MaxValue;
                for (int row = z; row <= top; row++)
                for (int column = x; column <= right; column++)
                    height = Mathf.Min(height, _view._mountainVertices[column, row].y);
                float bottom = groundSolid ? .05f : 2.1f;
                if (height <= bottom + .2f) { _view._mountainOccluders.Remove(key); return; }
                var min = _view.root.transform.position + new Vector3(x + .3f, bottom, z + .3f);
                var max = _view.root.transform.position + new Vector3(right - .3f, height - .1f, top - .3f);
                _view._mountainOccluders[key] = new Bounds((min + max) * .5f, max - min);
            }

            internal bool MountainHidden(Bounds bounds, Vector3 position)
            {
                float distance = bounds.SqrDistance(position);
                var min = bounds.min;
                var max = bounds.max;
                foreach (var occluder in _view._mountainOccluders.Values)
                {
                    if (occluder.Contains(position) || occluder.Intersects(bounds) ||
                        occluder.SqrDistance(position) >= distance) continue;
                    bool hidden = true;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vector3((i & 1) == 0 ? min.x : max.x,
                            (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                        var direction = corner - position;
                        if (!occluder.IntersectRay(new Ray(position, direction), out float entry) ||
                            entry >= direction.magnitude - .05f) { hidden = false; break; }
                    }
                    if (hidden) return true;
                }
                return false;
            }

            internal bool NaturalRoofAt(IntVec3 cell)
            {
                return cell.InBounds(_view.map) && _view.map.roofGrid.RoofAt(cell)?.isNatural == true;
            }

            internal bool VisibleNaturalRockAt(IntVec3 cell)
            {
                if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell)) return false;
                var rock = cell.GetEdifice(_view.map);
                return rock?.def.building != null && rock.def.building.isNaturalRock &&
                    rock.RotatedSize.x == 1 && rock.RotatedSize.z == 1 && rock.Graphic is Graphic_Linked &&
                    _view.ThingModel(rock, rock.Graphic) == null;
            }

            internal Vector3 NaturalRockVertex(int x, int z, int layer)
            {
                var outward = Vector2.zero;
                bool fixedCorner = false;
                bool mountainCorner = false;
                for (int dz = -1; dz <= 0; dz++)
                for (int dx = -1; dx <= 0; dx++)
                {
                    var cell = new IntVec3(x + dx, 0, z + dz);
                    if (!_view.SolidNeighbor(cell)) continue;
                    var building = cell.GetEdifice(_view.map);
                    if (building.def.building?.isNaturalRock != true || _view.map.fogGrid.IsFogged(cell)) fixedCorner = true;
                    outward -= new Vector2(dx + .5f, dz + .5f);
                    if (layer != 2 || !_view.NaturalRoofAt(cell) || !_view.VisibleNaturalRockAt(cell)) continue;
                    var horizontal = cell + (dx == -1 ? IntVec3.East : IntVec3.West);
                    var vertical = cell + (dz == -1 ? IntVec3.North : IntVec3.South);
                    if ((!_view.NaturalRoofAt(horizontal) && !_view.SolidNeighbor(horizontal)) ||
                        (!_view.NaturalRoofAt(vertical) && !_view.SolidNeighbor(vertical))) mountainCorner = true;
                }
                if (layer == 2 && mountainCorner && !fixedCorner && _view._mountainVertices != null)
                    return _view._mountainVertices[x, z];
                float noise = Mathf.PerlinNoise(x * .39f + 32.8f, z * .39f + 16.4f);
                float height = layer == 0 ? 0 : layer == 1 ? .65f + noise * .7f : 2;
                float amount = layer == 0 ? .04f : layer == 1 ? .13f + noise * .13f : .06f;
                var offset = fixedCorner ? Vector2.zero : outward.normalized * amount;
                return new Vector3(x + offset.x, height, z + offset.y);
            }

            internal void NaturalRockFace(Geometry geometry, int leftX, int leftZ, int rightX, int rightZ)
            {
                var bottomLeft = _view.NaturalRockVertex(leftX, leftZ, 0);
                var bottomRight = _view.NaturalRockVertex(rightX, rightZ, 0);
                var middleLeft = _view.NaturalRockVertex(leftX, leftZ, 1);
                var middleRight = _view.NaturalRockVertex(rightX, rightZ, 1);
                var topLeft = _view.NaturalRockVertex(leftX, leftZ, 2);
                var topRight = _view.NaturalRockVertex(rightX, rightZ, 2);
                MountainFace(geometry, bottomLeft, middleLeft, middleRight, bottomRight);
                MountainFace(geometry, middleLeft, topLeft, topRight, middleRight);
            }

            internal void DrawNaturalRock(Geometry geometry, IntVec3 cell, int faces, bool roofed)
            {
                int start = geometry.Vertices.Count;
                int x = cell.x, z = cell.z;
                if ((faces & 1) != 0) _view.NaturalRockFace(geometry, x, z, x + 1, z);
                if ((faces & 2) != 0) _view.NaturalRockFace(geometry, x + 1, z + 1, x, z + 1);
                if ((faces & 4) != 0) _view.NaturalRockFace(geometry, x, z + 1, x, z);
                if ((faces & 8) != 0) _view.NaturalRockFace(geometry, x + 1, z, x + 1, z + 1);
                if (!roofed) MountainFace(geometry, _view.NaturalRockVertex(x, z, 2), _view.NaturalRockVertex(x, z + 1, 2),
                    _view.NaturalRockVertex(x + 1, z + 1, 2), _view.NaturalRockVertex(x + 1, z, 2));
                geometry.SetOutlineBounds(start, new Bounds(new Vector3(x + .5f, 1, z + .5f), new Vector3(1, 2, 1)));
            }

            internal bool RoofVisible(IntVec3 cell, RoofDef roof)
            {
                // Natural roofs form the mountain silhouette even over undiscovered
                // rock and caves. Keep their contents hidden by the normal fog rules.
                return roof != null && _view.DrawSpatialRoof(cell) && (roof.isNatural || !_view.map.fogGrid.IsFogged(cell));
            }

            internal void BuildRoofs(Vector2Int? onlyChunk = null)
            {
                var roofStyles = _view.map.GetComponent<RoofStyles>();
                if (_view._mountainVertices == null) _view.BuildMountainVertices();
                if (_view._mountainSurfaceMaterials == null) _view._mountainSurfaceMaterials = new Material[_view.map.Size.x, _view.map.Size.z];
                if (_view.roofMask == null)
                    _view.roofMask = new Texture2D(_view.map.Size.x, _view.map.Size.z, TextureFormat.RGBA32, false, true)
                    { name = "RimVision roof occlusion", hideFlags = HideFlags.DontSave,
                        filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                int startX = onlyChunk.HasValue ? onlyChunk.Value.x * ChunkSize : 0;
                int startZ = onlyChunk.HasValue ? onlyChunk.Value.y * ChunkSize : 0;
                int endX = onlyChunk.HasValue ? Mathf.Min(startX + ChunkSize, _view.map.Size.x) : _view.map.Size.x;
                int endZ = onlyChunk.HasValue ? Mathf.Min(startZ + ChunkSize, _view.map.Size.z) : _view.map.Size.z;
                for (int z = startZ; z < endZ; z++)
                for (int x = startX; x < endX; x++)
                {
                    _view.terrainScratch[x, z] = null;
                    _view._mountainSurfaceMaterials[x, z] = null;
                    _view.roofMask.SetPixel(x, z, Color.black);
                    var cell = new IntVec3(x, 0, z);
                    var roof = _view.map.roofGrid.RoofAt(cell);
                    if (!_view.RoofVisible(cell, roof)) continue;
                    _view.roofMask.SetPixel(x, z, Color.white);
                    if (!_roofMaterials.TryGetValue(roof, out var material))
                    {
                        var color = roof.isNatural ? new Color(.70f, .68f, .63f) : new Color(.48f, .52f, .56f);
                        var texture = ModAssets.LoadTexture(roof.isNatural
                            ? "textures/terrain/surfaces/RoughStone" : "textures/terrain/surfaces/Concrete");
                        material = _view.NewMaterial(texture ?? Texture2D.whiteTexture, color, true);
                        // World-space tiling stays continuous across merged rectangles
                        // and chunks, instead of stretching one texture over a mountain.
                        material.SetFloat("_SurfaceTile", 1);
                        material.SetFloat("_WallSurface", 1);
                        float scale = roof.isNatural ? .25f : .5f;
                        material.SetVector("_SurfaceScale", new Vector4(scale, scale, 0, 0));
                        // A ceiling covers the floor; projecting its entire silhouette
                        // onto the ground would paint a second, offset roof indoors.
                        material.SetFloat("_Roof", 1);
                        _roofMaterials.Add(roof, material);
                    }
                    if (roof.isNatural && _view.DrawSpatialMountain(cell))
                    {
                        material = _view.MountainRockMaterial(cell, material);
                        _view._mountainSurfaceMaterials[x, z] = material;
                        _view.BuildMountainRoof(cell, material);
                        if (_view.VisibleNaturalRockAt(cell)) continue;
                        var a = _view.NaturalRockVertex(x, z, 2);
                        var b = _view.NaturalRockVertex(x, z + 1, 2);
                        var c = _view.NaturalRockVertex(x + 1, z + 1, 2);
                        var d = _view.NaturalRockVertex(x + 1, z, 2);
                        if (a != new Vector3(x, 2f, z) || b != new Vector3(x, 2f, z + 1) ||
                            c != new Vector3(x + 1, 2f, z + 1) || d != new Vector3(x + 1, 2f, z))
                        {
                            MountainFace(_view.Batch(material, new Vector3(x, 0, z)), a, d, c, b);
                            continue;
                        }
                    }
                    else if (_view.DrawSpatialRoofTop(cell) && roofStyles.At(cell) is RoofProfile profile)
                    {
                        _view.BuildProceduralRoof(cell, profile, material);
                        continue;
                    }
                    _view.terrainScratch[x, z] = material;
                }
                _view.roofMask.Apply(false, false);
                for (int z = startZ; z < endZ; z += ChunkSize)
                for (int x = startX; x < endX; x += ChunkSize)
                    _view.BuildMountainOccluder(new Vector2Int(x / ChunkSize, z / ChunkSize));
                _view.BuildTerrain(_view.terrainScratch, onlyChunk, true);
            }

            internal void BuildTerrain(Material[,] cells, Vector2Int? onlyChunk = null, bool roofSlab = false)
            {
                // Merge equal, flat tiles within each chunk. World UVs preserve texture
                // scale and continuity even across rectangles and chunk boundaries.
                int startZ = onlyChunk.HasValue ? onlyChunk.Value.y * ChunkSize : 0;
                int startX = onlyChunk.HasValue ? onlyChunk.Value.x * ChunkSize : 0;
                int limitZ = onlyChunk.HasValue ? Mathf.Min(startZ + ChunkSize, _view.map.Size.z) : _view.map.Size.z;
                int limitX = onlyChunk.HasValue ? Mathf.Min(startX + ChunkSize, _view.map.Size.x) : _view.map.Size.x;
                if (!roofSlab)
                {
                    if (_view._spatialProvider != null)
                    for (int z = startZ; z < limitZ; z++)
                    for (int x = startX; x < limitX; x++)
                        if (!_view.DrawSpatialFloor(new IntVec3(x, 0, z))) cells[x, z] = null;
                    _view.UpdateShorelineMask(onlyChunk);
                    _view.BuildShorelineGround(startX, startZ, limitX, limitZ);
                    _view.BuildGroundTransitions(startX, startZ, limitX, limitZ);
                    BuildGroundSides(cells, startX, startZ, limitX, limitZ);
                }
                for (int chunkZ = startZ; chunkZ < limitZ; chunkZ += ChunkSize)
                for (int chunkX = startX; chunkX < limitX; chunkX += ChunkSize)
                {
                    int endX = Mathf.Min(chunkX + ChunkSize, _view.map.Size.x);
                    int endZ = Mathf.Min(chunkZ + ChunkSize, _view.map.Size.z);
                    for (int z = chunkZ; z < endZ; z++)
                    for (int x = chunkX; x < endX; x++)
                    {
                        var material = cells[x, z];
                        if (material == null) continue;
                        bool roofTop = !roofSlab || _view.DrawSpatialRoofTop(new IntVec3(x, 0, z));
                        int right = x + 1;
                        while (right < endX && cells[right, z] == material &&
                            (!roofSlab || _view.DrawSpatialRoofTop(new IntVec3(right, 0, z)) == roofTop)) right++;
                        int top = z + 1;
                        while (top < endZ)
                        {
                            bool same = true;
                            for (int column = x; column < right; column++)
                                if (cells[column, top] != material || roofSlab &&
                                    _view.DrawSpatialRoofTop(new IntVec3(column, 0, top)) != roofTop) { same = false; break; }
                            if (!same) break;
                            top++;
                        }
                        if (roofSlab && !roofTop)
                        {
                            if (!_roofUndersideMaterials.TryGetValue(material, out var underside))
                            {
                                underside = new Material(material) { hideFlags = HideFlags.DontSave };
                                underside.SetInt("_Cull", (int)CullMode.Back);
                                _view.materials.Add(underside);
                                _roofUndersideMaterials.Add(material, underside);
                            }
                            material = underside;
                        }
                        var geometry = _view.Batch(material, new Vector3(x, 0, z));
                        if (roofSlab)
                        {
                            float ceiling = _view.SpatialRoofHeight - .12f;
                            if (_view._mountainRoofTypes[x, z] == 0 && roofTop)
                                geometry.Box(new Vector3((x + right) * .5f, ceiling + .06f, (z + top) * .5f),
                                    new Vector3(right - x, .12f, top - z), new Rect(0, 0, 1, 1));
                            else
                                geometry.Quad(new Vector3(x, ceiling, z), new Vector3(right, ceiling, z),
                                    new Vector3(right, ceiling, top), new Vector3(x, ceiling, top), Vector3.down,
                                    Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
                        }
                        else if (material.HasProperty("_WaveHeight") && !Rim3DSettings.Current.SimpleWater)
                            BuildWaterSurface(geometry, x, z, right, top);
                        else geometry.Quad(
                            new Vector3(x, 0, z), new Vector3(x, 0, top),
                            new Vector3(right, 0, top), new Vector3(right, 0, z), Vector3.up,
                            new Vector2(x / 8f, z / 8f), new Vector2(x / 8f, top / 8f),
                            new Vector2(right / 8f, top / 8f), new Vector2(right / 8f, z / 8f));
                        for (int row = z; row < top; row++)
                            for (int column = x; column < right; column++) cells[column, row] = null;
                    }
                }
            }

            internal Geometry Batch(Material material, Vector3 position)
            {
                var key = new Vector2Int(Mathf.FloorToInt(position.x / ChunkSize), Mathf.FloorToInt(position.z / ChunkSize));
                if (!_view.batches.TryGetValue(key, out var chunk))
                    _view.batches.Add(key, chunk = new Dictionary<Material, Geometry>());
                if (!chunk.TryGetValue(material, out var geometry)) chunk.Add(material, geometry = new Geometry());
                return geometry;
            }

            internal GameObject CreateMesh(Material material, Geometry geometry, string name)
            {
                var reused = _view.TakeChunkMesh(material); var mesh = reused != null ? reused.GetComponent<MeshFilter>().sharedMesh : new Mesh { name = "RimVision " + name, indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave }; mesh.Clear();
                mesh.SetVertices(geometry.Vertices); mesh.SetNormals(geometry.Normals); mesh.SetUVs(0, geometry.Uvs); mesh.SetTriangles(geometry.Indices, 0);
                if (geometry.Colors.Count != 0) mesh.SetColors(geometry.Colors);
                mesh.SetUVs(1, geometry.CardCenters);
                bool hasOutlineVolume = geometry.ApplyOutlineData(mesh);
                mesh.RecalculateBounds();
                if (material.HasProperty("_WaveHeight"))
                {
                    var bounds = mesh.bounds;
                    bounds.Expand(new Vector3(0, material.GetFloat("_WaveHeight") * 2, 0));
                    mesh.bounds = bounds;
                }
                if (material.GetFloat("_Billboard") > .5f)
                {
                    // Enclose every yaw of each card, without padding the entire
                    // chunk vertically or adding twelve cells to all three axes.
                    var bounds = mesh.bounds;
                    for (int i = 0; i < geometry.Vertices.Count; i++)
                    {
                        var vertex = geometry.Vertices[i];
                        var center = geometry.CardCenters[i];
                        float radius = Mathf.Abs(vertex.x - center.x);
                        bounds.Encapsulate(new Vector3(center.x - radius, vertex.y, center.z - radius));
                        bounds.Encapsulate(new Vector3(center.x + radius, vertex.y, center.z + radius));
                    }
                    mesh.bounds = bounds;
                }
                if (reused == null) _view.meshes.Add(mesh);
                geometry.ApplyPlantWind(mesh);
                var obj = reused != null ? reused : new GameObject(name) { layer = Layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(_view.geometryParent != null ? _view.geometryParent : _view.root.transform, false);
                var filter = obj.GetComponent<MeshFilter>();
                if (filter == null) filter = obj.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = obj.GetComponent<MeshRenderer>();
                if (renderer == null) renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                // Visible walls and objects clear the receiver bit; only terrain
                // may receive the separate ground-shadow projection.
                material.SetInt("_GroundStencil", material == _view._groundCutoutMaterial ? GroundCutoutStencil : material.GetFloat("_Ground") > .5f ? 64 : 0);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                if (reused != null) renderer.ResetLocalBounds();
                _view.ConfigureMountainLod(filter, renderer, material);
                if (hasOutlineVolume) _view.RegisterOutlineRenderer(renderer);
                else _view._outlines.UnregisterOutlineRenderer(renderer);
                if (reused != null)
                {
                    foreach (var child in obj.GetComponentsInChildren<MeshRenderer>(true))
                        if (child != renderer) _view.UpdateProjectedShadowBounds(renderer, child);
                    if (_view._constructionPlanMaterials.Contains(material) && _view.geometryParent != null && _view.chunkRoots.ContainsValue(_view.geometryParent.gameObject))
                        obj.SetActive(!_view._constructionPlansHidden);
                    return obj;
                }
                if (_view.groundShadowShader != null && _view.groundShadowShader.isSupported && material.GetFloat("_Ground") < .5f && material.GetFloat("_FlatItem") < .5f && material.GetFloat("_Roof") < .5f && !_wallDamageMaterials.ContainsValue(material))
                {
                    if (!_view.groundShadowMaterials.TryGetValue(material, out var groundMaterial))
                    {
                        groundMaterial = new Material(_view.groundShadowShader) { hideFlags = HideFlags.DontSave };
                        groundMaterial.CopyPropertiesFromMaterial(material);
                        // CopyProperties also copies the source render queue. Shadows
                        // must run after all opaque/cutout receivers have written depth
                        // and cleared the ground stencil on walls and other objects.
                        groundMaterial.renderQueue = (int)RenderQueue.Transparent;
                        groundMaterial.SetVector("_ProjectionDirection", _view.sunlight.transform.forward);
                        var direction = _view.sunlight.transform.forward;
                        groundMaterial.SetVector("_ShadowRight", new Vector3(direction.z, 0, -direction.x).normalized);
                        groundMaterial.SetFloat("_GroundY", _view.root.transform.position.y + .012f);
                        _view.materials.Add(groundMaterial);
                        _view.groundShadowMaterials.Add(material, groundMaterial);
                    }
                    var footprint = new GameObject(name + " ground shadow") { layer = Layer, hideFlags = HideFlags.DontSave };
                    footprint.transform.SetParent(obj.transform, false);
                    footprint.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var footprintRenderer = footprint.AddComponent<MeshRenderer>();
                    footprintRenderer.sharedMaterial = groundMaterial;
                    footprintRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    footprintRenderer.receiveShadows = false;
                    footprintRenderer.enabled = _view.shadowsEnabled;
                    _view.projectedShadows.Add(footprintRenderer);
                    _view.UpdateProjectedShadowBounds(renderer, footprintRenderer);
                    // Chunk geometry is stationary; pawn, held-item and door
                    // transforms (and pawn meshes) can change before each render.
                    if (_view.geometryParent == null || !_view.chunkRoots.ContainsValue(_view.geometryParent.gameObject))
                        _view.movingShadows.Add(renderer, footprintRenderer);
                }
                if (_view._constructionPlanMaterials.Contains(material) && _view.geometryParent != null && _view.chunkRoots.ContainsValue(_view.geometryParent.gameObject))
                    obj.SetActive(!_view._constructionPlansHidden);
                return obj;
            }

        }
    }

    internal sealed class Geometry
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Color> Colors = new List<Color>();
        public readonly List<Vector2> Uvs = new List<Vector2>();
        public readonly List<Vector3> CardCenters = new List<Vector3>();
        public readonly List<int> Indices = new List<int>();
        static readonly List<Vector4> _noOutlineCenters = new List<Vector4>();
        static readonly List<Vector4> _noPlantWind = new List<Vector4>();
        readonly List<Vector4> _outlineCenters = new List<Vector4>();
        List<Vector4> _plantWind;
        float _plantWindPadding;

        internal void SetPlantWind(int start, Vector3 root, float flexibility)
        {
            if (flexibility <= 0) return;
            if (_plantWind == null) _plantWind = new List<Vector4>();
            while (_plantWind.Count < Vertices.Count) _plantWind.Add(Vector4.zero);
            var data = new Vector4(root.x, root.y, root.z, -flexibility);
            for (int i = start; i < Vertices.Count; i++)
            {
                _plantWind[i] = data;
                float height = Mathf.Max(0, Vertices[i].y - root.y);
                _plantWindPadding = Mathf.Max(_plantWindPadding,
                    flexibility * height * (.35f + .65f * height / (1 + height)) * 1.2f);
            }
        }

        internal void ApplyPlantWind(Mesh mesh)
        {
            if (_plantWind == null)
            {
                if (_noPlantWind.Count > Vertices.Count)
                    _noPlantWind.RemoveRange(Vertices.Count, _noPlantWind.Count - Vertices.Count);
                while (_noPlantWind.Count < Vertices.Count) _noPlantWind.Add(Vector4.zero);
                mesh.SetUVs(3, _noPlantWind);
                return;
            }
            while (_plantWind.Count < Vertices.Count) _plantWind.Add(Vector4.zero);
            mesh.SetUVs(3, _plantWind);
            var bounds = mesh.bounds;
            bounds.Expand(new Vector3(_plantWindPadding * 2, 0, _plantWindPadding * 2));
            mesh.bounds = bounds;
        }

        internal Bounds GetBounds()
        {
            if (Vertices.Count == 0) return new Bounds();
            var bounds = new Bounds(Vertices[0], Vector3.zero);
            for (int i = 1; i < Vertices.Count; i++) bounds.Encapsulate(Vertices[i]);
            return bounds;
        }

        internal void SetOutlineBounds(int start, Bounds bounds, float orientation = 1)
        {
            if (_outlineCenters.Count > Vertices.Count) _outlineCenters.RemoveRange(Vertices.Count, _outlineCenters.Count - Vertices.Count);
            while (_outlineCenters.Count < Vertices.Count) _outlineCenters.Add(Vector4.zero);
            var center = bounds.center;
            var size = bounds.size;
            float radius = Mathf.Min(size.x, Mathf.Min(size.y, size.z)) > .00001f ? bounds.extents.magnitude : 0;
            var value = new Vector4(center.x, center.y, center.z, radius * orientation);
            for (int i = start; i < Vertices.Count; i++) _outlineCenters[i] = value;
        }

        internal void TransformOutlineCenters(Vector3 origin, float scale)
        {
            for (int i = 0; i < _outlineCenters.Count; i++)
            {
                var value = _outlineCenters[i];
                var center = (new Vector3(value.x, value.y, value.z) - origin) * scale;
                _outlineCenters[i] = new Vector4(center.x, center.y, center.z, value.w * Mathf.Abs(scale));
            }
        }

        List<Vector4> GetOutlineCenters()
        {
            if (_outlineCenters.Count == 0) SetOutlineBounds(0, GetBounds());
            while (_outlineCenters.Count < Vertices.Count) _outlineCenters.Add(Vector4.zero);
            return _outlineCenters;
        }

        internal bool ApplyOutlineData(Mesh mesh)
        {
            var centers = GetOutlineCenters();
            for (int i = 0; i < centers.Count; i++)
                if (centers[i].w != 0)
                {
                    mesh.SetUVs(2, centers);
                    return true;
                }
            mesh.SetUVs(2, _noOutlineCenters);
            return false;
        }

        public void Append(Geometry source, Vector3 center, Quaternion rotation)
        {
            int start = Vertices.Count;
            var outlineCenters = source.GetOutlineCenters();
            if (_outlineCenters.Count > start) _outlineCenters.RemoveRange(start, _outlineCenters.Count - start);
            while (_outlineCenters.Count < start) _outlineCenters.Add(Vector4.zero);
            bool copyColors = Colors.Count != 0 || source.Colors.Count != 0;
            if (copyColors) while (Colors.Count < start) Colors.Add(Color.white);
            for (int i = 0; i < source.Vertices.Count; i++)
            {
                Vertices.Add(center + rotation * source.Vertices[i]);
                Normals.Add(rotation * source.Normals[i]);
                Uvs.Add(source.Uvs[i]);
                CardCenters.Add(Vector3.zero);
                var outline = outlineCenters[i];
                var outlineCenter = center + rotation * new Vector3(outline.x, outline.y, outline.z);
                _outlineCenters.Add(new Vector4(outlineCenter.x, outlineCenter.y, outlineCenter.z, outline.w));
                if (copyColors) Colors.Add(i < source.Colors.Count ? source.Colors[i] : Color.white);
            }
            foreach (int index in source.Indices) Indices.Add(start + index);
        }
        public void OutlineLine(Vector3 a, Vector3 b, float width)
        {
            var direction = b - a;
            if (direction.sqrMagnitude < 1e-8f) return;
            direction.Normalize();
            var side = Vector3.Cross(direction, Mathf.Abs(direction.y) < .9f ? Vector3.up : Vector3.right).normalized;
            var up = Vector3.Cross(side, direction).normalized;
            var s = side * (width * .5f);
            var u = up * (width * .5f);
            var uv = new Rect(0, 0, 1, 1);
            Face(a+s-u, b+s-u, b+s+u, a+s+u, side, uv);
            Face(a-s+u, b-s+u, b-s-u, a-s-u, -side, uv);
            Face(a+s+u, b+s+u, b-s+u, a-s+u, up, uv);
            Face(a-s-u, b-s-u, b+s-u, a+s-u, -up, uv);
        }
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            int i = Vertices.Count;
            if (_outlineCenters.Count > i) _outlineCenters.RemoveRange(i, _outlineCenters.Count - i);
            Vertices.Add(a); Vertices.Add(b); Vertices.Add(c); Vertices.Add(d);
            for (int n = 0; n < 4; n++) Normals.Add(normal);
            for (int n = 0; n < 4; n++) CardCenters.Add(Vector3.zero);
            Uvs.Add(ua); Uvs.Add(ub); Uvs.Add(uc); Uvs.Add(ud);
            Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2); Indices.Add(i); Indices.Add(i + 2); Indices.Add(i + 3);
        }
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Rect uv)
        { Quad(a, b, c, d, normal, new Vector2(uv.xMin, uv.yMin), new Vector2(uv.xMin, uv.yMax), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMax, uv.yMin)); }
        public void Box(Vector3 center, Vector3 size, Rect uv)
        { Box(center, size, uv, uv, 15, false); }
        public void Box(Vector3 center, Vector3 size, Rect sideUv, Rect topUv, int faces, bool repeatSides = true, bool includeTop = true)
        {
            int start = Vertices.Count;
            float x = center.x - size.x / 2, X = center.x + size.x / 2;
            float z = center.z - size.z / 2, Z = center.z + size.z / 2, y = center.y, Y = y + size.y;
            if ((faces & 1) != 0) WallFace(new Vector3(x,y,z),new Vector3(X,y,z),size.y,Vector3.back,sideUv,repeatSides);
            if ((faces & 2) != 0) WallFace(new Vector3(X,y,Z),new Vector3(x,y,Z),size.y,Vector3.forward,sideUv,repeatSides);
            if ((faces & 4) != 0) WallFace(new Vector3(x,y,Z),new Vector3(x,y,z),size.y,Vector3.left,sideUv,repeatSides);
            if ((faces & 8) != 0) WallFace(new Vector3(X,y,z),new Vector3(X,y,Z),size.y,Vector3.right,sideUv,repeatSides);
            if (includeTop) Face(new Vector3(x,Y,z),new Vector3(x,Y,Z),new Vector3(X,Y,Z),new Vector3(X,Y,z),Vector3.up,topUv);
            SetOutlineBounds(start, new Bounds(center + Vector3.up * (size.y * .5f), size));
        }
        void WallFace(Vector3 left, Vector3 right, float height, Vector3 normal, Rect uv, bool repeat)
        {
            if (!repeat)
            {
                Face(left, left + Vector3.up * height, right + Vector3.up * height, right, normal, uv);
                return;
            }
            // One atlas tile per metre, without repeating across neighboring atlas cells.
            for (float bottom = 0; bottom < height; bottom += 1)
            {
                float top = Mathf.Min(bottom + 1, height);
                var slice = new Rect(uv.xMin, uv.yMin, uv.width, uv.height * (top - bottom));
                Face(left + Vector3.up * bottom, left + Vector3.up * top,
                    right + Vector3.up * top, right + Vector3.up * bottom, normal, slice);
            }
        }
        public void FloorCard(Vector3 center, float width, float depth)
        { Face(center+new Vector3(-width/2,0,-depth/2),center+new Vector3(-width/2,0,depth/2),center+new Vector3(width/2,0,depth/2),center+new Vector3(width/2,0,-depth/2),Vector3.up,new Rect(0,0,1,1)); }
        public void RotatedFloorCard(Vector3 center, float width, float depth, float angle)
        {
            int start = Vertices.Count;
            FloorCard(center, width, depth);
            var rotation = Quaternion.Euler(0, angle, 0);
            for (int i = start; i < Vertices.Count; i++) Vertices[i] = center + rotation * (Vertices[i] - center);
        }
        public void DiagonalCards(Vector3 center, float width, float height, float angle)
        {
            int start = Vertices.Count;
            var rotation = Quaternion.Euler(0, angle, 0);
            var up = Vector3.up * height;
            var uv = new Rect(0, 0, 1, 1);
            var right = rotation * new Vector3(.70710678f, 0, .70710678f) * (width * .5f);
            var normal = rotation * new Vector3(.70710678f, 0, -.70710678f);
            Face(center - right, center - right + up, center + right + up, center + right, normal, uv);
            right = rotation * new Vector3(.70710678f, 0, -.70710678f) * (width * .5f);
            normal = rotation * new Vector3(-.70710678f, 0, -.70710678f);
            Face(center - right, center - right + up, center + right + up, center + right, normal, uv);
            SetOutlineBounds(start, new Bounds());
        }
        public void Upright(Vector3 center, float width, float height, bool crossed)
        {
            var right = (crossed ? Vector3.forward : Vector3.right) * width / 2;
            Face(center-right,center-right+Vector3.up*height,center+right+Vector3.up*height,center+right,crossed?Vector3.right:Vector3.back,new Rect(0,0,1,1));
            for (int i = Vertices.Count - 4; i < Vertices.Count; i++) CardCenters[i] = center;
        }
    }
}





