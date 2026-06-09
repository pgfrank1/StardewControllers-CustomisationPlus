using Microsoft.Xna.Framework.Graphics;
using StarControl.Config;
using StarControl.Data;
using StarControl.Graphics;
using StarControl.Patches;
using StardewValley.Tools;

namespace StarControl.Menus;

internal class RemappingController(
    IInputHelper inputHelper,
    ModConfig config,
    Farmer who,
    QuickSlotResolver resolver,
    QuickSlotRenderer renderer
)
{
    public event EventHandler<ItemActivationEventArgs>? ItemActivated;

    private const float BASE_OPACITY = 0.6f;

    // Should generally be kept in sync with RadialMenuController.QUICK_SLOT_ANIMATION_DURATION_MS.
    // Otherwise there may be "pops" where the combined displays are darker or lighter than they
    // should be.
    private const int FADE_DURATION_MS = 250;

    public bool HudVisible { get; set; }

    public Dictionary<SButton, RemappingSlot> Slots
    {
        get => slots;
        set
        {
            slots = value;
            ResolveSlots();
        }
    }

    private readonly HashSet<SButton> downButtons = [];
    private readonly Dictionary<SButton, IRadialMenuItem> resolvedItems = [];

    private float fadeTimeMs;
    private Dictionary<SButton, RemappingSlot> slots = [];

    public void Draw(SpriteBatch b)
    {
        if (Game1.IsHudDrawn && renderer.SpriteOpacity > 0)
        {
            renderer.Draw(b, Viewports.DefaultViewport);
        }
    }

    public void Refresh()
    {
        ResolveSlots();
    }

    public void Update(TimeSpan elapsed, bool isMenuActive)
    {
        Fade(elapsed, isMenuActive);
        renderer.Update(elapsed);

        if (isMenuActive)
        {
            return;
        }

        foreach (var (button, slot) in Slots)
        {
            var buttonState = inputHelper.GetState(button);
            var item = resolvedItems.GetValueOrDefault(button);
            if (item is null)
            {
                // Slots that are assigned and visible, but can't be used, should still bypass their
                // normal function. If they're not even visible then the result is ambiguous, but
                // may be less confusing to allow the default behavior in case player doesn't think
                // to check the Instant Actions menu for what is assigned.
                if (
                    buttonState == SButtonState.Pressed
                    && Context.IsPlayerFree
                    && Game1.activeClickableMenu is null
                )
                {
                    inputHelper.Suppress(button);
                    renderer.FlashError(button);
                    Sound.Play(config.Sound.ItemErrorSound);
                }
                continue;
            }
            var isControllerButton = button.TryGetController(out var cb);
            var controllerButton = isControllerButton ? cb : default;
            var wasButtonDown = downButtons.Contains(button);
            var wasPatched = InputPatches.ToolUseButton == controllerButton;
            if (wasButtonDown || wasPatched)
            {
                // If the controller disconnects mid-press, SMAPI can freeze the button's state as
                // Held/suppressed instead of transitioning to Released, which would leak the
                // simulated tool button (InputPatches.ToolUseButton) and downButtons. Treat a
                // disconnected controller as button-up so the existing release path below cleans up.
                var controllerDisconnected =
                    isControllerButton && !Game1.input.GetGamePadState().IsConnected;
                var isButtonUp =
                    controllerDisconnected
                    || (
                        !inputHelper.IsSuppressed(button)
                        && buttonState is SButtonState.Released or SButtonState.None
                    );
                // We have to release the simulated tool button as soon as the remapped button
                // is released, because some tools won't allow release until *after* they detect
                // that the tool button is released.
                if (wasPatched && isButtonUp)
                {
                    InputPatches.ToolUseButton = null;
                }
                if (wasButtonDown && isButtonUp)
                {
                    if (item.EndActivation())
                    {
                        downButtons.Remove(button);
                    }
                }
                else
                {
                    if (
                        item is InventoryMenuItem inv
                        && inv.Item is Tool
                        && Context.IsPlayerFree
                        && !item.IsActivating()
                    )
                    {
                        var result = item.Activate(
                            who,
                            DelayedActions.None,
                            ItemActivationType.Instant
                        );
                        if (result == ItemActivationResult.ToolUseStarted)
                        {
                            downButtons.Add(button);
                        }
                    }
                    else
                    {
                        item.ContinueActivation();
                    }
                }
                continue;
            }
            if (buttonState == SButtonState.Pressed)
            {
                if (Context.IsPlayerFree && Game1.activeClickableMenu is null)
                {
                    inputHelper.Suppress(button);
                }
                if (!Context.CanPlayerMove && !item.IsActivating())
                {
                    continue;
                }
                // We have to apply the patch _before_ trying to activate the item, because the
                // activation may depend on that button's state.
                InputPatches.ToolUseButton = controllerButton;
                var wasToolUseStarted = false;
                try
                {
                    // We don't allow an entirely new tool activation to occur if the player can't
                    // move but the item is still activating (per condition above). However, we do
                    // still want to translate the button press into a "tool press" since the item
                    // will usually require it in order to keep using the same button.
                    // (For example, to play the fishing bobber game with an alternate button.)
                    if (Context.IsPlayerFree)
                    {
                        var result = item.Activate(
                            who,
                            DelayedActions.None,
                            ItemActivationType.Instant
                        );
                        if (result != ItemActivationResult.Ignored)
                        {
                            if (item is InventoryMenuItem inv && inv.Item is Tool)
                            {
                                downButtons.Add(button);
                                wasToolUseStarted = true;
                            }
                            else if (result == ItemActivationResult.ToolUseStarted)
                            {
                                downButtons.Add(button);
                                wasToolUseStarted = true;
                            }
                            ItemActivated?.Invoke(this, new(item, result));
                            break;
                        }
                        else
                        {
                            renderer.FlashError(button);
                            Sound.Play(config.Sound.ItemErrorSound);
                        }
                    }
                    else
                    {
                        wasToolUseStarted = true;
                    }
                }
                finally
                {
                    if (!wasToolUseStarted)
                    {
                        InputPatches.ToolUseButton = null;
                    }
                }
            }
        }
    }

    internal void SetRendererOpacity(float opacity)
    {
        renderer.BackgroundOpacity = BASE_OPACITY * opacity;
        renderer.SpriteOpacity = opacity;
    }

    private void Fade(TimeSpan elapsed, bool isMenuActive)
    {
        var shouldBeVisible = HudVisible && !isMenuActive;
        if (
            shouldBeVisible && fadeTimeMs >= FADE_DURATION_MS
            || !shouldBeVisible && fadeTimeMs <= 0
        )
        {
            return;
        }
        var delta = shouldBeVisible ? elapsed.TotalMilliseconds : -elapsed.TotalMilliseconds;
        fadeTimeMs = Math.Clamp(fadeTimeMs + (float)delta, 0, FADE_DURATION_MS);
        var fadeProgress = fadeTimeMs / FADE_DURATION_MS;
        var fadeOpacity = (fadeProgress < 1 ? MathF.Sin(fadeProgress * MathF.PI / 2f) : 1);
        SetRendererOpacity(fadeOpacity);
    }

    private void ResolveSlots()
    {
        Logger.Log(LogCategory.QuickSlots, "Starting refresh of Remapping Slots.");
        renderer.Slots = Slots.ToDictionary(x => x.Key, x => x.Value as IItemLookup);
        resolvedItems.Clear();
        foreach (var (button, slot) in Slots)
        {
            if (string.IsNullOrEmpty(slot.Id))
            {
                continue;
            }
            Logger.Log(
                LogCategory.QuickSlots,
                $"Item data for remapping slot {button}: ID = {slot.IdType}:{slot.Id}"
            );
            var slottedItem = resolver.ResolveItem(slot.Id, slot.IdType);
            if (slottedItem is not null)
            {
                Logger.Log(
                    LogCategory.QuickSlots,
                    $"Created remapping slot item '{slottedItem.Title}' for {button} slot.",
                    LogLevel.Info
                );
                resolvedItems.Add(button, slottedItem);
            }
            else
            {
                Logger.Log(
                    LogCategory.QuickSlots,
                    $"No item or invalid item data for remapping slot button {button}."
                );
            }
        }
        renderer.SlotItems = resolvedItems;
        renderer.Invalidate();
    }
}
