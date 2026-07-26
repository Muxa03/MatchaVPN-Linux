namespace MatchaLab.App.Services;

public enum TunnelStatus { Disconnected, Connecting, Connected, Reasserting, Error }

public interface ITunnelController
{
    TunnelStatus Status { get; }
    ulong RxBytes { get; }
    ulong TxBytes { get; }
    event Action<TunnelStatus>? StatusChanged;

    Task StartAsync(string awgConfig);
    Task StopAsync();
}

public sealed class StubTunnelController : ITunnelController
{
    private readonly System.Timers.Timer _t = new(1000);

    public TunnelStatus Status { get; private set; } = TunnelStatus.Disconnected;
    public ulong RxBytes { get; private set; }
    public ulong TxBytes { get; private set; }
    public event Action<TunnelStatus>? StatusChanged;

    public StubTunnelController()
    {
        _t.Elapsed += (_, _) =>
        {
            RxBytes += (ulong)Random.Shared.Next(60_000, 700_000);
            TxBytes += (ulong)Random.Shared.Next(15_000, 160_000);
        };
    }

    public async Task StartAsync(string awgConfig)
    {
        Set(TunnelStatus.Connecting);
        await Task.Delay(900);
        RxBytes = 0; TxBytes = 0;
        _t.Start();
        Set(TunnelStatus.Connected);
    }

    public Task StopAsync()
    {
        _t.Stop();
        Set(TunnelStatus.Disconnected);
        return Task.CompletedTask;
    }

    private void Set(TunnelStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }
}
