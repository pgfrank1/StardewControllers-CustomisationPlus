using System.IO;
using System.Reflection;
using StarControl.Api;
using StarControl.Config;
using StarControl.Data;
using StarControl.Gmcm;
using StarControl.Graphics;
using StarControl.Input;
using StarControl.Menus;
using StarControl.Patches;
using StarControl.UI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;

namespace StarControl;

public class ModEntry : Mod
{
    private const string GMCM_MOD_ID = "spacechase0.GenericModConfigMenu";

    private readonly PerScreen<RadialMenuController> menuController;
    private readonly PerScreen<ModMenu> modMenu;
    private readonly PerScreen<RemappingController> remappingController;

    private RadialMenuController MenuController => menuController.Value;
    private ModMenu ModMenu => modMenu.Value;
    private RemappingController RemappingController => remappingController.Value;

    // Global state, generally initialized in Entry.
    private StarControlApi api = null!;
    private ModConfig config = null!;
    private Gmcm.GenericModConfigMenu? configMenu;
    private IGenericModMenuConfigApi? configMenuApi;
    private GenericModConfigSync? gmcmSync;
    private KeybindActivator keybindActivator = null!;
    private PageRegistry pageRegistry = null!;

    public ModEntry()
    {
        modMenu = new(CreateModMenu);
        menuController = new(CreateMenuController);
        remappingController = new(CreateRemappingController);
    }

    public override void Entry(IModHelper helper)
    {
        Logger.Monitor = Monitor;
        config = Helper.ReadConfig<ModConfig>();
        config.Input.ApplyLegacyThumbStickPreference();
        Logger.Config = config.Debug;
        I18n.Init(helper.Translation);
        var builtInItems = new BuiltInItems(ModManifest);
        pageRegistry = new(config);
        pageRegistry.RegisterItem(ModManifest, builtInItems.MainMenu);
        pageRegistry.RegisterItem(ModManifest, builtInItems.Journal);
        pageRegistry.RegisterItem(ModManifest, builtInItems.Map);
        pageRegistry.RegisterItem(ModManifest, builtInItems.Mail);
        pageRegistry.RegisterItem(ModManifest, builtInItems.Crafting);
        api = new(pageRegistry, config, Monitor);
        keybindActivator = new(helper.Input);
        Sound.Enabled = config.Sound.EnableUiSounds;
        ConfigurationViewModel.Saved += ConfigurationViewModel_Saved;

        helper.Events.GameLoop.GameLaunched += GameLoop_GameLaunched;
        // Ensure menu gets updated at the right time.
        helper.Events.GameLoop.ReturnedToTitle += GameLoop_ReturnedToTitle;
        helper.Events.GameLoop.SaveLoaded += GameLoop_SaveLoaded;
        helper.Events.Player.InventoryChanged += Player_InventoryChanged;
        helper.Events.GameLoop.UpdateTicked += GameLoop_UpdateTicked;
        // For optimal latency: handle input before the Update loop, perform actions/rendering after.
        helper.Events.GameLoop.UpdateTicking += GameLoop_UpdateTicking;
        helper.Events.Input.ButtonsChanged += Input_ButtonsChanged;
        helper.Events.Display.RenderingHud += Display_RenderingHud;
        helper.Events.Display.RenderedHud += Display_RenderedHud;

        Patcher.PatchAll(ModManifest);
        GamePatches.SuppressRightStickChatBox = config.Input.SuppressRightStickChatBox;
        GamePatches.IsRadialMenuActive = () =>
            menuController.IsActiveForScreen() && menuController.Value.IsMenuActive;
    }

    public override object? GetApi()
    {
        return api;
    }

    protected override void Dispose(bool disposing)
    {
        // Remapping slots are saved immediately whenever the slots are edited, but other actions
        // like toggling the HUD avoid eagerly writing to disk for performance reasons, as they are
        // meant to be performed "in-world" and not in a menu.
        //
        // So instead we save the file one last time, including any of this semi-persistent
        // transient state, on game exit; at the time of writing, Dispose covers both exiting from
        // the game's main menu and Alt+F4 exits on Windows. Other OSes not tested.
        SaveRemappingFile();
    }

