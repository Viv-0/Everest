using Microsoft.Xna.Framework;
using Mono.Cecil;
using Monocle;
using MonoMod;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Celeste.Patches {
    class patch_SeekerStatue : SeekerStatue {
        public patch_SeekerStatue(EntityData data, Vector2 offset) : 
            base(data, offset) {
            // no-op. MonoMod ignores this - we only need this to make the compiler shut up.
        }


        [MonoModPatch("<>c__DisplayClass3_0")]
        class patch_SeekerStatue_ctorDelegate {

            [MonoModPatch("<>4__this")]
            private patch_FinalBossMovingBlock _this = default;
            public EntityData data; public Vector2 offset;


            [MonoModReplace]
            [MonoModPatch("<.ctor>b__0")]
            public void ctor_delegate(string f) {
                if (f == "hatch") { 
                    Seeker entity = new Seeker(data, offset) {
                        Light = { Alpha = 0f }
                    };
                    ((patch_Entity) (Entity) entity).EntityData = data;
                    _this.Scene.Add(entity);
                    _this.RemoveSelf();
                }
            }
        }
    }
}
