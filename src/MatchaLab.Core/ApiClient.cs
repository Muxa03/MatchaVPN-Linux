using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MatchaLab.Core;

public sealed class ApiClient
{
    public const string BaseUrl = "https://matchavpn.space";
    private readonly HttpClient _http;

    public ApiClient(HttpClient? http = null)
        => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<Catalog?> GetCatalogAsync(string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/catalog");
        req.Headers.Add("X-Token", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<Catalog>(cancellationToken: ct);
    }

    public async Task<string?> ResolveAsync(string token, string server, string proto,
                                            CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/resolve");
        req.Headers.Add("X-Token", token);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { server, proto }), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var r = await resp.Content.ReadFromJsonAsync<ResolveResp>(cancellationToken: ct);
        return string.IsNullOrEmpty(r?.Config) ? null : r!.Config;
    }

    public async Task<ServicesResp?> GetServicesAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<ServicesResp>($"{BaseUrl}/services", ct); }
        catch { return null; }
    }
}
