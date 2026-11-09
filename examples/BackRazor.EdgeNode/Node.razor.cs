using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using HiveMQtt.Client;
using HiveMQtt.Client.Events;
using HiveMQtt.MQTT5.ReasonCodes;
using HiveMQtt.MQTT5.Types;
using HiveMQtt.Sparkplug.Payload;
using HiveMQtt.Sparkplug.Protobuf;
using Microsoft.AspNetCore.Components;
using R3;

namespace BackRazor.EdgeNode;

public sealed record DeviceCommand(string Id, Payload Payload);

public partial class Node
{
    [Parameter] public IReadOnlyList<DeviceSpec> Items { get; set; } = [];
    [Inject] public BrokerOptions Options { get; set; } = null!;
    [Inject] public ILogger<Node> Log { get; set; } = null!;
    [Inject] public IConfiguration HostConfiguration { get; set; } = null!;
    [Inject] public IHostEnvironment Environment { get; set; } = null!;
    private readonly Guid _instance = Guid.NewGuid();
    private readonly string _runtime = RuntimeFeature.IsDynamicCodeSupported ? "jit" : "native-aot";
    private readonly SemaphoreSlim _publish = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Subject<DeviceCommand> _commands = new();
    private Task _connection = Task.CompletedTask;
    private HiveMQClient? _client;
    private CancellationTokenSource? _failed;
    private long _epoch = -1;
    private int _sequence;
    private int _rebirthRequested;
    private byte _bdSeq;
    private bool _ready;
    private bool _disposing;
    private string _status = "connecting";

    public long Epoch => Interlocked.Read(ref _epoch);
    public Observable<DeviceCommand> Commands => _commands.AsObservable();

    protected override void OnInitialized() => _connection = ConnectLoopAsync();

    private string Topic(string kind, string? device = null) =>
        $"spBv1.0/{Options.GroupId}/{kind}/{Options.NodeId}" + (device is null ? "" : $"/{device}");

    private async Task SetStatusAsync(string status)
    {
        if (!_disposing)
            await InvokeAsync(() => { _status = status; StateHasChanged(); });
    }

    private async Task<byte> NextBdSeqAsync()
    {
        var directory = Path.GetFullPath(HostConfiguration["state-dir"] ?? ".state", Environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Options.GroupId}/{Options.NodeId}")));
        var path = Path.Combine(directory, name + ".bdseq");
        var value = File.Exists(path) ? byte.Parse(await File.ReadAllTextAsync(path, _stop.Token)) : (byte)0;
        await File.WriteAllTextAsync(path + ".tmp", unchecked((byte)(value + 1)).ToString(), _stop.Token);
        File.Move(path + ".tmp", path, overwrite: true);
        return value;
    }

