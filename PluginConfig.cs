using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace AutoUpdater;


public sealed class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")] 
    public override int Version { get; set; } = 3;

    [JsonPropertyName("UpdateCheckInterval")]
    public int UpdateCheckInterval { get; set; } = 180;

    [JsonPropertyName("ShutdownDelay")]
    public int ShutdownDelay { get; set; } = 120;

    [JsonPropertyName("ShutdownMessageInterval")]
    public int ShutdownMessageInterval { get; set; } = 30;

    [JsonPropertyName("MinPlayersInstantShutdown")]
    public int MinPlayersInstantShutdown { get; set; } = 0;

    [JsonPropertyName("ShutdownOnMapChangeIfPendingUpdate")]
    public bool ShutdownOnMapChangeIfPendingUpdate { get; set; } = true;
}