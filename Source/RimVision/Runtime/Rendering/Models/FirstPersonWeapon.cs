using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly FirstPersonWeaponRenderer _firstPersonWeaponRenderer;
        Vector3 firstPersonMuzzle => _firstPersonWeaponRenderer.Muzzle;
        bool firstPersonWeaponVisible => _firstPersonWeaponRenderer.Visible;

        internal void DrawSniperScope(Rect viewport) => _firstPersonWeaponRenderer.DrawSniperScope(viewport);

        void UpdateFirstPersonWeapon() => _firstPersonWeaponRenderer.UpdateFirstPersonWeapon();

        void DisposeFirstPersonWeapon() => _firstPersonWeaponRenderer?.DisposeFirstPersonWeapon();
        float _aimBlend;
        float _aimZoom = 1;
        float _weaponRecoil;
        float _weaponRecoilVelocity;
        Pawn _presentationPawn;
        ThingWithComps _presentationWeapon;

        internal float SniperScopeOpacity => (!_viewCamera.HasFrame || _viewCamera.Frame.ShowFirstPersonItems) && CanAimWeapon && _presentationWeapon?.def.defName == "Gun_SniperRifle"
            ? Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.35f, .95f, _aimBlend)) : 0;
        bool CanAimWeapon => !ApiInputBlocked(API.RimVisionInputChannels.Combat) && CanCaptureMouse && !TextInputFocused && buildTool == null && zoneTool == null && !SelectingFPVTarget &&
            ControlledPawn?.Drafted == true && ControlledPawn.equipment?.Primary?.def.IsRangedWeapon == true &&
            !controller.IsResting && !controller.IsActing && !controller.CombatRecovering &&
            (PawnControl.ForPawn(ControlledPawn)?.IsRunning != true || Rim3DSettings.Current.ShootWhileRunningEnabled);

        public bool WeaponAiming => CanAimWeapon && mouseCaptured && ViewBindings.MouseHeld(1);

        static float WeaponAimZoom(ThingDef weapon)
        {
            if (weapon?.IsRangedWeapon != true) return 1;
            switch (weapon.defName)
            {
                case "Gun_SniperRifle": return 4;
                case "Gun_ChargeLance": return 2.5f;
                case "Gun_BoltActionRifle": return 2;
                case "Gun_AssaultRifle":
                case "Gun_ChargeRifle": return 1.5f;
                case "Gun_LMG": return 1.35f;
                case "Gun_HeavySMG":
                case "Gun_MachinePistol": return 1.2f;
                case "Gun_Revolver":
                case "Gun_Autopistol":
                case "Gun_PumpShotgun":
                case "Gun_ChainShotgun":
                case "Gun_Minigun": return 1.1f;
                default: return 1.25f;
            }
        }

        float WeaponAimFieldOfView(float fieldOfView) => Mathf.Lerp(fieldOfView,
            2 * Mathf.Atan(Mathf.Tan(fieldOfView * .5f * Mathf.Deg2Rad) / _aimZoom) * Mathf.Rad2Deg, _aimBlend);

        void UpdateWeaponPresentation(float dt)
        {
            var pawn = FirstPerson ? ControlledPawn : null;
            var weapon = pawn?.equipment?.Primary;
            if (_presentationPawn != pawn || _presentationWeapon != weapon || !FirstPerson || controller.InputBlocked)
            {
                _presentationPawn = pawn;
                _presentationWeapon = weapon;
                _aimZoom = WeaponAimZoom(weapon?.def);
                _aimBlend = _weaponRecoil = _weaponRecoilVelocity = 0;
                _viewCamera.ResetRecoil();
            }
            _aimBlend = Mathf.MoveTowards(_aimBlend, WeaponAiming ? 1 : 0, dt * 9);
            float spring = _weaponRecoilVelocity + 20 * _weaponRecoil;
            float decay = Mathf.Exp(-20 * dt);
            _weaponRecoil = (_weaponRecoil + spring * dt) * decay;
            _weaponRecoilVelocity = (_weaponRecoilVelocity - 20 * spring * dt) * decay;
            _viewCamera.UpdateRecoil(dt, FirstPerson && !controller.InputBlocked && !cameraFlight && !falling && Application.isFocused);
        }

        void AddWeaponRecoil()
        {
            if (!FirstPerson || ControlledPawn?.equipment?.Primary?.def.IsRangedWeapon != true) return;
            _apiExtensions?.Notify(new API.RimVisionViewEvent(API.RimVisionViewEventKind.Shot, ControlledPawn,
                ControlledPawn.equipment.Primary, Find.TickManager.TicksGame));
            float strength = Mathf.Lerp(1, .65f, _aimBlend) * (controller.Sneaking ? .8f : 1);
            _weaponRecoil = Mathf.Min(.1f, _weaponRecoil + .012f * strength);
            _weaponRecoilVelocity = Mathf.Min(2, _weaponRecoilVelocity + 1.2f * strength);
            var pawn = ControlledPawn;
            var weapon = pawn.equipment.Primary;
            var verb = weapon.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
            if (verb != null && !controller.InputBlocked && !cameraFlight && !falling && Application.isFocused)
                _viewCamera.AddRecoil(pawn, weapon, verb, strength);
        }

        float MeleeSwing(Pawn pawn)
        {
            int started = pawn == ControlledPawn ? controller.LastMeleeAttackTick :
                pawn.stances?.curStance is Stance_Cooldown cooldown && cooldown.verb?.IsMeleeAttack == true
                    ? cooldown.startedTick : -1000;
            float progress = (Find.TickManager.TicksGame - started) / 24f;
            return progress >= 0 && progress < 1 ? Mathf.Sin(progress * Mathf.PI) : 0;
        }

        sealed class FirstPersonWeaponRenderer
        {
            readonly MapView _view;

            internal FirstPersonWeaponRenderer(MapView view)
            {
                _view = view;
            }

            internal Vector3 Muzzle => _firstPersonMuzzle;
            internal bool Visible => _firstPersonWeaponVisible;
            CommandBuffer _firstPersonWeaponPass;
            Mesh _firstPersonWeaponMesh;
            Mesh _firstPersonHandMesh;
            Vector3 _rightHandGrip;
            Vector3 _leftHandGrip;
            Mesh _muzzleFlashMesh;
            Material _muzzleFlashMaterial;
            Vector3 _weaponMuzzle;
            Material _firstPersonWeaponMaterial;
            Material _firstPersonHandMaterial;
            Material _firstPersonWeaponSource;
            bool _firstPersonWeaponVolume;
            static readonly int _firstPersonProjectionId = Shader.PropertyToID("_Rim3DFirstPersonProjection");
            Vector3 _firstPersonMuzzle;
            bool _firstPersonWeaponVisible;
            MaterialPropertyBlock _extensionProperties;
            Bounds _weaponAimBounds;
            readonly HeldCard _carriedModel = new HeldCard();
            MeshFilter[] _carriedModelParts;
            Material[] _carriedModelMaterials;
            Texture2D _sniperScopeMask;
            Pawn _swayPawn;
            Thing _swayThing;
            int _swayFrame = -1;
            Vector3 _swayLook;
            Vector2 _lookSway;
            float _swayPhase;
            float _runBlend;
            Pawn _meleePawn;
            Thing _meleeWeapon;
            int _meleeStarted = -1000;
            bool _meleeStab;

            void UpdateSway(Pawn pawn, Thing thing)
            {
                int frame = Time.frameCount;
                var look = _view.camera.transform.eulerAngles;
                if (_swayPawn != pawn || _swayThing != thing || _swayFrame < frame - 1)
                {
                    _swayPawn = pawn;
                    _swayThing = thing;
                    _swayLook = look;
                    _lookSway = Vector2.zero;
                    _swayPhase = 0;
                    _runBlend = 0;
                    _swayFrame = frame;
                }
                if (_swayFrame == frame) return;
                _swayFrame = frame;
                float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
                if (!Find.TickManager.Paused)
                    _runBlend = Mathf.MoveTowards(_runBlend, !Rim3DSettings.Current.ShootWhileRunningEnabled && thing?.def.IsRangedWeapon == true &&
                        PawnControl.ForPawn(pawn)?.IsRunning == true ? 1 : 0, dt * 7);
                var target = new Vector2(Mathf.DeltaAngle(_swayLook.y, look.y),
                    Mathf.DeltaAngle(_swayLook.x, look.x)) * (-.012f / Mathf.Max(dt, .001f));
                _swayLook = look;
                _lookSway = Vector2.Lerp(_lookSway, Vector2.ClampMagnitude(target, 2), 1 - Mathf.Exp(-12 * dt));
                if (!Find.TickManager.Paused) _swayPhase = (_swayPhase + dt * 1.7f) % (Mathf.PI * 2);
            }

            internal void DrawSniperScope(Rect viewport)
            {
                float opacity = _view.SniperScopeOpacity;
                if (opacity <= 0 || Event.current.type != EventType.Repaint) return;
                if (_sniperScopeMask == null)
                {
                    const int size = 512;
                    _sniperScopeMask = new Texture2D(size, size, TextureFormat.RGBA32, false)
                    {
                        name = "RimVision sniper scope",
                        hideFlags = HideFlags.DontSave,
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear
                    };
                    var pixels = new Color32[size * size];
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            float dx = (x + .5f) / size - .5f;
                            float dy = (y + .5f) / size - .5f;
                            float radius = Mathf.Sqrt(dx * dx + dy * dy);
                            float rim = Mathf.SmoothStep(0, .6f, Mathf.InverseLerp(.42f, .47f, radius));
                            float outside = Mathf.InverseLerp(.47f - 1f / size, .47f + 1f / size, radius);
                            pixels[y * size + x] = new Color32(0, 0, 0, (byte)(Mathf.Max(rim, outside) * 255));
                        }
                    _sniperScopeMask.SetPixels32(pixels);
                    _sniperScopeMask.Apply(false, true);
                }
                float diameter = Mathf.Min(viewport.width, viewport.height) * .98f;
                var center = viewport.center;
                var scope = new Rect(center.x - diameter * .5f, center.y - diameter * .5f, diameter, diameter);
                var oldColor = GUI.color;
                try
                {
                    GUI.color = new Color(0, 0, 0, opacity);
                    GUI.DrawTexture(new Rect(viewport.x, viewport.y, viewport.width, scope.y - viewport.y), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(viewport.x, scope.yMax, viewport.width, viewport.yMax - scope.yMax), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(viewport.x, scope.y, scope.x - viewport.x, diameter), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(scope.xMax, scope.y, viewport.xMax - scope.xMax, diameter), Texture2D.whiteTexture);
                    GUI.color = new Color(1, 1, 1, opacity);
                    GUI.DrawTexture(scope, _sniperScopeMask);
                    GUI.color = new Color(0, 0, 0, opacity);
                    float radius = diameter * .47f;
                    GUI.DrawTexture(new Rect(center.x - radius, center.y - .5f, radius * 2, 1), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(center.x - .5f, center.y - radius, 1, radius * 2), Texture2D.whiteTexture);
                }
                finally { GUI.color = oldColor; }
            }

            internal void UpdateFirstPersonWeapon()
            {
                _firstPersonWeaponVisible = false;
                _firstPersonWeaponPass?.Clear();
                var pawn = _view.ControlledPawn;
                if (_view._viewCamera.HasFrame && !_view._viewCamera.Frame.ShowFirstPersonItems || !Rim3DSettings.Current.FirstPersonItems || _view.MortarAiming || _view.cameraFlight || _view.FallPresentationActive || !_view.FirstPerson || pawn == null || _view.controller.IsResting) return;
                if (_view.SniperScopeOpacity >= .99f) return;
                // Capture the native attack before its job returns to manual control.
                _ = _view.controller.CombatRecovering;
                float swing = _view.MeleeSwing(pawn);
                var carried = pawn.carryTracker?.CarriedThing;
                if (pawn.Drafted && HeldHumanPawn(carried) == null || _view.HeldThingOnEatSurface(pawn, carried, out _, out _, out _)) carried = null;
                var weapon = carried ?? (pawn.Drafted ? pawn.equipment?.Primary : null);
                if (!pawn.Drafted && weapon == null) return;
                bool unarmed = weapon == null;
                bool weaponVolume = !unarmed && carried == null && weapon.def.IsRangedWeapon;
                if (unarmed && (swing <= 0 || !Rim3DSettings.Current.FirstPersonHands)) return;
                var graphic = !unarmed && !weapon.Destroyed ? HeldThingGraphic(weapon) : null;
                if (!unarmed && graphic == null && HeldHumanPawn(weapon) == null) return;
                bool animal = !unarmed && IsHeldAnimal(weapon);
                bool human = !unarmed && HeldHumanPawn(weapon) != null;
                var source = unarmed ? null : _view.HeldThingMaterial(weapon, graphic, carried == null);
                if (!unarmed && (source == null || source.mainTexture == null)) return;
                if (_carriedModel.Thing != carried || _carriedModel.Source != source)
                {
                    ClearCarriedModel();
                    _carriedModel.Thing = carried;
                    _carriedModel.Source = source;
                    if (carried != null && _view._heldThingPresentation.CreateCarriedModel(_carriedModel, carried, graphic))
                    {
                        _carriedModel.Object.SetActive(false);
                        _carriedModelParts = _carriedModel.Object.GetComponentsInChildren<MeshFilter>(true);
                        _carriedModelMaterials = new Material[_carriedModelParts.Length];
                    }
                }
                if (_firstPersonWeaponMaterial == null)
                {
                    var weaponShader = ModAssets.LoadShader("Rim3D/FirstPersonWeapon");
                    if (weaponShader == null || !weaponShader.isSupported) return;
                    _firstPersonWeaponMaterial = new Material(weaponShader) { hideFlags = HideFlags.DontSave };
                    _view.materials.Add(_firstPersonWeaponMaterial);
                    _firstPersonHandMaterial = new Material(weaponShader) { hideFlags = HideFlags.DontSave };
                    _firstPersonHandMaterial.mainTexture = Texture2D.whiteTexture;
                    _firstPersonHandMaterial.SetFloat("_UseMask", 0);
                    _firstPersonHandMaterial.SetFloat("_HandShading", 1);
                    _firstPersonHandMesh = HeldThingPresentation.CreateHandMesh();
                    _view.meshes.Add(_firstPersonHandMesh);
                    _view.materials.Add(_firstPersonHandMaterial);
                    // Local +X is the muzzle direction; +Y keeps the grip below it.
                    _firstPersonWeaponMesh = new Mesh { name = "RimVision perspective weapon", hideFlags = HideFlags.DontSave };
                    _firstPersonWeaponMesh.vertices = new[] {
                        new Vector3(-.5f, -.5f, 0), new Vector3(-.5f, .5f, 0),
                        new Vector3(.5f, .5f, 0), new Vector3(.5f, -.5f, 0) };
                    _firstPersonWeaponMesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
                    _firstPersonWeaponMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    _firstPersonWeaponMesh.RecalculateBounds();
                    _view.meshes.Add(_firstPersonWeaponMesh);
                    _muzzleFlashMesh = UnityEngine.Object.Instantiate(_firstPersonWeaponMesh);
                    _muzzleFlashMesh.name = "RimVision first person muzzle flash";
                    _view.meshes.Add(_muzzleFlashMesh);
                    _muzzleFlashMaterial = new Material(weaponShader) { hideFlags = HideFlags.DontSave };
                    _muzzleFlashMaterial.mainTexture = ModAssets.LoadTexture("textures/things/mote/ShotFlash");
                    _muzzleFlashMaterial.SetFloat("_Flash", 1);
                    _view.materials.Add(_muzzleFlashMaterial);
                    _firstPersonWeaponPass = new CommandBuffer { name = "RimVision first person weapon" };
                }
                if (unarmed)
                {
                    _firstPersonWeaponSource = null;
                    _firstPersonWeaponMaterial.mainTexture = Texture2D.whiteTexture;
                    _firstPersonWeaponMaterial.mainTextureScale = Vector2.one;
                    _firstPersonWeaponMaterial.mainTextureOffset = Vector2.zero;
                    _firstPersonWeaponMaterial.SetColor("_Color", pawn.story?.SkinColor ?? new Color(.65f, .45f, .32f));
                    _firstPersonWeaponMaterial.SetFloat("_UseMask", 0);
                }
                else if (_firstPersonWeaponSource != source || _firstPersonWeaponVolume != weaponVolume)
                {
                    _firstPersonWeaponSource = source;
                    _firstPersonWeaponVolume = weaponVolume;
                    if (weaponVolume)
                    {
                        var volumeMaterial = _view.ConvertMaterial(source, false);
                        var geometry = _view.WeaponVolume(volumeMaterial, 1, 1, false);
                        _firstPersonWeaponMesh.Clear();
                        _firstPersonWeaponMesh.SetVertices(geometry.Vertices);
                        _firstPersonWeaponMesh.SetUVs(0, geometry.Uvs);
                        _firstPersonWeaponMesh.SetNormals(geometry.Normals);
                        _firstPersonWeaponMesh.SetTriangles(geometry.Indices, 0);
                        _firstPersonWeaponMesh.RecalculateBounds();
                        _weaponAimBounds = _firstPersonWeaponMesh.bounds;
                        _rightHandGrip = new Vector3(Mathf.Lerp(_weaponAimBounds.min.x, _weaponAimBounds.max.x, .32f), _weaponAimBounds.min.y, 0);
                        _leftHandGrip = new Vector3(Mathf.Lerp(_weaponAimBounds.min.x, _weaponAimBounds.max.x, .68f), _weaponAimBounds.min.y, 0);
                        _weaponMuzzle = new Vector3(_weaponAimBounds.max.x, _weaponAimBounds.center.y, 0);
                        if (_view.spriteMasks.TryGetValue(volumeMaterial, out var fallback))
                        {
                            var volumeMask = _view.ReadVolumeMask(volumeMaterial, fallback, out int columns, out int rows);
                            int minX = columns, minY = rows, maxX = -1, maxY = -1;
                            for (int y = 0; y < rows; y++)
                                for (int x = 0; x < columns; x++)
                                {
                                    if (volumeMask[y * columns + x] * volumeMaterial.color.a < 64) continue;
                                    minX = Mathf.Min(minX, x); minY = Mathf.Min(minY, y);
                                    maxX = Mathf.Max(maxX, x); maxY = Mathf.Max(maxY, y);
                                }
                            if (maxX >= minX && maxY >= minY)
                            {
                                _rightHandGrip = HeldThingPresentation.WeaponGrip(volumeMask, columns, rows, volumeMaterial.color.a, minX, maxX, .32f);
                                _leftHandGrip = HeldThingPresentation.WeaponGrip(volumeMask, columns, rows, volumeMaterial.color.a, minX, maxX, .68f);
                                int muzzleMinY = rows, muzzleMaxY = -1;
                                for (int y = 0; y < rows; y++)
                                    if (volumeMask[y * columns + maxX] * volumeMaterial.color.a >= 64)
                                    {
                                        muzzleMinY = Mathf.Min(muzzleMinY, y);
                                        muzzleMaxY = Mathf.Max(muzzleMaxY, y);
                                    }
                                _weaponMuzzle = new Vector3((float)(maxX + 1) / columns - .5f,
                                    (muzzleMinY + muzzleMaxY + 1) / (2f * rows) - .5f, 0);
                                _weaponAimBounds.SetMinMax(
                                    new Vector3((float)minX / columns - .5f, (float)minY / rows - .5f, _weaponAimBounds.min.z),
                                    new Vector3((float)(maxX + 1) / columns - .5f, (float)(maxY + 1) / rows - .5f, _weaponAimBounds.max.z));
                            }
                        }
                    }
                    else
                    {
                        _firstPersonWeaponMesh.Clear();
                        _firstPersonWeaponMesh.vertices = new[] { new Vector3(-.5f,-.5f,0),new Vector3(-.5f,.5f,0),new Vector3(.5f,.5f,0),new Vector3(.5f,-.5f,0) };
                        _firstPersonWeaponMesh.uv = new[] { Vector2.zero,Vector2.up,Vector2.one,Vector2.right };
                        _firstPersonWeaponMesh.triangles = new[] {0,1,2,0,2,3,2,1,0,3,2,0};
                        _firstPersonWeaponMesh.RecalculateBounds();
                    }
                    _firstPersonWeaponMaterial.mainTexture = source.mainTexture;
                    _firstPersonWeaponMaterial.mainTextureScale = source.mainTextureScale;
                    _firstPersonWeaponMaterial.mainTextureOffset = source.mainTextureOffset;
                    _firstPersonWeaponMaterial.SetColor("_Color", source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
                    var mask = source.HasProperty("_MaskTex") ? source.GetTexture("_MaskTex") : null;
                    _firstPersonWeaponMaterial.SetTexture("_MaskTex", mask != null ? mask : Texture2D.blackTexture);
                    _firstPersonWeaponMaterial.SetFloat("_UseMask", mask != null ? 1 : 0);
                    _firstPersonWeaponMaterial.SetColor("_ColorTwo", source.HasProperty("_ColorTwo") ? source.GetColor("_ColorTwo") : Color.white);
                }
                // Camera space: the stock is near the right shoulder, the muzzle is
                // farther away. Perspective projection shrinks the far end naturally.
                var hands = new Vector3(.38f, -.22f, .75f);
                var direction = (new Vector3(0, 0, 50) - hands).normalized;
                var normal = Vector3.Cross(direction, Vector3.up).normalized;
                var up = Vector3.Cross(normal, direction).normalized;
                var rotation = Quaternion.LookRotation(normal, up) *
                    Quaternion.Euler(0, 0, -(weapon?.def.equippedAngleOffset ?? 0));
                bool melee = carried == null && (unarmed || weapon.def.IsMeleeWeapon);
                if (melee)
                {
                    MeleePose(pawn, weapon, unarmed, out hands, out rotation);
                }
                else if (swing > 0)
                {
                    // G also animates a close-range strike with a ranged weapon.
                    hands += new Vector3(-swing * .16f, swing * .08f, swing * .25f);
                    rotation *= Quaternion.Euler(0, swing * 20, -swing * 25);
                }
                var drawSize = unarmed ? Vector2.one : HeldThingSize(weapon, graphic);
                float length = Mathf.Clamp(drawSize.x * .85f, .6f, 1f);
                float height = length * drawSize.y / Mathf.Max(.01f, drawSize.x);
                float depth = weaponVolume ? 2f / 3f : 1f;
                if (carried == null && !unarmed && weapon.def.IsRangedWeapon)
                {
                    float aim = Mathf.SmoothStep(0, 1, _view._aimBlend);
                    var aimRotation = Quaternion.LookRotation(Vector3.left, Vector3.up) *
                        Quaternion.Euler(0, 0, -weapon.def.equippedAngleOffset);
                    var aimHands = new Vector3(0, 0, .85f);
                    var bounds = _weaponAimBounds;
                    float top = float.NegativeInfinity;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = bounds.center + Vector3.Scale(bounds.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        corner = aimRotation * Vector3.Scale(corner, new Vector3(length, height, depth));
                        top = Mathf.Max(top, corner.y + .025f * corner.z);
                    }
                    aimHands.y = -top - .025f * aimHands.z;
                    hands = Vector3.Lerp(hands, aimHands, aim);
                    rotation = Quaternion.Slerp(rotation, aimRotation, aim);
                    direction = Vector3.Lerp(direction, Vector3.forward, aim).normalized;
                    hands += new Vector3(0, _view._weaponRecoil * .22f, -_view._weaponRecoil);
                    rotation = Quaternion.Euler(-_view._weaponRecoil * 65, 0, _view._weaponRecoil * 12) * rotation;
                }
                UpdateSway(pawn, weapon);
                float swayStrength = Mathf.Lerp(1, .2f, _view._aimBlend);
                var swayOffset = new Vector3(_lookSway.x * .008f + Mathf.Sin(_swayPhase) * .0025f,
                    -_lookSway.y * .008f + Mathf.Sin(_swayPhase * 2) * .0015f, 0) * swayStrength;
                var swayRotation = Quaternion.Euler(-_lookSway.y * swayStrength, _lookSway.x * swayStrength,
                    (-_lookSway.x * .4f + Mathf.Sin(_swayPhase) * .25f) * swayStrength);
                hands += swayOffset;
                rotation = swayRotation * rotation;
                direction = swayRotation * direction;
                if (carried == null && weaponVolume && _runBlend > 0)
                {
                    HeldThingPresentation.RunningWeaponPose(pawn, out var runOffset, out var runRotation);
                    hands = Vector3.Lerp(hands, new Vector3(.02f, -.28f, .48f) + runOffset + swayOffset, _runBlend);
                    rotation = Quaternion.Slerp(rotation, swayRotation * runRotation *
                        Quaternion.Euler(0, 0, -weapon.def.equippedAngleOffset), _runBlend);
                }
                var matrix = Matrix4x4.TRS(hands, rotation,
                    unarmed ? Vector3.one * .18f : new Vector3(length, height, depth));
                if (carried != null)
                {
                    // Resources face the camera upright, without weapon alignment,
                    // equipped angles or recoil. Preserve the sprite's aspect ratio.
                    float width = animal || human ? drawSize.x * PawnVisualScale : Mathf.Clamp(drawSize.x * .45f, .22f, .55f);
                    float itemHeight = width * drawSize.y / Mathf.Max(.01f, drawSize.x);
                    if (!animal && !human && itemHeight > .5f) { width *= .5f / itemHeight; itemHeight = .5f; }
                    var carryPosition = new Vector3(.2f, -.2f, .72f);
                    if (animal || human)
                    {
                        float bodyWidth = human ? itemHeight : width;
                        float bodyHeight = human ? width : itemHeight;
                        float halfViewHeight = carryPosition.z * Mathf.Tan(_view.camera.fieldOfView * .5f * Mathf.Deg2Rad);
                        float halfViewWidth = halfViewHeight * _view.camera.aspect;
                        float scale = Mathf.Min(1, halfViewHeight * 1.2f / Mathf.Max(.01f, bodyHeight));
                        scale = Mathf.Min(scale, halfViewWidth * 1.8f / Mathf.Max(.01f, bodyWidth));
                        width *= scale;
                        itemHeight *= scale;
                        carryPosition.x = Mathf.Min(carryPosition.x, halfViewWidth * .9f - bodyWidth * scale * .5f);
                        carryPosition.y = -halfViewHeight * 1.3f + bodyHeight * scale * .5f;
                    }
                    matrix = Matrix4x4.TRS(
                        carryPosition + swayOffset, swayRotation * Quaternion.Euler(0, 0, human ? 90 : 0), new Vector3(width, itemHeight, 1));
                }
                if (carried != null && _carriedModel.Model)
                    matrix = Matrix4x4.TRS(new Vector3(.32f, -.32f, .7f) + swayOffset,
                        swayRotation * Quaternion.Euler(-20, 0, 0), Vector3.one * .85f);
                HeldThingPresentation.GetWeaponHands(pawn, out bool hasRightHand, out bool hasLeftHand);
                var defaultGrip = melee ? hands - rotation * Vector3.right * (length * .3f) : matrix.MultiplyPoint3x4(_rightHandGrip);
                var defaultSupport = !unarmed && HeldThingPresentation.UsesPistolGrip(weapon.def)
                    ? defaultGrip + rotation * new Vector3(.025f, -.025f, .065f)
                    : matrix.MultiplyPoint3x4(_leftHandGrip) - rotation * Vector3.up * .025f;
                var itemScale = matrix.lossyScale;
                var inverse = matrix.inverse;
                var animation = new API.RimVisionWeaponAnimationFrame
                {
                    Pose = new API.RimVisionPose(matrix.GetColumn(3), matrix.rotation), Scale = itemScale,
                    RightHand = new API.RimVisionPose(inverse.MultiplyPoint3x4(defaultGrip), Quaternion.identity),
                    LeftHand = new API.RimVisionPose(inverse.MultiplyPoint3x4(defaultSupport), Quaternion.identity),
                    RightHandScale = new Vector3(.16f / itemScale.x, .14f / itemScale.y, .14f / itemScale.z),
                    LeftHandScale = new Vector3(.17f / itemScale.x, .13f / itemScale.y, .15f / itemScale.z),
                    Muzzle = _weaponMuzzle + Vector3.right * .015f, DrawItem = true,
                    DrawRightHand = Rim3DSettings.Current.FirstPersonHands && carried == null && !unarmed && (melee || weaponVolume) && hasRightHand,
                    DrawLeftHand = Rim3DSettings.Current.FirstPersonHands && carried == null && !unarmed && weaponVolume && hasLeftHand
                };
                API.RimVisionPresentation.AnimateWeapon(new API.RimVisionAnimationContext(pawn, weapon, _view.map, pawn,
                    true, carried != null, _view._aimBlend, _view._weaponRecoil, _view.controller.LastMeleeAttackTick), ref animation);
                matrix = Matrix4x4.TRS(animation.Pose.Position, animation.Pose.Rotation.normalized, animation.Scale);
                // Keep the completed world color, then depth-test the viewmodel against itself.
                _firstPersonWeaponPass.ClearRenderTarget(true, false, Color.clear);
                var projection = GL.GetGPUProjectionMatrix(_view.camera.projectionMatrix, true);
                _firstPersonWeaponMaterial.SetMatrix(_firstPersonProjectionId, projection);
                if (animation.DrawItem && animation.Mesh != null)
                {
                    if (_extensionProperties == null) _extensionProperties = new MaterialPropertyBlock();
                    _extensionProperties.Clear();
                    _extensionProperties.SetMatrix(_firstPersonProjectionId, projection);
                    _firstPersonWeaponPass.DrawMesh(animation.Mesh, matrix, animation.Material != null ? animation.Material : _firstPersonWeaponMaterial,
                        0, 0, _extensionProperties);
                }
                else if (animation.DrawItem && carried != null && _carriedModel.Model)
                {
                    var modelMatrix = matrix;
                    for (int i = 0; i < _carriedModelParts.Length; i++)
                    {
                        var material = _carriedModelMaterials[i];
                        if (material == null)
                        {
                            var modelSource = _carriedModelParts[i].GetComponent<MeshRenderer>().sharedMaterial;
                            material = new Material(_firstPersonWeaponMaterial) { hideFlags = HideFlags.DontSave };
                            material.CopyPropertiesFromMaterial(modelSource);
                            material.SetFloat("_Cull", modelSource.HasProperty("_Cull") ? modelSource.GetFloat("_Cull") : 0);
                            _carriedModelMaterials[i] = material;
                        }
                        material.SetMatrix(_firstPersonProjectionId, projection);
                        _firstPersonWeaponPass.DrawMesh(_carriedModelParts[i].sharedMesh, modelMatrix, material, 0, 0);
                    }
                }
                else if (animation.DrawItem && unarmed)
                {
                    _firstPersonHandMaterial.SetColor("_Color", pawn.story?.SkinColor ?? new Color(.65f, .45f, .32f));
                    _firstPersonHandMaterial.SetMatrix(_firstPersonProjectionId, projection);
                    _firstPersonWeaponPass.DrawMesh(_firstPersonHandMesh, matrix, _firstPersonHandMaterial, 0, 0);
                    _view._outlines.DrawFirstPersonOutline(_firstPersonWeaponPass, _firstPersonHandMesh, matrix, projection, _view.Target.height);
                }
                else if (animation.DrawItem) _firstPersonWeaponPass.DrawMesh(_firstPersonWeaponMesh, matrix, _firstPersonWeaponMaterial, 0, 0);
                _firstPersonHandMaterial.SetColor("_Color", pawn.story?.SkinColor ?? new Color(.65f, .45f, .32f));
                _firstPersonHandMaterial.SetMatrix(_firstPersonProjectionId, projection);
                if (animation.DrawRightHand)
                {
                    var handMatrix = matrix * Matrix4x4.TRS(animation.RightHand.Position, animation.RightHand.Rotation.normalized, animation.RightHandScale);
                    _firstPersonWeaponPass.DrawMesh(_firstPersonHandMesh, handMatrix, _firstPersonHandMaterial, 0, 0);
                    _view._outlines.DrawFirstPersonOutline(_firstPersonWeaponPass, _firstPersonHandMesh, handMatrix, projection, _view.Target.height);
                }
                if (animation.DrawLeftHand)
                {
                    var handMatrix = matrix * Matrix4x4.TRS(animation.LeftHand.Position, animation.LeftHand.Rotation.normalized, animation.LeftHandScale);
                    _firstPersonWeaponPass.DrawMesh(_firstPersonHandMesh, handMatrix, _firstPersonHandMaterial, 0, 0);
                    _view._outlines.DrawFirstPersonOutline(_firstPersonWeaponPass, _firstPersonHandMesh, handMatrix, projection, _view.Target.height);
                }
                _firstPersonMuzzle = matrix.MultiplyPoint3x4(animation.Muzzle);
                if (animation.DrawItem && carried == null && !unarmed && weapon.def.IsRangedWeapon && _muzzleFlashMaterial.mainTexture != null)
                {
                    float flash = Mathf.Clamp01(1 - (Time.unscaledTime - _view.lastPlayerShot) / .075f);
                    if (flash > 0)
                    {
                        _muzzleFlashMaterial.SetMatrix(_firstPersonProjectionId, projection);
                        _muzzleFlashMaterial.SetColor("_Color", new Color(1, 1, 1, flash));
                        var flashMatrix = Matrix4x4.TRS(_firstPersonMuzzle, Quaternion.identity, Vector3.one * .3f);
                        _firstPersonWeaponPass.DrawMesh(_muzzleFlashMesh, flashMatrix, _muzzleFlashMaterial, 0, 0);
                    }
                }
                _firstPersonWeaponVisible = carried == null && animation.DrawItem;
            }

            internal void Render(RenderTexture target)
            {
                if (_firstPersonWeaponPass == null) return;
                var previous = RenderTexture.active;
                try
                {
                    Graphics.SetRenderTarget(target);
                    Graphics.ExecuteCommandBuffer(_firstPersonWeaponPass);
                }
                finally
                {
                    RenderTexture.active = previous;
                }
            }

            internal void DisposeFirstPersonWeapon()
            {
                _extensionProperties = null;
                _swayPawn = null;
                _swayThing = null;
                _swayFrame = -1;
                _runBlend = 0;
                _meleePawn = null;
                _meleeWeapon = null;
                _meleeStarted = -1000;
                ClearCarriedModel();
                if (_sniperScopeMask != null) UnityEngine.Object.Destroy(_sniperScopeMask);
                _sniperScopeMask = null;
                if (_firstPersonWeaponPass != null)
                {
                    _firstPersonWeaponPass.Release();
                    _firstPersonWeaponPass = null;
                }
                _firstPersonWeaponVisible = false;
                _firstPersonWeaponSource = null;
                _firstPersonWeaponVolume = false;
                _firstPersonWeaponMaterial = null;
                _firstPersonHandMaterial = null;
                _firstPersonWeaponMesh = null;
                _firstPersonHandMesh = null;
                _muzzleFlashMesh = null;
                _muzzleFlashMaterial = null;
            }

            static bool StabbingTool(Tool tool)
            {
                if (tool?.capacities == null) return false;
                for (int i = 0; i < tool.capacities.Count; i++)
                    if (tool.capacities[i].defName == "Stab") return true;
                return false;
            }

            void MeleePose(Pawn pawn, Thing weapon, bool unarmed, out Vector3 position, out Quaternion rotation)
            {
                int started = _view.controller.LastMeleeAttackTick;
                if (_meleePawn != pawn || _meleeWeapon != weapon || _meleeStarted != started)
                {
                    _meleePawn = pawn;
                    _meleeWeapon = weapon;
                    _meleeStarted = started;
                    string name = weapon?.def.defName ?? "";
                    _meleeStab = unarmed || name.IndexOf("Knife", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Dagger", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!_meleeStab && pawn.stances?.curStance is Stance_Cooldown cooldown && cooldown.verb?.IsMeleeAttack == true)
                        _meleeStab = StabbingTool(cooldown.verb.tool);
                    else if (!_meleeStab && weapon?.def.tools != null)
                    {
                        float stab = 0, strike = 0;
                        foreach (var tool in weapon.def.tools)
                            if (StabbingTool(tool)) stab = Mathf.Max(stab, tool.power);
                            else strike = Mathf.Max(strike, tool.power);
                        _meleeStab = stab > strike;
                    }
                }
                float progress = (Find.TickManager.TicksGame - started) / 24f;
                var idle = new Vector3(.30f, -.30f, .65f);
                var idleRotation = Quaternion.Euler(-15, -20, 65);
                position = idle;
                rotation = idleRotation;
                if (progress >= 0 && progress < 1)
                {
                    float windup = Mathf.SmoothStep(0, 1, Mathf.Clamp01(progress / .18f));
                    float attack = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.18f, .38f, progress));
                    float recovery = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.42f, 1, progress));
                    var readyPosition = idle + (_meleeStab ? new Vector3(.04f, .025f, -.10f) : new Vector3(.13f, .18f, -.08f));
                    var hitPosition = _meleeStab ? new Vector3(.08f, -.13f, 1.12f) : new Vector3(-.18f, -.35f, .92f);
                    var readyRotation = _meleeStab ? Quaternion.LookRotation(Vector3.left, Vector3.up) : Quaternion.Euler(-30, -35, 110);
                    var hitRotation = _meleeStab ? Quaternion.LookRotation(Vector3.left, Vector3.up) : Quaternion.Euler(20, 25, -35);
                    position = Vector3.Lerp(Vector3.Lerp(Vector3.Lerp(idle, readyPosition, windup), hitPosition, attack), idle, recovery);
                    rotation = Quaternion.Slerp(Quaternion.Slerp(Quaternion.Slerp(idleRotation, readyRotation, windup), hitRotation, attack), idleRotation, recovery);
                }
                rotation *= Quaternion.Euler(0, 0, -(weapon?.def.equippedAngleOffset ?? 0));
            }

            void ClearCarriedModel()
            {
                if (_carriedModelMaterials != null)
                    foreach (var material in _carriedModelMaterials)
                        if (material != null) UnityEngine.Object.Destroy(material);
                _carriedModelMaterials = null;
                _carriedModelParts = null;
                _view.DestroyHeldCard(_carriedModel);
                _carriedModel.Thing = null;
                _carriedModel.Source = null;
            }
        }
    }
}
