using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DraggableCorners
{
    [Verse.StaticConstructorOnStartup]
    static class DraggableCorners
    {
        public static int initialDragAxis = -1;
        public static Action<DesignationDragger, IntVec3> DesignationDragger_TryAddDragCell_Action =
            (Action<DesignationDragger, IntVec3>)Delegate
                .CreateDelegate(typeof(Action<DesignationDragger, IntVec3>), null,
                    typeof(DesignationDragger).GetMethod("TryAddDragCell",
                        BindingFlags.NonPublic | BindingFlags.Instance));

        public static Func<DesignationDragger, IntVec3> DesignationDragger_startDragCell_Getter;

        static DraggableCorners()
        {
            var field = typeof(DesignationDragger).GetField("startDragCell",
                BindingFlags.NonPublic | BindingFlags.Instance);
            string methodName = field.ReflectedType.FullName + ".get_" + field.Name;
            DynamicMethod getterMethod =
                new DynamicMethod(methodName, typeof(IntVec3), new[] { typeof(DesignationDragger) }, true);
            ILGenerator gen = getterMethod.GetILGenerator();
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Ldfld, field);
            gen.Emit(OpCodes.Ret);
            DesignationDragger_startDragCell_Getter =
                (Func<DesignationDragger, IntVec3>)getterMethod.CreateDelegate(
                    typeof(Func<DesignationDragger, IntVec3>));

            Harmony harmony = new Harmony("rimworld.sparr.draggablecorners");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        public static void DrawDesignationCorners(DesignationDragger DD) {
            // IntVec3 beg = DD.startDragCell;
            IntVec3 beg = DesignationDragger_startDragCell_Getter(DD);

            // DD.TryAddDragCell(beg);
            DesignationDragger_TryAddDragCell_Action(DD, beg);

            IntVec3 end = UI.MouseCell();
            if (beg == end)
            {
                initialDragAxis = -1;
                return;
            }
            if (initialDragAxis == -1) {
                if (end.x != beg.x) {
                    initialDragAxis = 0;
                } else if (end.z != beg.z) {
                    initialDragAxis = 2;
                }
            }

            IntVec3 cur = beg;
            bool drawRectangle = false;
            Designator selDes = Find.DesignatorManager.SelectedDesignator;
            if (selDes is Designator_Build des)
            {
                if (des.PlacingDef is ThingDef def)
                {
                    drawRectangle =
                    (
                        (def.passability == Traversability.Impassable) &&
                        (def.category == ThingCategory.Building)
                    );
                }
            }
            void drawSegment(ref int curCoord, int endCoord)
            {
                while (curCoord != endCoord)
                {
                    curCoord += Math.Sign(endCoord - curCoord);
                    // DD.TryAddDragCell(cur);
                    DesignationDragger_TryAddDragCell_Action(DD, cur);
                }
            }
            if (drawRectangle)
            {
                drawSegment(ref cur.x, end.x);
                if (end.z != beg.z)
                {
                    drawSegment(ref cur.z, end.z);
                    if (end.x != beg.x)
                    {
                        drawSegment(ref cur.x, beg.x);
                        drawSegment(ref cur.z, beg.z + (cur.z > beg.z ? 1 : -1)); // don't draw beg again
                    }
                }
            }
            else
            {
                if (initialDragAxis == 0)
                {
                    drawSegment(ref cur.x, end.x);
                    drawSegment(ref cur.z, end.z);
                } else
                {
                    drawSegment(ref cur.z, end.z);
                    drawSegment(ref cur.x, end.x);
                }
            }
        }

    }

    [HarmonyPatch(typeof(DesignationDragger), "UpdateDragCellsIfNeeded")]
    static class DesignationDragger_UpdateDragCellsIfNeeded
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int blockBegin = -1, blockPastEnd = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt &&
                    codes[i].operand ==
                        AccessTools.Property(type: typeof(Designator), name: nameof(Designator.DraggableDimensions)).GetGetMethod() &&
                    codes[i+1].opcode == OpCodes.Ldc_I4_1 &&
                    codes[i+2].opcode == OpCodes.Bne_Un
                    )
                {
                    blockBegin = i + 3;
                    // found: if (this.SelDes.DraggableDimensions == 1)
                    Label nextBlockStart = (Label)codes[i+2].operand;
                    int j = i + 3;
                    while (j < codes.Count && !codes[j].labels.Contains(nextBlockStart))
                    {
                        j++;
                    }
                    blockPastEnd = j;
                    // replace contents of if{} with a call to DraggableCorners.DrawDesignationCorners(this)
                    codes.RemoveRange(blockBegin, blockPastEnd - blockBegin);
                    codes.Insert(blockBegin++, new CodeInstruction(opcode: OpCodes.Ldarg_0));
                    codes.Insert(blockBegin++, new CodeInstruction(opcode: OpCodes.Call, operand: typeof(DraggableCorners).GetMethod(nameof(DraggableCorners.DrawDesignationCorners))));
                }
            }
            return codes.AsEnumerable();
        }
    }

    [HarmonyPatch(typeof(DesignationDragger), "DraggerOnGUI")]
    static class DesignationDragger_DraggerOnGUI
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            int countStartDragCell = 0;
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count - 1; i++) {
                if (codes[i + 1].opcode == OpCodes.Ldflda &&
                    codes[i + 1].operand == typeof(DesignationDragger).GetField("startDragCell", BindingFlags.NonPublic | BindingFlags.Instance)
                    )
                {
                    // there are 4 of theses, we care about #2 and #4
                    countStartDragCell++;
                    switch (countStartDragCell) {
                        case 2:
                        case 4:
                            int bookmark = i;
                            // put a new Label on the original code
                            Label labelOriginal = il.DefineLabel();
                            codes[i].labels.Add(labelOriginal);
                            // put a new Label past the original code
                            Label labelDone = il.DefineLabel();
                            codes[i + 2].labels.Add(labelDone);
                            // push(DraggableCorners.initialDragAxis)
                            codes.Insert(i++, new CodeInstruction(opcode: OpCodes.Ldsfld, operand: typeof(DraggableCorners).GetField("initialDragAxis")));
                            // push 0 or 2 depending on the case, to compare to initialDragAxis
                            if (countStartDragCell == 2)
                            {
                                codes.Insert(i++, new CodeInstruction(opcode: OpCodes.Ldc_I4_2));
                            }
                            else
                            {
                                codes.Insert(i++, new CodeInstruction(opcode: OpCodes.Ldc_I4_0));
                            }
                            // jump over new code to the original code
                            codes.Insert(i++, new CodeInstruction(opcode: OpCodes.Bne_Un_S, operand: labelOriginal));
                            // push(Verse.UI::MouseCell())
                            codes.Insert(i++, new CodeInstruction(opcode: OpCodes.Call, operand: typeof(UI).GetMethod(nameof(UI.MouseCell))));
                            // replace mousecell with address-of-mousecell-variable
                            codes.Insert(i++, new CodeInstruction(opcode: OpCodes.Stloc_2));
                            codes.Insert(i++, new CodeInstruction(opcode: OpCodes.Ldloca_S, operand: 2));
                            // jump past the original code
                            codes.Insert(i++, new CodeInstruction(opcode: OpCodes.Br_S, operand: labelDone));
                            break;
                        default:
                            continue;
                    }
                }
            }
            return codes;
        }
    }

}