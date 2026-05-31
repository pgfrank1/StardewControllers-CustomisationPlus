using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace StarControl.Patches;

internal static class InputPatches
{
    public static Buttons? ToolUseButton { get; set; }
    public static TimeSpan RightStickSuppressionDuration { get; set; } = TimeSpan.FromSeconds(0.5);
    public static bool ForceHideCursor { get; set; }
    public static float RightStickCursorDeadZone { get; set; } = 0.25f;

    private static double rightStickSuppressUntilMs;
    private static bool rightStickCursorAwaitingMove;
    private static double rightStickCursorAwaitMoveAfterMs;
    private static Point lastMousePosition;
    private static int lastMouseScrollValue;
    private static int lastMouseHScrollValue;
    private static bool mouseRevealArmed;
    private static double mouseRevealIgnoreUntilMs;

    private const double MouseRevealDelayMs = 75;
    private const int MouseRevealMoveThreshold = 4;

    private static readonly FieldInfo GameInputField = AccessTools.Field(
        typeof(Game1),
        nameof(Game1.input)
    );
    private static readonly MethodInfo GetGamePadStateMethod = AccessTools.Method(
        typeof(InputState),
        nameof(InputState.GetGamePadState)
    );
    private static readonly MethodInfo GetRemappedGamePadStateMethod = AccessTools.Method(
        typeof(InputPatches),
        nameof(GetRemappedGamePadState)
    );
    private static readonly MethodInfo GetRemappedOldPadStateMethod = AccessTools.Method(
        typeof(InputPatches),
        nameof(GetRemappedOldPadState)
    );
    private static readonly FieldInfo OldPadStateField = AccessTools.Field(
        typeof(Game1),
        nameof(Game1.oldPadState)
    );

