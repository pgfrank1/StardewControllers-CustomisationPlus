using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewValley.Menus;

namespace StarControl.Patches;

internal static class GamePatches
{
    public static bool SuppressRightStickChatBox { get; set; }
    public static Func<bool>? IsRadialMenuActive { get; set; }

    private static List<IClickableMenu>? removedHudMenus;

    private static readonly MethodInfo IsButtonDownMethod = AccessTools.Method(
        typeof(GamePadState),
        nameof(GamePadState.IsButtonDown)
    );
    private static readonly MethodInfo IsRightStickDownOrSuppressedMethod = AccessTools.Method(
        typeof(GamePatches),
        nameof(IsRightStickDownOrSuppressed)
    );

    public static void IsHudDrawn_Postfix(ref bool __result)
    {
        if (IsRadialMenuActive?.Invoke() == true)
        {
            Game1.displayHUD = true;
            __result = true;
        }
    }

    public static void DrawHud_Prefix()
    {
        if (IsRadialMenuActive?.Invoke() != true || Game1.onScreenMenus is null)
        {
            return;
        }
        Game1.displayHUD = true;
        removedHudMenus = Game1.onScreenMenus.OfType<Toolbar>().Cast<IClickableMenu>().ToList();
        foreach (var menu in removedHudMenus)
        {
            Game1.onScreenMenus.Remove(menu);
        }
    }

    // Finalizer (not a postfix) so the Toolbar is always restored even if drawHUD — or another
    // patch on it — throws. A void finalizer does not swallow the exception; Harmony rethrows it
    // after this runs, so genuine errors still surface in the log.
    public static void DrawHud_Finalizer()
    {
        if (removedHudMenus is null || Game1.onScreenMenus is null)
        {
            return;
        }
        foreach (var menu in removedHudMenus)
        {
            if (!Game1.onScreenMenus.Contains(menu))
            {
                Game1.onScreenMenus.Add(menu);
            }
        }
        removedHudMenus = null;
    }

    public static IEnumerable<CodeInstruction> UpdateChatBox_Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator gen,
        MethodBase original
    )
    {
        return new CodeMatcher(instructions, gen)
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldloca_S),
                new CodeMatch(OpCodes.Ldc_I4, 128),
                new CodeMatch(OpCodes.Call, IsButtonDownMethod)
            )
            .ThrowIfNotMatch("Couldn't find right-stick button check in the method body")
            .SetOpcodeAndAdvance(OpCodes.Ldloc_S)
            .RemoveInstructions(2)
            .Insert(new CodeInstruction(OpCodes.Call, IsRightStickDownOrSuppressedMethod))
            .InstructionEnumeration();
    }

    private static bool IsRightStickDownOrSuppressed(GamePadState gamePadState)
    {
        return SuppressRightStickChatBox || gamePadState.IsButtonDown(Buttons.RightStick);
    }
}
