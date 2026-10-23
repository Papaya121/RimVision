using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        bool statusMenu;

        static readonly string[] StatusLabels = { "Rim3D_TabHealth", "Rim3D_TabSkills", "Rim3D_TabInventory", "Rim3D_TabMood", "Rim3D_TabSocial" };
        MainTabWindow colonyWindow;

        public bool PausedByPlayer { get; private set; }
        public bool StatusMenuOpen => FirstPerson && !controller.InputBlocked && statusMenu;
        bool ColonyTabOpen => Find.MainTabsRoot.OpenTab != null;
        bool ExternalInputBlocked => ColonyTabOpen || ExternalDialogInputBlocked;
        bool ExternalDialogInputBlocked
        {
            get
            {
                if (WorldRendererUtility.WorldSelected) return true;
                if (_apiExtensions?.InputBlocked == true) return true;
                foreach (var window in Find.WindowStack.Windows)
                    if (!(window is PreviewWindow) && window != colonyWindow &&
                        (window.forcePause || window.preventCameraMotion || window is FloatMenu)) return true;
                return false;
            }
        }

        public void OpenGameMenu() => _actionMenus.OpenGameMenu();

        void OpenColonyTab(MainButtonDef tab) => _actionMenus.OpenColonyTab(tab);

        void RefreshColonyTab() => _actionMenus.RefreshColonyTab();

        void CloseColonyTab() => _actionMenus.CloseColonyTab();
        float stepPhase;
        float heartbeatPhase;
        float hurtFlash;
        float damageKick;
        float oldWounds;
        float oldHealth = 1;
        float nextHealthSample;
        float pain;
        float _screenPain;
        float health = 1;
        Vector3 cameraBob;
        Texture2D injuryVignette;
        float controlLossBlend;
        float hungerEffect, fatigueEffect;
        float hungerTarget, fatigueTarget;
        float needsEffectPhase;

        AudioSource heartbeatAudio;
        AudioClip heartbeatClip;

        const float FallDuration = .85f;
        const float BlackoutDuration = .8f;
        const float BlackoutHold = .15f;
        const float OverviewRevealDuration = .4f;
        bool falling;
        float fallStarted;
        float overviewRevealStarted = -1;
        Vector3 fallPosition;
        Vector3 fallenPosition;
        Quaternion fallRotation;
        Quaternion fallenRotation;
        public bool FallPresentationActive => falling || overviewRevealStarted >= 0;

        bool UpdateFallTransition()
        {
            var pawn = ControlledPawn;
            if (!falling && pawn != null && (pawn.Dead || Rim3DMultiplayer.PawnLocked &&
                (pawn.Downed || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))))
            {
                falling = true;
                fallStarted = Time.unscaledTime;
                fallPosition = CameraMapPosition;
                fallRotation = camera.transform.rotation;
                var backward = Quaternion.Euler(0, yaw, 0) * Vector3.back;
                fallenPosition = fallPosition + backward * .45f;
                fallenPosition.x = Mathf.Clamp(fallenPosition.x, .2f, map.Size.x - .2f);
                fallenPosition.z = Mathf.Clamp(fallenPosition.z, .2f, map.Size.z - .2f);
                fallenPosition.y = .18f;
                fallenRotation = Quaternion.Euler(-86, yaw, 5);
                constructionMenu = actionMenu = statusMenu = false;
                constructionDef = null;
                buildTool = null;
                if (placementPreview != null) placementPreview.SetActive(false);
                controller.Sneaking = controller.Sprinting = false;
                heartbeatAudio?.Stop();
                ReleaseMouse();
            }
            if (falling)
            {
                if (Time.unscaledTime - fallStarted >= FallDuration + BlackoutDuration + BlackoutHold)
                {
                    _viewCamera.SetGroundFocus(fallenPosition);
                    controller.Dispose();
                    falling = false;
                    _viewCamera.SetPitchAndDistance(48, 24);
                    overviewRevealStarted = Time.unscaledTime;
                }
                PositionCamera();
                UpdateCombatEffects();
                return true;
            }
            if (overviewRevealStarted >= 0)
            {
                if (Time.unscaledTime - overviewRevealStarted >= OverviewRevealDuration)
                    overviewRevealStarted = -1;
                PositionCamera();
                UpdateCombatEffects();
                return true;
            }
            return false;
        }

        public void DrawFallTransition(Rect rect) => _hudPresentation.DrawFallTransition(rect);

        void ResetImmersion(Pawn pawn)
        {
            _viewCamera.ResetEyeHeight(); stepPhase = heartbeatPhase = hurtFlash = damageKick = 0;
            cameraBob = Vector3.zero;
            controlLossBlend = 0;
            hungerEffect = fatigueEffect = hungerTarget = fatigueTarget = needsEffectPhase = 0;
            oldWounds = Wounds(pawn);
            oldHealth = health = pawn.health.summaryHealth.SummaryHealthPercent;
            pain = pawn.health.hediffSet.PainTotal;
            _screenPain = ScreenPain(pawn);
            nextHealthSample = 0;
        }

        static float Wounds(Pawn pawn)
        {
            float total = 0;
            foreach (var hediff in pawn.health.hediffSet.hediffs)
                if (hediff is Hediff_Injury) total += hediff.Severity;
            return total;
        }

        static float ScreenPain(Pawn pawn)
        {
            if (Rim3DSettings.Current.ScarPainEffects) return pawn.health.hediffSet.PainTotal;
            if (!pawn.RaceProps.IsFlesh || pawn.Dead) return 0;
            float total = 0;
            bool hasScarPain = false;
            var hediffs = pawn.health.hediffSet.hediffs;
            foreach (var hediff in hediffs)
            {
                if (hediff is Hediff_Injury && hediff.IsPermanent())
                    hasScarPain |= hediff.PainOffset != 0;
                else total += hediff.PainOffset;
            }
            if (!hasScarPain) return pawn.health.hediffSet.PainTotal;
            if (pawn.genes != null) total += pawn.genes.PainOffset;
            var traits = pawn.story?.traits?.allTraits;
            if (traits != null)
                foreach (var trait in traits) total += trait.CurrentData.painOffset;
            foreach (var hediff in hediffs) total *= hediff.PainFactor;
            if (pawn.genes != null) total *= pawn.genes.PainFactor;
            if (traits != null)
                foreach (var trait in traits) total *= trait.CurrentData.painFactor;
            return Mathf.Clamp01(total);
        }

        void UpdateImmersion(Vector3 previousPosition, float dt)
        {
            var pawn = ControlledPawn;
            if (pawn == null) return;
            controlLossBlend = Mathf.MoveTowards(controlLossBlend, controller.InputBlocked ? 1 : 0, dt * 2);
            float moved = Vector3.Distance(previousPosition, controller.Position);
            stepPhase += moved * (controller.Sneaking ? 9 : 7);
            var bob = moved > .0001f ? new Vector3(Mathf.Sin(stepPhase * .5f) * .012f,
                Mathf.Sin(stepPhase) * (controller.Sneaking ? .007f : controller.Sprinting ? .025f : .013f), 0) : Vector3.zero;
            var settings = Rim3DSettings.Current;
            bob *= controller.Sprinting && !controller.Sneaking ? settings.RunCameraBobIntensity : settings.WalkCameraBobIntensity;
            cameraBob = Vector3.Lerp(cameraBob, Quaternion.Euler(0, yaw, 0) * bob, 1 - Mathf.Exp(-dt * 12));
            if (Time.unscaledTime >= nextHealthSample)
            {
                var food = pawn.needs?.food;
                var rest = pawn.needs?.rest;
                hungerTarget = food == null || food.CurCategory == HungerCategory.Fed ? 0 :
                    Mathf.Lerp(.18f, 1, Mathf.InverseLerp(.3f, 0, food.CurLevelPercentage));
                fatigueTarget = rest == null || rest.CurCategory == RestCategory.Rested ? 0 :
                    Mathf.Lerp(.18f, 1, Mathf.InverseLerp(.28f, 0, rest.CurLevelPercentage));
                float wounds = Wounds(pawn);
                health = pawn.health.summaryHealth.SummaryHealthPercent;
                pain = pawn.health.hediffSet.PainTotal;
                _screenPain = ScreenPain(pawn);
                float damage = Mathf.Max(wounds - oldWounds, (oldHealth - health) * 50);
                if (damage > .01f)
                {
                    hurtFlash = Mathf.Clamp(.25f + damage * .035f, .25f, 1);
                    damageKick = Mathf.Min(2, .4f + damage * .06f);
                    SayAction("Rim3D_WoundedHint".TranslateBindings().ToString());
                }
                oldWounds = wounds; oldHealth = health;
                nextHealthSample = Time.unscaledTime + .1f;
            }
            hurtFlash = Mathf.MoveTowards(hurtFlash, 0, dt * 1.5f);
            damageKick = Mathf.MoveTowards(damageKick, 0, dt * 7);
            hungerEffect = Mathf.MoveTowards(hungerEffect, hungerTarget, dt * .45f);
            fatigueEffect = Mathf.MoveTowards(fatigueEffect, fatigueTarget, dt * .35f);
            if (!Find.TickManager.Paused) needsEffectPhase = Mathf.Repeat(needsEffectPhase + dt * .8f, Mathf.PI * 12);
            float oldBeat = heartbeatPhase;
            if (!Find.TickManager.Paused)
                heartbeatPhase += dt * Mathf.Lerp(1, 2.2f, Mathf.Clamp01(pain + 1 - health + (1 - controller.Stamina) * .5f));
            float stress = Mathf.Clamp01(pain * .55f + (1 - health) * .65f);
            if (!Rim3DSettings.Current.Heartbeat && heartbeatAudio != null) heartbeatAudio.Stop();
            if (Rim3DSettings.Current.Heartbeat && stress > .15f && Mathf.FloorToInt(oldBeat) != Mathf.FloorToInt(heartbeatPhase))
            {
                if (heartbeatAudio == null)
                {
                    var audioObject = new GameObject("RimVision heartbeat") { hideFlags = HideFlags.DontSave };
                    audioObject.transform.SetParent(root.transform, false);
                    heartbeatAudio = audioObject.AddComponent<AudioSource>();
                    heartbeatAudio.spatialBlend = 0; heartbeatAudio.playOnAwake = false;
                    const int rate = 22050;
                    var samples = new float[rate / 3];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        float t = i / (float)rate;
                        float first = Mathf.Sin(t * 2 * Mathf.PI * 52) * Mathf.Exp(-t * 30) * Mathf.Min(1, t * 300);
                        float secondTime = t - .14f;
                        float second = secondTime > 0 ? Mathf.Sin(secondTime * 2 * Mathf.PI * 64) * Mathf.Exp(-secondTime * 35) * Mathf.Min(1, secondTime * 300) * .6f : 0;
                        samples[i] = (first + second) * .6f;
                    }
                    heartbeatClip = AudioClip.Create("RimVision heartbeat", samples.Length, 1, rate, false);
                    heartbeatClip.SetData(samples, 0);
                }
                heartbeatAudio.PlayOneShot(heartbeatClip, stress * .3f);
            }
        }

        public void DrawImmersion(Rect viewport) => _hudPresentation.DrawImmersion(viewport);

        static int StatusTabForKey(KeyCode key)
        {
            return ViewBindings.Matches(ViewAction.Character, key) ? 2 : -1;
        }

        void SelectStatusTab(int tab) => _hudPresentation.SelectStatusTab(tab);

        void ToggleStatusTab(int tab) => _hudPresentation.ToggleStatusTab(tab);

        void DrawStatusMenu(Rect viewport) => _hudPresentation.DrawStatusMenu(viewport);

        void DrawStatusPanel(Rect viewport) => _hudPresentation.DrawStatusPanel(viewport);

        void OpenNeedsHudMenu() => _hudPresentation.OpenNeedsHudMenu();

        static bool HealthOperationsAvailable(Pawn pawn)
        {
            if (pawn.Dead || pawn.Faction != Faction.OfPlayer ||
                (pawn.IsMutant && !pawn.mutant.Def.entitledToMedicalCare)) return false;
            foreach (var recipe in pawn.def.AllRecipes)
                if (recipe.AvailableNow && recipe.AvailableOnNow(pawn, null)) return true;
            return false;
        }


        float nextGearSummary;


        static float OverallArmor(Pawn pawn, StatDef stat)
        {
            float armor = 0;
            float natural = Mathf.Clamp01(pawn.GetStatValue(stat) * .5f);
            var worn = pawn.apparel?.WornApparel;
            foreach (var part in pawn.RaceProps.body.AllParts)
            {
                float unprotected = 1 - natural;
                if (worn != null)
                    foreach (var item in worn)
                        if (item.def.apparel.CoversBodyPart(part))
                            unprotected *= 1 - Mathf.Clamp01(item.GetStatValue(stat) * .5f);
                armor += part.coverageAbs * (1 - unprotected);
            }
            return Mathf.Clamp(armor * 2, 0, 2);
        }

        void DrawInventoryMenu(Rect list, Pawn pawn) => _hudPresentation.DrawInventoryMenu(list, pawn);

        static void DrawStatusSection(float width, ref float y, string title, Color color)
        {
            var oldColor = GUI.color;
            var oldFont = Text.Font;
            GUI.color = color;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(6,y + 8,width - 12,26), title);
            GUI.color = oldColor;
            Text.Font = oldFont;
            Widgets.DrawBoxSolid(new Rect(0,y + 35,width,1), new Color(.25f,.3f,.35f));
            y += 40;
        }

        static string IngestLabel(Thing food) => food.def.IsDrug ? "Rim3D_ConsumeDrug".Translate().ToString() : "Rim3D_Eat".Translate().ToString();

        bool CanIngestFPV(Thing food) => _ingestActions.CanIngestFPV(food);

        void IngestFPV(Thing food) => _ingestActions.IngestFPV(food);

        void DisposeImmersion()
        {
            CloseColonyTab();
            if (injuryVignette != null) UnityEngine.Object.Destroy(injuryVignette);
            if (heartbeatClip != null) UnityEngine.Object.Destroy(heartbeatClip);
            if (heartbeatAudio != null) UnityEngine.Object.Destroy(heartbeatAudio.gameObject);
            injuryVignette = null;
        }

        sealed partial class HudPresentation
        {
            static readonly string[] ColonyTabs = { "Architect", "Work", "Schedule", "Assign", "Animals", "Mechs", "Wildlife", "Research", "Quests", "World", "History", "Factions", "Ideos", "Menu" };

            int _statusTab;

            Vector2 _statusScroll;
            Vector2 _colonyScroll;

            internal void DrawFallTransition(Rect rect)
            {
                if (Event.current.type != EventType.Repaint) return;
                float alpha = _view.falling
                    ? Mathf.SmoothStep(0, 1, Mathf.Clamp01((Time.unscaledTime - _view.fallStarted - FallDuration) / BlackoutDuration))
                    : 1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01((Time.unscaledTime - _view.overviewRevealStarted) / OverviewRevealDuration));
                Widgets.DrawBoxSolid(rect, new Color(0, 0, 0, alpha));
            }

            internal void DrawImmersion(Rect viewport)
            {
                if (!_view.FirstPerson || (Event.current != null && Event.current.type != EventType.Repaint)) return;
                var settings = Rim3DSettings.Current;
                bool effects = settings.EffectsEnabled(settings.StatusEffects);
                float phase = Mathf.Repeat(_view.heartbeatPhase, 1);
                float beat = Mathf.Exp(-phase * 28) + .55f * Mathf.Exp(-Mathf.Abs(phase - .18f) * 38);
                if (effects)
                {
                    if (_view.injuryVignette == null)
                    {
                        _view.injuryVignette = new Texture2D(96, 96, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave,
                            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                        for (int y = 0; y < 96; y++) for (int x = 0; x < 96; x++)
                        {
                            float edge = Mathf.Max(Mathf.Abs(x / 95f * 2 - 1), Mathf.Abs(y / 95f * 2 - 1));
                            _view.injuryVignette.SetPixel(x, y, new Color(1, 1, 1, Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.45f, 1, edge))));
                        }
                        _view.injuryVignette.Apply(false, true);
                    }
                    var oldColor = GUI.color;
                    float hunger = _view.hungerEffect * settings.HungerEffectIntensity;
                    float fatigue = _view.fatigueEffect * settings.SleepEffectIntensity;
                    if (hunger > 0)
                    {
                        // Reuse the injury mask: no extra texture, camera or post-process.
                        float pulse = .5f + .5f * Mathf.Sin(_view.needsEffectPhase * 2);
                        GUI.color = new Color(.36f, .22f, .035f, hunger * (.18f + pulse * .15f));
                        GUI.DrawTexture(viewport, _view.injuryVignette);
                    }
                    if (fatigue > 0)
                    {
                        GUI.color = new Color(.025f, .035f, .075f, fatigue * .5f);
                        GUI.DrawTexture(viewport, _view.injuryVignette);
                        GUI.color = oldColor;
                        // Slow, partial eyelid droops leave the centre visible even at exhaustion.
                        float droop = Mathf.Pow(Mathf.Max(0, Mathf.Sin(_view.needsEffectPhase)), 8);
                        float cover = viewport.height * fatigue * (.025f + droop * .17f);
                        var eyelid = new Color(.015f, .02f, .035f, fatigue * .65f);
                        Widgets.DrawBoxSolid(new Rect(viewport.x, viewport.y, viewport.width, cover), eyelid);
                        Widgets.DrawBoxSolid(new Rect(viewport.x, viewport.yMax - cover, viewport.width, cover), eyelid);
                    }
                    float screenDistress = Mathf.Clamp01(_view._screenPain * .55f + (1 - _view.health) * .65f);
                    GUI.color = new Color(.42f, .015f, .02f,
                        Mathf.Clamp01(_view.hurtFlash * .85f + screenDistress * (.18f + beat * .48f)) * settings.PainEffectIntensity);
                    GUI.DrawTexture(viewport, _view.injuryVignette);
                    if (_view.controlLossBlend > 0)
                    {
                        float pulse = .5f + .5f * Mathf.Sin(_view.heartbeatPhase * Mathf.PI * 2);
                        GUI.color = new Color(.12f, .06f, .18f, _view.controlLossBlend * (.58f + pulse * .2f));
                        GUI.DrawTexture(viewport, _view.injuryVignette);
                        Widgets.DrawBoxSolid(viewport, new Color(.06f, .025f, .09f, _view.controlLossBlend * (.07f + pulse * .04f)));
                    }
                    GUI.color = oldColor;
                }
                if (effects && _view._viewCamera.SleepEyelidBlend > 0 && settings.SleepEffectIntensity > 0)
                {
                    float cover = viewport.height * .5f * Mathf.SmoothStep(0, 1, _view._viewCamera.SleepEyelidBlend);
                    var eyelid = new Color(0, 0, 0, settings.SleepEffectIntensity);
                    Widgets.DrawBoxSolid(new Rect(viewport.x, viewport.y, viewport.width, cover), eyelid);
                    Widgets.DrawBoxSolid(new Rect(viewport.x, viewport.yMax - cover, viewport.width, cover), eyelid);
                }
            }

            internal void SelectStatusTab(int tab)
            {
                if (_statusTab == tab) return;
                _statusTab = tab;
                _statusScroll = Vector2.zero;
            }

            internal void ToggleStatusTab(int tab)
            {
                _view.CloseColonyTab();
                bool close = _view.statusMenu;
                if (!close) _view.SelectStatusTab(tab);
                _view.statusMenu = !close;
                _view.constructionMenu = _view.actionMenu = false;
                _view.ReleaseMouse();
            }

            internal void DrawStatusMenu(Rect viewport)
            {
                var font = Text.Font;
                var anchor = Text.Anchor;
                bool wrap = Text.WordWrap;
                var color = GUI.color;
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
                GUI.color = Color.white;
                try { _view.DrawStatusPanel(viewport); }
                finally
                {
                    Text.Font = font;
                    Text.Anchor = anchor;
                    Text.WordWrap = wrap;
                    GUI.color = color;
                }
            }

            internal void DrawStatusPanel(Rect viewport)
            {
                var pawn = _view.ControlledPawn;
                if (pawn == null) return;
                float width = Mathf.Min(960, viewport.width - 32);
                float height = Mathf.Min(700, viewport.height - 64);
                var panel = new Rect(viewport.center.x - width * .5f, viewport.center.y - height * .5f, width, height);
                Widgets.DrawBoxSolid(panel, new Color(.07f,.09f,.12f,1));
                Widgets.Label(new Rect(panel.x + 16, panel.y + 10, panel.width - 70, 28), pawn.LabelShort + " · " + StatusLabels[_statusTab].Translate().ToString());
                if (Widgets.ButtonText(new Rect(panel.xMax - 42, panel.y + 6, 30, 28), "×")) { _view.statusMenu = false; return; }
                Widgets.Label(new Rect(panel.x + 16, panel.y + 40, panel.width - 32, 28),
                    pawn.needs?.mood != null
                        ? "Rim3D_CharacterSummaryMood".Translate(Mathf.RoundToInt(_view.health * 100),
                            Mathf.RoundToInt(pawn.needs.mood.CurLevelPercentage * 100)).ToString()
                        : "Rim3D_CharacterSummary".Translate(Mathf.RoundToInt(_view.health * 100)).ToString());
                float sidebar = panel.width >= 760 ? 172 : 138;
                float navY = panel.y + 82;
                for (int i = 0; i < StatusLabels.Length; i++)
                {
                    var button = new Rect(panel.x + 12, navY, sidebar - 12, 38);
                    if (_statusTab == i) Widgets.DrawBoxSolid(button.ExpandedBy(2), new Color(.3f,.6f,.65f));
                    if (Widgets.ButtonText(button, StatusLabels[i].Translate().ToString())) _view.SelectStatusTab(i);
                    navY += 44;
                }
                Widgets.Label(new Rect(panel.x + 16, navY + 12, sidebar - 20, 28), "Rim3D_Colony".Translate().ToString());
                navY += 46;
                int visibleTabs = 0;
                for (int i = 0; i < ColonyTabs.Length; i++)
                    if (DefDatabase<MainButtonDef>.GetNamedSilentFail(ColonyTabs[i])?.Worker.Visible == true) visibleTabs++;
                var navigation = new Rect(panel.x + 12, navY, sidebar - 12, Mathf.Max(0, panel.yMax - 16 - navY));
                var navigationContent = new Rect(0, 0, navigation.width - 16, visibleTabs * 34);
                MainButtonDef selectedTab = null;
                Widgets.BeginScrollView(navigation, ref _colonyScroll, navigationContent);
                bool enabled = GUI.enabled;
                try
                {
                    float y = 0;
                    for (int i = 0; i < ColonyTabs.Length; i++)
                    {
                        var tab = DefDatabase<MainButtonDef>.GetNamedSilentFail(ColonyTabs[i]);
                        if (tab == null || !tab.Worker.Visible) continue;
                        var button = new Rect(0, y, navigationContent.width, 30);
                        GUI.enabled = enabled && !tab.Worker.Disabled;
                        if (Widgets.ButtonText(button, tab.LabelCap.ToString())) selectedTab = tab;
                        TooltipHandler.TipRegion(button, tab.description);
                        y += 34;
                    }
                }
                finally
                {
                    GUI.enabled = enabled;
                    Widgets.EndScrollView();
                }
                if (selectedTab != null) { _view.OpenColonyTab(selectedTab); return; }
                var list = new Rect(panel.x + sidebar + 16, panel.y + 82, panel.width - sidebar - 32, panel.height - 98);
                Widgets.DrawBoxSolid(new Rect(list.x - 9, list.y, 1, list.height), new Color(.25f,.3f,.35f));
                if (_statusTab == 3)
                {
                    if (pawn.needs == null) Widgets.Label(list, "Rim3D_NoNeeds".Translate().ToString());
                    else
                    {
                        if (Widgets.ButtonText(new Rect(list.x, list.y, list.width, 30f),
                            "Rim3D_SettingAdditionalHudNeeds".Translate())) _view.OpenNeedsHudMenu();
                        list.yMin += 38f;
                        // The native needs layout accumulates row yMax values and
                        // requires local coordinates starting at zero.
                        Widgets.BeginGroup(list);
                        try
                        {
                            NeedsCardUtility.DoNeedsMoodAndThoughts(new Rect(0, 0, list.width, list.height), pawn, ref _statusScroll);
                        }
                        finally { Widgets.EndGroup(); }
                    }
                    return;
                }
                if (_statusTab == 4)
                {
                    if (pawn.relations == null) Widgets.Label(list, "Rim3D_NoRelations".Translate().ToString());
                    else SocialCardUtility.DrawSocialCard(list, pawn);
                    return;
                }
                if (_statusTab == 2) { _view.DrawInventoryMenu(list, pawn); return; }
                if (_statusTab == 1)
                {
                    var skillContent = new Rect(0, 0, list.width - 18, 40 + (pawn.skills?.skills.Count ?? 0) * 42);
                    Widgets.BeginScrollView(list, ref _statusScroll, skillContent);
                    float skillY = 0;
                    DrawStatusSection(skillContent.width, ref skillY, "Rim3D_SkillsHeading".Translate().ToString(), new Color(1,.8f,.45f));
                    if (pawn.skills != null)
                        foreach (var skill in pawn.skills.skills)
                        {
                            var row = new Rect(0,skillY,skillContent.width,38);
                            Widgets.DrawBoxSolid(row, new Color(.10f,.13f,.16f));
                            string passion = skill.passion == Passion.Major ? " · ★★" : skill.passion == Passion.Minor ? " · ★" : "";
                            Widgets.Label(new Rect(8,skillY + 2,skillContent.width - 16,24), skill.def.LabelCap + " · " + skill.Level + "/20" + passion);
                            Widgets.FillableBar(new Rect(8,skillY + 28,skillContent.width - 16,5), Mathf.Clamp01(skill.XpProgressPercent));
                            TooltipHandler.TipRegion(row, skill.def.description + "Rim3D_SkillExperience".Translate(Mathf.FloorToInt(skill.xpSinceLastLevel), Mathf.CeilToInt(skill.XpRequiredForLevelUp)).ToString() + (skill.TotallyDisabled ? "Rim3D_SkillDisabled".Translate().ToString() : ""));
                            skillY += 42;
                        }
                    Widgets.EndScrollView();
                    return;
                }

                Widgets.BeginGroup(list);
                try
                {
                    HealthCardUtility.DrawPawnHealthCard(new Rect(0, 0, list.width, list.height), pawn,
                        HealthOperationsAvailable(pawn), HealthCardUtility.ShowBloodLoss(pawn), pawn);
                }
                finally { Widgets.EndGroup(); }
            }

            internal void OpenNeedsHudMenu()
            {
                var preferences = Current.Game?.GetComponent<NeedsHudPreferences>();
                if (preferences == null) return;
                var options = new List<FloatMenuOption>();
                for (int group = 0; group < 2; group++)
                {
                    foreach (var def in DefDatabase<NeedDef>.AllDefsListForReading)
                    {
                        if ((!def.showOnNeedList && !typeof(Need_Mood).IsAssignableFrom(def.needClass)) ||
                            def == NeedDefOf.Food || def == NeedDefOf.Rest) continue;
                        bool chemical = typeof(Need_Chemical).IsAssignableFrom(def.needClass) ||
                            typeof(Need_Chemical_Any).IsAssignableFrom(def.needClass);
                        if (chemical != (group == 1)) continue;
                        bool selected = preferences.ShowAdditionalHudNeed(def);
                        string label = (selected ? "Rim3D_RemoveHudNeed" : "Rim3D_AddHudNeed").Translate(def.LabelCap);
                        options.Add(new FloatMenuOption(label, () =>
                        {
                            preferences.SetAdditionalHudNeed(def, !selected);
                        }));
                    }
                }
                if (options.Count == 0)
                    options.Add(new FloatMenuOption("Rim3D_NoAdditionalHudNeeds".Translate(), null));
                Find.WindowStack.Add(new FloatMenu(options));
            }

            Pawn _gearSummaryPawn;

            string _gearSummary;

            internal void DrawInventoryMenu(Rect list, Pawn pawn)
            {
                var inventory = pawn.inventory?.innerContainer;
                int items = inventory?.Count ?? 0;
                var carried = pawn.carryTracker?.CarriedThing;
                var weapon = pawn.equipment?.Primary;
                var apparel = pawn.apparel?.WornApparel;
                int apparelCount = apparel?.Count ?? 0;
                if (_gearSummaryPawn != pawn || Time.unscaledTime >= _view.nextGearSummary)
                {
                    _gearSummaryPawn = pawn;
                    _view.nextGearSummary = Time.unscaledTime + .5f;
                    var range = GenTemperature.ComfortableTemperatureRange(pawn);
                    _gearSummary = "Rim3D_ComfortTemperature".Translate(range.min.ToStringTemperature(), range.max.ToStringTemperature()) +
                        "\n" + "OverallArmor".Translate() + ": " +
                        "ArmorSharp".Translate() + " " + OverallArmor(pawn, StatDefOf.ArmorRating_Sharp).ToStringPercent() + " · " +
                        "ArmorBlunt".Translate() + " " + OverallArmor(pawn, StatDefOf.ArmorRating_Blunt).ToStringPercent() + " · " +
                        "ArmorHeat".Translate() + " " + OverallArmor(pawn, StatDefOf.ArmorRating_Heat).ToStringPercent();
                }
                bool hasEquipment = weapon != null || apparelCount > 0;
                float apparelHeight = apparelCount > 0 ? 0 : 32;
                for (int i = 0; i < apparelCount; i++) apparelHeight += _view.HasEquipmentActions(apparel[i]) ? 92 : 56;
                var content = new Rect(0, 0, list.width - 18, 92 + 2 * 40 + (hasEquipment ? 40 : 0) + (carried != null ? 150 : 0) +
                    (carried?.def.IsWeapon == true ? 40 : 0) + (weapon != null ? 150 : 0) +
                    apparelHeight + (items > 0 ? items * 92 + 40 : 28));
                Widgets.BeginScrollView(list, ref _statusScroll, content);
                float y = 0;
                Widgets.Label(new Rect(8, y, content.width - 16, 28), "Rim3D_InventoryMass".Translate(MassUtility.GearAndInventoryMass(pawn).ToString("0.0"), MassUtility.Capacity(pawn).ToString("0.0")).ToString()); y += 32;
                Widgets.Label(new Rect(8, y, content.width - 16, 56), _gearSummary); y += 60;
                if (hasEquipment)
                {
                    if (Widgets.ButtonText(new Rect(8, y, 260, 30), "Rim3D_EquipmentActions".TranslateBindings())) _view.OpenHeldItemActions();
                    y += 40;
                }
                if (weapon != null)
                {
                    DrawStatusSection(content.width, ref y, "Rim3D_WeaponHeading".Translate().ToString(), new Color(1, .8f, .45f));
                    Widgets.Label(new Rect(8, y, content.width - 48, 28), weapon.LabelCap);
                    Widgets.InfoCardButton(content.width - 30, y, weapon); y += 30;
                    if (Widgets.ButtonText(new Rect(8, y, 180, 30), "Rim3D_DropWeapon".Translate().ToString()))
                    { _view.DropEquippedFPVWeapon(); Widgets.EndScrollView(); return; }
                    if (Widgets.ButtonText(new Rect(196, y, 180, 30), "Rim3D_SwapHands".TranslateBindings().ToString()))
                    { _view.SwapHandsAndEquipment(); Widgets.EndScrollView(); return; }
                    y += 40;
                    if (Widgets.ButtonText(new Rect(8, y, 260, 30), "Rim3D_UseEquipmentActions".Translate()))
                        _view.OpenEquipmentItemActions(pawn, weapon);
                    y += 40;
                }
                if (carried != null)
                {
                    DrawStatusSection(content.width, ref y, "Rim3D_HandsHeading".Translate().ToString(), new Color(.7f, .85f, .65f));
                    Widgets.Label(new Rect(8, y, content.width - 48, 28), carried.LabelCap);
                    Widgets.InfoCardButton(content.width - 30, y, carried); y += 30;
                    if (Widgets.ButtonText(new Rect(8, y, 140, 30), "Rim3D_StoreShortcut".TranslateBindings().ToString()))
                    { _view.StoreFPVCarry(); Widgets.EndScrollView(); return; }
                    if (Widgets.ButtonText(new Rect(154, y, 120, 30), "Rim3D_Drop".Translate().ToString()))
                    { _view.DropFPVCarry(); Widgets.EndScrollView(); return; }
                    int heldAction = _view.PrimaryHeldItemAction(carried);
                    if (heldAction != int.MinValue && Widgets.ButtonText(new Rect(280, y, 120, 30), _view.HeldItemActionLabel(carried, heldAction)))
                    { _view.RequestHeldItemAction(carried, heldAction); Widgets.EndScrollView(); return; }
                    y += 40;
                    if (Widgets.ButtonText(new Rect(8, y, 220, 30), "Rim3D_HeldActions".Translate().ToString())) _view.OpenHeldItemActions();
                    y += 40;
                    if (carried.def.IsWeapon)
                    {
                        if (Widgets.ButtonText(new Rect(8, y, 180, 30), "Rim3D_SwapWeapon".TranslateBindings().ToString()))
                        { _view.SwapHandsAndEquipment(); Widgets.EndScrollView(); return; }
                        y += 40;
                    }
                }
                DrawStatusSection(content.width, ref y, "Rim3D_ApparelHeading".Translate(apparelCount).ToString(), new Color(.65f, .8f, .95f));
                if (apparelCount == 0) { Widgets.Label(new Rect(8, y, content.width - 16, 28), "Rim3D_NoApparel".Translate().ToString()); y += 32; }
                for (int i = 0; i < apparelCount; i++)
                {
                    var thing = apparel[i];
                    bool hasActions = _view.HasEquipmentActions(thing);
                    float rowHeight = hasActions ? 92 : 56;
                    if (y + rowHeight >= _statusScroll.y && y <= _statusScroll.y + list.height)
                    {
                        var row = new Rect(0, y, content.width, 50);
                        Widgets.DrawBoxSolid(row, new Color(.10f, .13f, .16f));
                        Widgets.ThingIcon(new Rect(8, y + 8, 32, 32), thing);
                        Widgets.Label(new Rect(48, y + 4, content.width - 174, 24), thing.LabelCap);
                        if (thing.def.useHitPoints)
                            Widgets.Label(new Rect(48, y + 26, content.width - 174, 24), "Rim3D_Durability".Translate(thing.HitPoints, thing.MaxHitPoints).ToString());
                        TooltipHandler.TipRegion(new Rect(0, y, content.width - 126, 50), thing.DescriptionDetailed);
                        Widgets.InfoCardButton(content.width - 30, y + 13, thing);
                        bool enabled = GUI.enabled;
                        GUI.enabled = enabled && !_view.controller.IsResting && !pawn.apparel.IsLocked(thing);
                        bool remove = Widgets.ButtonText(new Rect(content.width - 122, y + 10, 84, 30), "Rim3D_RemoveApparel".Translate());
                        GUI.enabled = enabled;
                        if (remove)
                        {
                            _view.RemoveFPVApparel(thing);
                            Widgets.EndScrollView();
                            return;
                        }
                        if (hasActions && Widgets.ButtonText(new Rect(8, y + 54, content.width - 16, 30),
                            "Rim3D_UseEquipmentActions".Translate())) _view.OpenEquipmentItemActions(pawn, thing);
                    }
                    y += rowHeight;
                }
                DrawStatusSection(content.width, ref y, "Rim3D_InventoryHeading".Translate(items).ToString(), new Color(.7f, .85f, .65f));
                if (items > 0)
                {
                    if (Widgets.ButtonText(new Rect(8, y, 220, 30), "Rim3D_MergeInventory".Translate().ToString()))
                    { _view.MergeFPVInventory(); Widgets.EndScrollView(); return; }
                    y += 40;
                }
                if (items == 0) Widgets.Label(new Rect(8, y, content.width - 16, 28), "Rim3D_EmptyInventory".Translate().ToString());
                for (int i = 0; i < items; i++)
                {
                    if (y + 92 < _statusScroll.y || y > _statusScroll.y + list.height) { y += 92; continue; }
                    var thing = inventory[i];
                    Widgets.DrawBoxSolid(new Rect(0, y, content.width, 86), new Color(.10f, .13f, .16f));
                    Widgets.ThingIcon(new Rect(8, y + 4, 32, 32), thing);
                    Widgets.Label(new Rect(48, y + 4, content.width - 88, 32), thing.LabelCap);
                    Widgets.InfoCardButton(content.width - 30, y + 8, thing);
                    if (Widgets.ButtonText(new Rect(8, y + 44, 126, 30), "Rim3D_TakeInHands".Translate().ToString())) { _view.TakeInventoryItem(thing); break; }
                    if (Widgets.ButtonText(new Rect(140, y + 44, 126, 30), "Rim3D_Drop".Translate().ToString())) { _view.DropInventoryItem(thing); break; }
                    if (_view.CanIngestFPV(thing) && Widgets.ButtonText(new Rect(272, y + 44, 126, 30), IngestLabel(thing)))
                    { _view.IngestFPV(thing); break; }
                    y += 92;
                }
                Widgets.EndScrollView();
            }

        }

        sealed partial class ActionMenus
        {
            WindowLayer _colonyWindowLayer;

            bool _colonyWindowCloseX;

            bool _externalUIWasBlocking;

            internal void OpenGameMenu() => _view.OpenColonyTab(MainButtonDefOf.Menu);

            internal void OpenColonyTab(MainButtonDef tab)
            {
                if (tab.Worker.Disabled) return;
                _view.CloseColonyTab();
                _view.colonyWindow = tab.TabWindow;
                if (_view.colonyWindow != null)
                {
                    _colonyWindowLayer = _view.colonyWindow.layer;
                    _colonyWindowCloseX = _view.colonyWindow.doCloseX;
                    // A click on the full-screen preview brings GameUI windows to the
                    // front. Keep this tab above it, using the normal window stack.
                    _view.colonyWindow.layer = WindowLayer.Dialog;
                    _view.colonyWindow.doCloseX = true;
                }
                _view.statusMenu = false;
                _view.ReleaseMouse();
                try { tab.Worker.InterfaceTryActivate(); }
                finally { _view.RefreshColonyTab(); }
            }

            internal void RefreshColonyTab()
            {
                if (_view.colonyWindow != null && !_view.colonyWindow.IsOpen)
                {
                    _view.colonyWindow.layer = _colonyWindowLayer;
                    _view.colonyWindow.doCloseX = _colonyWindowCloseX;
                    _view.colonyWindow = null;
                }
                // Letter actions can select a target and reopen Inspect behind the
                // full-screen preview. Clear that selection once the dialog is gone.
                var window = Rim3DPreview.Instance?.ActiveWindow;
                if (window?.IsOpen == true && window.View == _view &&
                    _view.colonyWindow == null && !_view.ExternalDialogInputBlocked &&
                    Find.MainTabsRoot.OpenTab?.TabWindow is MainTabWindow_Inspect)
                {
                    Find.Selector.ClearSelection();
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                }
                bool blocked = _view.ExternalInputBlocked;
                if (_externalUIWasBlocking && !blocked)
                    Rim3DPreview.RequestGameInputFocus?.Invoke();
                _externalUIWasBlocking = blocked;
            }

            internal void CloseColonyTab()
            {
                if (_view.colonyWindow == null) return;
                if (_view.colonyWindow.IsOpen) _view.colonyWindow.Close(false);
                _view.RefreshColonyTab();
            }

        }

        readonly IngestActions _ingestActions;

        sealed partial class IngestActions
        {
            readonly MapView _view;

            internal IngestActions(MapView view)
            {
                _view = view;
            }

            internal bool CanIngestFPV(Thing food)
            {
                var pawn = _view.ControlledPawn;
                if (food == null || food.Destroyed || food.def.category != ThingCategory.Item ||
                    pawn == null || !PawnControl.Eligible(pawn) || _view.controller.IsResting ||
                    !food.IngestibleNow || !pawn.RaceProps.CanEverEat(food) ||
                    DefDatabase<JobDef>.GetNamedSilentFail("Recovery_Rim3DIngest") == null) return false;
                var carried = pawn.carryTracker?.CarriedThing;
                if (carried != null && carried != food && !PawnControl.CanStoreCargo(pawn, carried)) return false;
                if (food == carried || pawn.inventory?.innerContainer.Contains(food) == true) return true;
                return food.Spawned && food.Map == _view.map && !food.IsForbidden(pawn) &&
                    !food.IsBurning() && pawn.CanReach(food, PathEndMode.Touch, Danger.Deadly) && pawn.CanReserve(food);
            }

            internal void IngestFPV(Thing food)
            {
                if (_view.SyncAction(Rim3DAction.Ingest, food)) return;
                if (_view.controller.IsResting) { _view.SayAction("Rim3D_StandToEat".Translate().ToString()); return; }
                if (!_view.CanIngestFPV(food))
                { _view.SayAction("Rim3D_CannotEat".Translate().ToString()); return; }
                if (_view.ControlledPawn.Drafted) _view.controller.SetCombat(false);
                // The native driver can eat directly from the hands. Only move
                // unrelated cargo to inventory, preserving the food's identity.
                if (_view.ControlledPawn.carryTracker?.CarriedThing != food && !_view.StoreFPVCarry()) return;
                var ingestDef = DefDatabase<JobDef>.GetNamedSilentFail("Recovery_Rim3DIngest");
                if (ingestDef == null) { _view.SayAction("Rim3D_ReloadIngestJob".Translate().ToString()); return; }
                var job = JobMaker.MakeJob(ingestDef, food);
                job.count = Mathf.Min(food.stackCount, food.def.ingestible.maxNumToIngestAtOnce);
                if (_view.controller.OrderAction(job))
                { _view.statusMenu = _view.actionMenu = false; _view.SayAction("Rim3D_EatingHint".TranslateBindings().ToString()); }
                else _view.SayAction(_view.controller.Status);
            }

        }
    }
}

