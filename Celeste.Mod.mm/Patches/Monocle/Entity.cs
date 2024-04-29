﻿using System;
using MonoMod;
using Celeste.Mod.Entities;
using Celeste;
using System.Xml;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Mono.Cecil;
using MonoMod.Utils;
using MonoMod.InlineRT;
using Mono.Cecil.Cil;
using System.Text.RegularExpressions;
using System.Reflection;
using Monocle;

namespace Monocle {
    class patch_Entity : Entity {

        [MonoModIgnore]
        [MonoModConstructor]
        [PatchEntityCtor]
        public extern void ctor(Vector2 position);

        public new Scene Scene {
            [MonoModIgnore]
            get;
            [MonoModIgnore]
            private set;
        }

        public readonly EntityData EntityData;
        public EntityID EntityID => (EntityData as patch_EntityData).EntityID;
        public event Action<Entity> PreUpdate;
        public event Action<Entity> PostUpdate;

        internal void DissociateFromScene() {
            Scene = null;
        }

        internal void _PreUpdate() => PreUpdate?.Invoke(this);

        internal void _PostUpdate() => PostUpdate?.Invoke(this);
    }
}

namespace MonoMod {
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchEntityCtor))]
    class PatchEntityCtorAttribute : Attribute { }

    static partial class MonoModRules {

        // Adds instructions to set EntityData from the ThreadStatic temporaryEntityData. This is implemented for having access to EntityData directly from each Entity upon load.
        // Since EntityData is a class, it is passed by-reference which means there's no massive overhead to doing this.
        // If there's a way to store this object to a living stack where it's only allocated to the stack for a short time that would be more ideal but I don't know of a way to do so.
        public static void PatchEntityCtor(ILContext context, CustomAttribute attrib) {
            FieldReference f_Entity_EntityData = context.Method.DeclaringType.FindField("EntityData");
            MethodReference m_LevelExt_GrabTemporaryEntityData = MonoModRule.Modder.Module.GetType("Celeste.LevelExt").FindMethod("GrabTemporaryEntityData");

            ILCursor cursor = new ILCursor(context);
            cursor.GotoNext(MoveType.Before, instr => instr.MatchLdarg(0), instr => instr.MatchLdarg(1));
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Dup);
            cursor.Emit(OpCodes.Call, m_LevelExt_GrabTemporaryEntityData);
            cursor.Emit(OpCodes.Stfld, f_Entity_EntityData);
            /* Resulting code:
            +  this.EntityData = LevelExt.GrabTemporaryEntityData(this);
		       Position = position;
	           Components = new ComponentList(this); 
            */
        }
    }
}
