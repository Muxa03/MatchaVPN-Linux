using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MatchaLab.App.Services;
using MatchaLab.Core;

namespace MatchaLab.App.ViewModels;

public partial class AppViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly SubscriptionStore _sub;
    private readonly SplitTunnelStore _split;
    private readonly ITunnelController _tunnel;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private DateTime _connectedAt;
    private int _tick;
    private string? _endpointHost;
    private bool _uiVisible = true;

    public event Action? KeyChanged;

    [ObservableProperty] private bool hasKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsBusy),
        nameof(PillText), nameof(HeroWord1), nameof(HeroWord2), nameof(CenterText))]
    private TunnelStatus status = TunnelStatus.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFlag), nameof(CurrentCountry), nameof(CurrentCountryDisplay), nameof(CurrentCity))]
    private CatalogServer? selectedServer;

    [ObservableProperty] private bool ruEnabled = true;
    [ObservableProperty] private bool autostartEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMatchaWg), nameof(IsHysteria2), nameof(ProtoName), nameof(ProtoSubtitle))]
    private string selectedProto = "amneziaWG";
    [ObservableProperty] private string? errorText;
    [ObservableProperty] private string pingText = "—";
    [ObservableProperty] private string speedText = "—";
    [ObservableProperty] private string trafficNum = "0";
    [ObservableProperty] private string trafficUnit = "МБ";
    [ObservableProperty] private string sessionTime = "--:--:--";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    private string themeId = "taro";

    public const string Version = "1.4.1";
    public string VersionText => $"{ThemeId} {Version}";
    public string AboutText => $"MatchaLab · версия {Version}";

    public string AutostartTitle =>
        OperatingSystem.IsWindows() ? "Запускать при входе в Windows" : "Запускать при входе в систему";
    public string KeyStorageNote =>
        OperatingSystem.IsWindows() ? "хранится зашифрованным (DPAPI)" : "хранится в вашем профиле";

    public ObservableCollection<CatalogServer> Servers { get; } = new();
    public ObservableCollection<string> Domains { get; } = new();
    public IReadOnlyList<Palette> Themes => ThemeManager.All;

    public bool IsConnected => Status == TunnelStatus.Connected;
    public bool IsBusy => Status is TunnelStatus.Connecting or TunnelStatus.Reasserting;

    public string PillText => IsConnected ? "ЗАЩИТА ВКЛЮЧЕНА" : "ЗАЩИТА ВЫКЛ";
    public string HeroWord1 => IsConnected ? "ТЫ" : "ЗАЩИТА";
    public string HeroWord2 => IsConnected ? "ПОД ЗАЩИТОЙ" : "ВЫКЛ";
    public string CenterText => IsConnected ? "СТОП" : "ПУСК";

    public string CurrentFlag => SelectedServer?.Flag ?? "🌐";
    public string CurrentCountry => SelectedServer?.Country ?? "выбери страну";
    public string CurrentCity => SelectedServer is { } s ? $"{s.City} · Матча" : "нажми, чтобы выбрать";

    public bool IsMatchaWg => SelectedProto != "hysteria2";
    public bool IsHysteria2 => SelectedProto == "hysteria2";
    public string ProtoName => IsHysteria2 ? "Hysteria2" : "MatchaWG";
    public string ProtoSubtitle => IsHysteria2 ? "QUIC · маскировка под HTTPS" : "обфускация трафика";

    public string CurrentCountryDisplay
    {
        get
        {
            if (SelectedServer is not { } s) return "выбери страну";
            if (s.Country.Length <= 9) return s.Country;
            var code = FlagToIso(s.Flag);
            return code ?? s.Country;
        }
    }

    private static string? FlagToIso(string? flag)
    {
        if (string.IsNullOrEmpty(flag)) return null;
        var sb = new System.Text.StringBuilder(2);
        foreach (var rune in flag.EnumerateRunes())
            if (rune.Value is >= 0x1F1E6 and <= 0x1F1FF)
                sb.Append((char)('A' + (rune.Value - 0x1F1E6)));
        return sb.Length == 2 ? sb.ToString() : null;
    }

    public string KeyMasked
    {
        get { var t = _sub.Token; return string.IsNullOrEmpty(t) ? "—" : (t.Length <= 8 ? t : $"{t[..4]}••••{t[^4..]}"); }
    }

    public AppViewModel(ApiClient api, SubscriptionStore sub, SplitTunnelStore split,
                        ITunnelController tunnel, AppSettings settings)
    {
        _api = api; _sub = sub; _split = split; _tunnel = tunnel; _settings = settings;

        hasKey = _sub.HasToken;
        ruEnabled = settings.RuEnabled;
        themeId = settings.Theme;
        autostartEnabled = Autostart.IsEnabled();
        _split.RuEnabled = settings.RuEnabled;
        _sub.SelectedServerId = settings.SelectedServerId;
        selectedProto = settings.Proto;
        _sub.SelectedProtoId = settings.Proto;
        if (tunnel is IProtocolRouter pr) pr.Protocol = settings.Proto;
        foreach (var d in settings.CustomDomains) _split.AddDomain(d);
        foreach (var d in _split.CustomDomains) Domains.Add(d);

        _tunnel.StatusChanged += OnTunnelStatus;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        status = _tunnel.Status;
        if (_tunnel.Status == TunnelStatus.Connected) { _connectedAt = DateTime.Now; _timer.Start(); }
    }

    private bool _initialized;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (!_sub.HasToken) return;
        await _split.RefreshAsync();
        await LoadCatalogAsync();
        if (_settings.WasConnected && Status == TunnelStatus.Disconnected && SelectedServer is not null)
            await ToggleAsync();
    }

    private async Task LoadCatalogAsync()
    {
        await _sub.RefreshCatalogAsync();
        Servers.Clear();
        foreach (var s in _sub.Servers) Servers.Add(s);
        SelectedServer = Servers.FirstOrDefault(s => s.Id == _sub.SelectedServerId) ?? Servers.FirstOrDefault();
        ErrorText = _sub.LastError;
    }

    private int _connectBusy;
    private CancellationTokenSource? _connectCts;

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (Status is TunnelStatus.Connected or TunnelStatus.Connecting or TunnelStatus.Reasserting)
        {
            _settings.WasConnected = false;
            _settings.Save();
            _connectCts?.Cancel();
            await _tunnel.StopAsync();
            return;
        }

        if (Interlocked.Exchange(ref _connectBusy, 1) == 1) return;
        var cts = _connectCts = new CancellationTokenSource();
        try
        {
            ErrorText = null;
            if (SelectedServer is null)
            {
                await LoadCatalogAsync();
                if (SelectedServer is null) { ErrorText = "Нет доступных серверов"; return; }
            }

            Status = TunnelStatus.Connecting;
            await _split.RefreshAsync();
            var raw = await _sub.ResolveSelectedAsync(cts.Token);
            if (cts.IsCancellationRequested) { Status = _tunnel.Status; return; }
            if (raw is null)
            {
                Status = TunnelStatus.Error;
                ErrorText = _sub.LastError ?? "Не удалось получить конфигурацию. Проверьте ключ и сеть.";
                return;
            }

            string cfg;
            if (_sub.SelectedProtoId == "hysteria2")
            {
                var stack = OperatingSystem.IsLinux() ? "gvisor" : "system";
                cfg = Hy2Config.Build(raw, _split.DirectCidrs(), _split.ViaVpn, stack);
                _endpointHost = Hy2Config.EndpointHost(raw);
            }
            else
            {
                cfg = AwgConfig.EnsureMtu(AwgConfig.WithAllowedIPs(raw, _split.TunnelAllowedIPs()));
                _endpointHost = AwgConfig.EndpointHost(cfg);
            }
            if (_tunnel is IProtocolRouter r) r.Protocol = _sub.SelectedProtoId;
            await _tunnel.StartAsync(cfg);
            _settings.WasConnected = true;
            _settings.Save();
        }
        catch (OperationCanceledException) { Status = _tunnel.Status; }
        catch (Exception ex) { Status = TunnelStatus.Error; ErrorText = ex.Message; }
        finally
        {
            if (ReferenceEquals(_connectCts, cts)) _connectCts = null;
            Interlocked.Exchange(ref _connectBusy, 0);
        }
    }

    [RelayCommand]
    private async Task SelectProtoAsync(string? proto)
    {
        var p = proto == "hysteria2" ? "hysteria2" : "amneziaWG";
        if (p == SelectedProto) return;
        await StopForSwitchAsync();
        SelectedProto = p;
        _sub.SelectedProtoId = p;
        _settings.Proto = p;
        _settings.Save();
        if (_tunnel is IProtocolRouter r) r.Protocol = p;
    }

    [RelayCommand]
    private async Task RefreshCatalogAsync() => await LoadCatalogAsync();

    [RelayCommand]
    private async Task SignOutAsync()
    {
        _settings.WasConnected = false;
        _settings.Save();
        if (Status != TunnelStatus.Disconnected) await _tunnel.StopAsync();
        _sub.SignOut();
        Servers.Clear();
        SelectedServer = null;
        HasKey = false;
        OnPropertyChanged(nameof(KeyMasked));
        KeyChanged?.Invoke();
    }

    public async void SetKey(string raw)
    {
        _sub.SetToken(raw);
        HasKey = _sub.HasToken;
        OnPropertyChanged(nameof(KeyMasked));
        KeyChanged?.Invoke();
        if (HasKey) { await _split.RefreshAsync(); await LoadCatalogAsync(); }
    }

    public void SetTheme(string id)
    {
        ThemeManager.Apply(id, animate: true);
        ThemeId = id;
        _settings.Theme = id;
        _settings.Save();
    }

    public void AddDomain(string raw)
    {
        _split.AddDomain(raw);
        SyncDomains();
    }

    public void RemoveDomain(string d)
    {
        _split.RemoveDomain(d);
        SyncDomains();
    }

    private void SyncDomains()
    {
        Domains.Clear();
        foreach (var d in _split.CustomDomains) Domains.Add(d);
        _settings.CustomDomains = _split.CustomDomains.ToList();
        _settings.Save();
    }

    partial void OnSelectedServerChanged(CatalogServer? value)
    {
        if (value is null) return;
        var changed = _sub.SelectedServerId != value.Id;
        _sub.SelectedServerId = value.Id;
        _settings.SelectedServerId = value.Id;
        _settings.Save();
        if (changed) _ = StopForSwitchAsync();
    }

    private int _switchBusy;

    private async Task StopForSwitchAsync()
    {
        if (Status is TunnelStatus.Disconnected) return;
        if (Interlocked.Exchange(ref _switchBusy, 1) == 1) return;
        try
        {
            _settings.WasConnected = false;
            _settings.Save();
            await _tunnel.StopAsync();
        }
        catch (Exception ex) { Status = TunnelStatus.Error; ErrorText = ex.Message; }
        finally { Interlocked.Exchange(ref _switchBusy, 0); }
    }

    partial void OnRuEnabledChanged(bool value)
    {
        _split.RuEnabled = value;
        _settings.RuEnabled = value;
        _settings.Save();
    }

    partial void OnAutostartEnabledChanged(bool value)
    {
        Autostart.Set(value);
        _settings.Autostart = value;
        _settings.Save();
    }

    private void OnTunnelStatus(TunnelStatus s) => Dispatcher.UIThread.Post(() =>
    {
        Status = s;
        switch (s)
        {
            case TunnelStatus.Connected:
                _connectedAt = DateTime.Now; _tick = 0;
                SessionTime = "00:00:00"; TrafficNum = "0"; TrafficUnit = "МБ"; SpeedText = "—";
                if (_uiVisible) _timer.Start();
                break;
            case TunnelStatus.Disconnected:
                _timer.Stop(); SessionTime = "--:--:--"; PingText = "—"; SpeedText = "—";
                break;
            case TunnelStatus.Error:
                _timer.Stop();
                break;
        }
    });

    public void SetUiVisible(bool visible)
    {
        if (_uiVisible == visible) return;
        _uiVisible = visible;
        if (!visible) { _timer.Stop(); return; }
        if (Status == TunnelStatus.Connected)
        {
            SessionTime = (DateTime.Now - _connectedAt).ToString(@"hh\:mm\:ss");
            _timer.Start();
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        SessionTime = (DateTime.Now - _connectedAt).ToString(@"hh\:mm\:ss");

        var total = _tunnel.RxBytes + _tunnel.TxBytes;
        var mb = total / (1024.0 * 1024.0);
        if (mb >= 1024) { TrafficNum = (mb / 1024).ToString("0.0"); TrafficUnit = "ГБ"; }
        else { TrafficNum = mb.ToString("0"); TrafficUnit = "МБ"; }

        if (++_tick % 5 == 0) await PingAsync();
    }

    [ObservableProperty] private bool measuringSpeed;

    private static readonly HttpClient SpeedHttp = CreateSpeedClient();

    private static HttpClient CreateSpeedClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"MatchaLab/{Version}");
        return c;
    }

    private static readonly string[] SpeedUrls =
    {
        "https://speed.cloudflare.com/__down?bytes=60000000",
        "http://speedtest.tele2.net/10MB.zip",
    };

    [RelayCommand]
    private async Task MeasureSpeedAsync()
    {
        if (!IsConnected || MeasuringSpeed) return;
        MeasuringSpeed = true;
        SpeedText = "…";
        try
        {
            var mbps = await RunSpeedProbeAsync();
            SpeedText = FormatSpeed(mbps);
        }
        catch { SpeedText = "—"; }
        finally { MeasuringSpeed = false; }
    }

    private static string FormatSpeed(double mbps) => mbps >= 100
        ? mbps.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
        : mbps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<double> RunSpeedProbeAsync()
    {
        foreach (var url in SpeedUrls)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await SpeedHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[81920];
                long bytes = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(6))
                {
                    var n = await s.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    bytes += n;
                }
                sw.Stop();
                if (bytes < 262_144 || sw.Elapsed.TotalSeconds < 0.5) continue;
                return bytes * 8 / sw.Elapsed.TotalSeconds / 1_000_000.0;
            }
            catch { }
        }
        throw new InvalidOperationException("Источники замера недоступны");
    }

    private static readonly HttpClient PingHttp = new() { Timeout = TimeSpan.FromSeconds(4) };

    private async Task PingAsync()
    {
        if (_endpointHost is { } host && SelectedProto != "hysteria2")
        {
            try
            {
                using var icmp = new System.Net.NetworkInformation.Ping();
                var reply = await icmp.SendPingAsync(host, 1500);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                {
                    PingText = Math.Max(1, reply.RoundtripTime).ToString();
                    return;
                }
            }
            catch { }
        }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await PingHttp.GetAsync($"{ApiClient.BaseUrl}/health");
            PingText = Math.Max(1, sw.ElapsedMilliseconds).ToString();
        }
        catch { PingText = "—"; }
    }
}
