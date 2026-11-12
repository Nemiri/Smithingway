using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Smithingway.Services;
using Smithingway.Sync;
using Smithingway.Windows;

namespace Smithingway;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/smithingway";
    private const string ShortCommand = "/smw";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly Configuration config;
    private readonly RemoteCache remote;
    private readonly CustomizationStore store;
    private readonly SyncService sync;
    private readonly TradeService trade;
    private readonly ChatLinkService chatLinks;
    private readonly TooltipService tooltips;
    private readonly ItemNameLabelService nameLabels;
    private readonly TooltipSource tooltipSource;
    private readonly InspectService inspect;
    private readonly ContextMenuService contextMenu;
    private readonly DevTools devTools;

    private readonly WindowSystem windowSystem = new("Smithingway");
    private readonly ArmoryWindow armoryWindow;
    private readonly SettingsWindow settingsWindow;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        remote = new RemoteCache(PluginInterface.GetPluginConfigDirectory());
        store = new CustomizationStore(config, remote);
        sync = new SyncService(config, store, remote);
        trade = new TradeService(config, store, sync);
        chatLinks = new ChatLinkService(config, store, sync, trade);
        tooltips = new TooltipService(config, store, chatLinks, trade);
        nameLabels = new ItemNameLabelService(config, store, trade);
        tooltipSource = new TooltipSource(config);
        inspect = new InspectService(config, store, sync);
        devTools = new DevTools(store, remote);

        armoryWindow = new ArmoryWindow(store, sync, ToggleSettings);
        settingsWindow = new SettingsWindow(config, sync, remote, devTools);
        windowSystem.AddWindow(armoryWindow);
        windowSystem.AddWindow(settingsWindow);

        contextMenu = new ContextMenuService(store, armoryWindow.OpenFor);

        var help = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Armory to rename items. '/smithingway settings' for settings, '/smithingway test [itemId]' to preview chat links.",
        };
        CommandManager.AddHandler(Command, help);
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "Alias for /smithingway.", ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleArmory;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleSettings;
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        Framework.RunOnFrameworkThread(sync.PublishLocal);
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleArmory;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettings;
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(ShortCommand);

        contextMenu.Dispose();
        inspect.Dispose();
        tooltipSource.Dispose();
        nameLabels.Dispose();
        tooltips.Dispose();
        chatLinks.Dispose();
        trade.Dispose();
        sync.Dispose();
        windowSystem.RemoveAllWindows();
    }

    private void ToggleArmory() => armoryWindow.Toggle();
    private void ToggleSettings() => settingsWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "":
                ToggleArmory();
                break;
            case "settings":
            case "config":
                ToggleSettings();
                break;
            case "test":
                uint? itemId = parts.Length > 1 && uint.TryParse(parts[1], out var id) ? id : null;
                devTools.RunChatLinkTest(itemId);
                break;
            default:
                ChatGui.Print("[Smithingway] /smithingway — Armory · /smithingway settings · /smithingway test [itemId]");
                break;
        }
    }

    private void OnLogin()
    {
        armoryWindow.ResetState();
        sync.PublishLocal();
    }

    private void OnLogout(int type, int code) => armoryWindow.ResetState();
}
