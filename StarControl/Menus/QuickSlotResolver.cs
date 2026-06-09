using System.Reflection;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Tools;

namespace StarControl.Menus;

internal class QuickSlotResolver(Farmer player, ModMenu modMenu)
{
    public static Item? ResolveInventoryItem(string id, ICollection<Item> items)
    {
        Logger.Log(LogCategory.QuickSlots, $"Searching for inventory item equivalent to '{id}'...");
        if (ItemRegistry.GetData(id) is not { } data)
        {
            Logger.Log(
                LogCategory.QuickSlots,
                $"'{id}' does not have valid item data; aborting search."
            );
            return null;
        }
        // Melee weapons don't have upgrades or base items, but if we didn't find an exact match, it
        // is often helpful to find any other melee weapon that's available.
        // Only apply fuzzy matching to melee weapons; slingshots must match exactly.
        // NOTE (surprise vector): when the exact configured weapon isn't in the inventory, the block
        // below substitutes the best available melee weapon of the same scythe/non-scythe kind. The
        // ranged-weapon exclusion is a string heuristic on the qualified id ("Slingshot"/"Bow"), so a
        // modded ranged weapon without those tokens would be treated as melee and fuzzy-matched.
        if (
            data.ItemType.Identifier == "(W)"
            && !data.QualifiedItemId.Contains("Slingshot")
            && !data.QualifiedItemId.Contains("Bow")
        )
        {
            // We'll match scythes to scythes, and non-scythes to non-scythes.
            // Most likely, the player wants Iridium Scythe if the slot says Scythe. The upgraded
            // version is simply better, like a more typical tool.
            //
            // With real weapons it's fuzzier because the highest-level weapon isn't necessarily
            // appropriate for the situation. If there's one quick slot for Galaxy Sword and another
            // for Galaxy Hammer, then activating those slots should activate their *specific*
            // weapons respectively if both are in the inventory.
            //
            // So we match on the inferred "type" (scythe vs. weapon) and then for non-scythe
            // weapons specifically (and only those), give preference to exact matches before
            // sorting by level.
            var isScythe = IsScythe(data);
            Logger.Log(
                LogCategory.QuickSlots,
                $"Item '{id}' appears to be a weapon with (scythe = {isScythe})."
            );
            var bestWeapon = items
                .OfType<MeleeWeapon>()
                .Where(weapon => weapon.Name.Contains("Scythe") == isScythe)
                .OrderByDescending(weapon => !isScythe && weapon.QualifiedItemId == id)
                .ThenByDescending(weapon => weapon.getItemLevel())
                .FirstOrDefault();
            if (bestWeapon is not null)
            {
                Logger.Log(
                    LogCategory.QuickSlots,
                    "Best weapon match in inventory is "
                        + $"{bestWeapon.Name} with ID {bestWeapon.QualifiedItemId}."
                );
                return bestWeapon;
            }
        }
        var baseItem = data.GetBaseItem();
        Logger.Log(
            LogCategory.QuickSlots,
            "Searching for regular item using base item "
                + $"{baseItem.InternalName} with ID {baseItem.QualifiedItemId}."
        );
        var match = items
            .Where(item => item is not null)
            .Where(item =>
                item.QualifiedItemId == id
                || ItemRegistry
                    .GetDataOrErrorItem(item.QualifiedItemId)
                    .GetBaseItem()
                    .QualifiedItemId == baseItem.QualifiedItemId
            )
            .OrderByDescending(item => item is Tool tool ? tool.UpgradeLevel : 0)
            .ThenByDescending(item => item.Quality)
            .FirstOrDefault();
        Logger.Log(
            LogCategory.QuickSlots,
            $"Best match by quality/upgrade level is "
                + $"{match?.Name ?? "(nothing)"} with ID {match?.QualifiedItemId ?? "N/A"}."
        );
        return match;
    }

    private static bool IsScythe(ParsedItemData data)
    {
        var method = typeof(MeleeWeapon).GetMethod(
            "IsScythe",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null
        );
        if (method is not null)
        {
            try
            {
                return (bool)method.Invoke(null, [data.QualifiedItemId])!;
            }
            catch
            {
                // Fall through to heuristic below.
            }
        }
        var name = data.InternalName ?? string.Empty;
        return name.Contains("Scythe", StringComparison.OrdinalIgnoreCase)
            || data.QualifiedItemId.Contains("Scythe", StringComparison.OrdinalIgnoreCase);
    }

    public IRadialMenuItem? ResolveItem(string id, ItemIdType idType)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        return idType switch
        {
            ItemIdType.GameItem => ResolveInventoryItem(id, player.Items) is { } item
                ? new InventoryMenuItem(item)
                : null,
            ItemIdType.ModItem => modMenu.GetItem(id),
            _ => null,
        };
    }
}
