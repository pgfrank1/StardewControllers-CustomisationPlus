using HarmonyLib;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;

namespace StarControl.Patches;

internal static class Patcher
{
    public static void PatchAll(IManifest mod)
    {
        var harmony = new Harmony(mod.UniqueID);
        TryPatch(
            harmony,
            typeof(Game1),
            "UpdateChatBox",
            transpiler: new(typeof(GamePatches), nameof(GamePatches.UpdateChatBox_Transpiler))
        );
        var genericGamePadStateTranspiler = new HarmonyMethod(
            typeof(InputPatches),
            nameof(InputPatches.GenericGamePadStateTranspiler)
        );
        var genericOldPadStateTranspiler = new HarmonyMethod(
            typeof(InputPatches),
            nameof(InputPatches.GenericOldPadStateTranspiler)
        );
        TryPatch(
            harmony,
            typeof(Game1),
            nameof(Game1.didPlayerJustLeftClick),
            transpiler: genericGamePadStateTranspiler
        );
        TryPatch(
            harmony,
            typeof(Game1),
            "get_IsHudDrawn",
            postfix: new(typeof(GamePatches), nameof(GamePatches.IsHudDrawn_Postfix))
        );
        TryPatch(
            harmony,
            typeof(Game1),
            "drawHUD",
            prefix: new(typeof(GamePatches), nameof(GamePatches.DrawHud_Prefix)),
            finalizer: new(typeof(GamePatches), nameof(GamePatches.DrawHud_Finalizer))
        );
        TryPatch(
            harmony,
            typeof(Game1),
            "drawMouseCursor",
            prefix: new(typeof(InputPatches), nameof(InputPatches.DrawMouseCursor_Prefix))
        );
        // Correct the "Always Show Tool Hit Location" marker for controller play. Target the
        // GetToolLocation(Vector2, bool) overload specifically (Character has two overloads);
        // parameterTypes disambiguates so a future signature change degrades to a skipped patch
        // rather than an ambiguous-match crash.
        TryPatch(
            harmony,
            typeof(Character),
            nameof(Character.GetToolLocation),
            prefix: new(typeof(InputPatches), nameof(InputPatches.GetToolLocation_Prefix)),
            parameterTypes: new[] { typeof(Vector2), typeof(bool) }
        );
        TryPatch(
            harmony,
            typeof(InputState),
            nameof(InputState.GetGamePadState),
            postfix: new(typeof(InputPatches), nameof(InputPatches.GetGamePadState_Postfix))
        );
        TryPatch(
            harmony,
            typeof(FishingRod),
            nameof(FishingRod.beginUsing),
            transpiler: genericGamePadStateTranspiler
        );
        TryPatch(
            harmony,
            typeof(FishingRod),
            nameof(FishingRod.tickUpdate),
            transpiler: genericGamePadStateTranspiler
        );
        TryPatch(
            harmony,
            typeof(BobberBar),
            nameof(BobberBar.update),
            transpiler: genericOldPadStateTranspiler
        );
    }

    private static void TryPatch(
        Harmony harmony,
        Type targetType,
        string targetMethodName,
        HarmonyMethod? prefix = null,
        HarmonyMethod? postfix = null,
        HarmonyMethod? transpiler = null,
        HarmonyMethod? finalizer = null,
        Type[]? parameterTypes = null
    )
    {
        try
        {
            var method = AccessTools.Method(targetType, targetMethodName, parameterTypes);
            if (method is null)
            {
                Logger.Log(
                    $"Harmony patching skipped: method {MethodName()} does not exist.",
                    LogLevel.Warn
                );
                return;
            }
            harmony.Patch(method, prefix, postfix, transpiler, finalizer);
            Logger.Log($"Patched {MethodName()}.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to patch {MethodName()}: {ex}", LogLevel.Error);
        }

        string MethodName() => targetType.FullName + '.' + targetMethodName;
    }
}
