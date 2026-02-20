using Google.Protobuf;
using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;
using ZXing;
using ZXing.Common;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsRadioShareQrPage : Page
{
    private Config.Types.LoRaConfig? _lora;
    private readonly Dictionary<int, Channel> _channelsByIndex = new();
    private bool _isLoading;
    private ChannelSet? _loadedChannelSet;

    public SettingsRadioShareQrPage()
    {
        InitializeComponent();
        Loaded += SettingsRadioShareQrPage_Loaded;
    }

    private async void SettingsRadioShareQrPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        _channelsByIndex.Clear();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to generate share QR URL.";
            ShareUrlBox.Text = "";
            _isLoading = false;
            return;
        }

        try
        {
            StatusText.Text = "Loading channel set...";

            var cfg = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.LoraConfig);
            _lora = cfg.Lora?.Clone() ?? new Config.Types.LoRaConfig();

            var channels = await AdminConfigClient.Instance.GetChannelsAsync(nodeNum, maxChannels: 8);
            foreach (var ch in channels)
            {
                if (ch.Index < 0 || ch.Index > 7)
                    continue;
                _channelsByIndex[ch.Index] = ch;
            }

            Channel0Check.IsChecked = _channelsByIndex.ContainsKey(0);
            Channel1Check.IsChecked = _channelsByIndex.ContainsKey(1);
            Channel2Check.IsChecked = _channelsByIndex.ContainsKey(2);
            Channel3Check.IsChecked = _channelsByIndex.ContainsKey(3);
            Channel4Check.IsChecked = _channelsByIndex.ContainsKey(4);
            Channel5Check.IsChecked = _channelsByIndex.ContainsKey(5);
            Channel6Check.IsChecked = _channelsByIndex.ContainsKey(6);
            Channel7Check.IsChecked = _channelsByIndex.ContainsKey(7);

            await RebuildShareUrlAsync();
            StatusText.Text = $"Generated URL from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load share data: " + ex.Message;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded)
            return;
        await RebuildShareUrlAsync();
    }

    private async Task RebuildShareUrlAsync()
    {
        try
        {
            var channelSet = new ChannelSet();
            if (_lora is not null)
                channelSet.LoraConfig = _lora.Clone();

            foreach (var index in SelectedIndexes())
            {
                if (!_channelsByIndex.TryGetValue(index, out var ch))
                    continue;

                if (ch.Role == Channel.Types.Role.Disabled || ch.Settings is null)
                    continue;

                channelSet.Settings.Add(ch.Settings.Clone());
            }

            var settingsString = channelSet.ToByteArray();
            var base64 = Convert.ToBase64String(settingsString);
            var base64url = ToBase64Url(base64);
            var addPart = ReplaceChannelsCheck.IsChecked == true ? string.Empty : "?add=true";
            ShareUrlBox.Text = $"https://meshtastic.org/e/{addPart}#{base64url}";
            await UpdateQrPreviewAsync(ShareUrlBox.Text);
        }
        catch (Exception ex)
        {
            ShareUrlBox.Text = "";
            QrImage.Source = null;
            StatusText.Text = "Failed to generate QR preview: " + ex.Message;
        }
    }

    private IEnumerable<int> SelectedIndexes()
    {
        if (Channel0Check is null ||
            Channel1Check is null ||
            Channel2Check is null ||
            Channel3Check is null ||
            Channel4Check is null ||
            Channel5Check is null ||
            Channel6Check is null ||
            Channel7Check is null)
            yield break;

        if (Channel0Check.IsChecked == true) yield return 0;
        if (Channel1Check.IsChecked == true) yield return 1;
        if (Channel2Check.IsChecked == true) yield return 2;
        if (Channel3Check.IsChecked == true) yield return 3;
        if (Channel4Check.IsChecked == true) yield return 4;
        if (Channel5Check.IsChecked == true) yield return 5;
        if (Channel6Check.IsChecked == true) yield return 6;
        if (Channel7Check.IsChecked == true) yield return 7;
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = ShareUrlBox.Text ?? "";
        if (!ClipboardUtil.TrySetText(url, flush: true))
        {
            StatusText.Text = "Could not copy URL to clipboard.";
            return;
        }

        StatusText.Text = "Share URL copied to clipboard.";
    }

    private async void LoadQrImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");

            if (App.MainWindowInstance is null)
            {
                StatusText.Text = "Window not available for file picker.";
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            var qrText = await DecodeQrTextFromFileAsync(file);
            if (string.IsNullOrWhiteSpace(qrText))
            {
                StatusText.Text = "No valid QR code found in image.";
                return;
            }

            if (!TryParseChannelSetFromQrText(qrText, out var parsed, out var addMode))
            {
                StatusText.Text = "QR code does not contain a valid Meshtastic channel set.";
                return;
            }

            _loadedChannelSet = parsed;
            ReplaceChannelsCheck.IsChecked = !addMode;
            StatusText.Text = $"Loaded QR: {parsed.Settings.Count} channels, ready to apply.";
            ShareUrlBox.Text = qrText;
            await UpdateQrPreviewAsync(qrText);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load QR image: " + ex.Message;
        }
    }

    private async void ApplyLoadedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedChannelSet is null)
        {
            StatusText.Text = "Load a QR image first.";
            return;
        }

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node before applying QR settings.";
            return;
        }

        try
        {
            StatusText.Text = "Applying QR settings to connected node...";

            if (_loadedChannelSet.LoraConfig is not null)
            {
                var config = new Config { Lora = _loadedChannelSet.LoraConfig.Clone() };
                await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, config);
            }

            var baseChannels = await AdminConfigClient.Instance.GetChannelsAsync(nodeNum, maxChannels: 8);
            var merged = MergeChannels(baseChannels, _loadedChannelSet.Settings, replaceChannels: ReplaceChannelsCheck.IsChecked == true);
            await AdminConfigClient.Instance.SaveChannelsAsync(nodeNum, merged);

            StatusText.Text = "QR settings applied and saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(
                text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(
                    text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "QR settings applied. Reconnected."
                    : "QR settings may be applied, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to apply QR settings: " + ex.Message;
        }
    }

    private static List<Channel> MergeChannels(IReadOnlyList<Channel> current, IEnumerable<ChannelSettings> loadedSettings, bool replaceChannels)
    {
        var result = current
            .Select(c => c?.Clone() ?? new Channel())
            .OrderBy(c => c.Index)
            .ToList();

        while (result.Count < 8)
            result.Add(new Channel { Index = result.Count, Role = Channel.Types.Role.Disabled });

        if (replaceChannels)
        {
            for (var i = 0; i < 8; i++)
            {
                result[i] = new Channel { Index = i, Role = Channel.Types.Role.Disabled };
            }

            var settingsList = loadedSettings?.ToList() ?? new List<ChannelSettings>();
            for (var i = 0; i < settingsList.Count && i < 8; i++)
            {
                result[i] = new Channel
                {
                    Index = i,
                    Role = i == 0 ? Channel.Types.Role.Primary : Channel.Types.Role.Secondary,
                    Settings = settingsList[i].Clone()
                };
            }

            return result;
        }

        var incoming = loadedSettings?.ToList() ?? new List<ChannelSettings>();
        foreach (var setting in incoming)
        {
            var idx = result.FindIndex(c => c.Role == Channel.Types.Role.Disabled);
            if (idx < 0)
                break;

            var role = idx == 0 ? Channel.Types.Role.Primary : Channel.Types.Role.Secondary;
            result[idx] = new Channel
            {
                Index = idx,
                Role = role,
                Settings = setting.Clone()
            };
        }

        return result;
    }

    private static bool TryParseChannelSetFromQrText(string text, out ChannelSet channelSet, out bool addMode)
    {
        channelSet = new ChannelSet();
        addMode = false;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var source = text.Trim();
        string payload;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            payload = uri.Fragment?.StartsWith("#") == true ? uri.Fragment.Substring(1) : "";
            var query = uri.Query ?? "";
            addMode = query.IndexOf("add=true", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        else
        {
            var hash = source.LastIndexOf('#');
            payload = hash >= 0 ? source[(hash + 1)..] : source;
        }

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            var normalized = FromBase64Url(payload);
            var data = Convert.FromBase64String(normalized);
            channelSet = ChannelSet.Parser.ParseFrom(data);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task UpdateQrPreviewAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            QrImage.Source = null;
            return;
        }

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            var qrCode = new PngByteQRCode(data);
            var pngBytes = qrCode.GetGraphic(12);
            QrImage.Source = await CreateBitmapImageFromPngBytesAsync(pngBytes);
            return;
        }
        catch
        {
            // Fallback to pixel renderer on systems where PNG decode path is unavailable.
        }

        QrImage.Source = await CreateWriteableBitmapFromQrTextAsync(text);
    }

    private static async Task<BitmapImage> CreateBitmapImageFromPngBytesAsync(byte[] pngBytes)
    {
        var bitmap = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static async Task<WriteableBitmap> CreateWriteableBitmapFromQrTextAsync(string text)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 300,
                Height = 300,
                Margin = 1
            }
        };

        var pixelData = writer.Write(text);
        var bitmap = new WriteableBitmap(pixelData.Width, pixelData.Height);
        using var pixelStream = bitmap.PixelBuffer.AsStream();
        await pixelStream.WriteAsync(pixelData.Pixels, 0, pixelData.Pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private static async Task<string?> DecodeQrTextFromFileAsync(StorageFile file)
    {
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = pixelData.DetachPixelData();
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var result = reader.Decode(source);
        return result?.Text;
    }

    private static string ToBase64Url(string base64)
    {
        return (base64 ?? string.Empty)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string FromBase64Url(string base64Url)
    {
        var value = (base64Url ?? string.Empty)
            .Replace('-', '+')
            .Replace('_', '/');

        var pad = value.Length % 4;
        if (pad > 0)
            value = value.PadRight(value.Length + (4 - pad), '=');

        return value;
    }
}
