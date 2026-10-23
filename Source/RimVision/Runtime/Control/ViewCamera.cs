using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly ViewCamera _viewCamera = new ViewCamera();
        Vector3 focus => _viewCamera.Focus;
        float yaw => _viewCamera.Yaw;
        float pitch => _viewCamera.Pitch;
        bool cameraFlight => _viewCamera.IsFlying;

        sealed class ViewCamera
        {
            Vector3 _focus;
            float _yaw = 0;
            float _pitch = 48;
            float _distance = 32;
            bool _cameraFlight;
            float _flightStarted, _flightDuration, _flightFov;
            Vector3 _flightPosition;
            Quaternion _flightRotation;
            float _eyeHeight = 1.35f;
            float _sleepCameraBlend;
            float _sleepBreathingPhase;
            Vector3 _sleepCameraPosition;
            float _sleepCameraYaw;
            float _sleepEyelidBlend;
            Vector3 _sleepCameraStartPosition;
            Quaternion _sleepCameraStartRotation;
            Vector2 _pendingRecoil;
            int _recoilShotIndex;
            int _recoilLastShotTick = -1000;

            internal Vector3 Focus => _focus;
            internal float Yaw => _yaw;
            internal float Pitch => _pitch;
            internal float Distance => _distance;
            internal bool IsFlying => _cameraFlight;
            internal float SleepEyelidBlend => _sleepEyelidBlend;

            internal void SetFocus(Vector3 position) => _focus = position;

            internal void SetGroundFocus(Vector3 position)
            {
                _focus = position;
                _focus.y = 0;
            }

            internal void SetLook(float yaw, float pitch)
            {
                _yaw = yaw;
                _pitch = pitch;
            }

            internal void SetYaw(float yaw) => _yaw = yaw;

            internal void SetPitchAndDistance(float pitch, float distance)
            {
                _pitch = pitch;
                _distance = distance;
            }

            internal void RestoreView(Vector3 focus, float yaw, float pitch, float distance)
            {
                _focus = focus;
                _yaw = yaw;
                _pitch = pitch;
                _distance = distance;
            }

            internal void ResetOverview(Vector3 home) => RestoreView(home, 0, 48, 32);

            internal void AddYaw(float delta) => _yaw += delta;

            internal void AddPitch(float delta, float minimum, float maximum) =>
                _pitch = Mathf.Clamp(_pitch + delta, minimum, maximum);

            internal void SubtractPitch(float delta, float minimum, float maximum) =>
                _pitch = Mathf.Clamp(_pitch - delta, minimum, maximum);

            internal void ResetRecoil()
            {
                _pendingRecoil = Vector2.zero;
                _recoilShotIndex = 0;
                _recoilLastShotTick = -1000;
            }

            internal void AddRecoil(Pawn pawn, ThingWithComps weapon, Verb_LaunchProjectile verb, float stanceFactor)
            {
                int tick = Find.TickManager.TicksGame;
                if (tick - _recoilLastShotTick > Mathf.Max(30, verb.TicksBetweenBurstShots + 12))
                    _recoilShotIndex = 0;
                _recoilLastShotTick = tick;
                _recoilShotIndex++;
                float sustained = verb.BurstShotCount > 1 ? Mathf.Clamp01((_recoilShotIndex - 2f) / 8f) : 0;
                float accuracy = verb.verbProps.GetHitChanceFactor(weapon, 25);
                float skill = Mathf.Clamp01((pawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0) / 20f);
                float strength = Mathf.Lerp(1.4f, .7f, accuracy) * Mathf.Lerp(1.2f, .75f, skill) * stanceFactor *
                    Mathf.Clamp(verb.TicksBetweenBurstShots / 6f, .7f, 1.5f);
                float phase = weapon.def.shortHash % 17 * .2f;
                _pendingRecoil.x += Mathf.Sin((_recoilShotIndex - 2) * .65f + phase) * sustained * .65f * strength;
                _pendingRecoil.y += Mathf.Lerp(.7f, 1.8f, sustained) * strength;
            }

            internal void UpdateRecoil(float dt, bool active)
            {
                if (!active) { ResetRecoil(); return; }
                if (Find.TickManager.Paused) return;
                var step = _pendingRecoil * (1 - Mathf.Exp(-24 * dt));
                _pendingRecoil -= step;
                AddYaw(step.x);
                SubtractPitch(step.y, -75, 75);
            }

            internal void PanOverview(Vector3 movement, float speed, float dt, Map map)
            {
                _focus += Quaternion.Euler(0, _yaw, 0) * movement * speed * dt;
                _focus.x = Mathf.Clamp(_focus.x, 0, map.Size.x);
                _focus.z = Mathf.Clamp(_focus.z, 0, map.Size.z);
            }

            internal void Zoom(float scrollDelta)
            {
                var limits = HasFrame ? _frame.ZoomLimits : new Vector2(4, 110);
                _distance = Mathf.Clamp(_distance * Mathf.Exp(-scrollDelta * .13f), limits.x, limits.y);
            }

            internal void CaptureFlight(Camera camera)
            {
                _flightPosition = camera.transform.localPosition;
                _flightRotation = camera.transform.rotation;
                _flightFov = camera.fieldOfView;
            }

            internal void SetFlightOrigin(Vector3 position, Quaternion rotation, float fieldOfView)
            {
                _flightPosition = position;
                _flightRotation = rotation;
                _flightFov = fieldOfView;
            }

            internal void BeginFlight(float started, float duration)
            {
                ResetRecoil();
                _flightStarted = started;
                _flightDuration = duration;
                _cameraFlight = true;
            }

            internal void StopFlight() => _cameraFlight = false;

            internal void ResetEyeHeight() => _eyeHeight = 1.35f;

            internal void InitializeSleepCamera(PawnControl controller)
            {
                _sleepCameraPosition = controller.Position;
                _sleepCameraPosition.y = .025f + .18f;
                _sleepCameraYaw = _yaw;
                _sleepCameraBlend = controller.State == PawnControlState.Sleeping || controller.State == PawnControlState.LyingAwake ? 1 : 0;
            }

            internal void ResetSleepEyelids() => _sleepEyelidBlend = 0;

            readonly API.RimVisionCamera.Session _apiCamera = new API.RimVisionCamera.Session();
            internal string ProviderId => _apiCamera.PrimaryId;
            internal void CloseApi(API.RimVisionCameraExitReason reason)
            { var restore = _apiCamera.Close(reason); if (restore.HasValue) _distance = restore.Value; }
            API.RimVisionCameraFrame _frame;
            internal API.RimVisionCameraFrame Frame => _frame;
            internal Ray AimRay { get; private set; }
            internal bool HasFrame { get; private set; }
            internal bool Custom { get; private set; }

            internal bool CalculatePose(MapView view, out Vector3 position, out Quaternion rotation)
            {
                var requestedMode = view.MortarAiming ? API.RimVisionCameraMode.Mortar : view.falling ? API.RimVisionCameraMode.Falling :
                    view.FirstPerson ? API.RimVisionCameraMode.Pawn : API.RimVisionCameraMode.Overview;
                if (_cameraFlight || view.eventCamera) requestedMode |= API.RimVisionCameraMode.Transition;
                _apiCamera.Prepare(new API.RimVisionCameraContext(view.map, view.ControlledPawn, requestedMode, view.controller.Position,
                    _focus, _yaw, _pitch, _distance, _apiCamera.ViewId), ref _distance);
                bool completed = CalculateNativePose(view, out position, out rotation);
                var mode = requestedMode;
                var frame = new API.RimVisionCameraFrame
                {
                    Pose = new API.RimVisionPose(position, rotation), Pivot = position,
                    CaptureMouse = view.FirstPerson, OrbitDistance = _distance, ZoomLimits = new Vector2(4, 110),
                    ListenerPose = new API.RimVisionPose(position, rotation),
                    FieldOfView = view.camera.fieldOfView, NearClip = view.camera.nearClipPlane,
                    FarClip = view.camera.farClipPlane, Orthographic = view.camera.orthographic,
                    OrthographicSize = Mathf.Max(.01f, view.camera.orthographicSize), MovementYaw = _yaw,
                    ShowBody = view.MortarAiming || _cameraFlight && (position - view.controller.Position).sqrMagnitude > 4,
                    ShowFirstPersonItems = view.FirstPerson && !view.MortarAiming && !view.falling,
                    PitchLimits = view.FirstPerson ? new Vector2(-75, 75) : new Vector2(20, 80),
                    LookSensitivity = 1, AllowLook = true, AllowZoom = !view.FirstPerson, ObstaclePadding = .18f
                };
                var actorOrigin = position;
                if (view.FirstPerson && view.ControlledPawn != null)
                {
                    actorOrigin = view.controller.Position;
                    actorOrigin.y += Mathf.Clamp(_eyeHeight * Mathf.Sqrt(view.ControlledPawn.BodySize), .45f, 1.5f) +
                        API.RimVisionSpatial.Height(view.ControlledPawn, actorOrigin);
                }
                var context = new API.RimVisionCameraContext(view.map, view.ControlledPawn, mode, view.controller.Position,
                    _focus, _yaw, _pitch, _distance, _apiCamera.ViewId);
                Custom = API.RimVisionCamera.Apply(context, ref frame, _apiCamera);
                position = frame.Pose.Position;
                rotation = frame.Pose.Rotation.normalized;
                var excluded = API.RimVisionControl.TryGetContext(view.ControlledPawn, out var nativeContext) ? nativeContext.Carrier : null;
                if (frame.AvoidObstacles)
                {
                    var displacement = position - frame.Pivot;
                    float distance = displacement.magnitude;
                    if (distance > .001f)
                    {
                        var sweepDirection = displacement / distance;
                        var right = rotation * Vector3.right * frame.CollisionRadius;
                        var up = rotation * Vector3.up * frame.CollisionRadius;
                        float allowed = distance;
                        var filter = new API.RimVisionPickFilter(API.RimVisionPickMask.Floor | API.RimVisionPickMask.Roof | API.RimVisionPickMask.Buildings | API.RimVisionPickMask.Plants, excluded, true);
                        for (int i = 0; i < (frame.CollisionRadius > 0 ? 9 : 1); i++)
                        {
                            var offset = right * ((i % 3) - 1) + up * ((i / 3) - 1);
                            if (view.TryPickSpatialView(new Ray(frame.Pivot + offset, sweepDirection), distance, filter, out var obstacle))
                                allowed = Mathf.Min(allowed, Mathf.Max(0, obstacle.Distance - frame.ObstaclePadding));
                        }
                        position = frame.Pivot + sweepDirection * allowed;
                    }
                }
                frame.Pose = new API.RimVisionPose(position, rotation);
                _distance = frame.OrbitDistance;
                _frame = frame;
                HasFrame = true;
                view.camera.fieldOfView = frame.FieldOfView;
                view.camera.nearClipPlane = frame.NearClip;
                view.camera.farClipPlane = frame.FarClip;
                view.camera.orthographic = frame.Orthographic;
                view.camera.orthographicSize = frame.OrthographicSize;
                var direction = rotation * Vector3.forward;
                if (frame.AimFromActor && view.FirstPerson)
                {
                    var target = view.TryPickSpatialView(new Ray(position, direction), 500, new API.RimVisionPickFilter(API.RimVisionPickMask.All, excluded), out var hit) ? hit.Point : position + direction * 500;
                    direction = target - actorOrigin;
                    AimRay = new Ray(actorOrigin, direction.sqrMagnitude > .0001f ? direction.normalized : rotation * Vector3.forward);
                }
                else AimRay = new Ray(position, direction);
                return completed;
            }

            internal bool CalculateNativePose(MapView view, out Vector3 position, out Quaternion rotation)
            {
                view.camera.orthographic = false;
                view.camera.farClipPlane = Mathf.Max(view.map.Size.x, view.map.Size.z) *
                    (Rim3DSettings.Current.NeighborMaps ? 4.5f : 2) + 100;
                if (view.MortarAiming)
                {
                    view._mortarTargeting.Pose(view.camera, out position, out rotation);
                    return false;
                }
                rotation = Quaternion.Euler(_pitch, _yaw, 0);
                var settings = Rim3DSettings.Current;
                view.camera.fieldOfView = view.FirstPerson ? Mathf.Lerp(view.camera.fieldOfView, view.WeaponAimFieldOfView(settings.FieldOfView) +
                    (settings.EffectsEnabled(settings.CameraBob) && view.controller.Sprinting && !view.controller.Sneaking && view.controller.Stamina > .05f ? 5 * (1 - view._aimBlend) : 0),
                    1 - Mathf.Exp(-Time.unscaledDeltaTime * 16)) : 55;
                view.camera.nearClipPlane = view.FirstPerson ? .04f : .15f;
                if (view.falling)
                {
                    float progress = Mathf.Clamp01((Time.unscaledTime - view.fallStarted) / FallDuration);
                    float drop = progress * progress * (3 - 2 * progress);
                    position = Vector3.Lerp(view.fallPosition, view.fallenPosition, drop);
                    rotation = Quaternion.Slerp(view.fallRotation, view.fallenRotation, drop);
                    // Brief ground impact settles before the blackout begins.
                    position.y += Mathf.Sin(Mathf.Clamp01((progress - .8f) / .2f) * Mathf.PI) * .025f;
                }
                else if (view.FirstPerson)
                {
                    position = view.controller.Position;
                    bool sitting = position.ToIntVec3().GetEdifice(view.map)?.def.building?.isSittable == true;
                    float targetEyeHeight = view.controller.Sneaking ? .78f : sitting ? 1f : 1.35f;
                    _eyeHeight = Mathf.Lerp(_eyeHeight, targetEyeHeight, 1 - Mathf.Exp(-Time.unscaledDeltaTime * 12));
                    position.y += Mathf.Clamp(_eyeHeight * Mathf.Sqrt(view.ControlledPawn.BodySize), .45f, 1.5f) +
                        API.RimVisionSpatial.Height(view.ControlledPawn, position);
                    if (settings.EffectsEnabled(settings.CameraBob)) position += view.cameraBob;
                    if (settings.EffectsEnabled(settings.DamageShake)) rotation *= Quaternion.Euler(view.damageKick * -1.4f, 0, view.damageKick * .8f);
                    if (view.controller.InputBlocked)
                    {
                        _yaw = Mathf.LerpAngle(_yaw, view.ControlledPawn.Rotation.AsAngle, 1 - Mathf.Exp(-Time.unscaledDeltaTime * 5));
                        float targetPitch = view.ControlledPawn.Downed ? -35 : view.ControlledPawn.CurJob?.def == JobDefOf.Vomit ? 55 : 4;
                        _pitch = Mathf.Lerp(_pitch, targetPitch, 1 - Mathf.Exp(-Time.unscaledDeltaTime * 3));
                        if (view.ControlledPawn.Downed) position.y = .25f + API.RimVisionSpatial.Height(view.ControlledPawn, position);
                        rotation = Quaternion.Euler(_pitch, _yaw, settings.EffectsEnabled(settings.CameraBob) ? Mathf.Sin(view.heartbeatPhase * Mathf.PI) * 1.2f : 0);
                    }
                    bool lying = view.controller.State == PawnControlState.Sleeping || view.controller.State == PawnControlState.LyingAwake;
                    if (lying && _sleepCameraBlend <= 0)
                    {
                        _sleepCameraStartPosition = view.CameraMapPosition;
                        _sleepCameraStartRotation = view.camera.transform.rotation;
                    }
                    _sleepCameraBlend = Mathf.MoveTowards(_sleepCameraBlend, lying ? 1 : 0, Time.unscaledDeltaTime * 1.2f);
                    _sleepEyelidBlend = Mathf.MoveTowards(_sleepEyelidBlend,
                        view.controller.State == PawnControlState.Sleeping && _sleepCameraBlend > .65f ? 1 : 0,
                        Time.unscaledDeltaTime * (lying ? 1.5f : 3f));
                    if (_sleepCameraBlend > 0)
                    {
                        if (lying)
                        {
                            var pawn = view.ControlledPawn;
                            var bed = pawn.CurrentBed();
                            _sleepCameraYaw = bed != null ? bed.Rotation.AsAngle : pawn.Rotation.AsAngle;
                            _sleepCameraPosition = bed != null ? BedPawnPosition(pawn, bed, true) : view.controller.Position;
                            _sleepCameraPosition.y = (bed != null ? view.PawnBedSurfaceHeight(pawn, bed, true, .8f, .8f) : .025f) + .18f;
                            _sleepCameraPosition -= Quaternion.Euler(0, _sleepCameraYaw, 0) * Vector3.forward * .22f;
                        }
                        float bedYaw = _sleepCameraYaw;
                        var bedPosition = _sleepCameraPosition;
                        if (!Find.TickManager.Paused) _sleepBreathingPhase += Time.unscaledDeltaTime * 1.6f;
                        float breath = settings.EffectsEnabled(settings.CameraBob) && view.controller.State == PawnControlState.Sleeping ? Mathf.Sin(_sleepBreathingPhase) : 0;
                        bedPosition.y += breath * .012f;
                        float blend = Mathf.SmoothStep(0, 1, _sleepCameraBlend);
                        position = Vector3.Lerp(lying ? _sleepCameraStartPosition : position, bedPosition, blend);
                        rotation = Quaternion.Slerp(lying && _sleepCameraBlend < 1 ? _sleepCameraStartRotation : rotation,
                            Quaternion.Euler(-70 + _pitch * .2f + breath * .6f, bedYaw, breath * .4f), blend);
                    }
                    API.RimVisionPresentation.Apply(view.ControlledPawn, true, ref position, ref rotation);
                    _focus = position; _focus.y = 0;
                }
                else position = _focus - rotation * Vector3.forward * _distance;
                if (view.falling) _cameraFlight = false;
                if (_cameraFlight)
                {
                    float progress = Mathf.Clamp01((Time.unscaledTime - _flightStarted) / _flightDuration);
                    // Zero speed and acceleration at both ends soften perspective changes.
                    float blend = view.eventCamera ? progress * progress * (3 - 2 * progress) :
                        progress * progress * progress * (progress * (progress * 6 - 15) + 10);
                    position = Vector3.Lerp(_flightPosition, position, blend);
                    rotation = Quaternion.Slerp(_flightRotation, rotation, blend);
                    view.camera.fieldOfView = Mathf.Lerp(_flightFov, view.FirstPerson ? settings.FieldOfView : 55, blend);
                    if (progress >= 1)
                    {
                        _cameraFlight = false;
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
