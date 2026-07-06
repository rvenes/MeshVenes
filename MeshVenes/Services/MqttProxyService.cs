using Google.Protobuf;
using Meshtastic.Protobufs;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeshVenes.Services;

public sealed class MqttProxyService
{
    public static MqttProxyService Instance { get; } = new();

    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly MqttFactory _factory = new();

    private const string DefaultTopicRoot = "msh";
    private const string PublicBrokerHost = "mqtt.meshtastic.org";

    private bool _initialized;
    private IMqttClient? _client;
    private ModuleConfig.Types.MQTTConfig _currentConfig = new();
    private uint _connectedNodeNum;
    private string _topicFilter = string.Empty;
    private IReadOnlyList<string> _topicFilters = Array.Empty<string>();
    private string _configSignature = string.Empty;
    private CancellationTokenSource? _reconnectLoopCts;
    private Task? _reconnectLoopTask;
    private bool _manualSuspend;

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
            var subscribeFilters = await ResolveSubscribeFiltersAsync(nodeNum, config).ConfigureAwait(false);
            if (_manualSuspend && !forceReconnect)
            {
                await ApplyConfigWhileSuspendedAsync(nodeNum, config, subscribeFilters).ConfigureAwait(false);
                return;
            }

            await ApplyConfigAsync(nodeNum, config, subscribeFilters, forceReconnect).ConfigureAwait(false);
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
            _manualSuspend = false;
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

    public async Task DisconnectRuntimeProxyAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            _manualSuspend = true;
            await StopInternalLockedAsync("Proxy manually disconnected.", clearConfigEnabled: false).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
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
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
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

