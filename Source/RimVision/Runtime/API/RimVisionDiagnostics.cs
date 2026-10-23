using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Recovery.Rim3D.API
{
    public readonly struct RimVisionRegistrationInfo
    {
        public string Area { get; }
        public string Id { get; }
        public int Priority { get; }
        public string ProviderType { get; }
        internal RimVisionRegistrationInfo(string area, string id, int priority, string type)
        { Area = area; Id = id; Priority = priority; ProviderType = type; }
    }

    public readonly struct RimVisionInputBlockInfo
    {
        public string ExtensionId { get; }
        public RimVisionInputChannels Channels { get; }
        internal RimVisionInputBlockInfo(string id, RimVisionInputChannels channels) { ExtensionId = id; Channels = channels; }
    }

    public readonly struct RimVisionApiFault
    {
        public string Area { get; }
        public string Id { get; }
        public string Message { get; }
        public bool Disabled { get; }
        internal RimVisionApiFault(string area, string id, Exception error, bool disabled)
        { Area = area; Id = id; Message = error.GetType().Name + ": " + error.Message; Disabled = disabled; }
    }

    public static class RimVisionDiagnostics
    {
        static readonly SortedDictionary<string, Func<RimVisionRegistrationInfo[]>> Registries =
            new SortedDictionary<string, Func<RimVisionRegistrationInfo[]>>(StringComparer.Ordinal);
        static readonly HashSet<string> Gameplay = new HashSet<string>(StringComparer.Ordinal);
        static readonly List<RimVisionApiFault> Faults = new List<RimVisionApiFault>();
        static int _revision;
        static bool _initialized;
        internal static void Changed() { _revision++; }
        internal static int RegistryRevision { get { EnsureRegistries(); return _revision; } }
        internal static void Register(string area, bool gameplay, Func<RimVisionRegistrationInfo[]> read)
        { Registries.Add(area, read); if (gameplay) Gameplay.Add(area); Changed(); }
        public static void ReportFault(string area, string id, Exception error, bool disabled = false)
        {
            if (string.IsNullOrWhiteSpace(area) || string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Use an area and registration ID.");
            if (error == null) throw new ArgumentNullException(nameof(error));
            for (int i = Faults.Count - 1; i >= 0; i--)
                if (Faults[i].Area == area && Faults[i].Id == id) { Faults.RemoveAt(i); break; }
            if (Faults.Count == 128) Faults.RemoveAt(0);
            Faults.Add(new RimVisionApiFault(area, id, error, disabled));
        }
        public static RimVisionRegistrationInfo[] GetRegistrations()
        {
            EnsureRegistries();
            var result = new List<RimVisionRegistrationInfo>();
            foreach (var read in Registries.Values) result.AddRange(read());
            return result.ToArray();
        }
        public static RimVisionInputBlockInfo[] GetInputBlocks() => Rim3DPreview.Instance?.ActiveWindow?.View?.GetApiInputBlocks() ?? Array.Empty<RimVisionInputBlockInfo>();
        public static RimVisionApiFault[] GetFaults() => Faults.ToArray();
        public static string CurrentCameraProvider => Rim3DPreview.Instance?.ActiveWindow?.View?.CameraProviderId;
        public static RimVisionInputChannels BlockedInput => RimVisionApi.Current?.BlockedInput ?? RimVisionInputChannels.None;
        public static string Describe()
        {
            var text = new StringBuilder("RimVision API ").Append(RimVisionApi.Version)
                .Append("\nCamera: ").Append(CurrentCameraProvider ?? "native")
                .Append("\nBlocked input: ").Append(BlockedInput);
            foreach (var block in GetInputBlocks()) text.Append("\nINPUT ").Append(block.ExtensionId).Append(": ").Append(block.Channels);
            foreach (var entry in GetRegistrations()) text.Append('\n').Append(entry.Area).Append(" / ").Append(entry.Id).Append(" [").Append(entry.Priority).Append(']');
            foreach (var fault in Faults) text.Append("\nERROR ").Append(fault.Area).Append(" / ").Append(fault.Id)
                .Append(fault.Disabled ? " (disabled): " : ": ").Append(fault.Message);
            return text.ToString();
        }
        internal static void AppendGameplay(StringBuilder text)
        {
            foreach (var entry in GetRegistrations())
                if (Gameplay.Contains(entry.Area)) text.Append('\n').Append(entry.Area.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(entry.Area).Append(entry.Id.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(entry.Id).Append(':').Append(entry.Priority.ToString(CultureInfo.InvariantCulture));
        }
        static void EnsureRegistries()
        {
            if (_initialized) return;
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionApi).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionControl).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionCombat).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionSpatial).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionCamera).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionPresentation).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionInput).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionHud).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionModels).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RimVisionInteractions).TypeHandle);
            _initialized = true;
        }
    }

    public static class RimVisionCapabilities
    {
        sealed class Contract { internal Version Version; internal bool Multiplayer; }
        static readonly ApiRegistry<Contract> Contracts = new ApiRegistry<Contract>("contracts");
        static readonly HashSet<string> Features = new HashSet<string>(StringComparer.Ordinal)
        {
            "camera.lifecycle", "camera.volume", "input.gestures", "input.axes", "jobs.lifecycle",
            "interactions", "models", "animation.events", "effects", "view.lifecycle", "diagnostics", "multiplayer.contracts"
        };
        public static bool TryGetFeatureVersion(string feature, out Version version)
        { version = feature != null && Features.Contains(feature) ? new Version(1, 0, 0) : null; return version != null; }
        public static IDisposable RegisterContract(string id, Version version, bool multiplayer = true) =>
            Contracts.Add(id, new Contract { Version = version ?? throw new ArgumentNullException(nameof(version)), Multiplayer = multiplayer }, 0);
        static string _fingerprint;
        static int _fingerprintRevision = -1;
        public static bool MatchesGameplayFingerprint(string fingerprint) => string.Equals(GameplayFingerprint, fingerprint, StringComparison.Ordinal);
        public static string GameplayFingerprint
        {
            get
            {
                int revision = RimVisionDiagnostics.RegistryRevision;
                if (_fingerprint != null && _fingerprintRevision == revision) return _fingerprint;
                var text = new StringBuilder().Append(RimVisionApi.Version);
                RimVisionDiagnostics.AppendGameplay(text);
                foreach (var contract in Contracts.Entries)
                    if (contract.Active && contract.Value.Multiplayer) text.Append('\n').Append(contract.Id.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(contract.Id).Append('=').Append(contract.Value.Version);
                using (var hash = SHA256.Create()) _fingerprint = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
                _fingerprintRevision = revision;
                return _fingerprint;
            }
        }
    }
}
