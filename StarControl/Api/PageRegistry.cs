using System.Collections;
using StarControl.Config;
using StarControl.Menus;

namespace StarControl.Api;

/// <summary>
/// Helper for mod-registered pages; manages per-player page lists and handles invalidation.
/// </summary>
internal class PageRegistry(ModConfig config)
{
    public record ItemRegistration(IManifest Mod, IRadialMenuItem Item);

    record PageRegistration(IManifest Mod, string Key, IRadialMenuPageFactory Factory);

    public int Count => GetPageOrder().Count;

    // Index in the page list(s) where the player's custom items should be inserted.
    //
    // This is specified as a priority in the configuration, but the priorities are for the entire
    // mods, not individual pages. Assuming some mods register multiple pages, we need to track the
    // resolved page index since we (PageRegistry) are in charge of ordering.
    public int CustomItemsPageIndex { get; private set; }

    public IEnumerable<IManifest> Mods => pageRegistrations.Select(page => page.Mod).Distinct();

    public IReadOnlyList<ItemRegistration> StandaloneItems => standaloneItems;

    // Page order is determined by the mod priorities in the integration settings. This is a list
    // with size of Count (number of visible/enabled pages), each element being an the index of the
    // PageRegistrations entry that should appear on that page.
    private readonly List<int> pageOrder = [];

    // The primary data structure should be an indexed collection so that it can be composed into
    // other lists.
    private readonly List<PageRegistration> pageRegistrations = [];

    // Re-registrations will happen by key, so we need a way to refer back to the primary data.
    private readonly Dictionary<string, int> registrationIndices = [];

    private readonly List<ItemRegistration> standaloneItems = [];

    // Pages have to be tracked in order to be able to invalidate them. We can't do both in the same
    // place because registrations are global and page lists are per-player. Use weak references to
    // prevent memory leaks.
    private readonly List<WeakReference<PageList>> trackedPageLists = [];

    private bool isPageOrderDirty = true;

    public IRadialMenuPage CreatePage(int index, Farmer who)
    {
        return pageRegistrations[pageOrder[index]].Factory.CreatePage(who);
    }

    public IInvalidatableList<IRadialMenuPage> CreatePageList(Farmer who)
    {
        var pageList = new PageList(this, who);
        trackedPageLists.Add(new(pageList));
        return pageList;
    }

    public void InvalidateAll()
    {
        for (int i = trackedPageLists.Count - 1; i >= 0; i--)
        {
            if (trackedPageLists[i].TryGetTarget(out var pageList))
            {
                pageList.Clear();
            }
            else
            {
                trackedPageLists.RemoveAt(i);
            }
        }
        isPageOrderDirty = true;
    }

    public bool InvalidatePage(string key)
    {
        if (!registrationIndices.TryGetValue(key, out var index))
        {
            return false;
        }
        InvalidateIndex(index);
        return true;
    }

    public void RegisterItem(IManifest mod, IRadialMenuItem item)
    {
        standaloneItems.Add(new(mod, item));
    }

    public void RegisterPage(IManifest mod, string key, IRadialMenuPageFactory factory)
    {
        if (registrationIndices.TryGetValue(key, out var index))
        {
            InvalidateIndex(index);
            pageRegistrations[index] = new(mod, key, factory);
        }
        else
        {
            pageRegistrations.Add(new(mod, key, factory));
            registrationIndices.Add(key, pageRegistrations.Count - 1);
        }
    }

    private IReadOnlyList<int> GetPageOrder()
    {
        if (isPageOrderDirty)
        {
            pageOrder.Clear();
            CustomItemsPageIndex = -1;
            var realIndicesByMod = pageRegistrations
                .Select((registration, index) => (registration, index))
                .ToLookup(x => x.registration.Mod.UniqueID, x => x.index);
            var priorityIndex = 0;
            foreach (var priority in config.Integrations.Priorities)
            {
                if (priorityIndex == config.Integrations.CustomItemsPriority)
                {
                    CustomItemsPageIndex = pageOrder.Count;
                }
                if (!priority.Enabled)
                {
                    continue;
                }
                foreach (var index in realIndicesByMod[priority.ModId])
                {
                    pageOrder.Add(index);
                }
                priorityIndex++;
            }
            if (CustomItemsPageIndex == -1)
            {
                CustomItemsPageIndex = pageOrder.Count;
            }
            isPageOrderDirty = false;
        }
        return pageOrder;
    }

    private void InvalidateIndex(int index)
    {
        for (int i = trackedPageLists.Count - 1; i >= 0; i--)
        {
            if (trackedPageLists[i].TryGetTarget(out var pageList))
            {
                pageList.InvalidateAt(index);
            }
            else
            {
                trackedPageLists.RemoveAt(i);
            }
        }
    }
}

internal class PageList(PageRegistry registry, Farmer who) : IInvalidatableList<IRadialMenuPage>
{
    public IRadialMenuPage this[int index] => GetPageAt(index);

    public int Count => registry.Count;

    private readonly List<IRadialMenuPage?> pages = [];

    public void Clear()
    {
        pages.Clear();
    }

    public IEnumerator<IRadialMenuPage> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return GetPageAt(i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Invalidate()
    {
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i] = null;
        }
    }

    public void InvalidateAt(int index)
    {
        pages[index] = null;
    }

    private IRadialMenuPage GetPageAt(int index)
    {
        while (pages.Count <= index)
        {
            pages.Add(null);
        }
        return pages[index] ??= registry.CreatePage(index, who);
    }
}
