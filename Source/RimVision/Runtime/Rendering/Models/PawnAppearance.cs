using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        int pawnTextureResolutionLimit;

        static int PawnSourceResolution(Pawn pawn, Rot4 facing)
        {
            pawn.Drawer.renderer.EnsureGraphicsInitialized();
            return Mathf.Max(32, PawnNodeResolution(pawn.Drawer.renderer.renderTree.rootNode, pawn, facing));
        }

        static int PawnNodeResolution(PawnRenderNode node, Pawn pawn, Rot4 facing)
        {
            if (node == null) return 0;
            int resolution = PawnGraphicResolution(node.PrimaryGraphic, pawn, facing);
            var graphics = node.Graphics;
            if (graphics != null)
                for (int i = 0; i < graphics.Count; i++)
                    resolution = Mathf.Max(resolution, PawnGraphicResolution(graphics[i], pawn, facing));
            if (node.children != null)
                foreach (var child in node.children)
                    resolution = Mathf.Max(resolution, PawnNodeResolution(child, pawn, facing));
            return resolution;
        }

        static int PawnGraphicResolution(Graphic graphic, Pawn pawn, Rot4 facing)
        {
            var material = graphic?.MatAt(facing, pawn);
            var texture = material?.mainTexture;
            if (texture == null || texture == BaseContent.BadTex) return 0;
            var scale = material.mainTextureScale;
            return Mathf.CeilToInt(Mathf.Max(texture.width * Mathf.Abs(scale.x),
                texture.height * Mathf.Abs(scale.y)));
        }

        void EnsurePawnCaptureTexture(ref RenderTexture texture, Pawn pawn, Rot4 facing, bool headOnly)
        {
            int limit = Mathf.Min(Mathf.Clamp(Rim3DSettings.Current.MaxPawnTextureResolution, 128, 4096),
                SystemInfo.maxTextureSize);
            int width = Mathf.Min(PawnSourceResolution(pawn, facing), headOnly ? limit : limit * 4 / 5);
            int height = headOnly ? width : Mathf.RoundToInt(width * 1.25f);
            if (texture != null && texture.width == width && texture.height == height) return;
            if (texture != null)
            {
                pawnTextures.Remove(texture);
                texture.Release();
                UnityEngine.Object.Destroy(texture);
            }
            texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp, useMipMap = false };
            pawnTextures.Add(texture);
            texture.Create();
        }

        sealed class PawnView
        {
            public readonly PawnDirection[] Directions = new PawnDirection[4];
            public Material Material;
            public Mesh Mesh;
            public int Direction = -1;
            public bool Prone;
            public bool HeadOnly;
            public Building_Bed Bed;
            public float Width, Height, BedHeight;
            public PawnDirection BedHead;
            public Vector3 WalkPosition;
            public float WalkPhase, WalkBlend;
            public float CrouchBlend;
            public int CrouchFrame = -1;
            public int WalkTick = -1, LastWalkTick;
            public bool AnimalModelChecked;
            public int AnimalModelDetail = -1;
            public OptionalModelLibrary.Model AnimalModel;
            public Graphic AnimalGraphic;
            public Color AnimalColor;
            public float AnimalScale;
            public Bounds AnimalBounds;
            public Dictionary<Material, Geometry> AnimalHitGeometry;
            public float AnimalYaw, AnimalTargetYaw;
            public int AnimalYawFrame = -1;
        }

        sealed class PawnDirection
        {
            public RenderTexture Texture;
            public bool Valid;
            public float Padding;
            public float VisibleHeight;
            public float CaptureScale = 1;
            public byte[] Mask;
            public Rect VisibleBounds = new Rect(0, 0, 1, 1);
        }

        readonly Dictionary<Pawn, PawnView> pawnViews = new Dictionary<Pawn, PawnView>();
        readonly HashSet<Pawn> dirtyPawnAppearances = new HashSet<Pawn>();
        readonly HashSet<Pawn> pronePawns = new HashSet<Pawn>();
        readonly List<Pawn> pawnAppearanceWork = new List<Pawn>();

        // The upright surface faces the camera; its artwork shows the pawn's
        // world direction relative to that camera, just like RimWorld's Rot4 views.
        Rot4 PawnViewDirection(Pawn pawn, bool prone) => prone ? Rot4.South :
            Rot4.FromAngleFlat(pawn.Rotation.AsAngle - yaw);

        static bool PawnIsProne(Pawn pawn) => pawn.Dead || pawn.Downed || pawn.GetPosture() != PawnPosture.Standing;

        static float PawnPortraitScale(Pawn pawn, Rot4 facing)
        {
            if (pawn.RaceProps.Humanlike) return 1;
            var graphic = pawn.Drawer.renderer.BodyGraphic;
            var size = graphic?.drawSize ?? pawn.ageTracker.CurKindLifeStage.bodyGraphicData?.drawSize ?? Vector2.one;
            var offset = graphic?.DrawOffset(facing) ?? Vector3.zero;
            // The 4:5 portrait has a narrower horizontal field. Fit either
            // orientation, including the rotated portrait of a downed animal.
            float extent = Mathf.Max(size.x, size.y) + 2 * Mathf.Max(Mathf.Abs(offset.x), Mathf.Abs(offset.z) + .1f);
            return Mathf.Max(1, extent * .75f / (2 * (128f / 160f)) * 1.1f);
        }

        static void CapturePawnAppearance(Pawn pawn, RenderTexture target, Rot4 facing,
            float captureScale, bool headOnly)
        {
            var cacheCamera = Find.PawnCacheCamera;
            var previousTarget = RenderTexture.active;
            var previousClear = cacheCamera.clearFlags;
            var previousColor = cacheCamera.backgroundColor;
            int previousMask = cacheCamera.cullingMask;
            try
            {
                cacheCamera.clearFlags = CameraClearFlags.SolidColor;
                cacheCamera.backgroundColor = Color.clear;
                cacheCamera.cullingMask = 0;
                // Match the native portrait's corpse pose, while rendering into
                // our own cache so HD textures are not stored twice.
                bool rotated = !headOnly && pawn.health.State != PawnHealthState.Mobile &&
                    !pawn.ageTracker.CurLifeStage.alwaysDowned;
                var offset = headOnly ? -pawn.Drawer.renderer.BaseHeadOffsetAt(facing) :
                    rotated ? new Vector3(-.18f, 0, -.18f) : Vector3.zero;
                Find.PawnCacheRenderer.RenderPawn(pawn, target,
                    headOnly ? Vector3.zero : new Vector3(0, 0, .1f),
                    headOnly ? 2.5f : .75f / captureScale, rotated ? 85 : 0, facing,
                    pawn.health.hediffSet.HasHead, true, !headOnly, !headOnly,
                    offset, null, null, false);
            }
            finally
            {
                cacheCamera.clearFlags = previousClear;
                cacheCamera.backgroundColor = previousColor;
                cacheCamera.cullingMask = previousMask;
                RenderTexture.active = previousTarget;
            }
        }

        float BedSurfaceHeight(Building_Bed bed)
        {
            var model = optionalModels?.Find(bed.def.defName, bed.Graphic.path);
            if (model == null) return IsGroundSpot(bed.def) ? .025f : .65f;
            float height = model.Height, largestArea = 0;
            // Use the broad mattress surface, rather than a taller headboard.
            foreach (var face in model.Faces)
            {
                if (face.Normal.y < .99f) continue;
                var min = model.Vertices[face.Indices[0]];
                var max = min;
                foreach (int index in face.Indices)
                {
                    min = Vector3.Min(min, model.Vertices[index]);
                    max = Vector3.Max(max, model.Vertices[index]);
                }
                float area = (max.x - min.x) * (max.z - min.z);
                if (area <= largestArea) continue;
                largestArea = area;
                height = max.y + model.HeightOffset;
            }
            return height;
        }

        internal static Vector3 BedPawnPosition(Pawn pawn, Building_Bed bed, bool headOnly)
        {
            // RimWorld places each sleeper on their own head cell, including
            // both slots of a double bed. DrawPos may contain animation offsets.
            var position = pawn.Position.ToVector3Shifted();
            if (!headOnly)
            {
                var along = bed.Rotation.AsQuat * Vector3.forward;
                position += along * Vector3.Dot(bed.TrueCenter() - position, along);
            }
            return position;
        }

        float PawnBedSurfaceHeight(Pawn pawn, Building_Bed bed, bool headOnly, float width, float length)
        {
            var graphic = bed.Graphic;
            var model = graphic != null ? ThingModel(bed, graphic) : null;
            if (model == null) return IsGroundSpot(bed.def) ? .025f : .65f;
            var position = Quaternion.Inverse(bed.Rotation.AsQuat) *
                (BedPawnPosition(pawn, bed, headOnly) - bed.TrueCenter());
            position.x += bed.def.size.x * .5f;
            position.z += bed.def.size.z * .5f;
            int links = OptionalModelLinks(bed, graphic, model);
            float height = 0;
            for (int z = 0; z < 5; z++)
                for (int x = 0; x < 5; x++)
                {
                    var sample = position + new Vector3((x * .25f - .5f) * width, 0,
                        (z * .25f - .5f) * length);
                    height = Mathf.Max(height, model.SurfaceHeight(sample, links));
                }
            return height > 0 ? height : BedSurfaceHeight(bed);
        }

        static Rect PawnVisibleBounds(byte[] mask)
        {
            if (mask == null) return new Rect(0, 0, 1, 1);
            int minX = 64, minY = 80, maxX = -1, maxY = -1;
            for (int y = 0; y < 80; y++)
                for (int x = 0; x < 64; x++)
                {
                    if (mask[y * 64 + x] < 64) continue;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            return maxX < minX ? new Rect(0, 0, 1, 1) :
                Rect.MinMaxRect(minX / 64f, minY / 80f, (maxX + 1) / 64f, (maxY + 1) / 80f);
        }

        void PositionPawnCard(Pawn pawn, Thing body, Transform card)
        {
            var view = pawnViews[pawn];
            var position = body.DrawPos;
            position.y = body is Corpse ? GroundThingSurfaceHeight(position) : API.RimVisionSpatial.Height(pawn, position);
            float angle = view.AnimalModel != null ? pawn.Rotation.AsAngle : view.Prone ? body.Rotation.AsAngle : yaw;
            if (body is Corpse && workSurfaceIngredients.TryGetValue(body, out var workTable))
            {
                position = workTable.TrueCenter();
                position.y = TableSurfaceHeight(workTable);
                angle = workTable.Rotation.AsAngle;
            }
            if (view.Bed != null)
            {
                var bed = view.Bed;
                angle = bed.Rotation.AsAngle + 180;
                position = BedPawnPosition(pawn, bed, view.HeadOnly);
                position.y = view.BedHeight;
            }
            if (view.AnimalModel != null && !view.Prone && view.Bed == null && !(body is Corpse))
                angle = AnimalModelAngle(pawn, view, position);
            if (!(body is Corpse)) position.y += PawnCrouchOffset(pawn);
            float roll = 0;
            var animation = new API.RimVisionPawnAnimationFrame
            { Pose = new API.RimVisionPose(position, Quaternion.Euler(0, angle, 0)), Scale = Vector3.one, AnimateWalk = true };
            if (!(body is Corpse)) API.RimVisionPresentation.AnimatePawn(new API.RimVisionAnimationContext(pawn, null, map,
                ControlledPawn, false, false, pawn == ControlledPawn ? _aimBlend : 0, pawn == ControlledPawn ? _weaponRecoil : 0,
                pawn == ControlledPawn ? controller.LastMeleeAttackTick : -1000), ref animation);
            position = animation.Pose.Position;
            if (animation.AnimateWalk) AnimatePawnWalk(pawn, body, view, ref position, angle, ref roll);
            if (view.AnimalModel != null && view.Prone)
            {
                roll = 90;
                position.y -= view.AnimalBounds.min.x;
            }
            var rotation = animation.Pose.Rotation * Quaternion.Euler(0, 0, roll);
            if (!(body is Corpse)) API.RimVisionPresentation.Apply(pawn, false, ref position, ref rotation);
            card.localPosition = position;
            card.localRotation = rotation;
            card.localScale = animation.Scale;
        }

        float PawnCrouchOffset(Pawn pawn)
        {
            if (!Rim3DMultiplayer.Active || !pawnViews.TryGetValue(pawn, out var view)) return 0;
            var control = pawn == ControlledPawn ? controller : PawnControl.ForPawn(pawn);
            bool crouching = !view.Prone && view.Bed == null && control?.IsCrouching == true;
            if (view.CrouchFrame != Time.frameCount)
            {
                view.CrouchFrame = Time.frameCount;
                view.CrouchBlend = Mathf.MoveTowards(view.CrouchBlend, crouching ? 1 : 0, Mathf.Min(Time.unscaledDeltaTime, .1f) * 9);
            }
            return view.Prone || view.Bed != null ? 0 : -PawnControl.CrouchDepth * view.CrouchBlend;
        }

        static float AnimalModelAngle(Pawn pawn, PawnView view, Vector3 position)
        {
            if (view.AnimalYawFrame == Time.frameCount) return view.AnimalYaw;
            var movement = position - view.WalkPosition;
            movement.y = 0;
            int elapsedTicks = Find.TickManager.TicksGame - view.WalkTick;
            bool reset = view.AnimalYawFrame < 0 || view.WalkTick < 0 || elapsedTicks < 0 ||
                elapsedTicks > 30 || movement.sqrMagnitude > 2.25f;
            if (reset)
                view.AnimalYaw = view.AnimalTargetYaw = pawn.Rotation.AsAngle;
            else if (!Find.TickManager.Paused)
            {
                if (pawn.pather != null && pawn.pather.Moving)
                {
                    if (movement.sqrMagnitude > .00000001f)
                        view.AnimalTargetYaw = Mathf.Atan2(movement.x, movement.z) * Mathf.Rad2Deg;
                }
                else view.AnimalTargetYaw = pawn.Rotation.AsAngle;
                view.AnimalYaw = Mathf.LerpAngle(view.AnimalYaw, view.AnimalTargetYaw,
                    1 - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, .1f) * 10));
            }
            view.AnimalYawFrame = Time.frameCount;
            return view.AnimalYaw;
        }

        static void AnimatePawnWalk(Pawn pawn, Thing body, PawnView view, ref Vector3 position, float angle, ref float roll)
        {
            int tick = Find.TickManager.TicksGame;
            var movement = position - view.WalkPosition;
            movement.y = 0;
            float distanceSquared = movement.sqrMagnitude;
            int elapsedTicks = tick - view.WalkTick;
            // Track the unanimated position. Teleports and hidden/respawned pawns
            // start a fresh stride instead of advancing through the skipped path.
            bool reset = view.WalkTick < 0 || elapsedTicks < 0 || elapsedTicks > 30 ||
                distanceSquared > 2.25f || view.Prone || view.Bed != null || body is Corpse;
            view.WalkPosition = position;
            view.WalkTick = tick;
            if (reset)
            {
                view.WalkPhase = (pawn.thingIDNumber & 255) * (Mathf.PI * 2 / 256);
                view.WalkBlend = 0;
                view.LastWalkTick = tick - 3;
                return;
            }

            bool humanlike = pawn.RaceProps.Humanlike;
            float size = Mathf.Clamp(view.Height / (1.6f * PawnVisualScale), .45f, 1.6f);
            if (!Find.TickManager.Paused)
            {
                float distance = Mathf.Sqrt(distanceSquared);
                if (distance > .0001f)
                {
                    float stride = (humanlike ? 1.8f : 2.2f) * size;
                    view.WalkPhase = (view.WalkPhase + distance * (Mathf.PI * 2 / stride)) % (Mathf.PI * 2);
                    view.LastWalkTick = tick;
                }
                // A short grace period bridges frames between simulation ticks.
                bool walking = distance > .0001f ||
                    (tick - view.LastWalkTick <= 2 && pawn.pather != null && pawn.pather.Moving);
                float blendStep = Mathf.Max(Mathf.Clamp(elapsedTicks, 0, 12) / 60f, distance / 3f) * 8;
                view.WalkBlend = Mathf.MoveTowards(view.WalkBlend, walking ? 1 : 0, blendStep);
            }
            if (view.WalkBlend <= 0) return;

            float sway = Mathf.Sin(view.WalkPhase);
            float lift = .5f - .5f * Mathf.Cos(view.WalkPhase * 2);
            float amount = view.WalkBlend * size;
            position.y += lift * (humanlike ? .028f : .022f) * amount;
            position += Quaternion.Euler(0, angle, 0) * Vector3.right * (sway * .012f * amount);
            roll = sway * (humanlike ? 1.15f : .7f) * view.WalkBlend;
        }

        float? RaycastBed(Building_Bed bed, Ray ray)
        {
            if (_objectPresentation.TryRaycastOptionalModel(bed, ray, out var distance))
                return float.IsPositiveInfinity(distance) ? (float?)null : distance;
            float height = BedSurfaceHeight(bed);
            var center = bed.TrueCenter();
            center.y = height * .5f;
            var size = bed.RotatedSize;
            return new Bounds(center, new Vector3(size.x, height, size.z)).IntersectRay(ray, out distance)
                ? distance : (float?)null;
        }

        float? RaycastPawnSprite(Pawn pawn, Ray ray) => RaycastPawnSprite(pawn, ray, true);

        float? RaycastPawnSprite(Pawn pawn, Ray ray, bool includeGroundBodyVolume)
        {
            if (pawnViews.TryGetValue(pawn, out var modelView) && modelView.AnimalModel != null &&
                pawnCards.TryGetValue(pawn, out var modelCard))
            {
                var inverse = Quaternion.Inverse(modelCard.localRotation);
                var scale = includeGroundBodyVolume ? Vector3.one : modelCard.localScale;
                var origin = inverse * (ray.origin - modelCard.localPosition);
                var direction = inverse * ray.direction;
                origin = new Vector3(origin.x / scale.x, origin.y / scale.y, origin.z / scale.z);
                direction = new Vector3(direction.x / scale.x, direction.y / scale.y, direction.z / scale.z);
                float factor = direction.magnitude;
                if (factor < .000001f) return null;
                var modelRay = new Ray(origin, direction / factor);
                if (!modelView.AnimalBounds.IntersectRay(modelRay, out _)) return null;
                var hit = RaycastAnimalGeometry(modelView, modelRay);
                return hit.HasValue ? hit.Value / factor : (float?)null;
            }
            // Lying pawns have a body volume above the mattress, including the
            // torso under covers. A flat alpha-tested head card cannot represent it.
            if (PawnIsProne(pawn) && (includeGroundBodyVolume || pawn.CurrentBed() != null))
            {
                var bed = pawn.CurrentBed();
                var center = pawn.DrawPos;
                var bodyRotation = Quaternion.Euler(0, pawn.Rotation.AsAngle, 0);
                float bodyWidth = .8f, length = 1.6f * PawnVisualScale;
                float surface = 0;
                if (pawnCards.TryGetValue(pawn, out var proneCard) && pawnViews.TryGetValue(pawn, out var proneView))
                {
                    center = proneCard.localPosition;
                    bodyRotation = proneCard.localRotation;
                    bodyWidth = proneView.Width;
                    length = proneView.Height;
                }
                if (bed != null)
                {
                    surface = pawnViews.TryGetValue(pawn, out var bedView) && bedView.Bed == bed
                        ? bedView.BedHeight : BedSurfaceHeight(bed);
                    bodyRotation = Quaternion.Euler(0, bed.Rotation.AsAngle + 180, 0);
                    center = BedPawnPosition(pawn, bed, false);
                    bodyWidth = .8f;
                    length = bed.def.size.z;
                }
                center.y = surface + .045f + .14f;
                var inverse = Quaternion.Inverse(bodyRotation);
                var localRay = new Ray(inverse * (ray.origin - center), inverse * ray.direction);
                return new Bounds(Vector3.zero, new Vector3(bodyWidth, .28f, length)).IntersectRay(localRay, out var bodyHit)
                    ? bodyHit : (float?)null;
            }
            if (!pawnCards.TryGetValue(pawn, out var card))
            {
                float bodyHeight = PawnIsProne(pawn) ? .35f : 1.6f * PawnVisualScale;
                var center = pawn.DrawPos; center.y = bodyHeight * .5f;
                return new Bounds(center, new Vector3(.8f, bodyHeight, .8f)).IntersectRay(ray, out var hit) ? hit : (float?)null;
            }
            var view = pawnViews[pawn];
            var material = view.Material;
            if (!spriteMasks.TryGetValue(material, out var mask)) return null;
            float width = view.Width, height = view.Height;
            var position = card.localPosition;
            bool prone = pronePawns.Contains(pawn);
            var rotation = card.localRotation;
            var normal = prone ? Vector3.up : rotation * Vector3.back;
            if (prone) position.y += .045f;
            if (!new Plane(normal, position).Raycast(ray, out var distance)) return null;
            var local = Quaternion.Inverse(rotation) * (ray.GetPoint(distance) - position);
            if (!includeGroundBodyVolume)
                local = new Vector3(local.x / card.localScale.x, local.y / card.localScale.y, local.z / card.localScale.z);
            float u = local.x / width + .5f;
            float v = prone ? local.z / height + .5f : local.y / height + BottomPadding(material);
            if (u < 0 || u >= 1 || v < 0 || v >= 1) return null;
            return mask[Mathf.FloorToInt(v * 80) * 64 + Mathf.FloorToInt(u * 64)] >= 64 ? distance : (float?)null;
        }

        float? RaycastAnimalGeometry(PawnView view, Ray ray)
        {
            if (view.AnimalHitGeometry == null) return null;
            float nearest = float.PositiveInfinity;
            foreach (var group in view.AnimalHitGeometry)
            {
                var geometry = group.Value;
                spriteMasks.TryGetValue(group.Key, out var mask);
                float opaque = group.Key.GetFloat("_Opaque");
                float alpha = group.Key.color.a;
                var texture = group.Key.mainTexture;
                for (int i = 0; i < geometry.Indices.Count; i += 3)
                {
                    int a = geometry.Indices[i], b = geometry.Indices[i + 1], c = geometry.Indices[i + 2];
                    var edge1 = geometry.Vertices[b] - geometry.Vertices[a];
                    var edge2 = geometry.Vertices[c] - geometry.Vertices[a];
                    var cross = Vector3.Cross(ray.direction, edge2);
                    float determinant = Vector3.Dot(edge1, cross);
                    if (Mathf.Abs(determinant) < .00000001f) continue;
                    float inverse = 1 / determinant;
                    var offset = ray.origin - geometry.Vertices[a];
                    float u = Vector3.Dot(offset, cross) * inverse;
                    if (u < 0 || u > 1) continue;
                    var offsetCross = Vector3.Cross(offset, edge1);
                    float v = Vector3.Dot(ray.direction, offsetCross) * inverse;
                    if (v < 0 || u + v > 1) continue;
                    float distance = Vector3.Dot(edge2, offsetCross) * inverse;
                    if (distance < 0 || distance >= nearest) continue;
                    if (mask != null && opaque < .25f)
                    {
                        var uv = geometry.Uvs[a] * (1 - u - v) + geometry.Uvs[b] * u + geometry.Uvs[c] * v;
                        if (texture != null && texture.wrapMode == TextureWrapMode.Repeat)
                            uv = new Vector2(Mathf.Repeat(uv.x, 1), Mathf.Repeat(uv.y, 1));
                        else if (texture != null && texture.wrapMode == TextureWrapMode.Mirror)
                            uv = new Vector2(Mathf.PingPong(uv.x, 1), Mathf.PingPong(uv.y, 1));
                        int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * 64), 0, 63);
                        int y = Mathf.Clamp(Mathf.FloorToInt(uv.y * 80), 0, 79);
                        if (Mathf.Lerp(mask[y * 64 + x] / 255f * alpha, 1, opaque) < .25f) continue;
                    }
                    nearest = distance;
                }
            }
            return float.IsPositiveInfinity(nearest) ? (float?)null : nearest;
        }

        void OnPawnChanged(Pawn pawn)
        {
            if (pawn != null && _compassHeads.TryGetValue(pawn, out var head)) head.Valid = false;
            if (pawn != null && heldPawnPortraits.TryGetValue(pawn, out var held)) held.Valid = false;
            if (pawn != null && (pawn.MapHeld == map || pawnCards.ContainsKey(pawn)))
                dirtyPawnAppearances.Add(pawn);
        }

        void UpdatePawnAppearances()
        {
            UpdateAnimalPlantModelSettings();
            int limit = Rim3DSettings.Current.MaxPawnTextureResolution;
            if (pawnTextureResolutionLimit != limit)
            {
                pawnTextureResolutionLimit = limit;
                foreach (var pawn in pawnViews.Keys) dirtyPawnAppearances.Add(pawn);
                foreach (var portrait in heldPawnPortraits.Values) portrait.Valid = false;
            }
            if (dirtyPawnAppearances.Count == 0) return;
            pawnAppearanceWork.Clear();
            pawnAppearanceWork.AddRange(dirtyPawnAppearances);
            dirtyPawnAppearances.Clear();
            foreach (var pawn in pawnAppearanceWork)
            {
                // Apparel, wounds and pose invalidate every cached direction.
                if (pawnViews.TryGetValue(pawn, out var view))
                {
                    view.AnimalModelChecked = false;
                    foreach (var direction in view.Directions)
                        if (direction != null) direction.Valid = false;
                    if (view.BedHead != null) view.BedHead.Valid = false;
                }
                Thing body = pawn.Corpse != null ? (Thing)pawn.Corpse : pawn;
                if (!body.Spawned || body.Map != map || map.fogGrid.IsFogged(body.Position)) continue;
                UpdatePawnView(pawn, body);
            }
            pawnAppearanceWork.Clear();
        }

        void RemovePawnGeometry(Pawn pawn, PawnView view)
        {
            if (pawnCards.TryGetValue(pawn, out var card))
            {
                RemoveMovingShadows(card);
                foreach (var filter in card.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && meshes.Remove(filter.sharedMesh))
                        UnityEngine.Object.Destroy(filter.sharedMesh);
                UnityEngine.Object.Destroy(card.gameObject);
                pawnCards.Remove(pawn);
            }
            view.Mesh = null;
            view.Direction = -1;
            view.AnimalModel = null;
            view.AnimalHitGeometry = null;
        }

        bool TryUpdateAnimalModel(Pawn pawn, Thing body, PawnView view, bool prone, Building_Bed bed)
        {
            int detail = _objectPresentation.ModelDetailAtDistance(Vector3.Distance(CameraMapPosition, body.TrueCenter()), view.AnimalModelDetail);
            if (detail != view.AnimalModelDetail)
            {
                view.AnimalModelDetail = detail;
                view.AnimalModelChecked = false;
            }
            if (!view.AnimalModelChecked)
            {
                view.AnimalModelChecked = true;
                try
                {
                    pawn.Drawer.renderer.EnsureGraphicsInitialized();
                    var graphic = pawn.Drawer.renderer.BodyGraphic;
                    var model = Rim3DSettings.Current.AnimalPlantModels && graphic != null ? ThingModel(pawn, graphic) : null;
                    if (detail == 3) model = null;
                    var color = model != null ? OptionalModelTint(pawn, graphic, Rot4.North) : pawn.DrawColor;
                    var scale = model != null ? OptionalModelScale(pawn, graphic) : 1;
                    if (model != view.AnimalModel || graphic != view.AnimalGraphic || color != view.AnimalColor || scale != view.AnimalScale)
                    {
                        if (view.AnimalModel != null) RemovePawnGeometry(pawn, view);
                        view.AnimalGraphic = graphic;
                        view.AnimalColor = color;
                        view.AnimalScale = scale;
                        if (model != null)
                        {
                            var groups = new Dictionary<Material, Geometry>();
                            if (TryDrawOptionalModel(pawn, graphic, Vector3.zero, groups, model) && groups.Count > 0)
                            {
                                RemovePawnGeometry(pawn, view);
                                var modelRoot = new GameObject("RimVision animal: " + pawn.LabelShort)
                                    { layer = Layer, hideFlags = HideFlags.DontSave };
                                modelRoot.transform.SetParent(root.transform, false);
                                pawnCards.Add(pawn, modelRoot.transform);
                                var previousParent = geometryParent;
                                geometryParent = modelRoot.transform;
                                try
                                {
                                    bool hasBounds = false;
                                    foreach (var group in groups)
                                    {
                                        CreateMesh(group.Key, group.Value, "Animal model part");
                                        if (group.Key.GetFloat("_Opaque") < .25f) BottomPadding(group.Key);
                                        foreach (var vertex in group.Value.Vertices)
                                        {
                                            if (!hasBounds) { view.AnimalBounds = new Bounds(vertex, Vector3.zero); hasBounds = true; }
                                            else view.AnimalBounds.Encapsulate(vertex);
                                        }
                                    }
                                }
                                finally { geometryParent = previousParent; }
                                view.AnimalModel = model;
                                view.AnimalHitGeometry = groups;
                                view.Height = view.AnimalBounds.size.y;
                                view.Width = view.AnimalBounds.size.x;
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    RemovePawnGeometry(pawn, view);
                    Log.Warning("RimVision animal model: " + error.Message);
                }
            }
            if (view.AnimalModel == null) return false;
            view.Prone = prone;
            view.HeadOnly = false;
            view.Bed = bed;
            view.BedHeight = bed != null ? PawnBedSurfaceHeight(pawn, bed, false,
                Mathf.Min(view.AnimalBounds.size.y, .9f),
                Mathf.Min(view.AnimalBounds.size.z, Mathf.Max(.5f, bed.def.size.z - .2f))) : 0;
            if (prone) pronePawns.Add(pawn); else pronePawns.Remove(pawn);
            return true;
        }

        void UpdatePawnView(Pawn pawn, Thing body)
        {
            bool prone = PawnIsProne(pawn) || body is Corpse;
            var bed = prone && !(body is Corpse) ? pawn.CurrentBed() : null;
            bool headOnly = bed != null && pawn.RaceProps.Humanlike && !bed.def.building.bed_showSleeperBody;
            if (!pawnViews.TryGetValue(pawn, out var view))
                pawnViews.Add(pawn, view = new PawnView());
            if (pawn.RaceProps.Animal && TryUpdateAnimalModel(pawn, body, view, prone, bed)) return;
            var facing = PawnViewDirection(pawn, prone);
            bool animalFrontRear = !prone && pawn.RaceProps.Animal &&
                (facing == Rot4.North || facing == Rot4.South);
            if (animalFrontRear)
            {
                // Top-down front/back artwork reads as a vertical body on a card.
                // Prefer the nearest side portrait without changing gameplay rotation.
                float relativeAngle = Mathf.DeltaAngle(yaw, pawn.Rotation.AsAngle);
                facing = relativeAngle >= 0 ? Rot4.East : Rot4.West;
            }
            var direction = headOnly ? view.BedHead : view.Directions[facing.AsInt];
            if (direction != null && direction.Valid && view.Direction == facing.AsInt && view.Prone == prone && view.Bed == bed && view.HeadOnly == headOnly) return;
            try
            {
                if (direction == null)
                {
                    direction = new PawnDirection();
                    if (headOnly) view.BedHead = direction;
                    else view.Directions[facing.AsInt] = direction;
                }
                bool refresh = !direction.Valid;
                if (refresh)
                {
                    direction.CaptureScale = headOnly ? 1 : PawnPortraitScale(pawn, facing);
                    // Match source detail up to the user's cap. Inspect native
                    // render nodes only when appearance changes, never per frame.
                    EnsurePawnCaptureTexture(ref direction.Texture, pawn, facing, headOnly);
                    CapturePawnAppearance(pawn, direction.Texture, facing, direction.CaptureScale, headOnly);
                }
                if (view.Material == null)
                {
                    view.Material = NewMaterial(direction.Texture, Color.white, false);
                    view.Material.SetFloat("_Unlit", 1);
                }
                var material = view.Material;
                material.mainTexture = direction.Texture;
                if (groundShadowMaterials.TryGetValue(material, out var shadow))
                    shadow.mainTexture = direction.Texture;
                if (refresh)
                {
                    bottomPadding.Remove(material);
                    visibleSpriteHeights.Remove(material);
                    spriteMasks.Remove(material);
                    direction.Padding = BottomPadding(material);
                    visibleSpriteHeights.TryGetValue(material, out direction.VisibleHeight);
                    spriteMasks.TryGetValue(material, out direction.Mask);
                    direction.VisibleBounds = PawnVisibleBounds(direction.Mask);
                    direction.Valid = true;
                }
                else
                {
                    bottomPadding[material] = direction.Padding;
                    visibleSpriteHeights[material] = direction.VisibleHeight;
                    if (direction.Mask != null) spriteMasks[material] = direction.Mask;
                    else spriteMasks.Remove(material);
                }

                var geometry = new Geometry();
                // Compensate for the wider capture so large animals keep their
                // world size rather than shrinking to fit a human-sized card.
                float height = 1.6f * PawnVisualScale * direction.CaptureScale;
                float width = 1.15f * PawnVisualScale * direction.CaptureScale;
                if (headOnly) { width = .8f; height = .8f; }
                var cardCenter = Vector3.up * .045f;
                if (bed != null)
                {
                    var bounds = direction.VisibleBounds;
                    if (!headOnly)
                    {
                        // Scale uniformly to the visible artwork, not its transparent
                        // margins. Each sleeper fits one lane without a squeezed body.
                        float aspect = direction.Texture.width / (float)direction.Texture.height;
                        height = Mathf.Min(height, Mathf.Max(.5f, bed.def.size.z - .2f) / bounds.height,
                            .9f / (aspect * bounds.width));
                        width = height * aspect;
                    }
                    cardCenter -= new Vector3((bounds.center.x - .5f) * width, 0,
                        (bounds.center.y - .5f) * height);
                }
                if (prone) geometry.FloorCard(cardCenter, width, height);
                else geometry.Upright(Vector3.down * (height * direction.Padding), width, height, false);
                if (!pawnCards.TryGetValue(pawn, out var card))
                {
                    var previousParent = geometryParent;
                    geometryParent = null;
                    try { card = CreateMesh(material, geometry, "Pawn: " + pawn.LabelShort).transform; }
                    finally { geometryParent = previousParent; }
                    pawnCards.Add(pawn, card);
                    view.Mesh = card.GetComponent<MeshFilter>().sharedMesh;
                }
                else
                {
                    // Surface and projected shadow share the same mesh.
                    var mesh = view.Mesh;
                    mesh.Clear(); mesh.SetVertices(geometry.Vertices); mesh.SetNormals(geometry.Normals);
                    mesh.SetUVs(0, geometry.Uvs); mesh.SetUVs(1, geometry.CardCenters);
                    mesh.SetTriangles(geometry.Indices, 0); mesh.RecalculateBounds();
                }
                view.Direction = facing.AsInt;
                view.Prone = prone;
                view.HeadOnly = headOnly;
                view.Bed = bed;
                view.BedHeight = bed != null ? PawnBedSurfaceHeight(pawn, bed, headOnly,
                    width * direction.VisibleBounds.width, height * direction.VisibleBounds.height) : 0;
                view.Width = width;
                view.Height = height;
                if (prone) pronePawns.Add(pawn); else pronePawns.Remove(pawn);
                PositionPawnCard(pawn, body, card);
            }
            catch (Exception error) { Log.Warning("RimVision pawn appearance: " + error.Message); }
        }
    }
}