    /// <summary>
    /// Builds the subscription list the same way the official clients do:
    /// one filter per downlink-enabled channel ({root}/2/e/{channel}/+, plus
    /// {root}/2/json/{channel}/+ when JSON is enabled) and always the PKI
    /// topic for direct messages. Falls back to {root}/2/e/# wildcards when
    /// the channel list cannot be read.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveSubscribeFiltersAsync(uint nodeNum, ModuleConfig.Types.MQTTConfig config)
    {
        var enabled = config.Enabled && config.ProxyToClientEnabled && !string.IsNullOrWhiteSpace(config.Address);
        if (!enabled)
            return Array.Empty<string>();

        var root = GetRootTopic(config);
        try
        {
            var channels = await AdminConfigClient.Instance.GetChannelsAsync(nodeNum).ConfigureAwait(false);

            var presetName = "LongFast";
            try
            {
                var loraConfig = await AdminConfigClient.Instance
                    .GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.LoraConfig)
                    .ConfigureAwait(false);
                presetName = GetModemPresetName(loraConfig.Lora?.ModemPreset);
            }
            catch
            {
                // Preset name only matters for unnamed channels; keep the default.
            }

            var filters = new List<string>();
            foreach (var channel in channels)
            {
                if (channel.Role == Channel.Types.Role.Disabled)
                    continue;
                if (channel.Settings?.DownlinkEnabled != true)
                    continue;

                var name = string.IsNullOrWhiteSpace(channel.Settings.Name)
                    ? presetName
                    : channel.Settings.Name.Trim();

                filters.Add($"{root}/2/e/{name}/+");
                if (config.JsonEnabled)
                    filters.Add($"{root}/2/json/{name}/+");
            }

            filters.Add($"{root}/2/e/PKI/+");
            return filters.Distinct(StringComparer.Ordinal).ToList();
        }
        catch
        {
            var fallback = new List<string> { $"{root}/2/e/#" };
            if (config.JsonEnabled)
                fallback.Add($"{root}/2/json/#");
            return fallback;
        }
    }

    private static string GetRootTopic(ModuleConfig.Types.MQTTConfig config)
    {
        var root = (config.Root ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(root) ? DefaultTopicRoot : root;
    }

    private static string GetModemPresetName(Config.Types.LoRaConfig.Types.ModemPreset? preset) => preset switch
    {
        // Deprecated presets are kept so nodes on old firmware still map to
        // the correct channel topic name.
#pragma warning disable CS0612
        Config.Types.LoRaConfig.Types.ModemPreset.LongSlow => "LongSlow",
        Config.Types.LoRaConfig.Types.ModemPreset.VeryLongSlow => "VLongSlow",
#pragma warning restore CS0612
        Config.Types.LoRaConfig.Types.ModemPreset.MediumSlow => "MediumSlow",
        Config.Types.LoRaConfig.Types.ModemPreset.MediumFast => "MediumFast",
        Config.Types.LoRaConfig.Types.ModemPreset.ShortSlow => "ShortSlow",
        Config.Types.LoRaConfig.Types.ModemPreset.ShortFast => "ShortFast",
        Config.Types.LoRaConfig.Types.ModemPreset.LongModerate => "LongMod",
        Config.Types.LoRaConfig.Types.ModemPreset.ShortTurbo => "ShortTurbo",
        _ => "LongFast"
    };

    private async Task ApplyConfigAsync(uint nodeNum, ModuleConfig.Types.MQTTConfig config, IReadOnlyList<string> subscribeFilters, bool forceReconnect)
    {
        var address = (config.Address ?? string.Empty).Trim();
        var enabled = config.Enabled && config.ProxyToClientEnabled && !string.IsNullOrWhiteSpace(address);
        var brokerText = string.IsNullOrWhiteSpace(address) ? "—" : address;
        var signature = BuildSignature(config, nodeNum, subscribeFilters);

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            _currentConfig = config.Clone();
            _connectedNodeNum = nodeNum;
            _proxyEnabledByConfig = enabled;
            _broker = brokerText;
            _topicFilters = subscribeFilters;
            _topicFilter = string.Join(", ", subscribeFilters);

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

    private async Task ApplyConfigWhileSuspendedAsync(uint nodeNum, ModuleConfig.Types.MQTTConfig config, IReadOnlyList<string> subscribeFilters)
    {
        var address = (config.Address ?? string.Empty).Trim();
        var enabled = config.Enabled && config.ProxyToClientEnabled && !string.IsNullOrWhiteSpace(address);
        var brokerText = string.IsNullOrWhiteSpace(address) ? "—" : address;

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            _currentConfig = config.Clone();
            _connectedNodeNum = nodeNum;
            _proxyEnabledByConfig = enabled;
            _broker = brokerText;
            _topicFilters = subscribeFilters;
            _topicFilter = string.Join(", ", subscribeFilters);
            _configSignature = BuildSignature(config, nodeNum, subscribeFilters);
            await StopInternalLockedAsync("Proxy manually disconnected.", clearConfigEnabled: false).ConfigureAwait(false);
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
        // Exponential backoff like the official clients: 1s doubling to 30s,
        // reset after a successful broker session.
        var reconnectDelay = TimeSpan.FromSeconds(1);
        var maxReconnectDelay = TimeSpan.FromSeconds(30);

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
                reconnectDelay = TimeSpan.FromSeconds(1);
                await WaitForDisconnectOrStopAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState("Broker connection failed.", _broker, ex.Message);
                await DelaySafeAsync(reconnectDelay, ct).ConfigureAwait(false);
                reconnectDelay = reconnectDelay * 2 > maxReconnectDelay ? maxReconnectDelay : reconnectDelay * 2;
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

        var payload = ReadPayload(arg.ApplicationMessage);
        var proxyMsg = new MqttClientProxyMessage
        {
            Topic = topic,
            Retained = arg.ApplicationMessage.Retain
        };

        if (IsJsonTopic(topic))
        {
            // JSON topics are forwarded as text (like the official clients),
            // but only when JSON is enabled and the payload parses as JSON —
            // binary payloads on json topics would trigger parse errors on
            // the device.
            if (!_currentConfig.JsonEnabled || !TryDecodeJsonText(payload, out var jsonText))
            {
                Interlocked.Increment(ref _droppedCount);
                RaiseStateChanged();
                return;
            }

            proxyMsg.Text = jsonText;
        }
        else if (IsServiceEnvelopeTopic(topic))
        {
            if (payload.Length > 0)
                proxyMsg.Data = ByteString.CopyFrom(payload);
            else
                proxyMsg.Text = string.Empty;
        }
        else
        {
            Interlocked.Increment(ref _droppedCount);
            RaiseStateChanged();
            return;
        }

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

    private static bool IsServiceEnvelopeTopic(string topic)
    {
        var normalized = (topic ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.IndexOf("/2/e/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.IndexOf("/2/c/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsJsonTopic(string topic)
        => (topic ?? string.Empty).IndexOf("/2/json/", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TryDecodeJsonText(byte[] payload, out string jsonText)
    {
        jsonText = string.Empty;
        if (payload.Length == 0)
            return false;

        try
        {
            jsonText = Encoding.UTF8.GetString(payload);
            using var _ = JsonDocument.Parse(jsonText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildSignature(ModuleConfig.Types.MQTTConfig config, uint nodeNum, IReadOnlyList<string> subscribeFilters)
    {
        return string.Join("|",
            nodeNum.ToString(CultureInfo.InvariantCulture),
            config.Enabled ? "1" : "0",
            config.ProxyToClientEnabled ? "1" : "0",
            config.TlsEnabled ? "1" : "0",
            config.JsonEnabled ? "1" : "0",
            config.Address ?? string.Empty,
            config.Root ?? string.Empty,
            config.Username ?? string.Empty,
            config.Password ?? string.Empty,
            string.Join(",", subscribeFilters));
    }

    private async Task WaitForDisconnectOrStopAsync(IMqttClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _proxyEnabledByConfig && client.IsConnected)
            await DelaySafeAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }

    private async Task SubscribeCurrentTopicFilterAsync(IMqttClient client, CancellationToken ct)
    {
        var filters = _topicFilters;
        if (filters.Count == 0)
            return;

        var builder = _factory.CreateSubscribeOptionsBuilder();
        foreach (var filter in filters)
        {
            builder.WithTopicFilter(f =>
            {
                f.WithTopic(filter);
                // Match the official clients: QoS 1 and no-local so the
                // node's own uplinked packets are not echoed back to it.
                f.WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
                f.WithNoLocal(true);
            });
        }

        await client.SubscribeAsync(builder.Build(), ct).ConfigureAwait(false);
    }

    private static MqttClientOptions BuildClientOptions(ModuleConfig.Types.MQTTConfig config)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId("MeshVenesMqttProxy-" + Guid.NewGuid().ToString("N"))
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithCleanSession(true);

        // Like the official clients: always use TLS against the public
        // Meshtastic broker, regardless of the node's tls_enabled flag.
        var tls = config.TlsEnabled || IsPublicMeshtasticBroker(config.Address);

        ConfigureServer(builder, config, tls);

        var username = (config.Username ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(username))
            builder.WithCredentials(username, config.Password ?? string.Empty);

        if (tls)
            builder.WithTlsOptions(o => o.UseTls());

        return builder.Build();
    }

    private static bool IsPublicMeshtasticBroker(string? address)
    {
        var raw = (address ?? string.Empty).Trim();
        if (raw.Length == 0)
            return false;

        var afterScheme = raw.Contains("://", StringComparison.Ordinal)
            ? raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..]
            : raw;
        var host = afterScheme.Split('/')[0].Split(':')[0];
        return string.Equals(host, PublicBrokerHost, StringComparison.OrdinalIgnoreCase);
    }

    private static void ConfigureServer(MqttClientOptionsBuilder builder, ModuleConfig.Types.MQTTConfig config, bool tls)
    {
        var addressRaw = (config.Address ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(addressRaw))
            throw new InvalidOperationException("MQTT address is empty.");

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
