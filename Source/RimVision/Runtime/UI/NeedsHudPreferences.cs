using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Recovery.Rim3D
{
    public sealed class NeedsHudPreferences : GameComponent
    {
        HashSet<string> _additionalHudNeeds;

        public NeedsHudPreferences(Game game)
        {
            _additionalHudNeeds = Rim3DSettings.Current.CopyAdditionalHudNeeds();
        }

        internal bool ShowAdditionalHudNeed(NeedDef def) => _additionalHudNeeds.Contains(def.defName);

        internal void SetAdditionalHudNeed(NeedDef def, bool selected)
        {
            if (selected) _additionalHudNeeds.Add(def.defName);
            else _additionalHudNeeds.Remove(def.defName);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref _additionalHudNeeds, "additionalHudNeeds", LookMode.Value);
            if (_additionalHudNeeds == null)
                _additionalHudNeeds = Rim3DSettings.Current.CopyAdditionalHudNeeds();
        }
    }
}
