using Meshtastic.Core;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace MeshtasticWin.Services;

public sealed class BluetoothLeTransport : IRadioTransport
{
    // Some Meshtastic BLE firmwares/devices throw COMException on CCCD writes.
    // Polling mode avoids that failure path and keeps connection stable.
    private static readonly bool PreferPollingMode = false;

    private static readonly Guid MeshtasticServiceUuid = new("6ba1b218-15a8-461f-9fa8-5dcae273eafd");
    private static readonly Guid ToRadioUuid = new("f75c76d2-129e-4dad-a1dd-7866124401e7");
    private static readonly Guid FromRadioUuid = new("2c55e69e-4993-11ed-b878-0242ac120002");
    private static readonly Guid FromNumUuid = new("ed9da18c-a800-4f66-a670-aa7547e34453");
    private static readonly Guid LegacyFromRadioUuid = new("8ba2bcc2-ee02-4a55-a531-c525c5e454d5");

    private readonly string _deviceId;
    private readonly object _sync = new();

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _toRadio;
    private GattCharacteristic? _fromRadio;
    private GattCharacteristic? _fromNum;
    private GattCharacteristic? _notifyCharacteristic;
    private readonly SemaphoreSlim _drainLock = new(1, 1);
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private uint _lastFromNum;
    private int _isDisconnecting;

    public event Action<string>? Log;
    public event Action<byte[]>? BytesReceived;

    public bool IsConnected => _device is not null && _toRadio is not null && _fromRadio is not null;

