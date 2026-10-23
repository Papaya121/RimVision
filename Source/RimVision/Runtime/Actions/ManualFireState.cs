using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Recovery.Rim3D
{
    // Weapon-owned simulation data: changing hands or loading a save does not reset heat.
    public sealed class FPVWeaponFireState : IExposable
    {
        public ThingWithComps Weapon;
        public int Heat, LastShotTick, NextShotTick, OverheatedUntil, BurstRemaining;
        internal int OneUseShotsFired;
        float _instability;
        public int Capacity => Weapon?.def.defName == "Gun_Minigun" ? 900 :
            Weapon?.def.defName == "Gun_LMG" ? 720 : 480;
        public bool Overheated(int tick) => tick < OverheatedUntil;
        public int HeatAt(int tick) => OverheatedUntil > LastShotTick
            ? System.Math.Max(0, Capacity * (OverheatedUntil - tick) / 300)
            : System.Math.Max(0, Heat - System.Math.Max(0, tick - LastShotTick));

        internal float InstabilityAt(int tick) => System.Math.Max(0,
            _instability - System.Math.Max(0, tick - LastShotTick - 12) / 60f);

        public void AddShot(int tick, int interval, int burstCount)
        {
            _instability = System.Math.Min(1, InstabilityAt(tick) +
                UnityEngine.Mathf.Clamp(6f / interval, .5f, 2f) / UnityEngine.Mathf.Clamp(burstCount, 3, 8));
            int heatMultiplier = Weapon?.def.defName == "Gun_ChainShotgun" ||
                Weapon?.def.defName == "Gun_MachinePistol" ? 4 : 2;
            Heat = System.Math.Min(Capacity, HeatAt(tick) + System.Math.Max(2, interval) * heatMultiplier);
            LastShotTick = tick;
            if (Heat < Capacity) return;
            OverheatedUntil = tick + 300;
            BurstRemaining = 0;
            NextShotTick = System.Math.Max(NextShotTick, OverheatedUntil);
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref Weapon, "weapon");
            Scribe_Values.Look(ref Heat, "heat");
            Scribe_Values.Look(ref LastShotTick, "lastShotTick");
            Scribe_Values.Look(ref NextShotTick, "nextShotTick");
            Scribe_Values.Look(ref OverheatedUntil, "overheatedUntil");
            Scribe_Values.Look(ref BurstRemaining, "burstRemaining");
            Scribe_Values.Look(ref OneUseShotsFired, "oneUseShotsFired");
            Scribe_Values.Look(ref _instability, "instability");
        }
    }

    public sealed class FPVFireState : GameComponent
    {
        List<FPVWeaponFireState> weapons = new List<FPVWeaponFireState>();
        readonly Dictionary<ThingWithComps, FPVWeaponFireState> lookup = new Dictionary<ThingWithComps, FPVWeaponFireState>();
        public FPVFireState(Game game) { }

        public FPVWeaponFireState Get(ThingWithComps weapon, bool create = false)
        {
            if (weapon == null) return null;
            if (lookup.TryGetValue(weapon, out var state) || !create) return state;
            state = new FPVWeaponFireState { Weapon = weapon };
            lookup.Add(weapon, state);
            weapons.Add(state);
            return state;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
                weapons.RemoveAll(state => state.Weapon == null || state.Weapon.Destroyed);
            Scribe_Collections.Look(ref weapons, "rimVisionManualFire", LookMode.Deep);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            if (weapons == null) weapons = new List<FPVWeaponFireState>();
            lookup.Clear();
            weapons.RemoveAll(state => state?.Weapon == null || state.Weapon.Destroyed);
            foreach (var state in weapons) lookup[state.Weapon] = state;
        }
    }
}