    private async Task ConnectLoopAsync()
    {
        while (!_stop.IsCancellationRequested && !_disposing)
        {
            HiveMQClient? client = null;
            using var failed = new CancellationTokenSource();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, failed.Token);
            try
            {
                await SetStatusAsync("connecting");
                using (var probe = new TcpClient())
                    await probe.ConnectAsync(Options.Host, Options.Port, attempt.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), attempt.Token);
                var bdSeq = await NextBdSeqAsync();
                var builder = new HiveMQClientOptionsBuilder().WithBroker(Options.Host).WithPort(Options.Port)
                    .WithUseTls(Options.UseTls).WithCleanStart(true).WithSessionExpiryInterval(0)
                    .WithKeepAlive(15).WithAutomaticReconnect(false)
                    .WithClientId("backrazor-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Options.GroupId}/{Options.NodeId}")))[..20]);
                if (Options.UserName is not null) builder.WithUserName(Options.UserName);
                if (Options.Password is not null)
                {
                    using var password = new SecureString();
                    foreach (var character in Options.Password) password.AppendChar(character);
                    password.MakeReadOnly();
                    builder.WithPassword(password.Copy());
                }
                var options = builder.Build();
                // NDEATH contains bdSeq only: no payload seq or timestamp.
                options.LastWillAndTestament = new LastWillAndTestament(Topic("NDEATH"), SparkplugPayloadEncoder.Encode(DeathPayload(bdSeq)), QualityOfService.AtLeastOnceDelivery, false);
                client = new HiveMQClient(options);
                client.OnMessageReceived += Receive;
                var connected = await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5), attempt.Token);
                if (connected.ReasonCode != ConnAckReasonCode.Success) throw new IOException("MQTT connection was rejected.");
                await client.SubscribeAsync(new SubscribeOptionsBuilder()
                    .WithSubscription(Topic("NCMD"), QualityOfService.AtMostOnceDelivery)
                    .WithSubscription(Topic("DCMD") + "/+", QualityOfService.AtMostOnceDelivery).Build())
                    .WaitAsync(TimeSpan.FromSeconds(5), attempt.Token);
                await _publish.WaitAsync(attempt.Token);
                try
                {
                    _client = client;
                    _failed = failed;
                    _bdSeq = bdSeq;
                    await NodeBirthAsync(attempt.Token);
                    _ready = true;
                }
                finally { _publish.Release(); }
                Log.LogInformation("node-online {NodeId} bdSeq={BdSeq}", Options.NodeId, bdSeq);
                await SetStatusAsync("connected");
                while (client.IsConnected() && !attempt.IsCancellationRequested && !_disposing)
                {
                    if (Interlocked.Exchange(ref _rebirthRequested, 0) != 0) await RebirthAsync();
                    await Task.Delay(250, attempt.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                Log.LogWarning("MQTT connection interrupted: {Error}", error.Message);
                await SetStatusAsync("disconnected");
            }
            finally
            {
                await _publish.WaitAsync();
                try { _ready = false; _client = null; _failed = null; }
                finally { _publish.Release(); }
                if (client is not null)
                {
                    client.OnMessageReceived -= Receive;
                    client.Dispose();
                }
            }
            if (!_stop.IsCancellationRequested && !_disposing)
            {
                try { await Task.Delay(1000, _stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
        }
    }

    private void Receive(object? sender, OnMessageReceivedEventArgs args)
    {
        if (_disposing) return;
        var message = args.PublishMessage;
        if (message.Payload is null || message.Payload.Length > 1048576 || !SparkplugPayloadEncoder.TryDecode(message.Payload, out var payload) || payload is null) return;
        if (message.Topic == Topic("NCMD") && payload.Metrics.Any(metric => metric.Name == "Node Control/Rebirth" && metric.BooleanValue))
            Interlocked.Exchange(ref _rebirthRequested, 1);
        else if (message.Topic?.StartsWith(Topic("DCMD") + "/", StringComparison.Ordinal) == true)
            _commands.OnNext(new DeviceCommand(message.Topic[(Topic("DCMD").Length + 1)..], payload));
    }

    private async Task RebirthAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _publish.WaitAsync(timeout.Token);
            try { if (_ready && !_disposing) await NodeBirthAsync(timeout.Token); }
            finally { _publish.Release(); }
        }
        catch (Exception error)
        {
            Log.LogWarning("Rebirth failed: {Error}", error.Message);
            _failed?.Cancel();
        }
    }

    private async Task NodeBirthAsync(CancellationToken cancellationToken)
    {
        _sequence = 0;
        var payload = SparkplugPayloadEncoder.CreatePayload(0);
        payload.Metrics.Add(SparkplugMetricBuilder.Create("bdSeq").WithCurrentTimestamp().WithInt64Value(_bdSeq).Build());
        payload.Metrics.Add(SparkplugMetricBuilder.Create("Node Control/Rebirth").WithCurrentTimestamp().WithBooleanValue(false).Build());
        await SendAsync("NBIRTH", null, payload, cancellationToken);
        _sequence = 1;
        Interlocked.Increment(ref _epoch);
    }

    private static Payload DeathPayload(byte bdSeq)
    {
        var payload = new Payload();
        payload.Metrics.Add(SparkplugMetricBuilder.Create("bdSeq").WithInt64Value(bdSeq).Build());
        return payload;
    }

    private Task SendAsync(string kind, string? device, Payload payload, CancellationToken cancellationToken) =>
        _client!.PublishAsync(new MQTT5PublishMessage
        {
            Topic = Topic(kind, device), Payload = SparkplugPayloadEncoder.Encode(payload), Retain = false,
            QoS = QualityOfService.AtMostOnceDelivery
        }, cancellationToken);

    private async Task<bool> PublishDeviceAsync(string kind, string id, long samples, double value, long epoch, CancellationToken cancellationToken)
    {
        if (!_ready || epoch != Epoch) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _publish.WaitAsync(timeout.Token);
        try
        {
            if (!_ready || epoch != Epoch) return false;
            var payload = SparkplugPayloadEncoder.CreatePayload(_sequence);
            if (kind != "DDEATH")
            {
                payload.Metrics.Add(SparkplugMetricBuilder.Create("Samples").WithCurrentTimestamp().WithInt64Value(samples).Build());
                payload.Metrics.Add(SparkplugMetricBuilder.Create("Value").WithCurrentTimestamp().WithDoubleValue(value).Build());
                if (kind == "DBIRTH") payload.Metrics.Add(SparkplugMetricBuilder.Create("Device Control/Reset").WithCurrentTimestamp().WithBooleanValue(false).Build());
            }
            await SendAsync(kind, id, payload, timeout.Token);
            _sequence = (_sequence + 1) & 255;
            return true;
        }
        catch (Exception error)
        {
            _ready = false;
            _failed?.Cancel();
            Log.LogWarning("Device publish failed: {Error}", error.Message);
            if (kind == "DDEATH" || cancellationToken.IsCancellationRequested) throw;
            return false;
        }
        finally { _publish.Release(); }
    }

    public Task<bool> BirthAsync(string id, long samples, double value, long epoch, CancellationToken cancellationToken) => PublishDeviceAsync("DBIRTH", id, samples, value, epoch, cancellationToken);
    public Task<bool> DataAsync(string id, long samples, double value, long epoch, CancellationToken cancellationToken) => PublishDeviceAsync("DDATA", id, samples, value, epoch, cancellationToken);
    public Task<bool> DeathAsync(string id, long epoch) => PublishDeviceAsync("DDEATH", id, 0, 0, epoch, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _publish.WaitAsync(timeout.Token);
            try
            {
                if (_ready && _client is not null)
                {
                    // MQTT 5 reason 04 asks the broker to publish the registered QoS 1 NDEATH Will.
                    await _client.DisconnectAsync(new DisconnectOptionsBuilder().WithReasonCode(DisconnectReasonCode.DisconnectWithWillMessage).Build())
                        .WaitAsync(timeout.Token);
                }
                _ready = false;
            }
            finally { _publish.Release(); }
        }
        finally
        {
            _stop.Cancel();
            await _connection;
            _commands.Dispose();
            _stop.Dispose();
            _publish.Dispose();
        }
    }
}
