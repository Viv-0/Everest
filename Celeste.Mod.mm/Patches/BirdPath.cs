using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod;
using MonoMod.Cil;
using MonoMod.InlineRT;
using MonoMod.Utils;
using System;
using System.Collections;

namespace Celeste {
    public class patch_BirdPath : BirdPath {

        [MonoModIgnore] 
        public new Vector2 speed;
        
        [MonoModIgnore]
        public new Vector2 target;
        
        private float oldAngle;
        private bool oldAngleInit = false;
        // Whether to apply the fix, see `PatchBirdPathUpdate`
        private bool angleFix = false;
        // Maximum rad/s turn speed, see `PatchBirdPathUpdate`
        private float angleFixMaxRotation = 1;
        // If this entity is placed in a vanilla map we will forcibly disable all changes
        // Note that `angleFix` is false by default so that will also apply in vanilla
        private bool inVanilla = false;
        
        // Compiler satisfaction
        [MonoModIgnore]
        public patch_BirdPath(EntityID id, EntityData data, Vector2 offset) : base(id, data, offset) {
        }

        [MonoModIgnore]
        public patch_BirdPath(EntityID id, Vector2 position, Vector2[] nodes, bool onlyOnce, bool onlyIfLeft, float speedMult) : base(id, position, nodes, onlyOnce, onlyIfLeft, speedMult) {
        }

        // We added two new entity data's, only this constructor is ever called, the second one is never called (and never should be)
        public extern void orig_ctor(EntityID id, EntityData data, Vector2 offset);
        [MonoModConstructor]
        public void ctor(EntityID id, EntityData data, Vector2 offset) {
            orig_ctor(id, data, offset);
            this.angleFix = data.Bool("angle_fix");
            this.angleFixMaxRotation = data.Float("angle_fix_max_rotation_speed");
        }

        public extern void orig_Added(Scene scene);

        public override void Added(Scene scene) {
            // Let's assume that this will only be placed in `Level`s
            inVanilla = (scene as Level)!.Session.Area.GetLevelSet() == "Celeste";
            orig_Added(scene);
        }

        [MonoModIgnore]
        [PatchBirdPathRoutine]
        private extern IEnumerator Routine();

        [MonoModIgnore]
        [PatchBirdPathUpdate]
        public override extern void Update();

        // Other algorithm to calculate the rotation of the bird, see `PatchBirdPathUpdate`
        // Used by the il patched code in `Update`
        public float CalcAngle() {
            if (!oldAngleInit) {
                oldAngleInit = true;
                oldAngle = this.speed.Angle();
            } else {
                float maxTurnSpeed = angleFixMaxRotation * Engine.DeltaTime;
                float newAngle = Calc.AngleLerp(this.speed.Angle(), oldAngle, 0.5F);
                if (Calc.AbsAngleDiff(newAngle, oldAngle) > maxTurnSpeed) {
                    oldAngle += maxTurnSpeed * -Calc.SignAngleDiff(newAngle, oldAngle);
                } else {
                    oldAngle = newAngle;
                }
            }

            return oldAngle + MathF.PI/2;
        }
    }
}