    private void ConfigurationViewModel_Saved(object? sender, EventArgs e)
    {
        pageRegistry.InvalidateAll();
        if (menuController.IsActiveForScreen())
        {
            menuController.Value.Invalidate();
        }
        Sound.Enabled = config.Sound.EnableUiSounds;
        GamePatches.SuppressRightStickChatBox = config.Input.SuppressRightStickChatBox;
    }

    [EventPriority(EventPriority.Low)]
    private void Display_RenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (MenuController.Enabled)
        {
            MenuController.Draw(e.SpriteBatch);
        }
        if (Context.IsPlayerFree && !MenuController.IsMenuActive)
        {
            RemappingController.Draw(e.SpriteBatch);
        }
    }

    private void Display_RenderingHud(object? sender, RenderingHudEventArgs e)
    {
        if (MenuController.IsMenuActive)
        {
            MenuController.PrepareHudForMenu();
        }
    }

    [EventPriority(EventPriority.Low - 10)]
    private void GameLoop_GameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        configMenuApi = Helper.ModRegistry.GetApi<IGenericModMenuConfigApi>(GMCM_MOD_ID);
        LoadGmcmKeybindings();

        var viewEngine = Helper.ModRegistry.GetApi<IViewEngine>("focustense.StardewUI");
        if (viewEngine is null)
        {
            Monitor.Log(
                "StardewUI Framework is not installed; some aspects of the mod will not be configurable in-game.",
                LogLevel.Warn
            );
            return;
        }
        viewEngine.RegisterCustomData($"Mods/{ModManifest.UniqueID}", "assets/ui/data");
        viewEngine.RegisterSprites($"Mods/{ModManifest.UniqueID}/Sprites", "assets/ui/sprites");
        viewEngine.RegisterViews($"Mods/{ModManifest.UniqueID}/Views", "assets/ui/views");
#if DEBUG
        viewEngine.EnableHotReloadingWithSourceSync();
