using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Recovery.Rim3D
{
    internal static class PerformanceCapture
    {
        const string HarmonyId = "papaya.rimvision.performance";
        static readonly string[] Phases = { "2D running", "2D paused", "3D running", "3D paused" };
        static Session _active;
        internal enum NativeDrawDecision { NotCovered, OtherMap, Gravship, Skipped }
        internal static bool Active => _active != null;
        internal static string LastReportPath { get; private set; }
        internal static int SecondsRemaining => _active == null ? 0 :
            Math.Max(0, (int)Math.Ceiling((_active.Deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));

        sealed class Totals
        {
            internal long Calls, Ticks, Maximum, Errors;
        }

        sealed class Metric
        {
            internal readonly string Name;
            internal readonly Totals[] Phases = { new Totals(), new Totals(), new Totals(), new Totals() };
            internal long Calls, Ticks, Maximum, Errors;
            internal Metric(string name) { Name = name; }
        }

        sealed class Phase
        {
            internal readonly List<double> Frames = new List<double>();
            internal double Seconds;
            internal long Ticks;
            internal int Collections;
        }

        readonly struct Measurement
        {
            internal readonly Session Owner;
            internal readonly Metric Metric;
            internal readonly long Started;
            internal Measurement(Session owner, Metric metric)
            { Owner = owner; Metric = metric; Started = Stopwatch.GetTimestamp(); }
        }

        sealed class Spike
        {
            internal double At, Milliseconds;
            internal int Phase, Ticks, Collections;
            internal string Work;
        }

        sealed class Session
        {
            internal readonly Game Game = Current.Game;
            internal readonly int MainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            internal readonly Dictionary<MethodBase, Metric> Methods = new Dictionary<MethodBase, Metric>();
            internal readonly List<Metric> Metrics = new List<Metric>();
            internal readonly List<string> Missing = new List<string>();
            internal readonly Phase[] Stats = { new Phase(), new Phase(), new Phase(), new Phase() };
            internal readonly long[][] NativeDrawDecisions = { new long[4], new long[4], new long[4], new long[4] };
            internal readonly List<Spike> Spikes = new List<Spike>(32);
            internal readonly StringBuilder Timeline = new StringBuilder();
            internal readonly StringBuilder Header = new StringBuilder();
            internal long Deadline, Warmup, Previous;
            internal bool Ready;
            internal int Phase, PreviousTick, PreviousGc, FrameCount;
            internal long IgnoredWorkerCalls;
            internal Map Map;
            internal double Elapsed, WindowSeconds;
            internal int WindowFrames, WindowTicks;

            internal void CompleteFrame(long now)
            {
                double seconds = (now - Previous) / (double)Stopwatch.Frequency;
                Previous = now;
                int tick = ReadTick(Map);
                int ticks = Math.Max(0, tick - PreviousTick);
                PreviousTick = tick;
                int gc = GC.CollectionCount(0);
                int collections = Math.Max(0, gc - PreviousGc);
                PreviousGc = gc;
                var stats = Stats[Phase];
                stats.Frames.Add(seconds * 1000);
                stats.Seconds += seconds;
                stats.Ticks += ticks;
                stats.Collections += collections;
                Elapsed += seconds;
                FrameCount++;
                WindowSeconds += seconds;
                WindowFrames++;
                WindowTicks += ticks;
                RecordSpike(seconds * 1000, ticks, collections);
                foreach (var metric in Metrics)
                {
                    var total = metric.Phases[Phase];
                    total.Calls += metric.Calls;
                    total.Ticks += metric.Ticks;
                    total.Maximum = Math.Max(total.Maximum, metric.Maximum);
                    total.Errors += metric.Errors;
                    metric.Calls = metric.Ticks = metric.Maximum = metric.Errors = 0;
                }
                if (WindowSeconds >= 1) Snapshot();
            }

            void RecordSpike(double milliseconds, int ticks, int collections)
            {
                if (milliseconds < 33.333) return;
                int replace = -1;
                if (Spikes.Count == 32)
                {
                    replace = 0;
                    for (int i = 1; i < Spikes.Count; i++)
                        if (Spikes[i].Milliseconds < Spikes[replace].Milliseconds) replace = i;
                    if (milliseconds <= Spikes[replace].Milliseconds) return;
                }
                Metric first = null, second = null, third = null;
                foreach (var metric in Metrics)
                {
                    if (metric.Ticks <= 0) continue;
                    if (first == null || metric.Ticks > first.Ticks) { third = second; second = first; first = metric; }
                    else if (second == null || metric.Ticks > second.Ticks) { third = second; second = metric; }
                    else if (third == null || metric.Ticks > third.Ticks) third = metric;
                }
                var spike = new Spike
                {
                    At = Elapsed, Milliseconds = milliseconds, Phase = Phase, Ticks = ticks, Collections = collections,
                    Work = Work(first) + " | " + Work(second) + " | " + Work(third)
                };
                if (replace < 0) Spikes.Add(spike); else Spikes[replace] = spike;
            }

            static string Work(Metric metric) => metric == null ? "-" :
                metric.Name + "=" + Milliseconds(metric.Ticks).ToString("F3", CultureInfo.InvariantCulture) + "ms";

            internal void Snapshot()
            {
                var map = Map?.Disposed == true ? null : Map;
                var manager = Current.Game == Game && Current.ProgramState == ProgramState.Playing ? Find.TickManager : null;
                var settings = Rim3DSettings.Current;
                Timeline.AppendFormat(CultureInfo.InvariantCulture,
                    "{0:F2}\t{1}\t{2:F2}\t{3:F2}\t{4}\t{5}\t{6}\t{7}\t{8:F0}\t{9:F2}\t{10:F1}\t{11}\t{12:F2}\n",
                    Elapsed, Phases[Phase], WindowFrames / Math.Max(.0001, WindowSeconds),
                    WindowTicks / Math.Max(.0001, WindowSeconds), map?.uniqueID ?? -1,
                    map?.mapPawns?.AllPawnsSpawned.Count ?? 0, map?.listerThings?.AllThings.Count ?? 0,
                    map?.listerThings?.ThingsInGroup(ThingRequestGroup.Projectile).Count ?? 0,
                    settings.ViewDistance, settings.RenderScale, GC.GetTotalMemory(false) / 1048576d,
                    manager?.CurTimeSpeed.ToString() ?? "unavailable", manager?.TickRateMultiplier ?? 0);
                WindowSeconds = 0;
                WindowFrames = WindowTicks = 0;
            }

            internal string Report(string reason)
            {
                if (WindowFrames > 0) Snapshot();
                var text = new StringBuilder(Header.ToString());
                text.AppendLine("Stop reason: " + reason);
                text.AppendLine("Recorded seconds: " + Elapsed.ToString("F3", CultureInfo.InvariantCulture));
                text.AppendLine("CPU times below are INCLUSIVE and overlap; do not sum nested rows.");
                text.AppendLine("Render CPU time can include waiting for the GPU; this is not a GPU profile.");
                text.AppendLine("TPS uses observed map tick progress, not a requested game speed. Gen0 collections are observations, not proof of a GC-caused stall.");
                text.AppendLine("Instrumentation is temporary but adds overhead, especially to frequently called methods. Warmup and the final partial frame are excluded.");
                text.AppendLine("Phases describe the start of each frame; switching views or loading can create transition spikes.");
                text.AppendLine("Ignored worker-thread calls: " + IgnoredWorkerCalls + "; timings cover the main thread only.");
                text.AppendLine("\nPHASES\nphase\tseconds\tframes\tFPS\tTPS\tp50_ms\tp95_ms\tp99_ms\tworst_ms\tframes_over_33ms\tgen0_collections");
                for (int phase = 0; phase < Stats.Length; phase++)
                {
                    var stats = Stats[phase];
                    if (stats.Frames.Count == 0) continue;
                    stats.Frames.Sort();
                    int slow = 0;
                    foreach (double frame in stats.Frames) if (frame > 33.333) slow++;
                    text.AppendFormat(CultureInfo.InvariantCulture,
                        "{0}\t{1:F3}\t{2}\t{3:F2}\t{4:F2}\t{5:F3}\t{6:F3}\t{7:F3}\t{8:F3}\t{9}\t{10}\n",
                        Phases[phase], stats.Seconds, stats.Frames.Count, stats.Frames.Count / stats.Seconds,
                        stats.Ticks / stats.Seconds, Percentile(stats.Frames, .5), Percentile(stats.Frames, .95),
                        Percentile(stats.Frames, .99), stats.Frames[stats.Frames.Count - 1], slow, stats.Collections);
                }
                for (int phase = 0; phase < Stats.Length; phase++)
                {
                    var stats = Stats[phase];
                    if (stats.Frames.Count == 0) continue;
                    text.AppendLine("\nCPU " + Phases[phase] + "\nmethod\tcalls\ttotal_ms\tms_per_frame\tmean_call_ms\tmax_call_ms\texceptions");
                    var ordered = new List<Metric>(Metrics);
                    ordered.Sort((a, b) => b.Phases[phase].Ticks.CompareTo(a.Phases[phase].Ticks));
                    foreach (var metric in ordered)
                    {
                        var total = metric.Phases[phase];
                        if (total.Calls == 0) continue;
                        double ms = Milliseconds(total.Ticks);
                        text.AppendFormat(CultureInfo.InvariantCulture, "{0}\t{1}\t{2:F3}\t{3:F3}\t{4:F4}\t{5:F3}\t{6}\n",
                            metric.Name, total.Calls, ms, ms / stats.Frames.Count, ms / total.Calls,
                            Milliseconds(total.Maximum), total.Errors);
                    }
                }
                text.AppendLine("\nTIMELINE\nseconds\tphase\tFPS\tTPS\tmap_id\tpawns\tthings\tprojectiles\tview_distance\trender_scale\tmanaged_MB\tspeed\ttick_rate_multiplier");
                text.Append(Timeline);
                text.AppendLine("\nWORST FRAMES\nseconds\tphase\tframe_ms\tticks\tgen0_collections\tlargest_inclusive_CPU_scopes");
                Spikes.Sort((a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
                foreach (var spike in Spikes)
                    text.AppendFormat(CultureInfo.InvariantCulture, "{0:F3}\t{1}\t{2:F3}\t{3}\t{4}\t{5}\n",
                        spike.At, Phases[spike.Phase], spike.Milliseconds, spike.Ticks, spike.Collections, spike.Work);
                text.AppendLine("\nHOOK COVERAGE");
                text.AppendLine("RimVision native dynamic draw gate decisions (also includes the final partial frame):");
                text.AppendLine("phase\tnot_covered\tother_map\tgravship\tskipped");
                for (int phase = 0; phase < NativeDrawDecisions.Length; phase++)
                {
                    var counts = NativeDrawDecisions[phase];
                    text.AppendFormat(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}\t{4}\n",
                        Phases[phase], counts[0], counts[1], counts[2], counts[3]);
                }
                foreach (var metric in Metrics) text.AppendLine("OK " + metric.Name);
                foreach (var missing in Missing) text.AppendLine("MISSING " + missing);
                return text.ToString();
            }
        }

        static double Milliseconds(long ticks) => ticks * (1000d / Stopwatch.Frequency);
        internal static void RecordNativeDrawDecision(NativeDrawDecision decision)
        {
            var session = _active;
            if (session?.Ready == true && System.Threading.Thread.CurrentThread.ManagedThreadId == session.MainThread)
                session.NativeDrawDecisions[session.Phase][(int)decision]++;
        }
        static double Percentile(List<double> sorted, double fraction) => sorted[Math.Min(sorted.Count - 1,
            Math.Max(0, (int)Math.Ceiling(sorted.Count * fraction) - 1))];

        static int ReadTick(Map map) => map != null && !map.Disposed && Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
            ? Rim3DMultiplayer.MapTicks(map) : Find.TickManager.TicksGame;

        static int CurrentPhase()
        {
            var window = Rim3DPreview.Instance?.ActiveWindow;
            bool spatial = window?.IsOpen == true && window.View != null && !WorldRendererUtility.WorldSelected &&
                Find.MainTabsRoot.OpenTab != MainButtonDefOf.Architect;
            return (spatial ? 2 : 0) + (Find.TickManager.Paused ? 1 : 0);
        }

        internal static void Toggle()
        {
            if (_active != null) { Stop("manual stop"); return; }
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            var session = new Session();
            try
            {
                InstallHooks(session);
                var settings = Rim3DSettings.Current;
                session.Header.AppendLine("RimVision performance capture v1");
                session.Header.AppendLine("UTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                session.Header.AppendLine("Assembly MVID: " + typeof(MapView).Module.ModuleVersionId);
                session.Header.AppendLine("Game: " + VersionControl.CurrentVersionStringWithRev);
                session.Header.AppendLine("Unity: " + Application.unityVersion + "; MP: " + Rim3DMultiplayer.Active);
                session.Header.AppendLine("CPU: " + SystemInfo.processorType + "; cores: " + SystemInfo.processorCount);
                session.Header.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + "; API: " + SystemInfo.graphicsDeviceType);
                session.Header.AppendLine("OS: " + SystemInfo.operatingSystem + "; RAM MB: " + SystemInfo.systemMemorySize);
                session.Header.AppendLine("Screen: " + Screen.width + "x" + Screen.height + "; vsync: " + QualitySettings.vSyncCount + "; target FPS: " + Application.targetFrameRate);
                session.Header.AppendFormat(CultureInfo.InvariantCulture,
                    "Settings: distance={0}, scale={1}, pawn texture cap={2}, AO={3}, shadows={4}, neighbor maps={5}\n",
                    settings.ViewDistance, settings.RenderScale, settings.MaxPawnTextureResolution,
                    settings.EffectsEnabled(settings.AmbientOcclusion), settings.EffectsEnabled(settings.GroundShadows), settings.NeighborMaps);
                session.Header.AppendLine("Maps at start:");
                foreach (var map in Current.Game.Maps)
                    session.Header.AppendLine("  " + map.uniqueID + ": " + map.Size.x + "x" + map.Size.z + "; pawns=" + map.mapPawns.AllPawnsSpawned.Count);
                session.Header.AppendLine("Active mods:");
                foreach (var mod in LoadedModManager.RunningModsListForReading)
                    session.Header.AppendLine("  " + mod.PackageIdPlayerFacing);
                long now = Stopwatch.GetTimestamp();
                session.Warmup = now + Stopwatch.Frequency * 2;
                session.Deadline = session.Warmup + Stopwatch.Frequency * 60;
                _active = session;
                Messages.Message("Rim3D_PerformanceStarted".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception error)
            {
                _active = null;
                RemoveHooks();
                Log.Error("RimVision performance capture could not start: " + error);
            }
        }

        internal static void Update()
        {
            var session = _active;
            if (session == null) return;
            try
            {
                if (Current.Game != session.Game || Current.ProgramState != ProgramState.Playing)
                { Stop("game changed or left play mode"); return; }
                long now = Stopwatch.GetTimestamp();
                if (now < session.Warmup) return;
                if (session.Ready) session.CompleteFrame(now);
                if (now >= session.Deadline || session.FrameCount >= 32768)
                { Stop("capture limit reached"); return; }
                int phase = CurrentPhase();
                var map = Find.CurrentMap;
                if (session.Ready && (phase != session.Phase || map != session.Map) && session.WindowFrames > 0) session.Snapshot();
                session.Phase = phase;
                session.Map = map;
                session.PreviousTick = ReadTick(map);
                session.PreviousGc = GC.CollectionCount(0);
                session.Previous = now;
                session.Ready = true;
            }
            catch (Exception error)
            {
                Log.Error("RimVision performance sampling failed: " + error);
                Stop("sampling failed: " + error.GetType().Name);
            }
        }

        internal static void Stop(string reason)
        {
            var session = _active;
            if (session == null) return;
            _active = null;
            RemoveHooks();
            try
            {
                string directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimVision", "Performance");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "RimVision-performance-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
                File.WriteAllText(path, session.Report(reason), new UTF8Encoding(false));
                LastReportPath = path;
                Log.Message("RimVision performance report: " + path);
                if (Current.ProgramState == ProgramState.Playing)
                    Messages.Message("Rim3D_PerformanceSaved".Translate(path), MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception error) { Log.Error("RimVision performance report could not be saved: " + error); }
        }

        static void RemoveHooks()
        {
            try { new Harmony(HarmonyId).UnpatchAll(HarmonyId); }
            catch (Exception error) { Log.Error("RimVision performance hooks cleanup: " + error); }
        }

        static void Begin(MethodBase __originalMethod, out Measurement __state)
        {
            var session = _active;
            if (session != null && System.Threading.Thread.CurrentThread.ManagedThreadId != session.MainThread)
            {
                System.Threading.Interlocked.Increment(ref session.IgnoredWorkerCalls);
                __state = default;
                return;
            }
            __state = session?.Ready == true && session.Methods.TryGetValue(__originalMethod, out var metric)
                ? new Measurement(session, metric) : default;
        }

        static Exception End(Exception __exception, Measurement __state)
        {
            if (__state.Owner != null && _active == __state.Owner)
            {
                long elapsed = Stopwatch.GetTimestamp() - __state.Started;
                var metric = __state.Metric;
                metric.Calls++;
                metric.Ticks += elapsed;
                metric.Maximum = Math.Max(metric.Maximum, elapsed);
                if (__exception != null) metric.Errors++;
            }
            return __exception;
        }

        static void InstallHooks(Session session)
        {
            var harmony = new Harmony(HarmonyId);
            var begin = new HarmonyMethod(AccessTools.Method(typeof(PerformanceCapture), nameof(Begin))) { priority = Priority.First };
            var end = new HarmonyMethod(AccessTools.Method(typeof(PerformanceCapture), nameof(End))) { priority = Priority.Last };
            Add(typeof(TickManager), "TickManagerUpdate");
            Add(typeof(TickManager), "DoSingleTick");
            Add(typeof(TickList), "Tick");
            Add(typeof(Root_Play), "Update");
            Add(typeof(Map), "MapUpdate");
            Add(typeof(DynamicDrawManager), "DrawDynamicThings");
            Add(typeof(MapDrawer), "DrawMapMesh");
            Add(typeof(MapDrawer), "MapMeshDrawerUpdate_First");
            Add(typeof(RegionAndRoomUpdater), "RebuildAllRegionsAndRooms");
            Add(typeof(RegionAndRoomUpdater), "RegenerateNewRegionsFromDirtyCells");
            Add(typeof(RegionAndRoomUpdater), "CreateOrUpdateRooms");
            Add(typeof(GlowGrid), "GlowGridUpdate_First");
            Add(typeof(MapComponentUtility), "MapComponentUpdate");
            Add(typeof(Pawn), "TickInterval");
            Add(typeof(Pawn), "ProcessPostTickVisuals");
            Add(typeof(PawnLeaner), "ProcessPostTickVisuals");
            Add(typeof(Pawn_JobTracker), "JobTrackerTickInterval");
            Add(typeof(Pawn_PathFollower), "PatherTick");
            Add(typeof(PawnControl), "Move", new[] { typeof(Vector2), typeof(float) });
            Add(typeof(PreviewWindow), "DoWindowContents");
            foreach (string name in new[] { "UpdateCamera", "UpdatePawnAppearances", "UpdatePawnPresentation",
                "PreparePawnAppearance", "RefreshPawnForPicking", "CapturePawnAppearance", "UpdateHeldThings",
                "UpdateWorldGeometry", "UpdateSpatialMaps", "UpdateCombatEffects", "PrepareSceneRendering",
                "Render", "DrawActivityOverlay", "UpdateGameLighting", "OnPawnChanged", "UpdateFPVActions",
                "UpdateImmersion", "PositionCamera" }) Add(typeof(MapView), name);
            Add(typeof(FPVManualBullet), "TickInterval");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "BottomPadding");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "BeginSpriteAnalysis");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "CompleteSpriteReadback");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "AnalyzeSpritePixels");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "RebuildChunk");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "DrawStaticThing");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "RemoveMovingShadows");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "ReleaseMountainLods");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "CollectChunkMeshes");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "CreateMesh");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "FinishChunkReuse");
            Add(AccessTools.Inner(typeof(MapView), "ObjectPresentation"), "FinishModelChunk");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "BuildTerrain");
            Add(AccessTools.Inner(typeof(MapView), "WorldGeometry"), "BuildRoofs");
            Add(AccessTools.Inner(typeof(MapView), "HeldThingPresentation"), "WeaponVolume");
            Add(AccessTools.Inner(typeof(MapView), "HeldThingPresentation"), "ReadVolumeMask");
            Add(AccessTools.Inner(typeof(MapView), "CombatEffectsRenderer"), "UpdateExplosionVisuals");
            Add(AccessTools.Inner(typeof(MapView), "CombatEffectsRenderer"), "UpdateGasVisuals");

            void Add(Type type, string name, Type[] parameters = null)
            {
                string label = (type?.Name ?? "unknown type") + "." + name;
                MethodInfo method;
                try { method = type == null ? null : AccessTools.DeclaredMethod(type, name, parameters); }
                catch (Exception error)
                {
                    session.Missing.Add(label + ": " + error.GetType().Name + " " + error.Message);
                    return;
                }
                if (method == null) { session.Missing.Add(label); return; }
                if (session.Methods.ContainsKey(method)) return;
                var metric = new Metric(label);
                session.Methods.Add(method, metric);
                try
                {
                    harmony.Patch(method, prefix: begin, finalizer: end);
                    session.Metrics.Add(metric);
                }
                catch (Exception error)
                {
                    session.Methods.Remove(method);
                    session.Missing.Add(label + ": " + error.GetType().Name + " " + error.Message);
                }
            }
        }
    }
}