namespace MonoMod {
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchBirdPathRoutine))]
    class PatchBirdPathRoutineAttribute : Attribute { }
    
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchBirdPathUpdate))]
    class PatchBirdPathUpdateAttribute : Attribute { }
    
    static partial class MonoModRules {

        /// <summary>
        /// This extra bird.speedMult in the percentage calculation is not supposed to be there,
        /// it causes the bird to be too fast or too slow if that field is not set to one,
        /// consider this as a vanilla bug.
        /// </summary>
        public static void PatchBirdPathRoutine(MethodDefinition method, CustomAttribute attrib) {
            TypeDefinition closureRoutineType = MonoModRule.Modder.Module.GetType("Celeste.BirdPath/<Routine>d__18");
            method = closureRoutineType.FindMethod("MoveNext")!;
            FieldReference closureThisField = closureRoutineType.FindField("<>4__this")!;
            TypeDefinition birdPathType = MonoModRule.Modder.Module.GetType("Celeste.BirdPath");
            FieldReference inVanillaField = birdPathType.FindField("inVanilla")!;
            
            new ILContext(method).Invoke(il => {
                ILCursor cursor = new(il);

                // Go before the bird.speedMult
                cursor.GotoNext(MoveType.Before, instr => instr.MatchLdloc1(), 
                    instr => instr.MatchLdfld("Celeste.BirdPath", "speedMult"));
                cursor.EmitLdarg0();
                cursor.EmitLdfld(closureThisField); // This is a closure, we need the actual instance
                cursor.EmitLdfld(inVanillaField); // Emit the vanilla check
                ILLabel skip = cursor.DefineLabel();
                ILLabel firstPart = cursor.DefineLabel();
                cursor.EmitBrfalse(firstPart);
                // And emulate a ternary for readability, otherwise decompilers will convert the for loop into a 
                // while loop
                cursor.GotoNext(MoveType.Before, i => i.MatchMul());
                // As such we keep the original speedMult in one branch
                cursor.EmitBr(skip);
                // And a 1 in the fixed one, then it is multiplied, so it's effectively a no-op
                cursor.EmitLdcR4(1);
                cursor.MarkLabel(skip);
                cursor.Index--; // The second branch is a single instr
                cursor.MarkLabel(firstPart);
            });
        }


        /// <summary>
        /// When speedMult is a low value it may happen that the target position set by the coroutine is behind
        /// the bird, causing the rotation to break and the bird will be facing a perpendicular direction (due to the lerp)
        /// so, via a new setting (actually two), the rotation is just obtained from the angle that the speed
        /// vector forms, with a maximum rotation speed to smoothen rapid changes.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="attrib"></param>
        public static void PatchBirdPathUpdate(MethodDefinition method, CustomAttribute attrib) {
            TypeDefinition birdPathType = MonoModRule.Modder.Module.GetType("Celeste.BirdPath");
            FieldReference angleFixField = birdPathType.FindField("angleFix")!;
            FieldReference spriteField = birdPathType.FindField("sprite")!;
            MethodReference calcAngleMethod = birdPathType.FindMethod("CalcAngle")!;

            new ILContext(method).Invoke(il => {
                ILCursor cursor = new(il);
                
                cursor.GotoNext(MoveType.Before, instr => instr.MatchLdarg0(),
                            instr => instr.MatchLdfld("Celeste.BirdPath", "speed"),
                            instr => instr.MatchCall("Monocle.Calc", "Angle"));

                // replace the right hand side of the rotation asignment with `CalcAngle` if `angleFix` is true
                Instruction startInstruction = cursor.Next!; // this is the vanilla procedure
                cursor.EmitLdarg0();
                Instruction jumpFix = cursor.Prev!; // there's a jump we have to fix
                cursor.EmitLdfld(angleFixField);
                cursor.EmitBrfalse(startInstruction);
                cursor.Prev.Operand = startInstruction; // blame monomod
                cursor.EmitLdarg0();
                cursor.EmitLdfld(spriteField); // we need to push the sprite instance since we'll jump to the stfld
                cursor.EmitLdarg0();
                cursor.EmitCall(calcAngleMethod);

                ILCursor cursor2 = cursor.Clone();
                // Find the asignment instr, its the next stfld
                cursor2.GotoNext(MoveType.Before, instr => instr.MatchStfld(out _)); 
                Instruction jmpTarget = cursor2.Next!;
                cursor.EmitBr(jmpTarget);
                // This basically creates an if-else block, a local to save the result that each branch of the if-else sets
                // and changes the assignment of the rotation to that local:
                // float rotation;
                // if (angleFix) {
                //     /* fallback logic */
                // } else {
                //     /* vanilla logic */
                // }
                // this.sprite.rotation = rotation;

                // an if jumps to the first instruction of the asignment, we moved that, fix it, so it jumps to the new first instruction
                // there's only one single beq.s in the whole method
                cursor.GotoPrev(MoveType.Before, instr => instr.MatchBeq(out _));
                cursor.Next!.Operand = jumpFix;
            });
        }
    }
}
