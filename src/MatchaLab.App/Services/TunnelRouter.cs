namespace MatchaLab.App.Services;

public interface IProtocolRouter
{
    string Protocol { get; set; }
}

public sealed class TunnelRouter : ITunnelController, IProtocolRouter
{
    private readonly ITunnelController _awg;
    private readonly ITunnelController _hy2;
    private ITunnelController _active;

    public string Protocol { get; set; } = "amneziaWG";

    private ITunnelController Target => Protocol == "hysteria2" ? _hy2 : _awg;

    public TunnelRouter(ITunnelController awg, ITunnelController hy2)
    {
        _awg = awg;
        _hy2 = hy2;
        _active = _hy2.Status == TunnelStatus.Connected ? _hy2 : _awg;
        if (_hy2.Status == TunnelStatus.Connected) Protocol = "hysteria2";

        _awg.StatusChanged += s => { if (_active == _awg) StatusChanged?.Invoke(s); };
        _hy2.StatusChanged += s => { if (_active == _hy2) StatusChanged?.Invoke(s); };
    }

    public TunnelStatus Status => _active.Status;
    public ulong RxBytes => _active.RxBytes;
    public ulong TxBytes => _active.TxBytes;
    public event Action<TunnelStatus>? StatusChanged;

    public async Task StartAsync(string config)
    {
        var target = Target;
        if (_active != target && _active.Status != TunnelStatus.Disconnected)
            await _active.StopAsync();
        _active = target;
        await target.StartAsync(config);
    }

    public Task StopAsync() => _active.StopAsync();
}
