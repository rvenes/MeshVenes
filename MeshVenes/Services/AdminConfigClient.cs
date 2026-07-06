using Google.Protobuf;
using Meshtastic.Protobufs;
using MeshVenes.Protocol;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeshVenes.Services;

public sealed class AdminConfigClient
{
    public static AdminConfigClient Instance { get; } = new();

    private readonly object _lock = new();
    private event Action<AdminEnvelope>? _incomingAdmin;
    private readonly Dictionary<uint, ByteString> _sessionPasskeysByNode = new();

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    // Spacing between staged writes in a batch so the firmware's admin queue
    // is not flooded; queue control in RadioClient adds further throttling.
    private static readonly TimeSpan InterBatchWriteDelay = TimeSpan.FromMilliseconds(150);

    private AdminConfigClient() { }

    public void PublishIncomingAdminMessage(uint fromNodeNum, AdminMessage admin)
    {
        if (admin is null)
            return;

        if (admin.SessionPasskey is not null && admin.SessionPasskey.Length > 0)
        {
            lock (_lock)
                _sessionPasskeysByNode[fromNodeNum] = ByteString.CopyFrom(admin.SessionPasskey.ToByteArray());
        }

        Action<AdminEnvelope>? handlers;
        lock (_lock)
            handlers = _incomingAdmin;

        handlers?.Invoke(new AdminEnvelope(fromNodeNum, admin.Clone()));
    }

    public async Task<Config> GetConfigAsync(uint nodeNum, AdminMessage.Types.ConfigType type, CancellationToken ct = default)
    {
        var req = new AdminMessage
        {
            GetConfigRequest = type
        };

        var response = await RequestResponseAsync(
            nodeNum,
            req,
            m => m.PayloadVariantCase == AdminMessage.PayloadVariantOneofCase.GetConfigResponse,
            DefaultTimeout,
            ct).ConfigureAwait(false);

        return response.GetConfigResponse?.Clone() ?? new Config();
    }

    public async Task<ModuleConfig> GetModuleConfigAsync(uint nodeNum, AdminMessage.Types.ModuleConfigType type, CancellationToken ct = default)
    {
        var req = new AdminMessage
        {
            GetModuleConfigRequest = type
        };

        var response = await RequestResponseAsync(
            nodeNum,
            req,
            m => m.PayloadVariantCase == AdminMessage.PayloadVariantOneofCase.GetModuleConfigResponse,
            DefaultTimeout,
            ct).ConfigureAwait(false);

        return response.GetModuleConfigResponse?.Clone() ?? new ModuleConfig();
    }

    public async Task<Channel> GetChannelAsync(uint nodeNum, int channelIndex, CancellationToken ct = default)
    {
        var req = new AdminMessage
        {
            GetChannelRequest = (uint)(channelIndex + 1)
        };

        var response = await RequestResponseAsync(
            nodeNum,
            req,
            m => m.PayloadVariantCase == AdminMessage.PayloadVariantOneofCase.GetChannelResponse && m.GetChannelResponse.Index == channelIndex,
            DefaultTimeout,
            ct).ConfigureAwait(false);

        return response.GetChannelResponse?.Clone() ?? new Channel { Index = channelIndex, Role = Channel.Types.Role.Disabled };
    }

    public async Task<IReadOnlyList<Channel>> GetChannelsAsync(uint nodeNum, int maxChannels = 8, bool treatTimeoutAsDisabled = true, CancellationToken ct = default)
    {
        var channels = new List<Channel>(Math.Max(1, maxChannels));

        for (var i = 0; i < maxChannels; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                channels.Add(await GetChannelAsync(nodeNum, i, ct).ConfigureAwait(false));
            }
            catch (TimeoutException) when (treatTimeoutAsDisabled)
            {
                channels.Add(new Channel { Index = i, Role = Channel.Types.Role.Disabled });
            }
        }

