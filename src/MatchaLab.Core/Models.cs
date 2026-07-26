using System.Text.Json.Serialization;

namespace MatchaLab.Core;

public sealed class CatalogServer
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("country")] public string Country { get; set; } = "";
    [JsonPropertyName("city")] public string City { get; set; } = "";
    [JsonPropertyName("flag")] public string Flag { get; set; } = "";
    [JsonPropertyName("protocols")] public List<string> Protocols { get; set; } = new();
}

public sealed class Catalog
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("servers")] public List<CatalogServer> Servers { get; set; } = new();
}

public sealed class ResolveResp
{
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("proto")] public string Proto { get; set; } = "";
    [JsonPropertyName("config")] public string Config { get; set; } = "";
}

public sealed class BypassService
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("cidrs")] public List<string> Cidrs { get; set; } = new();
}

public sealed class ServicesResp
{
    [JsonPropertyName("services")] public List<BypassService> Services { get; set; } = new();
    [JsonPropertyName("via_vpn")] public List<string>? ViaVpn { get; set; }
}
