using Google.Protobuf;
using Meshtastic.Protobufs;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MeshVenes.Services;

public sealed class MqttProxyService
{
    public static MqttProxyService Instance { get; } = new();

    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly MqttFactory _factory = new();

    private bool _initialized;
    private IMqttClient? _client;
    private ModuleConfig.Types.MQTTConfig _currentConfig = new();
    private uint _connectedNodeNum;
    private string _topicFilter = string.Empty;
    private string _configSignature = string.Empty;
    private CancellationTokenSource? _reconnectLoopCts;
    private Task? _reconnectLoopTask;

    private bool _proxyEnabledByConfig;
    private string _runtimeStatus = "Disabled";
    private string _broker = "—";
    private string _lastError = string.Empty;
    private DateTime? _lastBrokerConnectedUtc;
    private long _nodeToBrokerCount;
    private long _brokerToNodeCount;
    private long _droppedCount;

    public event Action? StateChanged;

    private MqttProxyService() { }

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        RadioClient.Instance.ConnectionChanged += RadioClient_ConnectionChanged;
        AppState.ConnectedNodeChanged += AppState_ConnectedNodeChanged;
        _ = RefreshFromConnectedNodeConfigAsync(forceReconnect: false);
    }

    public async Task ShutdownAsync()
    {
        if (!_initialized)
            return;

        _initialized = false;
        RadioClient.Instance.ConnectionChanged -= RadioClient_ConnectionChanged;
        AppState.ConnectedNodeChanged -= AppState_ConnectedNodeChanged;
        await StopInternalAsync("Stopped.", clearConfigEnabled: true).ConfigureAwait(false);
    }

    public MqttProxySnapshot GetSnapshot()
    {
        return new MqttProxySnapshot(
            IsConfiguredEnabled: _proxyEnabledByConfig,
            IsBrokerConnected: _client?.IsConnected == true,
            RuntimeStatus: _runtimeStatus,
            Broker: _broker,
            TopicFilter: _topicFilter,
            LastError: _lastError,
            LastBrokerConnectedUtc: _lastBrokerConnectedUtc,
            NodeToBrokerCount: _nodeToBrokerCount,
            BrokerToNodeCount: _brokerToNodeCount,
            DroppedCount: _droppedCount);
    }

    public async Task RefreshFromConnectedNodeConfigAsync(bool forceReconnect)
    {
        if (!_initialized)
            return;

        if (!RadioClient.Instance.IsConnected)
        {
            await StopInternalAsync("Radio disconnected.", clearConfigEnabled: false).ConfigureAwait(false);
            return;
        }

        if (!TryGetConnectedNodeNum(out var nodeNum))
        {
            SetState("Waiting for connected node identity...", "—", null);
            return;
        }

        try
        {
            SetState("Loading MQTT config from node...", _broker, null);
            var module = await AdminConfigClient.Instance
                .GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.MqttConfig)
                .ConfigureAwait(false);

            var config = module.Mqtt ?? new ModuleConfig.Types.MQTTConfig();
            await ApplyConfigAsync(nodeNum, config, forceReconnect).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState("MQTT config load failed.", _broker, ex.Message);
        }
    }

    public async Task ForceReconnectAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_proxyEnabledByConfig)
            {
                SetState("Proxy disabled in node config.", _broker, null);
                return;
            }

            _configSignature = string.Empty;
        }
        finally
        {
            _sync.Release();
        }

        await RefreshFromConnectedNodeConfigAsync(forceReconnect: true).ConfigureAwait(false);
    }

    public async Task HandleFromNodeMessageAsync(MqttClientProxyMessage proxyMessage)
    {
        if (proxyMessage is null)
            return;

        var client = _client;
        if (client is null || !client.IsConnected || !_proxyEnabledByConfig)
        {
            Interlocked.Increment(ref _droppedCount);
            RaiseStateChanged();
            return;
        }

        var topic = (proxyMessage.Topic ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(topic))
        {
            Interlocked.Increment(ref _droppedCount);
            RaiseStateChanged();
            return;
        }

        byte[] payload;
        if (proxyMessage.HasData)
            payload = proxyMessage.Data.ToByteArray();
        else if (proxyMessage.HasText)
            payload = Encoding.UTF8.GetBytes(proxyMessage.Text ?? string.Empty);
        else
            payload = Array.Empty<byte>();

        try
        {
            var appMsg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(proxyMessage.Retained)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            await client.PublishAsync(appMsg, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _nodeToBrokerCount);
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedCount);
            SetState("Publish to broker failed.", _broker, ex.Message);
        }
    }

    private async Task ApplyConfigAsync(uint nodeNum, ModuleConfig.Types.MQTTConfig config, bool forceReconnect)
    {
        var address = (config.Address ?? string.Empty).Trim();
        var root = (config.Root ?? string.Empty).Trim();
        var enabled = config.Enabled && config.ProxyToClientEnabled && !string.IsNullOrWhiteSpace(address);
        var brokerText = string.IsNullOrWhiteSpace(address) ? "—" : address;
        var signature = BuildSignature(config, nodeNum);

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            _currentConfig = config.Clone();
            _connectedNodeNum = nodeNum;
            _proxyEnabledByConfig = enabled;
            _broker = brokerText;
            _topicFilter = BuildTopicFilter(root);

            if (!enabled)
            {
                await StopInternalLockedAsync("Proxy disabled in node config.", clearConfigEnabled: true).ConfigureAwait(false);
                return;
            }

            if (!forceReconnect && string.Equals(_configSignature, signature, StringComparison.Ordinal) && _client is { IsConnected: true })
            {
                SetState("Broker connected (active).", _broker, null);
                return;
            }

            _configSignature = signature;
            await RestartReconnectLoopLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task RestartReconnectLoopLockedAsync()
    {
        await StopInternalLockedAsync("Reconnecting...", clearConfigEnabled: false).ConfigureAwait(false);

        _reconnectLoopCts = new CancellationTokenSource();
        var ct = _reconnectLoopCts.Token;
        _reconnectLoopTask = Task.Run(() => ReconnectLoopAsync(ct), ct);
        SetState("Connecting to broker...", _broker, null);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!RadioClient.Instance.IsConnected)
            {
                SetState("Waiting for radio connection...", _broker, null);
                await DelaySafeAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            if (!_proxyEnabledByConfig)
            {
                await DelaySafeAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var client = _client ?? CreateClient();
                _client = client;
                if (!client.IsConnected)
                {
                    var options = BuildClientOptions(_currentConfig);
                    await client.ConnectAsync(options, ct).ConfigureAwait(false);
                }

                await SubscribeCurrentTopicFilterAsync(client, ct).ConfigureAwait(false);
                SetState("Broker connected (active).", _broker, null);
                await WaitForDisconnectOrStopAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState("Broker connection failed.", _broker, ex.Message);
                await DelaySafeAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            }
        }
    }

    private IMqttClient CreateClient()
    {
        var client = _factory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
        client.DisconnectedAsync += OnClientDisconnectedAsync;
        client.ConnectedAsync += OnClientConnectedAsync;
        return client;
    }

    private Task OnClientConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        _lastBrokerConnectedUtc = DateTime.UtcNow;
        RaiseStateChanged();
        return Task.CompletedTask;
    }

    private Task OnClientDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        if (_proxyEnabledByConfig)
        {
            var reason = arg.Exception?.Message;
            SetState("Broker disconnected. Reconnecting...", _broker, reason);
        }

        return Task.CompletedTask;
    }

    private async Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        if (!_proxyEnabledByConfig || !RadioClient.Instance.IsConnected)
        {
            Interlocked.Increment(ref _droppedCount);
            RaiseStateChanged();
            return;
        }

        var topic = arg.ApplicationMessage.Topic ?? string.Empty;
        if (string.IsNullOrWhiteSpace(topic))
        {
            Interlocked.Increment(ref _droppedCount);
            RaiseStateChanged();
            return;
        }

        var proxyMsg = new MqttClientProxyMessage
        {
            Topic = topic,
            Retained = arg.ApplicationMessage.Retain
        };

        var payload = ReadPayload(arg.ApplicationMessage);
        if (payload.Length > 0)
            proxyMsg.Data = ByteString.CopyFrom(payload);
        else
            proxyMsg.Text = string.Empty;

        try
        {
            var sent = await RadioClient.Instance.SendMqttProxyMessageAsync(proxyMsg).ConfigureAwait(false);
            if (sent)
                Interlocked.Increment(ref _brokerToNodeCount);
            else
                Interlocked.Increment(ref _droppedCount);
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedCount);
            SetState("Forward to node failed.", _broker, ex.Message);
        }
    }

    private static byte[] ReadPayload(MqttApplicationMessage message)
    {
        var segment = message.PayloadSegment;
        if (segment.Array is null || segment.Count <= 0)
            return Array.Empty<byte>();

        var payload = new byte[segment.Count];
        Buffer.BlockCopy(segment.Array, segment.Offset, payload, 0, segment.Count);
        return payload;
    }

    private static string BuildSignature(ModuleConfig.Types.MQTTConfig config, uint nodeNum)
    {
        return string.Join("|",
            nodeNum.ToString(CultureInfo.InvariantCulture),
            config.Enabled ? "1" : "0",
            config.ProxyToClientEnabled ? "1" : "0",
            config.TlsEnabled ? "1" : "0",
            config.Address ?? string.Empty,
            config.Root ?? string.Empty,
            config.Username ?? string.Empty,
            config.Password ?? string.Empty);
    }

    private static string BuildTopicFilter(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "msh/#";

        if (root.Contains('#') || root.Contains('+'))
            return root;

        return root.TrimEnd('/') + "/#";
    }

    private async Task WaitForDisconnectOrStopAsync(IMqttClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _proxyEnabledByConfig && client.IsConnected)
            await DelaySafeAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }

    private async Task SubscribeCurrentTopicFilterAsync(IMqttClient client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_topicFilter))
            return;

        var options = _factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f =>
            {
                f.WithTopic(_topicFilter);
                f.WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce);
            })
            .Build();

        await client.SubscribeAsync(options, ct).ConfigureAwait(false);
    }

    private static MqttClientOptions BuildClientOptions(ModuleConfig.Types.MQTTConfig config)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId("MeshVenes-" + Guid.NewGuid().ToString("N"))
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithCleanSession(true);

        ConfigureServer(builder, config);

        var username = (config.Username ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(username))
            builder.WithCredentials(username, config.Password ?? string.Empty);

        if (config.TlsEnabled)
        {
            builder.WithTlsOptions(o =>
            {
                o.UseTls();
                o.WithAllowUntrustedCertificates(true);
                o.WithIgnoreCertificateChainErrors(true);
                o.WithIgnoreCertificateRevocationErrors(true);
            });
        }

        return builder.Build();
    }

    private static void ConfigureServer(MqttClientOptionsBuilder builder, ModuleConfig.Types.MQTTConfig config)
    {
        var addressRaw = (config.Address ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(addressRaw))
            throw new InvalidOperationException("MQTT address is empty.");

        var tls = config.TlsEnabled;

        if (!addressRaw.Contains("://", StringComparison.Ordinal))
            addressRaw = (tls ? "mqtts://" : "mqtt://") + addressRaw;

        if (Uri.TryCreate(addressRaw, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            var port = uri.IsDefaultPort ? (tls ? 8883 : 1883) : uri.Port;
            builder.WithTcpServer(host, port);
            return;
        }

        var hostPort = addressRaw.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var hostFallback = hostPort.Length > 0 ? hostPort[0] : addressRaw;
        var portFallback = hostPort.Length > 1 && int.TryParse(hostPort[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            ? p
            : tls ? 8883 : 1883;

        builder.WithTcpServer(hostFallback, portFallback);
    }

    private async void RadioClient_ConnectionChanged()
    {
        try
        {
            if (!RadioClient.Instance.IsConnected)
            {
                await StopInternalAsync("Radio disconnected.", clearConfigEnabled: false).ConfigureAwait(false);
                return;
            }

            await RefreshFromConnectedNodeConfigAsync(forceReconnect: false).ConfigureAwait(false);
        }
        catch
        {
            // Keep reconnect path resilient.
        }
    }

    private async void AppState_ConnectedNodeChanged()
    {
        try
        {
            await RefreshFromConnectedNodeConfigAsync(forceReconnect: false).ConfigureAwait(false);
        }
        catch
        {
            // Keep reconnect path resilient.
        }
    }

    private async Task StopInternalAsync(string status, bool clearConfigEnabled)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalLockedAsync(status, clearConfigEnabled).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task StopInternalLockedAsync(string status, bool clearConfigEnabled)
    {
        if (clearConfigEnabled)
        {
            _proxyEnabledByConfig = false;
            _topicFilter = string.Empty;
            _configSignature = string.Empty;
        }

        if (_reconnectLoopCts is not null)
        {
            try { _reconnectLoopCts.Cancel(); } catch { }
            _reconnectLoopCts.Dispose();
            _reconnectLoopCts = null;
        }

        if (_reconnectLoopTask is not null)
        {
            try { await _reconnectLoopTask.ConfigureAwait(false); } catch { }
            _reconnectLoopTask = null;
        }

        if (_client is not null)
        {
            try
            {
                if (_client.IsConnected)
                    await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore disconnect errors during cleanup.
            }

            _client.ApplicationMessageReceivedAsync -= OnApplicationMessageReceivedAsync;
            _client.DisconnectedAsync -= OnClientDisconnectedAsync;
            _client.ConnectedAsync -= OnClientConnectedAsync;
            _client.Dispose();
            _client = null;
        }

        SetState(status, _broker, null);
    }

    private void SetState(string status, string broker, string? error)
    {
        _runtimeStatus = status;
        _broker = string.IsNullOrWhiteSpace(broker) ? "—" : broker;
        _lastError = error ?? string.Empty;
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    private static bool TryGetConnectedNodeNum(out uint nodeNum)
    {
        nodeNum = 0;
        var idHex = AppState.ConnectedNodeIdHex;
        if (string.IsNullOrWhiteSpace(idHex))
            return false;

        var s = idHex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeNum);
    }

    private static async Task DelaySafeAsync(TimeSpan duration, CancellationToken ct)
    {
        if (duration <= TimeSpan.Zero)
            return;

        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during cancellation.
        }
    }
}

public sealed record MqttProxySnapshot(
    bool IsConfiguredEnabled,
    bool IsBrokerConnected,
    string RuntimeStatus,
    string Broker,
    string TopicFilter,
    string LastError,
    DateTime? LastBrokerConnectedUtc,
    long NodeToBrokerCount,
    long BrokerToNodeCount,
    long DroppedCount);