        return channels;
    }

    public async Task<User> GetOwnerAsync(uint nodeNum, CancellationToken ct = default)
    {
        var req = new AdminMessage
        {
            GetOwnerRequest = true
        };

        var response = await RequestResponseAsync(
            nodeNum,
            req,
            m => m.PayloadVariantCase == AdminMessage.PayloadVariantOneofCase.GetOwnerResponse,
            DefaultTimeout,
            ct).ConfigureAwait(false);

        return response.GetOwnerResponse?.Clone() ?? new User();
    }

    public async Task SaveConfigAsync(uint nodeNum, Config config, CancellationToken ct = default)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        await BeginEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        try
        {
            var req = new AdminMessage
            {
                SetConfig = config.Clone()
            };

            await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
            await CommitEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    public async Task SaveChannelsAsync(uint nodeNum, IEnumerable<Channel> channels, CancellationToken ct = default)
    {
        if (channels is null)
            throw new ArgumentNullException(nameof(channels));

        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        await BeginEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        try
        {
            foreach (var channel in channels)
            {
                ct.ThrowIfCancellationRequested();
                var req = new AdminMessage
                {
                    SetChannel = channel?.Clone() ?? new Channel()
                };
                await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
            }

            await CommitEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    public async Task SaveModuleConfigAsync(uint nodeNum, ModuleConfig moduleConfig, CancellationToken ct = default)
    {
        if (moduleConfig is null)
            throw new ArgumentNullException(nameof(moduleConfig));

        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        await BeginEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        try
        {
            var req = new AdminMessage
            {
                SetModuleConfig = moduleConfig.Clone()
            };

            await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
            await CommitEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    public async Task<string> GetCannedMessageModuleMessagesAsync(uint nodeNum, CancellationToken ct = default)
    {
        var req = new AdminMessage
        {
            GetCannedMessageModuleMessagesRequest = true
        };

        var response = await RequestResponseAsync(
            nodeNum,
            req,
            m => m.PayloadVariantCase == AdminMessage.PayloadVariantOneofCase.GetCannedMessageModuleMessagesResponse,
            DefaultTimeout,
            ct).ConfigureAwait(false);

        return response.GetCannedMessageModuleMessagesResponse ?? string.Empty;
    }

    public async Task SaveCannedMessageModuleMessagesAsync(uint nodeNum, string messages, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        await BeginEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        try
        {
            var req = new AdminMessage
            {
                SetCannedMessageModuleMessages = messages ?? string.Empty
            };

            await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
            await CommitEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    public async Task<string> GetRingtoneAsync(uint nodeNum, CancellationToken ct = default)
    {
        var req = new AdminMessage
        {
            GetRingtoneRequest = true
        };

        var response = await RequestResponseAsync(
            nodeNum,
            req,
            m => m.PayloadVariantCase == AdminMessage.PayloadVariantOneofCase.GetRingtoneResponse,
            DefaultTimeout,
            ct).ConfigureAwait(false);

        return response.GetRingtoneResponse ?? string.Empty;
    }

    public async Task SaveRingtoneAsync(uint nodeNum, string ringtone, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        await BeginEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        try
        {
            var req = new AdminMessage
            {
                SetRingtoneMessage = ringtone ?? string.Empty
            };

            await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
            await CommitEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    public async Task SaveOwnerAsync(uint nodeNum, User owner, CancellationToken ct = default)
    {
        if (owner is null)
            throw new ArgumentNullException(nameof(owner));

        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        await BeginEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        try
        {
            var req = new AdminMessage
            {
                SetOwner = owner.Clone()
            };

            await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
            await CommitEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    public async Task SetFavoriteNodeAsync(uint adminNodeNum, uint favoriteNodeNum, bool isFavorite, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(adminNodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage();
        if (isFavorite)
            req.SetFavoriteNode = favoriteNodeNum;
        else
            req.RemoveFavoriteNode = favoriteNodeNum;

        await SendWithoutResponseAsync(adminNodeNum, req, ct).ConfigureAwait(false);
    }

    public async Task<DeviceMetadata> GetDeviceMetadataAsync(uint nodeNum, CancellationToken ct = default)
    {
        var req = new AdminMessage
        {
            GetDeviceMetadataRequest = true
        };

        var response = await RequestResponseAsync(
            nodeNum,
            req,
            m => m.PayloadVariantCase == AdminMessage.PayloadVariantOneofCase.GetDeviceMetadataResponse,
            DefaultTimeout,
            ct).ConfigureAwait(false);

        return response.GetDeviceMetadataResponse?.Clone() ?? new DeviceMetadata();
    }

    public async Task SetIgnoredNodeAsync(uint adminNodeNum, uint targetNodeNum, bool isIgnored, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(adminNodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage();
        if (isIgnored)
            req.SetIgnoredNode = targetNodeNum;
        else
            req.RemoveIgnoredNode = targetNodeNum;

        await SendWithoutResponseAsync(adminNodeNum, req, ct).ConfigureAwait(false);
    }

    public async Task RemoveNodeAsync(uint adminNodeNum, uint targetNodeNum, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(adminNodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage
        {
            RemoveByNodenum = targetNodeNum
        };

        await SendWithoutResponseAsync(adminNodeNum, req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the node's real-time clock (admin set_time_only, firmware 2.5.5+).
    /// Useful for nodes without GPS or NTP access.
    /// </summary>
    public async Task SetTimeAsync(uint nodeNum, DateTimeOffset time, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage
        {
            SetTimeOnly = (uint)time.ToUnixTimeSeconds()
        };

        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    public async Task RebootNodeAsync(uint nodeNum, int seconds = 0, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage
        {
            RebootSeconds = seconds
        };

        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    public async Task ShutdownNodeAsync(uint nodeNum, int seconds = 0, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage
        {
            ShutdownSeconds = seconds
        };

        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    public async Task FactoryResetConfigAsync(uint nodeNum, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage
        {
            FactoryResetConfig = 1
        };

        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    public async Task ResetNodeDbAsync(uint nodeNum, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage
        {
            NodedbReset = true
        };

        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies all writes in <paramref name="batch"/> inside a single
    /// begin_edit_settings / commit_edit_settings transaction. Nothing is persisted
    /// to the node's flash until the final commit, so a failure part-way leaves the
    /// stored configuration untouched (a reboot restores the previous state).
    /// </summary>
    public async Task ApplyBatchAsync(uint nodeNum, SettingsWriteBatch batch, Action<int, int, string>? progress = null, CancellationToken ct = default)
    {
        if (batch is null)
            throw new ArgumentNullException(nameof(batch));
        if (batch.Count == 0)
            return;

        // A stale passkey (older than ~300 s or from before a reboot) makes the
        // firmware silently ignore writes, so always fetch a fresh one first.
        await RefreshSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        await BeginEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);

        var index = 0;
        foreach (var (label, message) in batch.Writes)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Invoke(index, batch.Count, label);
            await SendWithoutResponseAsync(nodeNum, message, ct).ConfigureAwait(false);
            await Task.Delay(InterBatchWriteDelay, ct).ConfigureAwait(false);
        }

        await CommitEditSettingsAsync(nodeNum, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the node (firmware 2.6+) to store a backup of its preferences in
    /// internal flash. Older firmware ignores the message.
    /// </summary>
    public async Task BackupPreferencesToFlashAsync(uint nodeNum, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage
        {
            BackupPreferences = AdminMessage.Types.BackupLocation.Flash
        };

        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the node (firmware 2.6+) to restore preferences from the backup in
    /// internal flash. The node reboots after a successful restore.
    /// </summary>
    public async Task RestorePreferencesFromFlashAsync(uint nodeNum, CancellationToken ct = default)
    {
        await EnsureSessionPasskeyAsync(nodeNum, ct).ConfigureAwait(false);

        var req = new AdminMessage
        {
            RestorePreferences = AdminMessage.Types.BackupLocation.Flash
        };

        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    public async Task RefreshSessionPasskeyAsync(uint nodeNum, CancellationToken ct = default)
    {
        lock (_lock)
            _sessionPasskeysByNode.Remove(nodeNum);

        _ = await GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.DeviceConfig, ct).ConfigureAwait(false);
    }

    private async Task EnsureSessionPasskeyAsync(uint nodeNum, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_sessionPasskeysByNode.TryGetValue(nodeNum, out var p) && p.Length > 0)
                return;
        }

        _ = await GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.DeviceConfig, ct).ConfigureAwait(false);
    }

    private async Task BeginEditSettingsAsync(uint nodeNum, CancellationToken ct)
    {
        var req = new AdminMessage { BeginEditSettings = true };
        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    private async Task CommitEditSettingsAsync(uint nodeNum, CancellationToken ct)
    {
        var req = new AdminMessage { CommitEditSettings = true };
        await SendWithoutResponseAsync(nodeNum, req, ct).ConfigureAwait(false);
    }

    private async Task SendWithoutResponseAsync(uint nodeNum, AdminMessage message, CancellationToken ct)
    {
        AttachSessionPasskey(nodeNum, message);
        var packetId = await RadioClient.Instance.SendAdminMessageAsync(nodeNum, message, wantResponse: false).ConfigureAwait(false);
        if (packetId == 0)
            throw new InvalidOperationException("Not connected");
        ct.ThrowIfCancellationRequested();
    }

    private async Task<AdminMessage> RequestResponseAsync(
        uint nodeNum,
        AdminMessage request,
        Func<AdminMessage, bool> predicate,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AdminMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));

        void Handler(AdminEnvelope envelope)
        {
            if (envelope.FromNodeNum != nodeNum)
                return;

            if (!predicate(envelope.Message))
                return;

            tcs.TrySetResult(envelope.Message);
        }

        lock (_lock)
            _incomingAdmin += Handler;

        try
        {
            AttachSessionPasskey(nodeNum, request);
            var packetId = await RadioClient.Instance.SendAdminMessageAsync(nodeNum, request, wantResponse: true).ConfigureAwait(false);
            if (packetId == 0)
                return new AdminMessage();

            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for admin response from node 0x{nodeNum:x8}.");
            }
        }
        finally
        {
            lock (_lock)
                _incomingAdmin -= Handler;
        }
    }

    private void AttachSessionPasskey(uint nodeNum, AdminMessage message)
    {
        if (message is null)
            return;

        if (message.SessionPasskey is not null && message.SessionPasskey.Length > 0)
            return;

        lock (_lock)
        {
            if (_sessionPasskeysByNode.TryGetValue(nodeNum, out var passkey) && passkey.Length > 0)
                message.SessionPasskey = ByteString.CopyFrom(passkey.ToByteArray());
        }
    }

    private readonly record struct AdminEnvelope(uint FromNodeNum, AdminMessage Message);
}

/// <summary>
/// An ordered set of settings writes that will be applied to a node as one
/// begin/commit_edit_settings transaction via <see cref="AdminConfigClient.ApplyBatchAsync"/>.
/// Callers control the order; reboot-triggering sections (device, network, LoRa)
/// should be added last.
/// </summary>
public sealed class SettingsWriteBatch
{
    private readonly List<(string Label, AdminMessage Message)> _writes = new();

    public int Count => _writes.Count;

    internal IReadOnlyList<(string Label, AdminMessage Message)> Writes => _writes;

    public void AddConfig(string label, Config config)
        => _writes.Add((label, new AdminMessage { SetConfig = config.Clone() }));

    public void AddModuleConfig(string label, ModuleConfig moduleConfig)
        => _writes.Add((label, new AdminMessage { SetModuleConfig = moduleConfig.Clone() }));

    public void AddChannel(string label, Channel channel)
        => _writes.Add((label, new AdminMessage { SetChannel = channel.Clone() }));

    public void AddOwner(string label, User owner)
        => _writes.Add((label, new AdminMessage { SetOwner = owner.Clone() }));

    public void AddCannedMessages(string label, string messages)
        => _writes.Add((label, new AdminMessage { SetCannedMessageModuleMessages = messages ?? string.Empty }));

    public void AddRingtone(string label, string ringtone)
        => _writes.Add((label, new AdminMessage { SetRingtoneMessage = ringtone ?? string.Empty }));
}
