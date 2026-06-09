using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StarControl.Config;
using StarControl.Graphics;
using StarControl.Input;
using StarControl.Patches;
using StardewValley;
using StardewValley.Menus;

namespace StarControl.Menus;

internal class RadialMenuController(
    IInputHelper inputHelper,
    ModConfig config,
    Farmer player,
    RadialMenuPainter radialMenuPainter,
    QuickSlotController quickSlotController,
    params IRadialMenu[] menus
)
{
    public event EventHandler<ItemActivationEventArgs>? ItemActivated;

    public IEnumerable<IRadialMenuItem> AllItems =>
        menus
            .SelectMany(menu => menu.Pages)
            .SelectMany(pages => pages.Items)
            .Where(item => item is not null)
            .Cast<IRadialMenuItem>();

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (value == enabled)
            {
                return;
            }
            Logger.Log(LogCategory.Menus, $"Controller enabled -> {value}");
            enabled = value;
            if (!value)
            {
                Reset();
            }
        }
    }
    public bool IsMenuActive => activeMenu is not null;

    private const int MENU_ANIMATION_DURATION_MS = 120;
    private const int QUICK_SLOT_ANIMATION_DURATION_MS = 250;

    // Fraction of a sector (past the halfway boundary) the cursor must cross before the focused
    // item switches, so analog drift resting on a boundary doesn't flip-flop between two items.
    private const float FOCUS_HYSTERESIS = 0.15f;

    private IRadialMenu? activeMenu;
    private float? cursorAngle;
    private PendingActivation? delayedItem;
    private TimeSpan elapsedActivationDelay;
    private bool enabled;
    private int focusedIndex;
    private IRadialMenuItem? focusedItem;
    private float menuOpenTimeMs;
    private float menuScale;
    private float quickSlotOpacity;
    private bool? previousMouseVisible;
    private bool usedRightStickInMenu;
    private bool? previousDisplayHud;
    private Toolbar? hiddenToolbar;

    public void Draw(SpriteBatch b, Rectangle? viewport = null)
    {
        if (activeMenu?.GetSelectedPage() is not { } page)
        {
            return;
        }
        viewport ??= Viewports.DefaultViewport;
        // Forcing a new sprite batch appears to be the only way to get the menu, which includes a
        // BasicEffect, to draw over rather than under the fade.
        b.End();
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        radialMenuPainter.Items = page.Items;
        radialMenuPainter.Scale = menuScale;
        radialMenuPainter.Paint(
            b,
            page.SelectedItemIndex,
            focusedIndex,
            cursorAngle,
            GetSelectionBlend(),
            viewport
        );
        quickSlotController.Draw(b, viewport.Value, quickSlotOpacity);
    }

    public void DrawFade(SpriteBatch b, Rectangle? viewport = null)
    {
        return;
    }

    public void Invalidate()
    {
        Logger.Log(LogCategory.Menus, "Menu controller invalidated.", LogLevel.Info);
        foreach (var menu in menus)
        {
            menu.Invalidate();
        }
        quickSlotController.Invalidate();
    }

    public void Update(TimeSpan elapsed)
    {
        foreach (var menu in menus)
        {
            menu.Toggle.PreUpdate(Enabled);
        }

        if (!Enabled)
        {
            return;
        }

        AnimateMenuOpen(elapsed);

        // Used only for animation, doesn't interfere with logic right now.
        quickSlotController.Update(elapsed);

        if (TryActivateDelayedItem(elapsed))
        {
            Logger.Log(LogCategory.Menus, "Delayed item was activated; skipping rest of update.");
            return;
        }

        var previousActiveMenu = activeMenu;
        TryInteractWithActiveMenu();
        foreach (var menu in menus)
        {
            if (menu == previousActiveMenu)
            {
                continue;
            }
            menu.Toggle.Update(allowOn: activeMenu is null);
            if (menu.Toggle.State != MenuToggleState.On)
            {
                continue;
            }
            Logger.Log(
                LogCategory.Menus,
                $"Menu {Array.IndexOf(menus, menu)} became active; "
                    + $"RememberSelection = {config.Input.RememberSelection}."
            );
            Sound.Play(config.Sound.MenuOpenSound);
            if (!config.Input.RememberSelection)
            {
                menu.ResetSelectedPage();
            }
            else if (menu.GetSelectedPage()?.IsEmpty() == true)
            {
                Logger.Log(
                    LogCategory.Menus,
                    "Menu is configured to remember selection, but the selected page is empty. "
                        + "Attempting to navigate to previous page.",
                    LogLevel.Info
                );
                // Will automatically try to find a non-empty page.
                menu.PreviousPage();
            }
            activeMenu = menu;
            if (previousActiveMenu is null && activeMenu is not null)
            {
                InputPatches.RightStickCursorDeadZone = Math.Clamp(
                    config.Input.ThumbstickDeadZone + 0.05f,
                    0.1f,
                    0.6f
                );
                InputPatches.AwaitRightStickMoveForCursor();
                InputPatches.ForceHideCursor = true;
                ResetMouseToPlayer();
                InputPatches.NotifyMousePositionReset();
                AnimateMenuOpen(elapsed); // Skip "zero" frame
            }
        }

        if (!IsMenuActive)
        {
            RestoreHudState();
        }
        var forceHideCursor =
            menus.Any(menu => menu.Toggle.State != MenuToggleState.Off) || menuOpenTimeMs > 0;
        InputPatches.ForceHideCursor = forceHideCursor;
        if (forceHideCursor)
        {
            if (previousMouseVisible is null)
            {
                previousMouseVisible = Game1.game1.IsMouseVisible;
            }
            Game1.game1.IsMouseVisible = false;
        }
        else if (previousMouseVisible is not null)
        {
            Game1.game1.IsMouseVisible = previousMouseVisible.Value;
            previousMouseVisible = null;
        }
        TryActivateQuickSlot();
    }

    private void ActivateFocusedItem()
    {
        if (focusedItem is null)
        {
            return;
        }
        if (
            SuppressIfPressed(config.Input.PrimaryActionButton)
            || CheckStickActivation(secondaryAction: false)
        )
        {
            Logger.Log(LogCategory.Menus, $"Primary activation triggered for {focusedItem.Title}.");
            ActivateItem(focusedItem, ItemActivationType.Primary);
        }
        else if (
            SuppressIfPressed(config.Input.SecondaryActionButton)
            || CheckStickActivation(secondaryAction: true)
        )
        {
            Logger.Log(
                LogCategory.Menus,
                $"Secondary activation triggered for {focusedItem.Title}."
            );
            ActivateItem(focusedItem, ItemActivationType.Secondary);
        }
    }

    private ItemActivationResult ActivateItem(
        IRadialMenuItem item,
        ItemActivationType activationType,
        bool allowDelay = true,
        bool forceSuppression = false
    )
    {
        if (!item.Enabled)
        {
            Sound.Play(config.Sound.ItemErrorSound);
            return ItemActivationResult.Ignored;
        }
        var result = item.Activate(
            player,
            allowDelay ? config.Input.DelayedActions : DelayedActions.None,
            activationType
        );
        Logger.Log(
            LogCategory.Activation,
            $"Activated {item.Title} with result: {result}",
            LogLevel.Info
        );
        switch (result)
        {
            case ItemActivationResult.Ignored:
                return result;
            case ItemActivationResult.Delayed:
                Sound.Play(config.Sound.ItemDelaySound);
                delayedItem = new(item, activationType);
                break;
            default:
                ItemActivated?.Invoke(this, new(item, result));
                if (forceSuppression)
                {
                    activeMenu?.Toggle.ForceOff();
                }
                activeMenu?.Toggle.ForceButtonSuppression();
                var activationSound = item.GetActivationSound(
                    player,
                    activationType,
                    config.Sound.ItemActivationSound
                );
                Sound.Play(activationSound ?? "");
                Reset(true);
                break;
        }
        return result;
    }

    private void AnimateMenuOpen(TimeSpan elapsed)
    {
        if (activeMenu is null || menuOpenTimeMs >= QUICK_SLOT_ANIMATION_DURATION_MS)
        {
            return;
        }
        menuOpenTimeMs += (float)elapsed.TotalMilliseconds;
        var menuProgress = MathHelper.Clamp(menuOpenTimeMs / MENU_ANIMATION_DURATION_MS, 0, 1);
        menuScale = menuProgress < 1 ? 1 - MathF.Pow(1 - menuProgress, 3) : 1;
        var quickSlotProgress = MathHelper.Clamp(
            menuOpenTimeMs / QUICK_SLOT_ANIMATION_DURATION_MS,
            0,
            1
        );
        quickSlotOpacity = quickSlotProgress < 1 ? MathF.Sin(quickSlotProgress * MathF.PI / 2f) : 1;
        Logger.Log(LogCategory.Menus, $"Menu animation frame: scale = {menuScale}", LogLevel.Trace);
    }

    private bool CheckStickActivation(bool secondaryAction)
    {
        if (activeMenu is null)
        {
            return false;
        }
        var activationMethod = secondaryAction
            ? config.Input.SecondaryActivationMethod
            : config.Input.PrimaryActivationMethod;
        if (activationMethod != ItemActivationMethod.ThumbStickPress)
        {
            return false;
        }
        var preference = GetThumbStickPreference(activeMenu);
        var result =
            preference == ThumbStickPreference.Both
                ? SuppressIfPressed(SButton.LeftStick) || SuppressIfPressed(SButton.RightStick)
                : SuppressIfPressed(
                    preference switch
                    {
                        ThumbStickPreference.AlwaysLeft => SButton.LeftStick,
                        ThumbStickPreference.AlwaysRight => SButton.RightStick,
                        _ => activeMenu.Toggle.IsRightSided()
                            ? SButton.RightStick
                            : SButton.LeftStick,
                    }
                );
        if (result)
        {
            Logger.Log(
                LogCategory.Input,
                $"Detected thumbstick activation (preference = {preference}), "
                    + $"secondary action = {secondaryAction}."
            );
        }
        return result;
    }

    private float GetSelectionBlend()
    {
        if (delayedItem is null)
        {
            return 1.0f;
        }
        var elapsed = (float)(
            config.Input.ActivationDelayMs - elapsedActivationDelay.TotalMilliseconds
        );
        return Animation.GetDelayFlashPosition(elapsed);
    }

    private void Reset(bool fromActivation = false)
    {
        Logger.Log(LogCategory.Menus, "Resetting menu controller state");
        delayedItem = null;
        elapsedActivationDelay = TimeSpan.Zero;
        focusedIndex = -1;
        focusedItem = null;
        cursorAngle = null;
        RestoreHudState();
        if (usedRightStickInMenu)
        {
            ResetMouseToPlayer();
            InputPatches.SuppressRightStickFor(InputPatches.RightStickSuppressionDuration);
        }
        if (Game1.player is not null)
        {
            Game1.player.lastClick = Vector2.Zero;
        }
        InputPatches.AwaitRightStickMoveForCursor();
        InputPatches.NotifyMousePositionReset();
        usedRightStickInMenu = false;
        if (fromActivation)
        {
            if (!config.Input.ReopenOnHold)
            {
                activeMenu?.Toggle.ForceOff();
            }
            activeMenu?.Toggle.ForceButtonSuppression();
        }
        activeMenu = null;
        menuOpenTimeMs = 0;
        menuScale = 0;
        quickSlotOpacity = 0;
        // Ensure the hidden-cursor state can't be stranded. Update()'s normal restore branch only
        // runs while the controller is enabled; when we reset because the controller is being
        // disabled (e.g. returning to title), that branch won't run again. In the normal in-Update
        // reset path this is harmless — the forceHideCursor re-evaluation later in the same Update
        // re-hides if a toggle is still partially active.
        InputPatches.ForceHideCursor = false;
        if (previousMouseVisible is not null)
        {
            Game1.game1.IsMouseVisible = previousMouseVisible.Value;
            previousMouseVisible = null;
        }
    }

    public void PrepareHudForMenu()
    {
        previousDisplayHud ??= Game1.displayHUD;
        Game1.displayHUD = true;
        if (Game1.onScreenMenus is null)
        {
            return;
        }
        var toolbars = Game1.onScreenMenus.OfType<Toolbar>().ToList();
        if (hiddenToolbar is null)
        {
            hiddenToolbar = toolbars.FirstOrDefault();
        }
        foreach (var toolbar in toolbars)
        {
            // Clear any stale hover item before removing the toolbar from onScreenMenus. While it's
            // removed, Toolbar.draw() (which normally nulls hoverItem every frame) never runs, and
            // Toolbar.performHoverAction is gated off while the cursor is hidden (Game1 only calls it
            // when wasMouseVisibleThisFrame is true). With both writers disabled a non-null hoverItem
            // would freeze and leak to mods that read it directly — e.g. UI Info Suite 2's sell-price
            // overlay, which then stays stuck on screen.
            toolbar.hoverItem = null;
            Game1.onScreenMenus.Remove(toolbar);
        }
    }

    public void RestoreHudState()
    {
        if (previousDisplayHud is not null)
        {
            Game1.displayHUD = previousDisplayHud.Value;
            previousDisplayHud = null;
        }
        if (hiddenToolbar is not null && Game1.onScreenMenus is not null)
        {
            // Belt-and-suspenders: ensure no hover item survived the hidden period before the
            // toolbar becomes live again, so the sell-price overlay can't reappear stale.
            hiddenToolbar.hoverItem = null;
            if (!Game1.onScreenMenus.Contains(hiddenToolbar))
            {
                Game1.onScreenMenus.Add(hiddenToolbar);
            }
            hiddenToolbar = null;
        }
    }

    private bool SuppressIfPressed(SButton button)
    {
        if (inputHelper.GetState(button) != SButtonState.Pressed)
        {
            return false;
        }
        Logger.Log(LogCategory.Input, $"Suppressing pressed button {button}.");
        inputHelper.Suppress(button);
        return true;
    }

    private bool TryActivateDelayedItem(TimeSpan elapsed)
    {
        if (delayedItem is not { } activation)
        {
            return false;
        }
        elapsedActivationDelay += elapsed;
        Logger.Log(
            LogCategory.Menus,
            "Delayed activation pending, "
                + $"{elapsedActivationDelay.TotalMilliseconds:F0} / "
                + $"{config.Input.ActivationDelayMs} ms elapsed.",
            LogLevel.Trace
        );
        if (elapsedActivationDelay.TotalMilliseconds >= config.Input.ActivationDelayMs)
        {
            Logger.Log(
                LogCategory.Menus,
                $"Delay of {config.Input.ActivationDelayMs} ms expired; activating "
                    + $"{activation.Item.Title}.",
                LogLevel.Info
            );
            var result = activation.Item.Activate(
                player,
                DelayedActions.None,
                activation.ActivationType
            );
            Logger.Log(
                LogCategory.Activation,
                $"Activated {activation.Item.Title} with result: {result}",
                LogLevel.Info
            );
            ItemActivated?.Invoke(this, new(activation.Item, result));
            Reset(true);
        }
        // We still return true here, even if the delay hasn't expired, because a delayed activation
        // should prevent any other menu state from changing.
        return true;
    }

    private void TryActivateQuickSlot()
    {
        if (activeMenu is null || delayedItem is not null || cursorAngle is not null)
        {
            return;
        }
        var nextActivation = quickSlotController.TryGetNextActivation(out var pressedButton);
        if (nextActivation is not null)
        {
            Logger.Log(
                LogCategory.QuickSlots,
                $"Quick slot activation detected for {nextActivation.Item.Title} in "
                    + $"{pressedButton} slot."
            );
            inputHelper.Suppress(pressedButton);
            Logger.Log(
                LogCategory.Input,
                $"Suppressed quick-slot activation button {pressedButton}."
            );
            if (nextActivation.RequireConfirmation)
            {
                Logger.Log(
                    LogCategory.QuickSlots,
                    "Confirmation required for quick slot; creating dialog."
                );
                var message = nextActivation.IsRegularItem
                    ? I18n.QuickSlotConfirmation_Item(nextActivation.Item.Title)
                    : I18n.QuickSlotConfirmation_Mod(nextActivation.Item.Title);
                Game1.activeClickableMenu = new ConfirmationDialog(
                    message,
                    _ =>
                    {
                        Logger.Log(
                            LogCategory.Activation,
                            "Activation confirmed from confirmation dialog."
                        );
                        Game1.activeClickableMenu = null;
                        ActivateItem(
                            nextActivation.Item,
                            nextActivation.ActivationType,
                            allowDelay: false,
                            // Forcing suppression here isn't done for any technical reason, it just seems more
                            // principle-of-least-surprise compliant not to have the menu immediately reopen or
                            // appear to stay open after e.g. switching a tool.
                            forceSuppression: true
                        );
                    },
                    onCancel: _ =>
                    {
                        Logger.Log(
                            LogCategory.Activation,
                            "Activation cancelled from confirmation dialog."
                        );
                    }
                );
            }
            else
            {
                var result = ActivateItem(nextActivation.Item, nextActivation.ActivationType);
                if (result == ItemActivationResult.Delayed)
                {
                    quickSlotController.ShowDelayedActivation(pressedButton);
                }
            }
        }
    }

    private void TryInteractWithActiveMenu()
    {
        if (activeMenu is null)
        {
            return;
        }

        activeMenu.Toggle.Update(allowOn: false);
        if (activeMenu.Toggle.State != MenuToggleState.On)
        {
            if (
                config.Input.PrimaryActivationMethod == ItemActivationMethod.TriggerRelease
                && focusedItem is not null
            )
            {
                Logger.Log(
                    LogCategory.Input,
                    "Trigger release activation detected for primary action."
                );
                activeMenu?.Toggle.ForceButtonSuppression();
                ActivateItem(focusedItem, ItemActivationType.Primary);
            }
            else if (
                config.Input.SecondaryActivationMethod == ItemActivationMethod.TriggerRelease
                && focusedItem is not null
            )
            {
                Logger.Log(
                    LogCategory.Input,
                    "Trigger release activation detected for secondary action."
                );
                activeMenu?.Toggle.ForceButtonSuppression();
                ActivateItem(focusedItem, ItemActivationType.Secondary);
            }
            else
            {
                Sound.Play(config.Sound.MenuCloseSound);
                Reset();
            }
            return;
        }

        int previousPageIndex = activeMenu.SelectedPageIndex;
        if (SuppressIfPressed(config.Input.PreviousPageButton))
        {
            if (activeMenu.PreviousPage())
            {
                Sound.Play(config.Sound.PreviousPageSound);
                Logger.Log(
                    LogCategory.Menus,
                    "Navigated to previous page "
                        + $"({previousPageIndex} -> {activeMenu.SelectedPageIndex})."
                );
            }
            else
            {
                Logger.Log(LogCategory.Menus, "Couldn't navigate to previous page.");
            }
        }
        else if (SuppressIfPressed(config.Input.NextPageButton))
        {
            if (activeMenu.NextPage())
            {
                Sound.Play(config.Sound.NextPageSound);
                Logger.Log(
                    LogCategory.Menus,
                    "Navigated to next page "
                        + $"({previousPageIndex} -> {activeMenu.SelectedPageIndex})."
                );
            }
            else
            {
                Logger.Log(LogCategory.Menus, "Couldn't navigate to next page.");
            }
        }

        UpdateFocus(activeMenu);
        ActivateFocusedItem();
    }

    private void UpdateFocus(IRadialMenu menu)
    {
        var thumbsticks = Game1.input.GetGamePadState().ThumbSticks;
        var preference = GetThumbStickPreference(menu);
        var position = preference switch
        {
            ThumbStickPreference.AlwaysLeft => thumbsticks.Left,
            ThumbStickPreference.AlwaysRight => thumbsticks.Right,
            ThumbStickPreference.Both => SelectThumbstick(thumbsticks.Left, thumbsticks.Right),
            _ => menu.Toggle.IsRightSided() ? thumbsticks.Right : thumbsticks.Left,
        };
        if (
            preference == ThumbStickPreference.AlwaysRight
            || (preference == ThumbStickPreference.SameAsTrigger && menu.Toggle.IsRightSided())
        )
        {
            if (position.Length() > config.Input.ThumbstickDeadZone)
            {
                usedRightStickInMenu = true;
            }
        }
        if (preference == ThumbStickPreference.Both)
        {
            if (thumbsticks.Right.Length() > config.Input.ThumbstickDeadZone)
            {
                usedRightStickInMenu = true;
            }
        }
        float? angle =
            position.Length() > config.Input.ThumbstickDeadZone
                ? MathF.Atan2(position.X, position.Y)
                : null;
        cursorAngle = (angle + MathHelper.TwoPi) % MathHelper.TwoPi;
        if (cursorAngle is not null && menu.GetSelectedPage() is { Items.Count: > 0 } page)
        {
            var itemAngle = MathHelper.TwoPi / page.Items.Count;
            var nextFocusedIndex =
                (int)MathF.Round(cursorAngle.Value / itemAngle) % page.Items.Count;
            // Hysteresis: when an item is already focused, require the cursor to move past the
            // sector boundary by FOCUS_HYSTERESIS before switching. Without this, a stick resting on
            // a boundary (common with analog drift just past the dead zone) flips between two items
            // every frame and re-triggers the focus sound.
            if (
                focusedIndex >= 0
                && focusedIndex < page.Items.Count
                && nextFocusedIndex != focusedIndex
            )
            {
                var focusedCenter = focusedIndex * itemAngle;
                var angleFromFocused = MathF.Abs(
                    MathHelper.WrapAngle(cursorAngle.Value - focusedCenter)
                );
                if (angleFromFocused < itemAngle * (0.5f + FOCUS_HYSTERESIS))
                {
                    nextFocusedIndex = focusedIndex;
                }
            }
            if (nextFocusedIndex != focusedIndex || page.Items[nextFocusedIndex] != focusedItem)
            {
                Logger.Log(
                    LogCategory.Menus,
                    $"Changed focused index from {focusedIndex} -> {nextFocusedIndex}. "
                        + $"(cursor angle = {cursorAngle} for {page.Items.Count} total items)"
                );
                Sound.Play(config.Sound.ItemFocusSound);
                focusedIndex = nextFocusedIndex;
                focusedItem = page.Items[focusedIndex];
            }
        }
        else
        {
            focusedIndex = -1;
            focusedItem = null;
        }
    }

    private ThumbStickPreference GetThumbStickPreference(IRadialMenu menu)
    {
        return menu switch
        {
            InventoryMenu => config.Input.InventoryThumbStickPreference,
            ModMenu => config.Input.ModMenuThumbStickPreference,
            _ => config.Input.ThumbStickPreference,
        };
    }

    private Vector2 SelectThumbstick(Vector2 left, Vector2 right)
    {
        var leftLen = left.Length();
        var rightLen = right.Length();
        if (rightLen > leftLen)
        {
            return right;
        }
        return left;
    }

    private static void ResetMouseToPlayer()
    {
        var player = Game1.player;
        if (player is null)
        {
            return;
        }
        var facingOffset = player.FacingDirection switch
        {
            0 => new Vector2(0, -1), // Up
            1 => new Vector2(1, 0), // Right
            2 => new Vector2(0, 1), // Down
            3 => new Vector2(-1, 0), // Left
            _ => Vector2.Zero,
        };
        var standingPos = player.getStandingPosition();
        var targetTile =
            new Vector2(
                (int)(standingPos.X / Game1.tileSize),
                (int)(standingPos.Y / Game1.tileSize)
            ) + facingOffset;
        var targetPos =
            targetTile * Game1.tileSize + new Vector2(Game1.tileSize / 2f, Game1.tileSize / 2f);
        var viewport = Game1.viewport;
        var cursorX = (int)(targetPos.X - viewport.X);
        var cursorY = (int)(targetPos.Y - viewport.Y);
        // ui_scale: false — cursorX/Y are in world-render (zoomLevel) space, derived from
        // Game1.viewport, so force the zoomLevel conversion regardless of the ambient uiMode.
        Game1.setMousePosition(cursorX, cursorY, ui_scale: false);
    }
}
