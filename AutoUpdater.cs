using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace AutoUpdater;

[MinimumApiVersion(369)]
public partial class AutoUpdater : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "AutoUpdater";
    public override string ModuleAuthor => "dranix, Marchand";
    public override string ModuleVersion => "1.1.0";

    private const string SteamApiEndpoint =
        "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";

    private const int TestRequiredVersion = 9999999;

    public required PluginConfig Config { get; set; } = new();
    private readonly Dictionary<int, bool> PlayersNotified = new();
    private double UpdateFoundTime;
    private bool IsServerLoading;
    private bool InstantShutdown;
    private volatile bool RestartRequired;
    private bool UpdateAvailable;
    private volatile int RequiredVersion;
    private Timer? ResendNotificationTimer;
    private Timer? UpdateCheckTimer;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
        RegisterListener<Listeners.OnServerHibernationUpdate>(OnServerHibernationUpdate);
        RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        UpdateCheckTimer = AddTimer(Config.UpdateCheckInterval, CheckServerVersion, TimerFlags.REPEAT);
    }

    public override void Unload(bool hotReload)
    {
        UpdateCheckTimer?.Kill();
        UpdateCheckTimer = null;

        ResendNotificationTimer?.Kill();
        ResendNotificationTimer = null;

        Dispose();
    }

    public void OnConfigParsed(PluginConfig config)
    {
        if (config.Version < Config.Version) Logger.LogWarning(Localizer["AutoUpdater.Console.ConfigVersionMismatch", Config.Version, config.Version]);

        Config = config;
    }

    private void OnGameServerSteamAPIActivated() => Logger.LogInformation(Localizer["AutoUpdater.Console.UpdateCheckInitiated"]);

    private void OnServerHibernationUpdate(bool isHibernating)
    {
        Logger.LogInformation($"Server hibernation status: {(isHibernating ? "Enabled" : "Disabled")}");
    }

    private void OnMapStart(string mapName)
    {
        PlayersNotified.Clear();
        IsServerLoading = false;
    }

    private void OnMapEnd()
    {
        if (RestartRequired && Config.ShutdownOnMapChangeIfPendingUpdate) PrepareServerShutdown();
        IsServerLoading = true;
    }

    private void OnClientConnected(int playerSlot)
    {
        CCSPlayerController? player = Utilities.GetPlayerFromSlot(playerSlot);
        if (player == null || (player?.IsBot ?? false) || (player?.IsHLTV ?? false)) return;

        PlayersNotified[playerSlot] = false;
    }

    private void OnClientDisconnect(int playerSlot)
    {
        PlayersNotified.Remove(playerSlot);
    }

    private async void CheckServerVersion()
    {
        try
        {
            if (RestartRequired || !await IsUpdateAvailable()) return;

            Server.NextFrame(() =>
            {
                try
                {
                    ManageServerUpdate();
                }
                catch (Exception ex)
                {
                    Logger.LogError(Localizer["AutoUpdater.Console.ErrorUpdateCheck", ex.Message]);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(Localizer["AutoUpdater.Console.ErrorUpdateCheck", ex.Message]);
        }
    }

    private void ManageServerUpdate()
    {
        if (RestartRequired) return;

        if (!UpdateAvailable)
        {
            UpdateFoundTime = Server.CurrentTime;
            UpdateAvailable = true;
            
            Logger.LogInformation(Localizer["AutoUpdater.Console.NewUpdateReleased", RequiredVersion]);
        }

        List<CCSPlayerController> players = GetCurrentPlayers();

        if (IsServerLoading) return;

        RestartRequired = true;
        InstantShutdown = players.Count <= Config.MinPlayersInstantShutdown;

        foreach (var player in players)
        {
            NotifyPlayerAboutUpdate(player);
            PlayersNotified[player.Slot] = true;
        }

        TimerFlags mapChangeFlag = Config.ShutdownOnMapChangeIfPendingUpdate ? TimerFlags.STOP_ON_MAPCHANGE : 0;

        ResendNotificationTimer = AddTimer(Config.ShutdownMessageInterval,
            ResendUpdateNotification,
            TimerFlags.REPEAT | mapChangeFlag);

        AddTimer(InstantShutdown ? 1 : Config.ShutdownDelay,
            PrepareServerShutdown,
            mapChangeFlag);
    }

    /*
    [ConsoleCommand("css_testupdate", "Simulates a detected update to test the restart flow")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnTestUpdateCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (RestartRequired)
        {
            info.ReplyToCommand("An update restart is already in progress.");
            return;
        }

        // No real Steam check ran, so seed a placeholder version for the
        // notification messages. A prior real detection's value is kept if set.
        if (RequiredVersion <= 0) RequiredVersion = TestRequiredVersion;

        Logger.LogWarning("Test update triggered via command. Running the restart flow.");
        info.ReplyToCommand("Test update triggered via command. Running the restart flow.");

        ManageServerUpdate();
    }
    */

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (!UpdateAvailable) return HookResult.Continue;

        CCSPlayerController? player = @event.Userid;

        if (player == null || player!.IsBot || player.TeamNum <= (byte)CsTeam.Spectator) return HookResult.Continue;
        if (PlayersNotified.TryGetValue(player.Slot, out bool notified) && notified) return HookResult.Continue;

        PlayersNotified[player.Slot] = true;

        Server.NextFrame(() =>
        {
            if (player is not { IsValid: true }) return;

            NotifyPlayerAboutUpdate(player);
        });

        return HookResult.Continue;
    }

    private void NotifyPlayerAboutUpdate(CCSPlayerController player)
    {
        if (InstantShutdown)
        {
            player.PrintToChat(
                $" {Localizer["AutoUpdater.Chat.Prefix"]} {Localizer["AutoUpdater.Chat.InstantRestart"]}");
            return;
        }

        int remainingTime = Math.Max(1, Config.ShutdownDelay - (int)(Server.CurrentTime - UpdateFoundTime));

        int minutes = remainingTime / 60;
        int seconds = remainingTime % 60;

        List<string> parts = new();
        if (minutes > 0) parts.Add(FormatTimeUnit(minutes, "AutoUpdater.Chat.MinuteLabel"));
        if (seconds > 0) parts.Add(FormatTimeUnit(seconds, "AutoUpdater.Chat.SecondLabel"));

        string timeToRestart = string.Join(" ", parts);

        player.PrintToChat(
            $" {Localizer["AutoUpdater.Chat.Prefix"]} {Localizer["AutoUpdater.Chat.NewUpdateReleased", RequiredVersion, timeToRestart]}");
    }

    private string FormatTimeUnit(int value, string labelKey)
    {
        string suffix = value != 1 ? $"{Localizer["AutoUpdater.Chat.PluralSuffix"]}" : string.Empty;
        return $"{value} {Localizer[labelKey]}{suffix}";
    }

    private void ResendUpdateNotification()
    {
        if (IsServerLoading) return;

        GetCurrentPlayers().ForEach(NotifyPlayerAboutUpdate);
    }

    private async Task<bool> IsUpdateAvailable()
    {
        string steamInfPatchVersion = await GetSteamInfPatchVersion();

        if (string.IsNullOrWhiteSpace(steamInfPatchVersion))
        {
            Logger.LogError(Localizer["AutoUpdater.Console.ErrorPatchVersionNull"]);
            return false;
        }

        UpToDateCheckResponse.UpToDateCheck? result;

        try
        {
            var response = await HttpClient.GetAsync(string.Format(SteamApiEndpoint, steamInfPatchVersion));

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning(Localizer["AutoUpdater.Console.WarningSteamRequestFailed", response.StatusCode]);
                return false;
            }

            result = (await response.Content.ReadFromJsonAsync<UpToDateCheckResponse>())?.Response;
        }
        catch (Exception ex)
        {
            Logger.LogError(Localizer["AutoUpdater.Console.ErrorSteamRequestException", ex.Message]);
            return false;
        }

        if (result is not { Success: true })
        {
            Logger.LogError(Localizer["AutoUpdater.Console.ErrorInvalidApiResponse"]);
            return false;
        }

        if (result.UpToDate) return false;

        if (result.RequiredVersion <= 0)
        {
            Logger.LogError(Localizer["AutoUpdater.Console.ErrorInvalidApiResponse"]);
            return false;
        }

        RequiredVersion = result.RequiredVersion;

        return true;
    }

    private async Task<string> GetSteamInfPatchVersion()
    {
        string steamInfPath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");

        if (!File.Exists(steamInfPath))
        {
            Logger.LogError(Localizer["AutoUpdater.Console.ErrorSteamInfNotFound", steamInfPath]);
            return string.Empty;
        }

        try
        {
            string steamInfContents = await File.ReadAllTextAsync(steamInfPath);
            Match match = PatchVersionRegex().Match(steamInfContents);

            if (match.Success) return match.Groups[1].Value;

            Logger.LogError(Localizer["AutoUpdater.Console.ErrorPatchVersionKeyNotFound", steamInfPath]);

            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogError(Localizer["AutoUpdater.Console.ErrorReadingSteamInf", ex.Message]);
        }

        return string.Empty;
    }

    private void PrepareServerShutdown()
    {
        ResendNotificationTimer?.Kill();
        ResendNotificationTimer = null;

        if (!InstantShutdown)
        {
            GetCurrentPlayers().ForEach(NotifyPlayerAboutUpdate);
        }

        AddTimer(1, ShutdownServer);
    }
    
    private void ShutdownServer()
    {
        Logger.LogInformation(Localizer["AutoUpdater.Console.ServerShutdownInitiated", RequiredVersion]);
        Server.ExecuteCommand("quit");
    }

    private static List<CCSPlayerController> GetCurrentPlayers()
    {
        return Utilities.GetPlayers().Where(controller => controller is { IsValid: true, IsBot: false, IsHLTV: false }).ToList();
    }

    [GeneratedRegex(@"PatchVersion=(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex PatchVersionRegex();
}  