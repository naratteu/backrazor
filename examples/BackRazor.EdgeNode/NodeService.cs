namespace BackRazor.EdgeNode;

internal sealed class NodeService(Root<Node, DeviceSpec> root, BrokerOptions options, IConfiguration configuration, IHostEnvironment environment, ILogger<NodeService> log)
    : BackgroundService
{
    private readonly string _path = Path.GetFullPath(configuration["config"] ?? "node.json", environment.ContentRootPath);
    private string? _accepted;
    public string? Error { get; private set; }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken, initial: true);
        await base.StartAsync(cancellationToken);
        log.LogInformation("Watching configuration: {Path}", _path);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await ReloadAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken, bool initial = false)
    {
        try
        {
            var text = await File.ReadAllTextAsync(_path, cancellationToken);
            if (text == _accepted && Error is null) return;
            var desired = Configuration.Parse(text);
            if (desired.Broker != options) throw new InvalidOperationException("Changing broker/node identity requires a process restart.");
            await root.ApplyAsync(desired.Devices, cancellationToken);
            _accepted = text;
            Error = null;
            log.LogInformation("configuration-applied {Count}", desired.Devices.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            if (initial) throw;
            if (Error != error.Message) log.LogWarning("Configuration rejected: {Error}", error.Message);
            Error = error.Message;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await root.DisposeAsync();
    }
}
