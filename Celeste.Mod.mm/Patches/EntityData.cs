#pragma warning disable CS0626 // Method, operator, or accessor is marked external and has no attributes on it


using Celeste.Mod;

namespace Celeste {
    class patch_EntityData : EntityData {
        /// <origdoc/>
        public extern bool orig_Has(string key);
        /// <inheritdoc cref="EntityData.Has(string)"/>
        public new bool Has(string key) {
            if (Values == null)
                return false;
            return orig_Has(key);
        }

        public EntityID EntityID;

        internal void InitializeEntityID() {
            int id = ID + (patch_LevelData._isRegisteringTriggers ? 10000000 : 0);
            if (Level == null) {
                Logger.Log(LogLevel.Error, "Everest", "Level was found to be null after Level set in EntityData!");
                EntityID = new EntityID(EntityID.None.Level, id);
            }
            else if (string.IsNullOrWhiteSpace(Level.Name))
                EntityID = new EntityID(EntityID.None.Level, id);
            else
                EntityID = new EntityID(Level?.Name ?? EntityID.None.Level, id);            
        }

    }
}
