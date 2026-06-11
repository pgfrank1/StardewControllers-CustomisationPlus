using StardewValley.Locations;

namespace StarControl.Menus;

/// <summary>
/// Utility class for dealing with the vagaries of vanilla item activation.
/// </summary>
internal static class FuzzyActivation
{
    /// <summary>
    /// Attempts to either consume/use or select/hold an item depending on both the requested action and the item's own
    /// in-game behavior.
    /// </summary>
    /// <remarks>
    /// Determining the correct path, particularly while taking into account desired menu delays, can be slippery because
    /// we don't know what an item will do until we actually try to do it (<see cref="SObject.performUseAction"/>, plus our
    /// various custom quick-actions). This abstracts most of the messiness away so that it fits somewhat neatly into the
    /// activation API.
    /// </remarks>
    /// <param name="who">Player who has the item in inventory and wants to use/select it.</param>
    /// <param name="item">The item to use/select.</param>
    /// <param name="delayedActions">Current activation delay settings.</param>
    /// <param name="preferredAction">The desired action, based on which button pressed, and assuming that action is
    /// possible for the given item.</param>
    /// <returns>The actual action that was performed.</returns>
    public static ItemActivationResult ConsumeOrSelect(
        Farmer who,
        Item item,
        DelayedActions? delayedActions = null,
        InventoryAction preferredAction = InventoryAction.Use
    )
    {
        Logger.Log(
            LogCategory.Activation,
            $"ConsumeOrSelect started for player = {who.UniqueMultiplayerID}, item = {item.Name}, "
                + $"delayedActions = {delayedActions}, preferredAction = {preferredAction}."
        );
        if (delayedActions == DelayedActions.All)
        {
            Logger.Log(
                LogCategory.Activation,
                $"ConsumeOrSelect({item.Name}) -> {ItemActivationResult.Delayed}, because "
                    + $"DelayedActions is set to '{delayedActions}'."
            );
            return ItemActivationResult.Delayed;
        }
        if (preferredAction == InventoryAction.Use && TryConsume(item))
        {
            Logger.Log(
                LogCategory.Activation,
                $"ConsumeOrSelect({item.Name}) -> {ItemActivationResult.Used}, because the item "
                    + "was consumed."
            );
            return ItemActivationResult.Used;
        }
        if (delayedActions == DelayedActions.ToolSwitch)
        {
            Logger.Log(
                LogCategory.Activation,
                $"ConsumeOrSelect({item.Name}) -> {ItemActivationResult.Delayed}, because the item "
                    + $"was not consumed and DelayedActions is set to '{delayedActions}'."
            );
            return ItemActivationResult.Delayed;
        }
        int itemIndex = who.Items.IndexOf(item);
        if (itemIndex < 0)
        {
            // The item can disappear from the inventory during its own activation — e.g. a
            // book whose ItemUsed trigger runs a RemoveItem action from inside
            // performUseAction. Selecting it would set CurrentToolIndex = -1, which crashes
            // every later Items[CurrentToolIndex] read (vanilla only guards the upper bound),
            // taking down the update and draw loops with it.
            Logger.Log(
                LogCategory.Activation,
                $"ConsumeOrSelect({item.Name}) -> {ItemActivationResult.Ignored}, because the "
                    + "item is no longer in the player's inventory."
            );
            return ItemActivationResult.Ignored;
        }
        who.CurrentToolIndex = itemIndex;
        Logger.Log(
            LogCategory.Activation,
            $"Set current tool index to {who.CurrentToolIndex} to match item selection."
        );
        if (Game1.player.CurrentTool is not null)
        {
            Game1.playSound("toolSwap");
        }
        Logger.Log(
            LogCategory.Activation,
            $"ConsumeOrSelect({item.Name}) -> {ItemActivationResult.Selected}, because the item "
                + $"was not consumed and DelayedActions is set to '{delayedActions}'."
        );
        return ItemActivationResult.Selected;
    }

    private static bool TryConsume(Item item)
    {
        if (item.Name == "Staircase")
        {
            if (Game1.currentLocation is MineShaft mineShaft)
            {
                var nextLevel = mineShaft.mineLevel + 1;
                Logger.Log(
                    LogCategory.Activation,
                    $"{item.Name} used in {nameof(MineShaft)} context; changing floor to "
                        + $"{nextLevel}."
                );
                ReduceStack(item);
                Game1.enterMine(nextLevel);
                Game1.playSound("stairsdown");
                return true;
            }
            Logger.Log(
                LogCategory.Activation,
                $"{item.Name} was not consumed because it was used outside a {nameof(MineShaft)} "
                    + "context."
            );
        }
        if (item is not SObject obj)
        {
            Logger.Log(
                LogCategory.Activation,
                $"{item.Name} cannot be directly consumed because it is not an Object type."
            );
            return false;
        }
        if (obj.Edibility > 0)
        {
            Logger.Log(
                LogCategory.Activation,
                $"{item.Name} has positive edibility and will be eaten."
            );
            ReduceStack(obj);
            Game1.player.eatObject(obj);
            return true;
        }
        if (obj.performUseAction(Game1.currentLocation))
        {
            Logger.Log(
                LogCategory.Activation,
                $"{item.Name} returned true from {nameof(obj.performUseAction)} and is assumed "
                    + "to have been consumed."
            );
            ReduceStack(obj);
            return true;
        }
        return false;
    }

    private static void ReduceStack(Item item)
    {
        if (item.Stack > 1)
        {
            item.Stack--;
            Logger.Log(LogCategory.Activation, $"Reduced stack for {item.Name} to {item.Stack}.");
        }
        else
        {
            Game1.player.Items.RemoveButKeepEmptySlot(item);
            Logger.Log(
                LogCategory.Activation,
                $"{item.Name} has only one copy in the stack; removing from inventory."
            );
        }
    }
}