#endif
        viewEngine.PreloadAssets();
        viewEngine.PreloadModels(
            typeof(ConfigurationViewModel),
            typeof(QuickSlotPickerViewModel),
            typeof(ConfirmationViewModel)
        );
        ViewEngine.Instance = viewEngine;
        ViewEngine.ViewAssetPrefix = $"Mods/{ModManifest.UniqueID}/Views";

        RegisterConfigMenu();
    }

    private void GameLoop_ReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        // Since the remapping controller will be recreated on next game load, we need to save its
        // settings here (including visibility) in order for them to persist on a reload.
        SaveRemappingFile();

        menuController.ResetAllScreens();
        remappingController.ResetAllScreens();
        modMenu.ResetAllScreens();
    }

    private void GameLoop_SaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        LoadRemappingSlots();
    }

    private void GameLoop_UpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        keybindActivator.Replay();
    }

    private void GameLoop_UpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        var wasActive = MenuController.IsMenuActive;
        MenuController.Enabled = Context.CanPlayerMove;
        MenuController.Update(Game1.currentGameTime.ElapsedGameTime);
        if (!wasActive && MenuController.IsMenuActive)
        {
            Game1.player.completelyStopAnimatingOrDoingAction();
            Game1.freezeControls = true;
        }
        else if (wasActive && !MenuController.IsMenuActive)
        {
            Game1.freezeControls = false;
        }
        if (MenuController.IsMenuActive)
        {
            MenuController.PrepareHudForMenu();
        }
        else
        {
            MenuController.RestoreHudState();
        }
        RemappingController.Update(
            Game1.currentGameTime.ElapsedGameTime,
            MenuController.IsMenuActive
        );
    }

    private void Input_ButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        if (!Context.IsPlayerFree)
        {
            return;
        }

        if (e.Pressed.Contains(config.Input.RemappingMenuButton) && ViewEngine.Instance is not null)
        {
            Helper.Input.Suppress(config.Input.RemappingMenuButton);
            OpenRemappingMenu();
            return;
        }

        if (e.Pressed.Contains(config.Input.RemappingHudButton))
        {
            RemappingController.HudVisible = !RemappingController.HudVisible;
            Logger.Log(
                LogCategory.QuickSlots,
                RemappingController.HudVisible ? "Remapping HUD enabled" : "Remapping HUD disabled"
            );
            return;
        }
    }

    private void MenuController_ItemActivated(object? sender, ItemActivationEventArgs e)
    {
        // Because we allow "internal" page changes within the radial menu that don't go through
        // Farmer.shiftToolbar (on purpose), the selected index can now be on a non-active page.
        // To avoid confusing the game's UI, check for this condition and switch to the backpack
        // page that actually does contain the index.
        if (
            e.Result is not ItemActivationResult.Selected or ItemActivationResult.ToolUseStarted
            || Game1.player.CurrentToolIndex < GameConstants.BACKPACK_PAGE_SIZE
        )
        {
            return;
        }
        var items = Game1.player.Items;
        var currentPage = Game1.player.CurrentToolIndex / GameConstants.BACKPACK_PAGE_SIZE;
        var indexOnPage = Game1.player.CurrentToolIndex % GameConstants.BACKPACK_PAGE_SIZE;
        var newFirstIndex = currentPage * GameConstants.BACKPACK_PAGE_SIZE;
        var itemsBefore = items.GetRange(0, newFirstIndex);
        var itemsAfter = items.GetRange(newFirstIndex, items.Count - newFirstIndex);
        items.Clear();
        items.AddRange(itemsAfter);
        items.AddRange(itemsBefore);
        Game1.player.CurrentToolIndex = indexOnPage;
    }

    private void Player_InventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        // We don't need to invalidate the menu if the only change was a quantity, since that's only
        // read at paint time. Any items added/removed, however, will change the layout/items in the
        // menu.
        if (e.Added.Any() || e.Removed.Any())
        {
            MenuController.Invalidate();
            RemappingController.Refresh();
        }
    }

    private void ActivateModMenuItem(ModMenuItemConfiguration item)
    {
        if (!item.Keybind.IsBound)
        {
            Game1.showRedMessage(I18n.Error_MissingBinding());
            return;
        }
        keybindActivator.Prepare(item.Keybind);
    }

    private RadialMenuController CreateMenuController()
    {
        var player = Game1.player;
        var menuPainter = new RadialMenuPainter(Game1.graphics.GraphicsDevice, config.Style);
        var inventoryToggle = new MenuToggle(
            Helper.Input,
            config.Input,
            c => c.InventoryMenuButton
        );
        var inventoryMenu = new InventoryMenu(inventoryToggle, player, config.Items);
        var quickSlotRenderer = new QuickSlotRenderer(Game1.graphics.GraphicsDevice, config);
        var quickSlotController = new QuickSlotController(
            Helper.Input,
            config,
            new QuickSlotResolver(player, ModMenu),
            quickSlotRenderer
        );
        var menuController = new RadialMenuController(
            Helper.Input,
            config,
            Game1.player,
            menuPainter,
            quickSlotController,
            inventoryMenu,
            ModMenu
        );
        menuController.ItemActivated += MenuController_ItemActivated;
        return menuController;
    }

    private ModMenu CreateModMenu()
    {
        var registeredPages = pageRegistry.CreatePageList(Game1.player);
        var modMenuToggle = new MenuToggle(Helper.Input, config.Input, c => c.ModMenuButton);
        var settingsSprite = new Lazy<Sprite?>(Sprites.Settings);
        var settingsItem = new ModMenuItem(
            id: "focustense.StarControl.Settings",
            title: I18n.ModMenu_SettingsItem_Name,
            description: I18n.ModMenu_SettingsItem_Description,
            texture: () => settingsSprite.Value?.Texture,
            sourceRectangle: () => settingsSprite.Value?.SourceRect,
            activate: (_, _, _) =>
            {
                if (!ViewEngine.IsInstalled)
                {
                    Game1.showRedMessage(I18n.Error_MissingStardewUI());
                    return ItemActivationResult.Ignored;
                }
                OpenConfigMenu();
                return ItemActivationResult.Custom;
            }
        );
        var instantActionsSprite = new Lazy<Sprite?>(Sprites.BugNet);
        var instantActionsItem = new ModMenuItem(
            id: "focustense.StarControl.InstantActions",
            title: I18n.ModMenu_InstantActionsItem_Name,
            description: I18n.ModMenu_InstantActionsItem_Description,
            texture: () => instantActionsSprite.Value?.Texture,
            sourceRectangle: () => instantActionsSprite.Value?.SourceRect,
            activate: (_, _, _) =>
            {
                if (!ViewEngine.IsInstalled)
                {
                    Game1.showRedMessage(I18n.Error_MissingStardewUI());
                    return ItemActivationResult.Ignored;
                }
                OpenRemappingMenu();
                return ItemActivationResult.Custom;
            }
        );
        return new ModMenu(
            modMenuToggle,
            config,
            settingsItem,
            instantActionsItem,
            ActivateModMenuItem,
            registeredPages,
            () => pageRegistry.CustomItemsPageIndex,
            pageRegistry.StandaloneItems.Select(x => x.Item)
        );
    }

    private RemappingController CreateRemappingController()
    {
        var resolver = new QuickSlotResolver(Game1.player, ModMenu);
        var renderer = new QuickSlotRenderer(Game1.graphics.GraphicsDevice, config)
        {
            UnassignedButtonsVisible = false,
        };
        var controller = new RemappingController(
            Helper.Input,
            config,
            Game1.player,
            resolver,
            renderer
        );
        controller.SetRendererOpacity(0);
        // For now, there's no difference between the activation handling of menus and instant
        // actions; it's only used to cycle the player's inventory. This could change later.
        controller.ItemActivated += MenuController_ItemActivated;
        return controller;
    }

    private void LoadGmcmKeybindings()
    {
        const string LOADER_METHOD = "Load";
        const string LOADER_TYPE = "StarControl.Gmcm.Loader";

        if (configMenuApi is null)
        {
            Logger.Log(
                $"Couldn't read global keybindings; mod {GMCM_MOD_ID} is not installed.",
                LogLevel.Warn
            );
            return;
        }
        Logger.Log(
            LogCategory.GmcmSync,
            "Generic Mod Config Menu is loaded; reading keybindings.",
            LogLevel.Info
        );
        try
        {
            var gmcmExtensionPath = Path.Combine(
                Helper.DirectoryPath,
                "assets",
                "extensions",
                "StarControl.Gmcm.dll"
            );
            if (!File.Exists(gmcmExtensionPath))
            {
                Logger.Log(
                    $"GMCM extension not found at '{gmcmExtensionPath}'; keybindings will not be synced.",
                    LogLevel.Warn
                );
                return;
            }
            var gmcmExtensionAssembly = Assembly.LoadFile(gmcmExtensionPath);
            var loaderType = gmcmExtensionAssembly.GetType(LOADER_TYPE);
            if (loaderType is null)
            {
                Logger.Log(
                    $"Failed to initialize GMCM extension: Type {LOADER_TYPE} was not found.",
                    LogLevel.Error
                );
                return;
            }
            var loadMethod = loaderType.GetMethod(
                LOADER_METHOD,
                BindingFlags.Static | BindingFlags.Public
            );
            if (loadMethod is null)
            {
                Logger.Log(
                    $"Failed to initialize GMCM extension: Method {LOADER_METHOD} was not "
                        + $"found on type {LOADER_TYPE}.",
                    LogLevel.Error
                );
                return;
            }
            Action<Action> openRealConfigMenu = onClose =>
                OpenConfigMenu(asRoot: true, onClose: onClose);
            loadMethod.Invoke(
                null,
                [ModManifest, openRealConfigMenu, Monitor, config.Debug.EnableGmcmDetailedLogging]
            );
        }
        catch (Exception ex)
        {
            Logger.Log(
                "Error loading GMCM extension; keybindings will not be synced.\n" + ex,
                LogLevel.Error
            );
            return;
        }
        if (IGenericModConfigKeybindings.Instance is not { } gmcmBindings)
        {
            Logger.Log(
                "Failed to load keybindings from installed version of GMCM. "
                    + "Check previous log messages for details.",
                LogLevel.Error
            );
            return;
        }
        Logger.Log(LogCategory.GmcmSync, "Finished reading keybindings from GMCM.", LogLevel.Info);
        gmcmSync = new(config, gmcmBindings);
        if (gmcmSync.SyncAll())
        {
            Helper.WriteConfig(config);
        }
        gmcmBindings.Saved += (_, e) =>
        {
            Logger.Log(
                LogCategory.GmcmSync,
                "Detected save from GMCM; re-syncing options.",
                LogLevel.Info
            );
            if (gmcmSync.SyncAll(e.Mod))
            {
                Helper.WriteConfig(config);
            }
        };
    }

    private void LoadRemappingSlots()
    {
        if (Context.IsSplitScreen && Context.ScreenId == 0)
        {
            return;
        }
        var remappingData = Helper.Data.ReadJsonFile<RemappingData>("remap.json") ?? new();
        RemappingController.Slots = remappingData.Slots;
        RemappingController.HudVisible = remappingData.HudVisible;
    }

    private void OpenConfigMenu(bool asRoot = false, Action? onClose = null)
    {
        ConfigurationMenu.Open(Helper, config, pageRegistry, asRoot: asRoot, onClose: onClose);
    }

    private void OpenRemappingMenu()
    {
        var context = new RemappingViewModel(
            Helper.Input,
            Game1.player,
            MenuController.AllItems.Where(item =>
                item is not InventoryMenuItem && !string.IsNullOrEmpty(item.Id)
            ),
            config.Input.RemappingMenuButton,
            config.Input.ThumbstickDeadZone,
            config.Style.ButtonIconSet,
            SaveRemappingSlots
        );
        context.Load(RemappingController.Slots);
        context.InitializeLayout();
        var controller = ViewEngine.OpenRootMenu("Remapping", context);
        context.Controller = controller;
        controller.PositionSelector = context.GetMenuPosition;
        // StardewUI's menu sets Game1.displayHUD = false while it's open and never restores
        // it on close, which permanently hides the HUD (and the latched value then poisons
        // the radial wheel's own HUD save/restore on the next open). Capture the HUD state
        // and restore it when the menu closes. Note: the Closing event (cleanupBeforeExit
        // lifecycle) is the one that fires at actual close time; Closed fires at open.
        var hudWasVisible = Game1.displayHUD;
        controller.Closing += () => Game1.displayHUD = hudWasVisible;
        Game1.activeClickableMenu = controller!.Menu;
    }

    private void RegisterConfigMenu()
    {
        if (configMenuApi is null)
        {
            return;
        }
        configMenu = new(
            configMenuApi,
            ModManifest,
            onClose => OpenConfigMenu(asRoot: true, onClose: onClose)
        );
        configMenu.Setup();
    }

    private void SaveRemappingFile()
    {
        if (
            Context.IsSplitScreen && Context.ScreenId == 0
            || !remappingController.IsActiveForScreen()
        )
        {
            return;
        }
        var remappingData = new RemappingData()
        {
            Slots = RemappingController.Slots,
            HudVisible = RemappingController.HudVisible,
        };
        Helper.Data.WriteJsonFile("remap.json", remappingData);
    }

    private void SaveRemappingSlots(Dictionary<SButton, RemappingSlot> data)
    {
        RemappingController.Slots = data;
        SaveRemappingFile();
    }
}
