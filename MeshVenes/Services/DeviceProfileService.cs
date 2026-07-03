using Meshtastic.Protobufs;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeshVenes.Services;

/// <summary>
/// Builds and applies <see cref="DeviceProfile"/> (.cfg) files, the interchange
/// format used by the official Meshtastic Android/Apple clients and the CLI.
/// </summary>
public static class DeviceProfileService
{
    public static async Task<DeviceProfile> BuildAsync(uint nodeNum, Action<string>? progress = null, CancellationToken ct = default)
    {
        var client = AdminConfigClient.Instance;
        var profile = new DeviceProfile();

        progress?.Invoke("owner");
        var owner = await client.GetOwnerAsync(nodeNum, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(owner.LongName))
            profile.LongName = owner.LongName;
        if (!string.IsNullOrEmpty(owner.ShortName))
            profile.ShortName = owner.ShortName;

        var config = new LocalConfig();
        progress?.Invoke("device config");
        config.Device = (await client.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.DeviceConfig, ct).ConfigureAwait(false)).Device?.Clone() ?? new Config.Types.DeviceConfig();
        progress?.Invoke("position config");
        config.Position = (await client.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.PositionConfig, ct).ConfigureAwait(false)).Position?.Clone() ?? new Config.Types.PositionConfig();
        progress?.Invoke("power config");
        config.Power = (await client.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.PowerConfig, ct).ConfigureAwait(false)).Power?.Clone() ?? new Config.Types.PowerConfig();
        progress?.Invoke("network config");
        config.Network = (await client.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.NetworkConfig, ct).ConfigureAwait(false)).Network?.Clone() ?? new Config.Types.NetworkConfig();
        progress?.Invoke("display config");
        config.Display = (await client.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.DisplayConfig, ct).ConfigureAwait(false)).Display?.Clone() ?? new Config.Types.DisplayConfig();
        progress?.Invoke("LoRa config");
        config.Lora = (await client.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.LoraConfig, ct).ConfigureAwait(false)).Lora?.Clone() ?? new Config.Types.LoRaConfig();
        progress?.Invoke("bluetooth config");
        config.Bluetooth = (await client.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.BluetoothConfig, ct).ConfigureAwait(false)).Bluetooth?.Clone() ?? new Config.Types.BluetoothConfig();
        progress?.Invoke("security config");
        config.Security = (await client.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.SecurityConfig, ct).ConfigureAwait(false)).Security?.Clone() ?? new Config.Types.SecurityConfig();
        profile.Config = config;

        var modules = new LocalModuleConfig();
        progress?.Invoke("MQTT module");
        modules.Mqtt = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.MqttConfig, ct).ConfigureAwait(false)).Mqtt?.Clone() ?? new ModuleConfig.Types.MQTTConfig();
        progress?.Invoke("serial module");
        modules.Serial = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.SerialConfig, ct).ConfigureAwait(false)).Serial?.Clone() ?? new ModuleConfig.Types.SerialConfig();
        progress?.Invoke("external notification module");
        modules.ExternalNotification = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.ExtnotifConfig, ct).ConfigureAwait(false)).ExternalNotification?.Clone() ?? new ModuleConfig.Types.ExternalNotificationConfig();
        progress?.Invoke("store & forward module");
        modules.StoreForward = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.StoreforwardConfig, ct).ConfigureAwait(false)).StoreForward?.Clone() ?? new ModuleConfig.Types.StoreForwardConfig();
        progress?.Invoke("range test module");
        modules.RangeTest = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.RangetestConfig, ct).ConfigureAwait(false)).RangeTest?.Clone() ?? new ModuleConfig.Types.RangeTestConfig();
        progress?.Invoke("telemetry module");
        modules.Telemetry = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.TelemetryConfig, ct).ConfigureAwait(false)).Telemetry?.Clone() ?? new ModuleConfig.Types.TelemetryConfig();
        progress?.Invoke("canned messages module");
        modules.CannedMessage = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.CannedmsgConfig, ct).ConfigureAwait(false)).CannedMessage?.Clone() ?? new ModuleConfig.Types.CannedMessageConfig();
        progress?.Invoke("detection sensor module");
        modules.DetectionSensor = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.DetectionsensorConfig, ct).ConfigureAwait(false)).DetectionSensor?.Clone() ?? new ModuleConfig.Types.DetectionSensorConfig();
        progress?.Invoke("PAX counter module");
        modules.Paxcounter = (await client.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.PaxcounterConfig, ct).ConfigureAwait(false)).Paxcounter?.Clone() ?? new ModuleConfig.Types.PaxcounterConfig();
        profile.ModuleConfig = modules;

        progress?.Invoke("channels");
        var channels = await client.GetChannelsAsync(nodeNum, 8, treatTimeoutAsDisabled: false, ct).ConfigureAwait(false);
        var channelSet = new ChannelSet { LoraConfig = config.Lora.Clone() };
        foreach (var channel in channels.OrderBy(c => c.Index))
        {
            if (channel.Role != Channel.Types.Role.Disabled && channel.Settings is not null)
                channelSet.Settings.Add(channel.Settings.Clone());
        }
        profile.ChannelUrl = ChannelUrlUtil.BuildShareUrl(channelSet);

        progress?.Invoke("ringtone");
        var ringtone = await client.GetRingtoneAsync(nodeNum, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(ringtone))
            profile.Ringtone = ringtone;

        progress?.Invoke("canned messages text");
        var cannedMessages = await client.GetCannedMessageModuleMessagesAsync(nodeNum, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(cannedMessages))
            profile.CannedMessages = cannedMessages;

        return profile;
    }

    /// <summary>
    /// Stages every section present in the profile as one settings batch, in the
    /// same safe order as the JSON import: reboot-triggering sections (security,
    /// bluetooth, network, device, LoRa) last.
    /// </summary>
    public static SettingsWriteBatch BuildApplyBatch(DeviceProfile profile)
    {
        if (profile is null)
            throw new ArgumentNullException(nameof(profile));

        var batch = new SettingsWriteBatch();

        if (profile.HasLongName || profile.HasShortName)
        {
            var owner = new User();
            if (profile.HasLongName)
                owner.LongName = profile.LongName;
            if (profile.HasShortName)
                owner.ShortName = profile.ShortName;
            batch.AddOwner("Owner names", owner);
        }

        var modules = profile.ModuleConfig;
        if (modules is not null)
        {
            if (modules.Mqtt is not null)
                batch.AddModuleConfig("MQTT config", new ModuleConfig { Mqtt = modules.Mqtt.Clone() });
            if (modules.Serial is not null)
                batch.AddModuleConfig("Serial config", new ModuleConfig { Serial = modules.Serial.Clone() });
            if (modules.ExternalNotification is not null)
                batch.AddModuleConfig("External notification config", new ModuleConfig { ExternalNotification = modules.ExternalNotification.Clone() });
            if (modules.StoreForward is not null)
                batch.AddModuleConfig("Store & forward config", new ModuleConfig { StoreForward = modules.StoreForward.Clone() });
            if (modules.RangeTest is not null)
                batch.AddModuleConfig("Range test config", new ModuleConfig { RangeTest = modules.RangeTest.Clone() });
            if (modules.Telemetry is not null)
                batch.AddModuleConfig("Telemetry config", new ModuleConfig { Telemetry = modules.Telemetry.Clone() });
            if (modules.CannedMessage is not null)
                batch.AddModuleConfig("Canned messages config", new ModuleConfig { CannedMessage = modules.CannedMessage.Clone() });
            if (modules.DetectionSensor is not null)
                batch.AddModuleConfig("Detection sensor config", new ModuleConfig { DetectionSensor = modules.DetectionSensor.Clone() });
            if (modules.Paxcounter is not null)
                batch.AddModuleConfig("PAX counter config", new ModuleConfig { Paxcounter = modules.Paxcounter.Clone() });
        }

        if (profile.HasCannedMessages)
            batch.AddCannedMessages("Canned messages text", profile.CannedMessages);
        if (profile.HasRingtone)
            batch.AddRingtone("Ringtone text", profile.Ringtone);

        var config = profile.Config;
        if (config is not null)
        {
            if (config.Display is not null)
                batch.AddConfig("Display config", new Config { Display = config.Display.Clone() });
            if (config.Position is not null)
                batch.AddConfig("Position config", new Config { Position = config.Position.Clone() });
            if (config.Power is not null)
                batch.AddConfig("Power config", new Config { Power = config.Power.Clone() });
        }

        if (profile.HasChannelUrl && ChannelUrlUtil.TryParseShareUrl(profile.ChannelUrl, out var channelSet, out _))
        {
            foreach (var channel in ChannelUrlUtil.ToReplacementChannels(channelSet.Settings))
                batch.AddChannel($"Channel {channel.Index}", channel);
        }

        if (config is not null)
        {
            if (config.Security is not null)
                batch.AddConfig("Security config", new Config { Security = config.Security.Clone() });
            if (config.Bluetooth is not null)
                batch.AddConfig("Bluetooth config", new Config { Bluetooth = config.Bluetooth.Clone() });
            if (config.Network is not null)
                batch.AddConfig("Network config", new Config { Network = config.Network.Clone() });
            if (config.Device is not null)
                batch.AddConfig("Device config", new Config { Device = config.Device.Clone() });
            if (config.Lora is not null)
                batch.AddConfig("LoRa config", new Config { Lora = config.Lora.Clone() });
        }

        return batch;
    }

    public static string DescribeProfile(DeviceProfile profile)
    {
        var channelCount = 0;
        if (profile.HasChannelUrl && ChannelUrlUtil.TryParseShareUrl(profile.ChannelUrl, out var channelSet, out _))
            channelCount = channelSet.Settings.Count;

        var parts = new System.Collections.Generic.List<string>();
        if (profile.HasLongName || profile.HasShortName)
            parts.Add($"Owner: {profile.LongName} ({profile.ShortName})");
        if (channelCount > 0)
            parts.Add($"Channels: {channelCount}");
        if (profile.Config is not null)
            parts.Add("Device configuration: yes" + (profile.Config.Security is not null ? " (includes security keys)" : string.Empty));
        if (profile.ModuleConfig is not null)
            parts.Add("Module configuration: yes");
        if (profile.HasRingtone)
            parts.Add("Ringtone: yes");
        if (profile.HasCannedMessages)
            parts.Add("Canned messages: yes");

        return parts.Count == 0 ? "Profile appears to be empty." : string.Join('\n', parts);
    }
}
