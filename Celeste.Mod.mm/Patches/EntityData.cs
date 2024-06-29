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

        internal void InitializeEntityID(string LevelName) {
            EntityID = new EntityID(string.IsNullOrWhiteSpace(LevelName) ? EntityID.None.Level : LevelName, ID + (patch_LevelData._isRegisteringTriggers ? 10000000 : 0));
        }

    }
}
