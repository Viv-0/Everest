using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.InlineRT;
using MonoMod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Monocle;
using Microsoft.Xna.Framework;

namespace Celeste.Patches {
    class patch_OshiroTrigger : OshiroTrigger {

        public patch_OshiroTrigger(EntityData data, Vector2 offset)
            : base(data, offset) {
            // no-op. MonoMod ignores this - we only need this to make the compiler shut up.
        }
        [MonoModIgnore]
        [PatchModSpawnEntityData] // handled in MonoModRules.Mod.cs
        public override extern void OnEnter(Player player);
    }
}
