using Mono.Cecil;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;
using MonoMod.InlineRT;
using MonoMod.Utils;
using System;

namespace MonoMod {
    /// <summary>
    /// Links the specified type / method / field / property / etc. to this one if the mod is targeting legacy MonoMod
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class RelinkLegacyMonoMod : Attribute {
        public RelinkLegacyMonoMod(string linkFromName) {}
    }

    /// <summary>
    /// Patches the FireBall::Added method to attach the EntityData to all FireBalls in the "group," since they're all kinda from the same EntityData, it's just that 1 creates the rest.
    /// </summary>
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchModSpawnEntityData))]
    class PatchModSpawnEntityDataAttribute : Attribute { }

    static partial class MonoModRules {

        // Init rules for patching mod DLLs
        private static void InitModRules(MonoModder modder) {
            // Relink against FNA
            RelinkAgainstFNA(modder);

            // Determine if the mod uses (legacy) MonoMod
            bool isMonoMod = false, isLegacyMonoMod = true;
            foreach (AssemblyNameReference name in modder.Module.AssemblyReferences) {
                if (name.Name.StartsWith("MonoMod.")) {
                    isMonoMod = true;

                    // MonoMod version numbers are actually date codes - safe to say no legacy build will come out post 2023
                    if (name.Version.Major >= 23)
                        isLegacyMonoMod = false;
                }
            }

            // If this is legacy MonoMod, relink against modern MonoMod
            if (isMonoMod && isLegacyMonoMod) {
                SetupLegacyMonoModRelinking(modder);
            } else
                isLegacyMonoMod = false;

            MonoModRule.Flag.Set("LegacyMonoMod", isLegacyMonoMod);
        }

        // in practice, people wanting to spawn extensions could use the new LevelExt method (scene as Level).Add(Entity e, EntityData d);
        public static void PatchModSpawnEntityData(ILContext context, CustomAttribute attrib) {
            FieldReference f_Entity_EntityData = MonoModRule.Modder.Module.GetType("Monocle.Entity").FindField("EntityData");
            ILCursor cursor = new ILCursor(context);

            cursor.GotoNext(MoveType.Before, i => i.MatchCall<Scene>("Add"));
            cursor.Emit(OpCodes.Dup);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldfld, f_Entity_EntityData);
            cursor.Emit(OpCodes.Stfld, f_Entity_EntityData);
        }
    }
}