    public BluetoothLeTransport(string deviceId)
    {
        _deviceId = deviceId;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return;

        Interlocked.Exchange(ref _isDisconnecting, 0);

        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        GattCharacteristic? toRadio = null;
        GattCharacteristic? fromRadio = null;
        GattCharacteristic? fromNum = null;
        GattCharacteristic? notifyCharacteristic = null;

        try
        {
            device = await BluetoothLEDevice.FromIdAsync(_deviceId);
            if (device is null)
                throw new InvalidOperationException("Bluetooth device not found.");

            service = await TryGetMeshtasticServiceAsync(device);
            if (service is null)
                throw new InvalidOperationException("Meshtastic BLE service not found.");

            toRadio = await TryGetCharacteristicAsync(service, ToRadioUuid);
            fromRadio = await TryGetCharacteristicAsync(service, FromRadioUuid)
                ?? await TryGetCharacteristicAsync(service, LegacyFromRadioUuid);
            fromNum = await TryGetCharacteristicAsync(service, FromNumUuid);

            if (toRadio is null)
            {
                throw new InvalidOperationException(
                    $"Meshtastic ToRadio characteristic not found. Available chars: {await DescribeCharacteristicsAsync(service)}");
            }

            if (fromRadio is null)
            {
                throw new InvalidOperationException(
                    $"Meshtastic FromRadio characteristic not found. Available chars: {await DescribeCharacteristicsAsync(service)}");
            }

            if (PreferPollingMode)
            {
                Log?.Invoke("BLE: using polling mode (notifications disabled for compatibility).");
            }
            else
            {
                await TryEnsurePairedAsync(_deviceId).ConfigureAwait(false);

                var notifyCandidates = GetNotifyCandidates(fromRadio, fromNum);
                string? notifyError = null;
                foreach (var candidate in notifyCandidates)
                {
                    candidate.ValueChanged += Notify_ValueChanged;
                    try
                    {
                        var enableError = await EnableNotificationsAsync(candidate);
                        if (enableError is not null && IsGattPermissionError(enableError))
                        {
                            var pairedNow = await TryEnsurePairedAsync(_deviceId).ConfigureAwait(false);
                            if (pairedNow)
                                enableError = await EnableNotificationsAsync(candidate).ConfigureAwait(false);
                        }

                        if (enableError is null)
                        {
                            notifyCharacteristic = candidate;
                            Log?.Invoke($"BLE: notifications enabled on {candidate.Uuid:D}");
                            break;
                        }

                        notifyError = $"{candidate.Uuid:D}: {enableError}";
                    }
                    catch (Exception ex)
                    {
                        notifyError = $"{candidate.Uuid:D}: {ex.Message}";
                        try { candidate.ValueChanged -= Notify_ValueChanged; }
                        catch (ObjectDisposedException) { }
                        catch (NullReferenceException) { }
                        catch (IOException) { }
                        catch (COMException) { }
                        continue;
                    }

                    try { candidate.ValueChanged -= Notify_ValueChanged; }
                    catch (ObjectDisposedException) { }
                    catch (NullReferenceException) { }
                    catch (IOException) { }
                    catch (COMException) { }
                }

                if (notifyCharacteristic is null)
                {
                    if (notifyCandidates.Count == 0)
                        Log?.Invoke("BLE: no Notify/Indicate characteristic found; using polling fallback.");
                    else
                        Log?.Invoke($"BLE: unable to enable notifications; using polling fallback. {notifyError}");
                }
            }

            lock (_sync)
            {
                _device = device;
                _service = service;
                _toRadio = toRadio;
                _fromRadio = fromRadio;
                _fromNum = fromNum;
                _notifyCharacteristic = notifyCharacteristic;
                _pollCts = new CancellationTokenSource();
            }

            Log?.Invoke($"Connected to Bluetooth LE {device.Name}");
            if (fromNum is not null)
            {
                var start = await TryReadFromNumAsync(fromNum);
                if (start.HasValue)
                    _lastFromNum = start.Value;
            }
            _ = DrainFromRadioMailboxAsync(null);
            _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }
        catch
        {
            if (notifyCharacteristic is not null)
            {
                try { notifyCharacteristic.ValueChanged -= Notify_ValueChanged; }
                catch (ObjectDisposedException) { }
                catch (NullReferenceException) { }
                catch (IOException) { }
                catch (COMException) { }
            }
            try { service?.Dispose(); }
            catch (ObjectDisposedException) { }
            catch (NullReferenceException) { }
            catch (IOException) { }
            catch (COMException) { }

            try { device?.Dispose(); }
            catch (ObjectDisposedException) { }
            catch (NullReferenceException) { }
            catch (IOException) { }
            catch (COMException) { }
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        BluetoothLEDevice? device;
        GattDeviceService? service;
        GattCharacteristic? notifyCharacteristic;
        CancellationTokenSource? pollCts;
        Task? pollTask;

        lock (_sync)
        {
            device = _device;
            service = _service;
            notifyCharacteristic = _notifyCharacteristic;
            pollCts = _pollCts;
            pollTask = _pollTask;

            _device = null;
            _service = null;
            _toRadio = null;
            _fromRadio = null;
            _fromNum = null;
            _notifyCharacteristic = null;
            _pollCts = null;
            _pollTask = null;
        }

        if (device is null)
            return;

        Interlocked.Exchange(ref _isDisconnecting, 1);
        try { pollCts?.Cancel(); } catch { }

        if (pollTask is not null)
        {
            try { await pollTask.ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (NullReferenceException) { }
            catch (IOException) { }
            catch (COMException) { }
            catch (OperationCanceledException) { }
        }

        if (notifyCharacteristic is not null)
        {
            try
            {
                notifyCharacteristic.ValueChanged -= Notify_ValueChanged;
            }
            catch (ObjectDisposedException) { }
            catch (NullReferenceException) { }
            catch (IOException) { }
            catch (COMException) { }
        }

        try { service?.Dispose(); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try { pollCts?.Dispose(); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try { device.Dispose(); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        Log?.Invoke("Disconnected");
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _isDisconnecting) != 0)
            return;

        var toRadio = _toRadio;
        if (toRadio is null)
            throw new InvalidOperationException("Not connected");

        var payload = TryExtractFramedPayload(data);
        using var writer = new DataWriter();
        writer.WriteBytes(payload);
        var buffer = writer.DetachBuffer();

        try
        {
            var result = await toRadio.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithoutResponse);
            if (result.Status != GattCommunicationStatus.Success)
            {
                var fallback = await toRadio.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse);
                if (fallback.Status != GattCommunicationStatus.Success)
                    throw new IOException("Bluetooth write failed.");
            }
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _isDisconnecting) != 0) { return; }
        catch (NullReferenceException) when (Volatile.Read(ref _isDisconnecting) != 0) { return; }
        catch (IOException) when (Volatile.Read(ref _isDisconnecting) != 0) { return; }

        Log?.Invoke($"TX {payload.Length} bytes (BLE)");
    }