    public static IEnumerable<CodeInstruction> GenericGamePadStateTranspiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator gen,
        MethodBase original
    )
    {
        return new CodeMatcher(instructions, gen)
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldsfld, GameInputField),
                new CodeMatch(OpCodes.Callvirt, GetGamePadStateMethod)
            )
            .Repeat(
                matcher =>
                    matcher
                        .SetAndAdvance(OpCodes.Call, GetRemappedGamePadStateMethod)
                        .RemoveInstructions(1),
                _ =>
                    throw new InvalidOperationException(
                        "Couldn't find call to Game1.input.GetGamePadState() in the method body"
                    )
            )
            .InstructionEnumeration();
    }

    public static IEnumerable<CodeInstruction> GenericOldPadStateTranspiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator gen,
        MethodBase original
    )
    {
        var stateLocal = gen.DeclareLocal(typeof(GamePadState));
        return new CodeMatcher(instructions, gen)
            .MatchStartForward(new CodeMatch(OpCodes.Ldsflda, OldPadStateField))
            .Repeat(
                matcher =>
                    matcher
                        .SetAndAdvance(OpCodes.Call, GetRemappedOldPadStateMethod)
                        .Insert(
                            new CodeInstruction(OpCodes.Stloc_S, stateLocal.LocalIndex),
                            new CodeInstruction(OpCodes.Ldloca_S, stateLocal.LocalIndex)
                        ),
                _ =>
                    throw new InvalidOperationException(
                        "Couldn't find call to Game1.oldPadState in the method body"
                    )
            )
            .InstructionEnumeration();
    }

    private static GamePadState GetRemappedGamePadState()
    {
        var gamepadState = Game1.input.GetGamePadState();
        // We are going to be suppressing our own input in the RemappingController to prevent
        // vanilla function, e.g. B button is mapped to a tool and therefore suppressed to avoid
        // bringing up the menu. This means we need to bypass that suppression in order to determine
        // if the button is actually being pressed, which requires going to the "raw" state
        // unmodified by SMAPI, similar to the hack used for trigger buttons in MenuToggle.
        //
        // Note however that we don't want to actually use this raw state as the _result_, since it
        // may lose other nuances, for example other unrelated buttons being suppressed. Only want
        // to pull remapped buttons from the raw state into the real un-remapped state.
        var rawState =
            Game1.playerOneIndex >= PlayerIndex.One
                ? GamePad.GetState(Game1.playerOneIndex)
                : new();
        RemapGamePadState(ref gamepadState, rawState);
        return gamepadState;
    }

    public static void SuppressRightStickFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }
        var nowMs = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        rightStickSuppressUntilMs = Math.Max(
            rightStickSuppressUntilMs,
            nowMs + duration.TotalMilliseconds
        );
        rightStickCursorAwaitingMove = true;
        rightStickCursorAwaitMoveAfterMs = Math.Max(
            rightStickCursorAwaitMoveAfterMs,
            rightStickSuppressUntilMs
        );
    }

    public static void AwaitRightStickMoveForCursor()
    {
        var nowMs = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        rightStickCursorAwaitingMove = true;
        mouseRevealArmed = true;
        rightStickCursorAwaitMoveAfterMs = Math.Max(rightStickCursorAwaitMoveAfterMs, nowMs);
        mouseRevealIgnoreUntilMs = Math.Max(mouseRevealIgnoreUntilMs, nowMs + MouseRevealDelayMs);
        lastMousePosition = Game1.getMousePosition(ui_scale: true);
    }

    public static void NotifyMousePositionReset()
    {
        var mouseState = Game1.input.GetMouseState();
        lastMousePosition = new Point(mouseState.X, mouseState.Y);
        lastMouseScrollValue = mouseState.ScrollWheelValue;
        lastMouseHScrollValue = mouseState.HorizontalScrollWheelValue;
    }

    public static void GetGamePadState_Postfix(ref GamePadState __result)
    {
        var nowMs = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        if (
            rightStickCursorAwaitingMove
            && nowMs >= rightStickCursorAwaitMoveAfterMs
            && __result.ThumbSticks.Right.Length() > RightStickCursorDeadZone
        )
        {
            rightStickCursorAwaitingMove = false;
        }
        if (!IsRightStickSuppressed())
        {
            return;
        }
        var sticks = __result.ThumbSticks;
        if (sticks.Right == Vector2.Zero)
        {
            return;
        }
        __result = new GamePadState(
            new GamePadThumbSticks(sticks.Left, Vector2.Zero),
            __result.Triggers,
            __result.Buttons,
            __result.DPad
        );
    }

    public static void ShouldDrawMouseCursor_Postfix(ref bool __result)
    {
        ClearAwaitIfMouseMoved();
        if (ShouldHideCursor())
        {
            __result = false;
        }
    }

    /// <summary>
    /// Whether StarControl is currently hiding the mouse cursor (radial menu open, or gamepad
    /// play awaiting right-stick movement). Single source of truth so the cursor-hiding and
    /// the tool-hit-marker correction below can't drift apart.
    /// </summary>
    private static bool ShouldHideCursor()
    {
        return ForceHideCursor || rightStickCursorAwaitingMove || IsRightStickSuppressed();
    }

    /// <summary>
    /// Vanilla bug workaround: the "Always Show Tool Hit Location" marker is drawn in
    /// Farmer.draw via the GetToolLocation(Vector2) overload using the raw mouse position,
    /// without the gamepad/mouse-visibility guard that the parameterless overload (and thus the
    /// actual tool use) applies. While we're hiding the cursor for controller play, that mouse
    /// position is stale, so the red marker can appear behind/beside the player even though the
    /// tool correctly acts on the tile in front. Force ignoreClick = true here, which is exactly
    /// what vanilla's guarded overload does for gamepad input, so the marker resolves to the
    /// facing-direction tile and matches where the tool actually hits.
    /// </summary>
    public static void GetToolLocation_Prefix(ref bool ignoreClick)
    {
        if (ShouldHideCursor())
        {
            ignoreClick = true;
        }
    }

    public static bool DrawMouseCursor_Prefix()
    {
        ClearAwaitIfMouseMoved();
        // Only take over cursor drawing for world gameplay. When a menu is open (mail, shop,
        // etc.) the menu draws its own cursor and uses Game1.mouseCursorTransparency; if we ran
        // our hiding logic here it would corrupt that menu cursor (it could render faint or in
        // the wrong layer). Defer entirely to vanilla while a menu is active.
        if (Game1.activeClickableMenu is not null || !ShouldHideCursor())
        {
            return true;
        }
        // We're hiding the cursor (radial menu open, or gamepad play awaiting right-stick
        // movement). Vanilla does two things inside drawMouseCursor that we'd otherwise lose by
        // skipping the whole method:
        //
        // 1. Its gamepad branch sets mouseCursorTransparency = 0 / wasMouseVisibleThisFrame =
        //    false. These are what make Game1.IsPerformingMousePlacement() return false, so
        //    GetPlacementGrabTile() tracks the tile in front of the player instead of the last
        //    mouse position. Without this, after the mouse has been used the placement target
        //    (and indicator) stick to the stale, possibly far-off-screen mouse location and the
        //    actual placement goes there too. Replicate that here so placement is player-relative
        //    while the cursor is hidden.
        // 2. It draws the placeable-item placement indicator (the green tile for seeds,
        //    saplings, furniture, etc.). Reproduce that below so it still shows.
        Game1.mouseCursorTransparency = 0f;
        Game1.wasMouseVisibleThisFrame = false;
        DrawPlacementBoundsIfNeeded();
        return false;
    }

    private static void DrawPlacementBoundsIfNeeded()
    {
        // Don't show the placement indicator while the radial menu itself is open.
        if (GamePatches.IsRadialMenuActive?.Invoke() == true)
        {
            return;
        }
        if (Game1.activeClickableMenu is not null || Game1.currentLocation is null)
        {
            return;
        }
        var player = Game1.player;
        if (
            player?.ActiveObject is null
            || Game1.eventUp
            || Game1.currentMinigame is not null
            || player.isRidingHorse()
            || !player.CanMove
            || !Game1.displayFarmer
        )
        {
            return;
        }
        // Caller has set mouseCursorTransparency to 0, so this matches vanilla's gamepad gate,
        // which falls back to the showPlacementTileForGamepad option (defaults to true).
        if (Game1.options.showPlacementTileForGamepad)
        {
            player.ActiveObject.drawPlacementBounds(Game1.spriteBatch, Game1.currentLocation);
        }
    }

    private static void ClearAwaitIfMouseMoved()
    {
        if (!rightStickCursorAwaitingMove || ForceHideCursor)
        {
            return;
        }
        var nowMs = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        var mouseState = Game1.input.GetMouseState();
        var currentMousePosition = new Point(mouseState.X, mouseState.Y);
        var hasButtonDown =
            mouseState.LeftButton == ButtonState.Pressed
            || mouseState.RightButton == ButtonState.Pressed
            || mouseState.MiddleButton == ButtonState.Pressed
            || mouseState.XButton1 == ButtonState.Pressed
            || mouseState.XButton2 == ButtonState.Pressed;
        var hasScroll =
            mouseState.ScrollWheelValue != lastMouseScrollValue
            || mouseState.HorizontalScrollWheelValue != lastMouseHScrollValue;
        var hasMouseInput = hasButtonDown || hasScroll;
        var delta = currentMousePosition - lastMousePosition;
        var movedEnough =
            mouseRevealArmed
            && nowMs >= mouseRevealIgnoreUntilMs
            && (
                Math.Abs(delta.X) >= MouseRevealMoveThreshold
                || Math.Abs(delta.Y) >= MouseRevealMoveThreshold
            );
        if (hasMouseInput || movedEnough)
        {
            rightStickCursorAwaitingMove = false;
        }
        lastMousePosition = currentMousePosition;
        lastMouseScrollValue = mouseState.ScrollWheelValue;
        lastMouseHScrollValue = mouseState.HorizontalScrollWheelValue;
    }

    private static bool IsRightStickSuppressed()
    {
        var nowMs = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        return nowMs > 0 && nowMs < rightStickSuppressUntilMs;
    }

    private static GamePadState GetRemappedOldPadState()
    {
        var gamepadState = Game1.oldPadState;
        RemapGamePadState(ref gamepadState);
        return gamepadState;
    }

    private static void RemapGamePadState(
        ref GamePadState gamepadState,
        GamePadState? rawState = null
    )
    {
        if (ToolUseButton is null)
        {
            return;
        }
        var downButtons = gamepadState.Buttons._buttons;
        var remapState = rawState ?? gamepadState;
        if (remapState.IsButtonDown(ToolUseButton.Value))
        {
            downButtons |= Buttons.X;
        }
        gamepadState.Buttons = new(downButtons);
    }
}