    private void Notify_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (Volatile.Read(ref _isDisconnecting) != 0)
            return;

        if ((sender.Uuid == FromRadioUuid || sender.Uuid == LegacyFromRadioUuid) &&
            args.CharacteristicValue is not null)
        {
            try
            {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                if (reader.UnconsumedBufferLength > 0)
                {
                    var payload = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(payload);
                    EmitFromRadioPayload(payload);
                    return;
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (NullReferenceException) { return; }
            catch (IOException) { return; }
            catch (COMException) { return; }
        }

        if (sender.Uuid == FromNumUuid && args.CharacteristicValue is not null)
        {
            try
            {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                if (reader.UnconsumedBufferLength >= 4)
                {
                    var target = reader.ReadUInt32();
                    _ = DrainFromRadioMailboxAsync(target);
                    return;
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (NullReferenceException) { return; }
            catch (IOException) { return; }
            catch (COMException) { return; }
        }

        _ = DrainFromRadioMailboxAsync(null);
    }

    private void EmitFromRadioPayload(byte[] payload)
    {
        if (payload.Length == 0 || Volatile.Read(ref _isDisconnecting) != 0)
            return;

        var framed = FrameMeshtasticPayload(payload);
        BytesReceived?.Invoke(framed);
    }

    private static byte[] TryExtractFramedPayload(byte[] data)
    {
        if (data.Length >= 4 &&
            data[0] == 0x94 &&
            data[1] == 0xC3)
        {
            int length = (data[2] << 8) | data[3];
            if (length > 0 && length == data.Length - 4)
            {
                var payload = new byte[length];
                System.Buffer.BlockCopy(data, 4, payload, 0, length);
                return payload;
            }
        }

        return data;
    }

    private static byte[] FrameMeshtasticPayload(byte[] payload)
    {
        if (payload.Length > 65535)
            throw new InvalidOperationException("Payload too large for Meshtastic framing.");

        var framed = new byte[payload.Length + 4];
        framed[0] = 0x94;
        framed[1] = 0xC3;
        framed[2] = (byte)((payload.Length >> 8) & 0xFF);
        framed[3] = (byte)(payload.Length & 0xFF);
        System.Buffer.BlockCopy(payload, 0, framed, 4, payload.Length);
        return framed;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    private static async Task<GattDeviceService?> TryGetMeshtasticServiceAsync(BluetoothLEDevice device)
    {
        var byUuidCached = await device.GetGattServicesForUuidAsync(MeshtasticServiceUuid, BluetoothCacheMode.Cached);
        if (byUuidCached.Status == GattCommunicationStatus.Success && byUuidCached.Services.Count > 0)
            return byUuidCached.Services[0];

        var byUuidUncached = await device.GetGattServicesForUuidAsync(MeshtasticServiceUuid, BluetoothCacheMode.Uncached);
        if (byUuidUncached.Status == GattCommunicationStatus.Success && byUuidUncached.Services.Count > 0)
            return byUuidUncached.Services[0];

        var cached = await device.GetGattServicesAsync(BluetoothCacheMode.Cached);
        if (cached.Status == GattCommunicationStatus.Success && cached.Services.Count > 0)
        {
            var match = cached.Services.FirstOrDefault(s => s.Uuid == MeshtasticServiceUuid);
            if (match is not null)
                return match;
        }

        var uncached = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (uncached.Status == GattCommunicationStatus.Success && uncached.Services.Count > 0)
        {
            var match = uncached.Services.FirstOrDefault(s => s.Uuid == MeshtasticServiceUuid);
            if (match is not null)
                return match;
        }

        return null;
    }

    private static async Task<GattCharacteristic?> TryGetCharacteristicAsync(GattDeviceService service, Guid uuid)
    {
        try
        {
            var uncachedByUuid = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
            if (uncachedByUuid.Status == GattCommunicationStatus.Success && uncachedByUuid.Characteristics.Count > 0)
                return uncachedByUuid.Characteristics[0];
        }
        catch (COMException) { }

        try
        {
            var cachedByUuid = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Cached);
            if (cachedByUuid.Status == GattCommunicationStatus.Success && cachedByUuid.Characteristics.Count > 0)
                return cachedByUuid.Characteristics[0];
        }
        catch (COMException) { }

        try
        {
            var uncachedAll = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (uncachedAll.Status == GattCommunicationStatus.Success)
            {
                var match = uncachedAll.Characteristics.FirstOrDefault(c => c.Uuid == uuid);
                if (match is not null)
                    return match;
            }
        }
        catch (COMException) { }

        try
        {
            var cachedAll = await service.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
            if (cachedAll.Status == GattCommunicationStatus.Success)
            {
                var match = cachedAll.Characteristics.FirstOrDefault(c => c.Uuid == uuid);
                if (match is not null)
                    return match;
            }
        }
        catch (COMException) { }

        return null;
    }

    private static async Task<string> DescribeCharacteristicsAsync(GattDeviceService service)
    {
        try
        {
            var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success)
                return $"status={chars.Status}";

            var list = chars.Characteristics.Select(c => c.Uuid.ToString("D")).ToList();
            return list.Count == 0 ? "(none)" : string.Join(", ", list);
        }
        catch (COMException ex)
        {
            return $"COMException 0x{ex.HResult:X8}";
        }
    }

    private static System.Collections.Generic.List<GattCharacteristic> GetNotifyCandidates(
        GattCharacteristic? fromRadio,
        GattCharacteristic? fromNum)
    {
        var candidates = new System.Collections.Generic.List<GattCharacteristic>(2);
        // Prefer FromRadio first, then FromNum fallback.

        if (fromRadio is not null)
        {
            candidates.Add(fromRadio);
        }

        if (fromNum is not null && !candidates.Any(c => c.Uuid == fromNum.Uuid))
        {
            candidates.Add(fromNum);
        }

        return candidates;
    }

    private async Task DrainFromRadioMailboxAsync(uint? targetFromNum)
    {
        var fromRadio = _fromRadio;
        if (fromRadio is null || Volatile.Read(ref _isDisconnecting) != 0)
            return;

        await _drainLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (targetFromNum.HasValue)
            {
                while (Volatile.Read(ref _isDisconnecting) == 0 && _lastFromNum != targetFromNum.Value)
                {
                    if (!await TryReadOneFromRadioAsync(fromRadio).ConfigureAwait(false))
                        break;

                    unchecked { _lastFromNum++; }
                }
                return;
            }

            while (Volatile.Read(ref _isDisconnecting) == 0)
            {
                if (!await TryReadOneFromRadioAsync(fromRadio).ConfigureAwait(false))
                    break;
            }
        }
        finally
        {
            _drainLock.Release();
        }
    }

    private async Task<bool> TryReadOneFromRadioAsync(GattCharacteristic fromRadio)
    {
        var readResult = await TryReadCharacteristicAsync(fromRadio, BluetoothCacheMode.Uncached).ConfigureAwait(false)
            ?? await TryReadCharacteristicAsync(fromRadio, BluetoothCacheMode.Cached).ConfigureAwait(false);

        if (readResult is null)
            return false;

        if (readResult.Status != GattCommunicationStatus.Success || readResult.Value is null)
            return false;

        var reader = DataReader.FromBuffer(readResult.Value);
        var payload = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(payload);

        if (payload.Length == 0)
            return false;

        if (Volatile.Read(ref _isDisconnecting) != 0)
            return false;

        EmitFromRadioPayload(payload);
        return true;
    }

    private static async Task<uint?> TryReadFromNumAsync(GattCharacteristic fromNum)
    {
        var result = await TryReadCharacteristicAsync(fromNum, BluetoothCacheMode.Uncached).ConfigureAwait(false)
            ?? await TryReadCharacteristicAsync(fromNum, BluetoothCacheMode.Cached).ConfigureAwait(false);

        if (result is null)
            return null;

        if (result.Status != GattCommunicationStatus.Success || result.Value is null)
            return null;

        var reader = DataReader.FromBuffer(result.Value);
        if (reader.UnconsumedBufferLength < 4)
            return null;

        return reader.ReadUInt32();
    }

    private static async Task<GattReadResult?> TryReadCharacteristicAsync(GattCharacteristic characteristic, BluetoothCacheMode cacheMode)
    {
        try
        {
            return await characteristic.ReadValueAsync(cacheMode);
        }
        catch (ObjectDisposedException) { return null; }
        catch (NullReferenceException) { return null; }
        catch (IOException) { return null; }
        catch (COMException) { return null; }
    }

    private static async Task<string?> EnableNotificationsAsync(GattCharacteristic characteristic)
    {
        var hasCccd = await HasCccdAsync(characteristic).ConfigureAwait(false);
        if (!hasCccd)
            return "Characteristic has no CCCD descriptor.";

        var props = characteristic.CharacteristicProperties;

        var tryNotify = (props & GattCharacteristicProperties.Notify) != 0;
        var tryIndicate = (props & GattCharacteristicProperties.Indicate) != 0;

        if (!tryNotify && !tryIndicate)
        {
            // Some BLE stacks/firmware report incomplete properties; probe both modes anyway.
            tryNotify = true;
            tryIndicate = true;
        }

        string? lastError = null;

        if (tryIndicate)
        {
            try
            {
                var indicateStatus = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Indicate);
                if (indicateStatus == GattCommunicationStatus.Success)
                    return null;

                lastError = $"Indicate status={indicateStatus}";
            }
            catch (Exception ex)
            {
                lastError = ex is COMException comEx
                    ? $"Indicate COMException 0x{comEx.HResult:X8}"
                    : $"Indicate {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (tryNotify)
        {
            try
            {
                var notifyStatus = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (notifyStatus == GattCommunicationStatus.Success)
                    return null;

                lastError = $"Notify status={notifyStatus}";
            }
            catch (Exception ex)
            {
                // Fallback to Notify after Indicate, some devices only accept one mode.
                lastError = ex is COMException comEx
                    ? $"Notify COMException 0x{comEx.HResult:X8}"
                    : $"Notify {ex.GetType().Name}: {ex.Message}";
            }
        }

        return lastError ?? "Unable to enable BLE notifications/indications.";
    }

    private static async Task<bool> HasCccdAsync(GattCharacteristic characteristic)
    {
        try
        {
            var result = await characteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
                return false;

            return result.Descriptors.Any(d => d.Uuid == GattDescriptorUuids.ClientCharacteristicConfiguration);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsGattPermissionError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        return error.IndexOf("0x80650005", StringComparison.OrdinalIgnoreCase) >= 0 ||
               error.IndexOf("0x80650003", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private async Task<bool> TryEnsurePairedAsync(string deviceId)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(deviceId);
            if (info?.Pairing is null)
                return false;

            if (info.Pairing.IsPaired)
                return true;

            if (!info.Pairing.CanPair)
                return false;

            DevicePairingResult pairResult;
            try
            {
                pairResult = await info.Pairing.PairAsync(DevicePairingProtectionLevel.Encryption);
            }
            catch
            {
                pairResult = await info.Pairing.PairAsync();
            }

            if (pairResult.Status == DevicePairingResultStatus.Paired ||
                pairResult.Status == DevicePairingResultStatus.AlreadyPaired)
            {
                Log?.Invoke("BLE: pairing completed.");
                return true;
            }

            Log?.Invoke($"BLE: pairing status={pairResult.Status}.");
            return false;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"BLE: pairing failed: {ex.Message}");
            return false;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _isDisconnecting) == 0)
        {
            var fromNum = _fromNum;
            if (fromNum is not null)
            {
                var currentFromNum = await TryReadFromNumAsync(fromNum).ConfigureAwait(false);
                if (currentFromNum.HasValue && currentFromNum.Value != _lastFromNum)
                {
                    await DrainFromRadioMailboxAsync(currentFromNum.Value).ConfigureAwait(false);
                }
                else
                {
                    await DrainFromRadioMailboxAsync(null).ConfigureAwait(false);
                }
            }
            else
            {
                await DrainFromRadioMailboxAsync(null).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
