using Google.Protobuf;
using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsImportExportPage : Page
{
    private const string SelectionPresetFolderName = "Selection";
    private const string LoraPresetEuStandard = "eu-standard";
    private const string LoraPresetNorwayAlternative = "no-alt-869618";

    private readonly List<SelectionPresetEntry> _selectionPresets = new();
    private readonly List<LoraFrequencyPresetEntry> _loraFrequencyPresets = new();

    public SettingsImportExportPage()
    {
        InitializeComponent();
        Loaded += SettingsImportExportPage_Loaded;
    }

    private void SettingsImportExportPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadLoraFrequencyPresets();
        StatusText.Text = "Ready.";
    }

    private void LoadPresets()
    {
        _selectionPresets.Clear();
        _selectionPresets.Add(new SelectionPresetEntry("Safe", BuiltInSelectionPresets.Safe, isBuiltIn: true, filePath: null));
        _selectionPresets.Add(new SelectionPresetEntry("Everything", BuiltInSelectionPresets.Everything, isBuiltIn: true, filePath: null));
        _selectionPresets.Add(new SelectionPresetEntry("Radio only", BuiltInSelectionPresets.RadioOnly, isBuiltIn: true, filePath: null));
        _selectionPresets.AddRange(LoadCustomSelectionPresets());

        SelectionPresetCombo.ItemsSource = _selectionPresets.Select(x => x.Name).ToList();
        SelectionPresetCombo.SelectedIndex = 0;
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTargetNode(out var nodeNum))
            return;

        if (!TryGetMainWindowHandle(out var hwnd))
        {
            StatusText.Text = "Window not available for file picker.";
            return;
        }

        var selection = BuildSelectionFromUi();
        if (!selection.AnySelected)
        {
            StatusText.Text = "Select at least one section.";
            return;
        }

        try
        {
            StatusText.Text = $"Exporting settings from node 0x{nodeNum:x8} (this can take time on remote nodes)...";
            SetEnabled(false);

            var backup = await BuildBackupAsync(nodeNum, selection);
            backup.Notes = NotesBox.Text?.Trim();

            var picker = new FileSavePicker
            {
                SuggestedFileName = $"meshtastic-config-{nodeNum:x8}-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".json"
            };
            picker.FileTypeChoices.Add("JSON file", new List<string> { ".json" });
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                StatusText.Text = "Export cancelled.";
                return;
            }

            var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
            StatusText.Text = $"Export complete: {file.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message;
        }
        finally
        {
            SetEnabled(true);
        }
    }

    private async void ImportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTargetNode(out var nodeNum))
            return;

        if (!TryGetMainWindowHandle(out var hwnd))
        {
            StatusText.Text = "Window not available for file picker.";
            return;
        }

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var backup = JsonSerializer.Deserialize<SettingsBackupFile>(json);
            if (backup is null)
            {
                StatusText.Text = "Invalid backup file.";
                return;
            }

            NotesBox.Text = backup.Notes ?? string.Empty;

            var pageSelection = BuildSelectionFromUi();
            var availableSelection = BuildAvailableSelection(backup);
            var defaultSelection = IntersectSelection(pageSelection, availableSelection);

            var selected = await ShowImportSelectionDialogAsync(backup, availableSelection, defaultSelection);
            if (selected is null)
            {
                StatusText.Text = "Import cancelled.";
                return;
            }

            if (!selected.AnySelected)
            {
                StatusText.Text = "Select at least one section.";
                return;
            }

            StatusText.Text = $"Applying selected sections to node 0x{nodeNum:x8} (this can take time on remote nodes)...";
            SetEnabled(false);
            await ApplyBackupAsync(nodeNum, backup, selected);
            StatusText.Text = "Import applied and saved.";
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var ok = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = ok ? "Import applied. Reconnected." : "Import may be applied, but reconnect failed.";
                return;
            }

            StatusText.Text = "Import failed: " + ex.Message;
        }
        finally
        {
            SetEnabled(true);
        }
    }

    private void LoadLoraFrequencyPresets()
    {
        _loraFrequencyPresets.Clear();
        _loraFrequencyPresets.Add(new LoraFrequencyPresetEntry(
            "EU standard frequency",
            LoraPresetEuStandard,
            "Use preset: true, Preset: LongFast, Region: EU_868, Override frequency: off."));
        _loraFrequencyPresets.Add(new LoraFrequencyPresetEntry(
            "Norway alternative frequency (869.618)",
            LoraPresetNorwayAlternative,
            "Use preset: false, Region: EU_868, Bandwidth: 62, Spread factor: 8, Coding rate: 5, Override frequency: 869.618."));

        LoraFrequencyPresetCombo.ItemsSource = _loraFrequencyPresets.Select(x => x.Name).ToList();
        LoraFrequencyPresetCombo.SelectedIndex = 0;
        UpdateLoraFrequencyPresetDetails();
    }

    private void LoraFrequencyPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLoraFrequencyPresetDetails();
    }

    private void UpdateLoraFrequencyPresetDetails()
    {
        var idx = LoraFrequencyPresetCombo.SelectedIndex;
        if (idx < 0 || idx >= _loraFrequencyPresets.Count)
        {
            LoraFrequencyPresetDetailsText.Text = string.Empty;
            ToolTipService.SetToolTip(LoraFrequencyPresetCombo, null);
            ToolTipService.SetToolTip(ApplyLoraFrequencyPresetButton, null);
            return;
        }

        var details = _loraFrequencyPresets[idx].Details;
        LoraFrequencyPresetDetailsText.Text = details;
        ToolTipService.SetToolTip(LoraFrequencyPresetCombo, details);
        ToolTipService.SetToolTip(ApplyLoraFrequencyPresetButton, details);
    }

    private async void ApplyLoraFrequencyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTargetNode(out var nodeNum))
            return;

        var idx = LoraFrequencyPresetCombo.SelectedIndex;
        if (idx < 0 || idx >= _loraFrequencyPresets.Count)
        {
            StatusText.Text = "Select a LoRa frequency preset first.";
            return;
        }

        var preset = _loraFrequencyPresets[idx];
        try
        {
            SetEnabled(false);
            StatusText.Text = $"Applying LoRa preset '{preset.Name}' to node 0x{nodeNum:x8}...";

            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.LoraConfig);
            var lora = config.Lora?.Clone() ?? new Config.Types.LoRaConfig();
            ApplyLoraFrequencyPreset(lora, preset.Key);

            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, new Config { Lora = lora });
            StatusText.Text = $"LoRa frequency preset applied: {preset.Name}.";

            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(
                text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var ok = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(
                    text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = ok
                    ? $"LoRa frequency preset applied: {preset.Name}. Reconnected."
                    : $"LoRa preset may be applied, but reconnect failed: {preset.Name}.";
                return;
            }

            StatusText.Text = "Failed to apply LoRa frequency preset: " + ex.Message;
        }
        finally
        {
            SetEnabled(true);
        }
    }

    private static void ApplyLoraFrequencyPreset(Config.Types.LoRaConfig lora, string presetKey)
    {
        var region = ParseEu868Region();
        lora.Region = region;

        switch (presetKey)
        {
            case LoraPresetEuStandard:
                lora.UsePreset = true;
                lora.ModemPreset = Config.Types.LoRaConfig.Types.ModemPreset.LongFast;
                lora.OverrideFrequency = 0f;
                break;

            case LoraPresetNorwayAlternative:
                lora.UsePreset = false;
                lora.ModemPreset = Config.Types.LoRaConfig.Types.ModemPreset.LongFast;
                lora.Bandwidth = 62;
                lora.SpreadFactor = 8;
                lora.CodingRate = 5;
                lora.OverrideFrequency = 869.618f;
                break;
        }
    }

    private static Config.Types.LoRaConfig.Types.RegionCode ParseEu868Region()
    {
        return Enum.TryParse<Config.Types.LoRaConfig.Types.RegionCode>("Eu868", ignoreCase: true, out var region)
            ? region
            : Config.Types.LoRaConfig.Types.RegionCode.Unset;
    }

    private async Task<BackupSelection?> ShowImportSelectionDialogAsync(SettingsBackupFile backup, BackupSelection available, BackupSelection selected)
    {
        CheckBox CreateOption(string label, string sectionKey, bool isAvailable, bool isSelected)
        {
            var cb = new CheckBox
            {
                Content = isAvailable ? label : $"{label} (not in file)",
                IsEnabled = isAvailable,
                IsChecked = isAvailable && isSelected,
                Margin = new Thickness(0, 2, 0, 2)
            };
            var preview = BuildSectionPreviewText(sectionKey, backup, isAvailable);
            AttachTooltip(cb, preview);
            return cb;
        }

        var cLora = CreateOption("LoRa config", "lora", available.IncludeLora, selected.IncludeLora);
        var cChannels = CreateOption("Channels", "channels", available.IncludeChannels, selected.IncludeChannels);
        var cSecurity = CreateOption("Security config", "security", available.IncludeSecurity, selected.IncludeSecurity);
        var cOwner = CreateOption("User / owner", "owner", available.IncludeOwner, selected.IncludeOwner);
        var cBluetooth = CreateOption("Bluetooth config", "bluetooth", available.IncludeBluetooth, selected.IncludeBluetooth);
        var cDevice = CreateOption("Device config", "device", available.IncludeDevice, selected.IncludeDevice);
        var cDisplay = CreateOption("Display config", "display", available.IncludeDisplay, selected.IncludeDisplay);
        var cNetwork = CreateOption("Network config", "network", available.IncludeNetwork, selected.IncludeNetwork);
        var cPosition = CreateOption("Position config", "position", available.IncludePosition, selected.IncludePosition);
        var cPower = CreateOption("Power config", "power", available.IncludePower, selected.IncludePower);
        var cCanned = CreateOption("Canned messages config", "module-canned", available.IncludeCannedModule, selected.IncludeCannedModule);
        var cCannedText = CreateOption("Canned messages text", "module-canned-text", available.IncludeCannedMessagesText, selected.IncludeCannedMessagesText);
        var cDetection = CreateOption("Detection sensor config", "module-detection", available.IncludeDetectionSensor, selected.IncludeDetectionSensor);
        var cExternal = CreateOption("External notification config", "module-external", available.IncludeExternalNotification, selected.IncludeExternalNotification);
        var cMqtt = CreateOption("MQTT config", "module-mqtt", available.IncludeMqtt, selected.IncludeMqtt);
        var cRange = CreateOption("Range test config", "module-range", available.IncludeRangeTest, selected.IncludeRangeTest);
        var cPax = CreateOption("PAX counter config", "module-pax", available.IncludePaxCounter, selected.IncludePaxCounter);
        var cRingtone = CreateOption("Ringtone text", "module-ringtone", available.IncludeRingtoneText, selected.IncludeRingtoneText);
        var cSerial = CreateOption("Serial config", "module-serial", available.IncludeSerial, selected.IncludeSerial);
        var cStoreForward = CreateOption("Store & forward config", "module-storeforward", available.IncludeStoreForward, selected.IncludeStoreForward);
        var cTelemetry = CreateOption("Telemetry config", "module-telemetry", available.IncludeTelemetry, selected.IncludeTelemetry);

        var allChecks = new[]
        {
            cLora, cChannels, cSecurity,
            cOwner, cBluetooth, cDevice, cDisplay, cNetwork, cPosition, cPower,
            cCanned, cCannedText, cDetection, cExternal, cMqtt, cRange, cPax, cRingtone, cSerial, cStoreForward, cTelemetry
        };

        var columns = new Grid { ColumnSpacing = 8 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var radioCol = new StackPanel { Spacing = 4 };
        radioCol.Children.Add(new TextBlock { Text = "Radio Configuration", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        radioCol.Children.Add(cLora);
        radioCol.Children.Add(cChannels);
        radioCol.Children.Add(cSecurity);
        Grid.SetColumn(radioCol, 0);
        columns.Children.Add(radioCol);

        var deviceCol = new StackPanel { Spacing = 4 };
        deviceCol.Children.Add(new TextBlock { Text = "Device Configuration", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        deviceCol.Children.Add(cOwner);
        deviceCol.Children.Add(cBluetooth);
        deviceCol.Children.Add(cDevice);
        deviceCol.Children.Add(cDisplay);
        deviceCol.Children.Add(cNetwork);
        deviceCol.Children.Add(cPosition);
        deviceCol.Children.Add(cPower);
        Grid.SetColumn(deviceCol, 1);
        columns.Children.Add(deviceCol);

        var moduleCol = new StackPanel { Spacing = 4 };
        moduleCol.Children.Add(new TextBlock { Text = "Module Configuration", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        moduleCol.Children.Add(cCanned);
        moduleCol.Children.Add(cCannedText);
        moduleCol.Children.Add(cDetection);
        moduleCol.Children.Add(cExternal);
        moduleCol.Children.Add(cMqtt);
        moduleCol.Children.Add(cRange);
        moduleCol.Children.Add(cPax);
        moduleCol.Children.Add(cRingtone);
        moduleCol.Children.Add(cSerial);
        moduleCol.Children.Add(cStoreForward);
        moduleCol.Children.Add(cTelemetry);
        Grid.SetColumn(moduleCol, 2);
        columns.Children.Add(moduleCol);

        var selectAllButton = new Button { Content = "Select all" };
        var selectNoneButton = new Button { Content = "Select none" };
        selectAllButton.Click += (_, __) =>
        {
            foreach (var cb in allChecks)
            {
                if (cb.IsEnabled)
                    cb.IsChecked = true;
            }
        };
        selectNoneButton.Click += (_, __) =>
        {
            foreach (var cb in allChecks)
            {
                if (cb.IsEnabled)
                    cb.IsChecked = false;
            }
        };

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actionRow.Children.Add(selectAllButton);
        actionRow.Children.Add(selectNoneButton);

        var optionsScroll = new ScrollViewer
        {
            Content = columns,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 560,
            MinHeight = 420,
            Width = 1000
        };

        var container = new StackPanel { Spacing = 8, Width = 1020, MinHeight = 620 };
        container.Children.Add(new TextBlock
        {
            Text = "Select exactly which sections to restore from this file.",
            TextWrapping = TextWrapping.Wrap
        });
        container.Children.Add(actionRow);
        container.Children.Add(optionsScroll);

        var dialog = new ContentDialog
        {
            Title = "Import section selection",
            PrimaryButtonText = "Restore selected",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = container
        };
        dialog.Resources["ContentDialogMinWidth"] = 1020d;
        dialog.Resources["ContentDialogMaxWidth"] = 1240d;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return null;

        return new BackupSelection
        {
            IncludeLora = cLora.IsChecked == true,
            IncludeChannels = cChannels.IsChecked == true,
            IncludeSecurity = cSecurity.IsChecked == true,
            IncludeOwner = cOwner.IsChecked == true,
            IncludeBluetooth = cBluetooth.IsChecked == true,
            IncludeDevice = cDevice.IsChecked == true,
            IncludeDisplay = cDisplay.IsChecked == true,
            IncludeNetwork = cNetwork.IsChecked == true,
            IncludePosition = cPosition.IsChecked == true,
            IncludePower = cPower.IsChecked == true,
            IncludeCannedModule = cCanned.IsChecked == true,
            IncludeCannedMessagesText = cCannedText.IsChecked == true,
            IncludeDetectionSensor = cDetection.IsChecked == true,
            IncludeExternalNotification = cExternal.IsChecked == true,
            IncludeMqtt = cMqtt.IsChecked == true,
            IncludeRangeTest = cRange.IsChecked == true,
            IncludePaxCounter = cPax.IsChecked == true,
            IncludeRingtoneText = cRingtone.IsChecked == true,
            IncludeSerial = cSerial.IsChecked == true,
            IncludeStoreForward = cStoreForward.IsChecked == true,
            IncludeTelemetry = cTelemetry.IsChecked == true
        };
    }


    private void ApplySelectionPreset_Click(object sender, RoutedEventArgs e)
    {
        var idx = SelectionPresetCombo.SelectedIndex;
        if (idx < 0 || idx >= _selectionPresets.Count)
            return;

        ApplySelectionPreset(_selectionPresets[idx].Selection);
        StatusText.Text = $"Selection preset applied: {_selectionPresets[idx].Name}";
    }

    private void ReloadPresets_Click(object sender, RoutedEventArgs e)
    {
        LoadPresets();
        StatusText.Text = "Presets reloaded.";
    }

    private void SaveSelectionPresetFile_Click(object sender, RoutedEventArgs e)
    {
        var name = (CustomPresetNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "Enter a custom preset name first.";
            return;
        }

        var safe = ToSafeFileName(name);
        var path = Path.Combine(GetPresetFolderPath(SelectionPresetFolderName), safe + ".json");
        var payload = new SelectionPresetFile
        {
            Name = name,
            Selection = BuildSelectionFromUi()
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        LoadPresets();
        StatusText.Text = $"Saved preset file: {Path.GetFileName(path)}";
    }

    private void DeleteSelectedSelectionPresetFile_Click(object sender, RoutedEventArgs e)
    {
        var idx = SelectionPresetCombo.SelectedIndex;
        if (idx < 0 || idx >= _selectionPresets.Count)
        {
            StatusText.Text = "Select a preset first.";
            return;
        }

        var preset = _selectionPresets[idx];
        if (preset.IsBuiltIn || string.IsNullOrWhiteSpace(preset.FilePath))
        {
            StatusText.Text = "Built-in presets cannot be deleted.";
            return;
        }

        try
        {
            File.Delete(preset.FilePath);
            LoadPresets();
            StatusText.Text = $"Deleted preset file: {Path.GetFileName(preset.FilePath)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to delete preset file: " + ex.Message;
        }
    }


    private void SelectionPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = SelectionPresetCombo.SelectedIndex;
        if (idx < 0 || idx >= _selectionPresets.Count)
            return;

        ApplySelectionPreset(_selectionPresets[idx].Selection);
        StatusText.Text = $"Selection preset applied: {_selectionPresets[idx].Name}";
    }

    private void SelectAllSections_Click(object sender, RoutedEventArgs e)
    {
        SetAllSectionChecks(true);
        StatusText.Text = "All sections selected.";
    }

    private void SelectNoSections_Click(object sender, RoutedEventArgs e)
    {
        SetAllSectionChecks(false);
        StatusText.Text = "All sections cleared.";
    }

    private async Task<SettingsBackupFile> BuildBackupAsync(uint nodeNum, BackupSelection selection)
    {
        var file = new SettingsBackupFile
        {
            Format = "MeshtasticWin.SettingsBackup.v1",
            CreatedUtc = DateTime.UtcNow,
            SourceNodeIdHex = $"0x{nodeNum:x8}",
            SourceNodeName = ResolveTargetNodeName()
        };

        if (selection.IncludeLora)
            file.LoraConfig = ToJsonElement(await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.LoraConfig));
        if (selection.IncludeChannels)
            file.Channels = (await AdminConfigClient.Instance.GetChannelsAsync(nodeNum, 8)).Select(ToJsonElement).ToList();
        if (selection.IncludeSecurity)
            file.SecurityConfig = ToJsonElement(await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.SecurityConfig));

        if (selection.IncludeOwner)
            file.Owner = ToJsonElement(await AdminConfigClient.Instance.GetOwnerAsync(nodeNum));
        if (selection.IncludeBluetooth)
            file.BluetoothConfig = ToJsonElement(await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.BluetoothConfig));
        if (selection.IncludeDevice)
            file.DeviceConfig = ToJsonElement(await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.DeviceConfig));
        if (selection.IncludeDisplay)
            file.DisplayConfig = ToJsonElement(await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.DisplayConfig));
        if (selection.IncludeNetwork)
            file.NetworkConfig = ToJsonElement(await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.NetworkConfig));
        if (selection.IncludePosition)
            file.PositionConfig = ToJsonElement(await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.PositionConfig));
        if (selection.IncludePower)
            file.PowerConfig = ToJsonElement(await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.PowerConfig));

        if (selection.IncludeCannedModule)
            file.ModuleCanned = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.CannedmsgConfig));
        if (selection.IncludeCannedMessagesText)
            file.CannedMessagesText = await AdminConfigClient.Instance.GetCannedMessageModuleMessagesAsync(nodeNum);
        if (selection.IncludeDetectionSensor)
            file.ModuleDetection = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.DetectionsensorConfig));
        if (selection.IncludeExternalNotification)
            file.ModuleExternalNotification = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.ExtnotifConfig));
        if (selection.IncludeMqtt)
            file.ModuleMqtt = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.MqttConfig));
        if (selection.IncludeRangeTest)
            file.ModuleRangeTest = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.RangetestConfig));
        if (selection.IncludePaxCounter)
            file.ModulePaxCounter = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.PaxcounterConfig));
        if (selection.IncludeRingtoneText)
            file.RingtoneText = await AdminConfigClient.Instance.GetRingtoneAsync(nodeNum);
        if (selection.IncludeSerial)
            file.ModuleSerial = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.SerialConfig));
        if (selection.IncludeStoreForward)
            file.ModuleStoreForward = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.StoreforwardConfig));
        if (selection.IncludeTelemetry)
            file.ModuleTelemetry = ToJsonElement(await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.TelemetryConfig));

        return file;
    }

    private async Task ApplyBackupAsync(uint nodeNum, SettingsBackupFile backup, BackupSelection selection)
    {
        if (selection.IncludeLora && HasJson(backup.LoraConfig))
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, ParseConfig(backup.LoraConfig));
        if (selection.IncludeChannels && backup.Channels?.Count > 0)
            await AdminConfigClient.Instance.SaveChannelsAsync(nodeNum, backup.Channels.Select(ParseChannel));
        if (selection.IncludeSecurity && HasJson(backup.SecurityConfig))
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, ParseConfig(backup.SecurityConfig));

        if (selection.IncludeOwner && HasJson(backup.Owner))
            await AdminConfigClient.Instance.SaveOwnerAsync(nodeNum, ParseOwner(backup.Owner));
        if (selection.IncludeBluetooth && HasJson(backup.BluetoothConfig))
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, ParseConfig(backup.BluetoothConfig));
        if (selection.IncludeDevice && HasJson(backup.DeviceConfig))
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, ParseConfig(backup.DeviceConfig));
        if (selection.IncludeDisplay && HasJson(backup.DisplayConfig))
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, ParseConfig(backup.DisplayConfig));
        if (selection.IncludeNetwork && HasJson(backup.NetworkConfig))
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, ParseConfig(backup.NetworkConfig));
        if (selection.IncludePosition && HasJson(backup.PositionConfig))
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, ParseConfig(backup.PositionConfig));
        if (selection.IncludePower && HasJson(backup.PowerConfig))
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, ParseConfig(backup.PowerConfig));

        if (selection.IncludeCannedModule && HasJson(backup.ModuleCanned))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModuleCanned));
        if (selection.IncludeCannedMessagesText && backup.CannedMessagesText is not null)
            await AdminConfigClient.Instance.SaveCannedMessageModuleMessagesAsync(nodeNum, backup.CannedMessagesText);
        if (selection.IncludeDetectionSensor && HasJson(backup.ModuleDetection))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModuleDetection));
        if (selection.IncludeExternalNotification && HasJson(backup.ModuleExternalNotification))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModuleExternalNotification));
        if (selection.IncludeMqtt && HasJson(backup.ModuleMqtt))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModuleMqtt));
        if (selection.IncludeRangeTest && HasJson(backup.ModuleRangeTest))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModuleRangeTest));
        if (selection.IncludePaxCounter && HasJson(backup.ModulePaxCounter))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModulePaxCounter));
        if (selection.IncludeRingtoneText && backup.RingtoneText is not null)
            await AdminConfigClient.Instance.SaveRingtoneAsync(nodeNum, backup.RingtoneText);
        if (selection.IncludeSerial && HasJson(backup.ModuleSerial))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModuleSerial));
        if (selection.IncludeStoreForward && HasJson(backup.ModuleStoreForward))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModuleStoreForward));
        if (selection.IncludeTelemetry && HasJson(backup.ModuleTelemetry))
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, ParseModuleConfig(backup.ModuleTelemetry));

        SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
    }

    private bool TryGetTargetNode(out uint nodeNum)
    {
        if (NodeIdentity.TryGetConnectedNodeNum(out nodeNum))
            return true;

        StatusText.Text = "Connect to a node first.";
        return false;
    }

    private static bool TryGetMainWindowHandle(out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (App.MainWindowInstance is null)
            return false;
        hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
        return hwnd != IntPtr.Zero;
    }

    private string ResolveTargetNodeName()
    {
        var id = AppState.GetEffectiveAdminTargetNodeIdHex();
        var node = AppState.Nodes.FirstOrDefault(x => string.Equals(x.IdHex, id, StringComparison.OrdinalIgnoreCase));
        return node?.Name ?? string.Empty;
    }

    private BackupSelection BuildSelectionFromUi() => new()
    {
        IncludeLora = IncludeLoraCheck.IsChecked == true,
        IncludeChannels = IncludeChannelsCheck.IsChecked == true,
        IncludeSecurity = IncludeSecurityCheck.IsChecked == true,
        IncludeOwner = IncludeOwnerCheck.IsChecked == true,
        IncludeBluetooth = IncludeBluetoothCheck.IsChecked == true,
        IncludeDevice = IncludeDeviceCheck.IsChecked == true,
        IncludeDisplay = IncludeDisplayCheck.IsChecked == true,
        IncludeNetwork = IncludeNetworkCheck.IsChecked == true,
        IncludePosition = IncludePositionCheck.IsChecked == true,
        IncludePower = IncludePowerCheck.IsChecked == true,
        IncludeCannedModule = IncludeCannedModuleCheck.IsChecked == true,
        IncludeCannedMessagesText = IncludeCannedMessagesTextCheck.IsChecked == true,
        IncludeDetectionSensor = IncludeDetectionSensorCheck.IsChecked == true,
        IncludeExternalNotification = IncludeExternalNotificationCheck.IsChecked == true,
        IncludeMqtt = IncludeMqttCheck.IsChecked == true,
        IncludeRangeTest = IncludeRangeTestCheck.IsChecked == true,
        IncludePaxCounter = IncludePaxCounterCheck.IsChecked == true,
        IncludeRingtoneText = IncludeRingtoneTextCheck.IsChecked == true,
        IncludeSerial = IncludeSerialCheck.IsChecked == true,
        IncludeStoreForward = IncludeStoreForwardCheck.IsChecked == true,
        IncludeTelemetry = IncludeTelemetryCheck.IsChecked == true
    };

    private void ApplySelectionPreset(BackupSelection preset)
    {
        IncludeLoraCheck.IsChecked = preset.IncludeLora;
        IncludeChannelsCheck.IsChecked = preset.IncludeChannels;
        IncludeSecurityCheck.IsChecked = preset.IncludeSecurity;
        IncludeOwnerCheck.IsChecked = preset.IncludeOwner;
        IncludeBluetoothCheck.IsChecked = preset.IncludeBluetooth;
        IncludeDeviceCheck.IsChecked = preset.IncludeDevice;
        IncludeDisplayCheck.IsChecked = preset.IncludeDisplay;
        IncludeNetworkCheck.IsChecked = preset.IncludeNetwork;
        IncludePositionCheck.IsChecked = preset.IncludePosition;
        IncludePowerCheck.IsChecked = preset.IncludePower;
        IncludeCannedModuleCheck.IsChecked = preset.IncludeCannedModule;
        IncludeCannedMessagesTextCheck.IsChecked = preset.IncludeCannedMessagesText;
        IncludeDetectionSensorCheck.IsChecked = preset.IncludeDetectionSensor;
        IncludeExternalNotificationCheck.IsChecked = preset.IncludeExternalNotification;
        IncludeMqttCheck.IsChecked = preset.IncludeMqtt;
        IncludeRangeTestCheck.IsChecked = preset.IncludeRangeTest;
        IncludePaxCounterCheck.IsChecked = preset.IncludePaxCounter;
        IncludeRingtoneTextCheck.IsChecked = preset.IncludeRingtoneText;
        IncludeSerialCheck.IsChecked = preset.IncludeSerial;
        IncludeStoreForwardCheck.IsChecked = preset.IncludeStoreForward;
        IncludeTelemetryCheck.IsChecked = preset.IncludeTelemetry;
    }

    private void SetAllSectionChecks(bool isChecked)
    {
        IncludeLoraCheck.IsChecked = isChecked;
        IncludeChannelsCheck.IsChecked = isChecked;
        IncludeSecurityCheck.IsChecked = isChecked;
        IncludeOwnerCheck.IsChecked = isChecked;
        IncludeBluetoothCheck.IsChecked = isChecked;
        IncludeDeviceCheck.IsChecked = isChecked;
        IncludeDisplayCheck.IsChecked = isChecked;
        IncludeNetworkCheck.IsChecked = isChecked;
        IncludePositionCheck.IsChecked = isChecked;
        IncludePowerCheck.IsChecked = isChecked;
        IncludeCannedModuleCheck.IsChecked = isChecked;
        IncludeCannedMessagesTextCheck.IsChecked = isChecked;
        IncludeDetectionSensorCheck.IsChecked = isChecked;
        IncludeExternalNotificationCheck.IsChecked = isChecked;
        IncludeMqttCheck.IsChecked = isChecked;
        IncludeRangeTestCheck.IsChecked = isChecked;
        IncludePaxCounterCheck.IsChecked = isChecked;
        IncludeRingtoneTextCheck.IsChecked = isChecked;
        IncludeSerialCheck.IsChecked = isChecked;
        IncludeStoreForwardCheck.IsChecked = isChecked;
        IncludeTelemetryCheck.IsChecked = isChecked;
    }

    private void SetEnabled(bool enabled)
    {
        LoraFrequencyPresetCombo.IsEnabled = enabled;
        ApplyLoraFrequencyPresetButton.IsEnabled = enabled;
        SelectionPresetCombo.IsEnabled = enabled;
        CustomPresetNameBox.IsEnabled = enabled;
        NotesBox.IsEnabled = enabled;
        IncludeLoraCheck.IsEnabled = enabled;
        IncludeChannelsCheck.IsEnabled = enabled;
        IncludeSecurityCheck.IsEnabled = enabled;
        IncludeOwnerCheck.IsEnabled = enabled;
        IncludeBluetoothCheck.IsEnabled = enabled;
        IncludeDeviceCheck.IsEnabled = enabled;
        IncludeDisplayCheck.IsEnabled = enabled;
        IncludeNetworkCheck.IsEnabled = enabled;
        IncludePositionCheck.IsEnabled = enabled;
        IncludePowerCheck.IsEnabled = enabled;
        IncludeCannedModuleCheck.IsEnabled = enabled;
        IncludeCannedMessagesTextCheck.IsEnabled = enabled;
        IncludeDetectionSensorCheck.IsEnabled = enabled;
        IncludeExternalNotificationCheck.IsEnabled = enabled;
        IncludeMqttCheck.IsEnabled = enabled;
        IncludeRangeTestCheck.IsEnabled = enabled;
        IncludePaxCounterCheck.IsEnabled = enabled;
        IncludeRingtoneTextCheck.IsEnabled = enabled;
        IncludeSerialCheck.IsEnabled = enabled;
        IncludeStoreForwardCheck.IsEnabled = enabled;
        IncludeTelemetryCheck.IsEnabled = enabled;
    }

    private static JsonElement ToJsonElement(IMessage message)
    {
        using var doc = JsonDocument.Parse(JsonFormatter.Default.Format(message));
        return doc.RootElement.Clone();
    }

    private static bool HasJson(JsonElement? value)
        => value.HasValue && value.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;

    private static string? BuildLoraImportPreviewText(SettingsBackupFile backup)
    {
        if (!HasJson(backup.LoraConfig))
            return null;

        try
        {
            var root = backup.LoraConfig!.Value;
            if (!root.TryGetProperty("lora", out var lora) || lora.ValueKind != JsonValueKind.Object)
                return "LoRa section found in file.";

            static string GetValue(JsonElement node, string name)
            {
                if (!node.TryGetProperty(name, out var value))
                    return "—";
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "—",
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => value.GetRawText()
                };
            }

            return string.Join('\n', new[]
            {
                "LoRa values from file:",
                $"Use preset: {GetValue(lora, "usePreset")}",
                $"Preset: {GetValue(lora, "modemPreset")}",
                $"Region: {GetValue(lora, "region")}",
                $"Bandwidth: {GetValue(lora, "bandwidth")}",
                $"Spread factor: {GetValue(lora, "spreadFactor")}",
                $"Coding rate: {GetValue(lora, "codingRate")}",
                $"Hop limit: {GetValue(lora, "hopLimit")}",
                $"Frequency offset: {GetValue(lora, "frequencyOffset")}",
                $"Override frequency: {GetValue(lora, "overrideFrequency")}"
            });
        }
        catch
        {
            return "LoRa section found in file.";
        }
    }

    private static string BuildSectionPreviewText(string sectionKey, SettingsBackupFile backup, bool isAvailable)
    {
        if (!isAvailable)
            return "Not present in file.";

        return sectionKey switch
        {
            "lora" => BuildLoraImportPreviewText(backup) ?? "LoRa section found in file.",
            "channels" => BuildChannelsPreviewText(backup),
            "module-canned-text" => BuildStringPreviewText("Canned messages text", backup.CannedMessagesText),
            "module-ringtone" => BuildStringPreviewText("Ringtone text", backup.RingtoneText),
            _ => BuildJsonPreviewText(sectionKey, GetSectionJson(sectionKey, backup))
        };
    }

    private static JsonElement? GetSectionJson(string sectionKey, SettingsBackupFile backup) => sectionKey switch
    {
        "security" => backup.SecurityConfig,
        "owner" => backup.Owner,
        "bluetooth" => backup.BluetoothConfig,
        "device" => backup.DeviceConfig,
        "display" => backup.DisplayConfig,
        "network" => backup.NetworkConfig,
        "position" => backup.PositionConfig,
        "power" => backup.PowerConfig,
        "module-canned" => backup.ModuleCanned,
        "module-detection" => backup.ModuleDetection,
        "module-external" => backup.ModuleExternalNotification,
        "module-mqtt" => backup.ModuleMqtt,
        "module-range" => backup.ModuleRangeTest,
        "module-pax" => backup.ModulePaxCounter,
        "module-serial" => backup.ModuleSerial,
        "module-storeforward" => backup.ModuleStoreForward,
        "module-telemetry" => backup.ModuleTelemetry,
        _ => null
    };

    private static string BuildChannelsPreviewText(SettingsBackupFile backup)
    {
        if (backup.Channels is null || backup.Channels.Count == 0)
            return "Channels section found in file (no entries).";

        var lines = new List<string>
        {
            $"Channels in file: {backup.Channels.Count}"
        };

        for (var i = 0; i < Math.Min(backup.Channels.Count, 5); i++)
        {
            var ch = backup.Channels[i];
            var role = ch.TryGetProperty("role", out var roleValue) ? roleValue.GetRawText() : "—";
            var name = ch.TryGetProperty("settings", out var settings) && settings.TryGetProperty("name", out var nameValue)
                ? (nameValue.GetString() ?? "—")
                : "—";
            lines.Add($"#{i}: role={role}, name={name}");
        }

        if (backup.Channels.Count > 5)
            lines.Add("...");

        return string.Join('\n', lines);
    }

    private static string BuildStringPreviewText(string title, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{title}: (empty)";

        var text = value.Trim();
        if (text.Length > 260)
            text = text[..260] + "...";
        return $"{title}:\n{text}";
    }

    private static string BuildJsonPreviewText(string sectionKey, JsonElement? json)
    {
        if (!HasJson(json))
            return "Section found in file.";

        var entries = new List<(string Path, string Value)>();
        FlattenJson(sectionKey, json!.Value, entries);
        var lines = entries
            .Select(e => $"{FormatFriendlyLabel(sectionKey, e.Path)}: {FormatFriendlyValue(e.Path, e.Value)}")
            .ToList();
        if (lines.Count == 0)
            return $"{sectionKey}: (no values)";

        const int maxLines = 16;
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines.Add("...");
        }

        return string.Join('\n', lines);
    }

    private static void FlattenJson(string path, JsonElement value, List<(string Path, string Value)> lines)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                    FlattenJson($"{path}.{prop.Name}", prop.Value, lines);
                break;

            case JsonValueKind.Array:
            {
                var i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    FlattenJson($"{path}[{i}]", item, lines);
                    i++;
                    if (i >= 6)
                    {
                        lines.Add(($"{path}[...]", "(truncated)"));
                        break;
                    }
                }
                break;
            }

            case JsonValueKind.String:
            {
                var text = value.GetString() ?? string.Empty;
                if (text.Length > 140)
                    text = text[..140] + "...";
                lines.Add((path, text));
                break;
            }

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                lines.Add((path, value.GetRawText()));
                break;
        }
    }

    private static void AttachTooltip(FrameworkElement target, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 18,
            MaxWidth = 540
        };

        var tip = new ToolTip { Content = tb };
        ToolTipService.SetToolTip(target, tip);
    }

    private static string FormatFriendlyLabel(string sectionKey, string rawPath)
    {
        var path = rawPath;
        if (path.StartsWith(sectionKey + ".", StringComparison.OrdinalIgnoreCase))
            path = path[(sectionKey.Length + 1)..];

        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count >= 2 && string.Equals(parts[0], parts[1], StringComparison.OrdinalIgnoreCase))
            parts.RemoveAt(0);

        var leaf = parts.Count == 0 ? path : parts[^1];

        return leaf.ToLowerInvariant() switch
        {
            "screenonsecs" => "Screen on for",
            "flipScreen" => "Flip screen",
            "flipscreen" => "Flip screen",
            _ => HumanizeToken(leaf)
        };
    }

    private static string FormatFriendlyValue(string rawPath, string rawValue)
    {
        var key = rawPath.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? rawPath;
        if (key.EndsWith("Secs", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("Seconds", StringComparison.OrdinalIgnoreCase))
            return $"{rawValue} s";
        return rawValue;
    }

    private static string HumanizeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return token;

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(token[i - 1]))
                sb.Append(' ');
            if (c == '_' || c == '-')
                sb.Append(' ');
            else
                sb.Append(c);
        }

        var text = sb.ToString().Trim();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static Config ParseConfig(JsonElement? value)
        => Config.Parser.ParseJson(ToJsonText(value));

    private static ModuleConfig ParseModuleConfig(JsonElement? value)
        => ModuleConfig.Parser.ParseJson(ToJsonText(value));

    private static Channel ParseChannel(JsonElement value)
        => Channel.Parser.ParseJson(ToJsonText(value));

    private static User ParseOwner(JsonElement? value)
        => User.Parser.ParseJson(ToJsonText(value));

    private static string ToJsonText(JsonElement? value)
    {
        if (!value.HasValue)
            return "{}";
        var v = value.Value;
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "{}") : v.GetRawText();
    }

    private static string GetPresetFolderPath(string subFolder)
    {
        var path = Path.Combine(AppDataPaths.BasePath, "ConfigPresets", subFolder);
        Directory.CreateDirectory(path);
        return path;
    }

    private static IEnumerable<SelectionPresetEntry> LoadCustomSelectionPresets()
    {
        var folder = GetPresetFolderPath(SelectionPresetFolderName);
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            SelectionPresetFile? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<SelectionPresetFile>(File.ReadAllText(file));
            }
            catch
            {
            }

            if (payload?.Selection is null)
                continue;

            var name = string.IsNullOrWhiteSpace(payload.Name) ? Path.GetFileNameWithoutExtension(file) : payload.Name.Trim();
            yield return new SelectionPresetEntry(name, payload.Selection, isBuiltIn: false, filePath: file);
        }
    }

    private static string ToSafeFileName(string raw)
    {
        var safe = raw.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');
        return string.IsNullOrWhiteSpace(safe) ? "preset" : safe;
    }

    private BackupSelection BuildAvailableSelection(SettingsBackupFile backup) => new()
    {
        IncludeLora = HasJson(backup.LoraConfig),
        IncludeChannels = backup.Channels?.Count > 0,
        IncludeSecurity = HasJson(backup.SecurityConfig),
        IncludeOwner = HasJson(backup.Owner),
        IncludeBluetooth = HasJson(backup.BluetoothConfig),
        IncludeDevice = HasJson(backup.DeviceConfig),
        IncludeDisplay = HasJson(backup.DisplayConfig),
        IncludeNetwork = HasJson(backup.NetworkConfig),
        IncludePosition = HasJson(backup.PositionConfig),
        IncludePower = HasJson(backup.PowerConfig),
        IncludeCannedModule = HasJson(backup.ModuleCanned),
        IncludeCannedMessagesText = backup.CannedMessagesText is not null,
        IncludeDetectionSensor = HasJson(backup.ModuleDetection),
        IncludeExternalNotification = HasJson(backup.ModuleExternalNotification),
        IncludeMqtt = HasJson(backup.ModuleMqtt),
        IncludeRangeTest = HasJson(backup.ModuleRangeTest),
        IncludePaxCounter = HasJson(backup.ModulePaxCounter),
        IncludeRingtoneText = backup.RingtoneText is not null,
        IncludeSerial = HasJson(backup.ModuleSerial),
        IncludeStoreForward = HasJson(backup.ModuleStoreForward),
        IncludeTelemetry = HasJson(backup.ModuleTelemetry)
    };

    private static BackupSelection IntersectSelection(BackupSelection preferred, BackupSelection available) => new()
    {
        IncludeLora = preferred.IncludeLora && available.IncludeLora,
        IncludeChannels = preferred.IncludeChannels && available.IncludeChannels,
        IncludeSecurity = preferred.IncludeSecurity && available.IncludeSecurity,
        IncludeOwner = preferred.IncludeOwner && available.IncludeOwner,
        IncludeBluetooth = preferred.IncludeBluetooth && available.IncludeBluetooth,
        IncludeDevice = preferred.IncludeDevice && available.IncludeDevice,
        IncludeDisplay = preferred.IncludeDisplay && available.IncludeDisplay,
        IncludeNetwork = preferred.IncludeNetwork && available.IncludeNetwork,
        IncludePosition = preferred.IncludePosition && available.IncludePosition,
        IncludePower = preferred.IncludePower && available.IncludePower,
        IncludeCannedModule = preferred.IncludeCannedModule && available.IncludeCannedModule,
        IncludeCannedMessagesText = preferred.IncludeCannedMessagesText && available.IncludeCannedMessagesText,
        IncludeDetectionSensor = preferred.IncludeDetectionSensor && available.IncludeDetectionSensor,
        IncludeExternalNotification = preferred.IncludeExternalNotification && available.IncludeExternalNotification,
        IncludeMqtt = preferred.IncludeMqtt && available.IncludeMqtt,
        IncludeRangeTest = preferred.IncludeRangeTest && available.IncludeRangeTest,
        IncludePaxCounter = preferred.IncludePaxCounter && available.IncludePaxCounter,
        IncludeRingtoneText = preferred.IncludeRingtoneText && available.IncludeRingtoneText,
        IncludeSerial = preferred.IncludeSerial && available.IncludeSerial,
        IncludeStoreForward = preferred.IncludeStoreForward && available.IncludeStoreForward,
        IncludeTelemetry = preferred.IncludeTelemetry && available.IncludeTelemetry
    };

    private sealed class SettingsBackupFile
    {
        public string Format { get; set; } = "MeshtasticWin.SettingsBackup.v1";
        public DateTime CreatedUtc { get; set; }
        public string? SourceNodeIdHex { get; set; }
        public string? SourceNodeName { get; set; }
        public string? Notes { get; set; }

        public JsonElement? LoraConfig { get; set; }
        public List<JsonElement>? Channels { get; set; }
        public JsonElement? SecurityConfig { get; set; }

        public JsonElement? Owner { get; set; }
        public JsonElement? BluetoothConfig { get; set; }
        public JsonElement? DeviceConfig { get; set; }
        public JsonElement? DisplayConfig { get; set; }
        public JsonElement? NetworkConfig { get; set; }
        public JsonElement? PositionConfig { get; set; }
        public JsonElement? PowerConfig { get; set; }

        public JsonElement? ModuleCanned { get; set; }
        public string? CannedMessagesText { get; set; }
        public JsonElement? ModuleDetection { get; set; }
        public JsonElement? ModuleExternalNotification { get; set; }
        public JsonElement? ModuleMqtt { get; set; }
        public JsonElement? ModuleRangeTest { get; set; }
        public JsonElement? ModulePaxCounter { get; set; }
        public string? RingtoneText { get; set; }
        public JsonElement? ModuleSerial { get; set; }
        public JsonElement? ModuleStoreForward { get; set; }
        public JsonElement? ModuleTelemetry { get; set; }
    }

    private sealed class BackupSelection
    {
        public bool IncludeLora { get; set; }
        public bool IncludeChannels { get; set; }
        public bool IncludeSecurity { get; set; }
        public bool IncludeOwner { get; set; }
        public bool IncludeBluetooth { get; set; }
        public bool IncludeDevice { get; set; }
        public bool IncludeDisplay { get; set; }
        public bool IncludeNetwork { get; set; }
        public bool IncludePosition { get; set; }
        public bool IncludePower { get; set; }
        public bool IncludeCannedModule { get; set; }
        public bool IncludeCannedMessagesText { get; set; }
        public bool IncludeDetectionSensor { get; set; }
        public bool IncludeExternalNotification { get; set; }
        public bool IncludeMqtt { get; set; }
        public bool IncludeRangeTest { get; set; }
        public bool IncludePaxCounter { get; set; }
        public bool IncludeRingtoneText { get; set; }
        public bool IncludeSerial { get; set; }
        public bool IncludeStoreForward { get; set; }
        public bool IncludeTelemetry { get; set; }

        public bool AnySelected =>
            IncludeLora || IncludeChannels || IncludeSecurity || IncludeOwner || IncludeBluetooth ||
            IncludeDevice || IncludeDisplay || IncludeNetwork || IncludePosition || IncludePower ||
            IncludeCannedModule || IncludeCannedMessagesText || IncludeDetectionSensor ||
            IncludeExternalNotification || IncludeMqtt || IncludeRangeTest || IncludePaxCounter ||
            IncludeRingtoneText || IncludeSerial || IncludeStoreForward || IncludeTelemetry;
    }

    private sealed class SelectionPresetEntry
    {
        public SelectionPresetEntry(string name, BackupSelection selection, bool isBuiltIn, string? filePath)
        {
            Name = name;
            Selection = selection;
            IsBuiltIn = isBuiltIn;
            FilePath = filePath;
        }

        public string Name { get; }
        public BackupSelection Selection { get; }
        public bool IsBuiltIn { get; }
        public string? FilePath { get; }
    }

    private sealed class SelectionPresetFile
    {
        public string? Name { get; set; }
        public BackupSelection? Selection { get; set; }
    }

    private sealed class LoraFrequencyPresetEntry
    {
        public LoraFrequencyPresetEntry(string name, string key, string details)
        {
            Name = name;
            Key = key;
            Details = details;
        }

        public string Name { get; }
        public string Key { get; }
        public string Details { get; }
    }

    private static class BuiltInSelectionPresets
    {
        public static BackupSelection Safe => new()
        {
            IncludeLora = true,
            IncludeChannels = true,
            IncludeSecurity = false,
            IncludeOwner = true,
            IncludeBluetooth = true,
            IncludeDevice = true,
            IncludeDisplay = true,
            IncludeNetwork = true,
            IncludePosition = true,
            IncludePower = true,
            IncludeCannedModule = true,
            IncludeCannedMessagesText = true,
            IncludeDetectionSensor = true,
            IncludeExternalNotification = true,
            IncludeMqtt = true,
            IncludeRangeTest = true,
            IncludePaxCounter = true,
            IncludeRingtoneText = true,
            IncludeSerial = true,
            IncludeStoreForward = true,
            IncludeTelemetry = true
        };

        public static BackupSelection Everything => new()
        {
            IncludeLora = true,
            IncludeChannels = true,
            IncludeSecurity = true,
            IncludeOwner = true,
            IncludeBluetooth = true,
            IncludeDevice = true,
            IncludeDisplay = true,
            IncludeNetwork = true,
            IncludePosition = true,
            IncludePower = true,
            IncludeCannedModule = true,
            IncludeCannedMessagesText = true,
            IncludeDetectionSensor = true,
            IncludeExternalNotification = true,
            IncludeMqtt = true,
            IncludeRangeTest = true,
            IncludePaxCounter = true,
            IncludeRingtoneText = true,
            IncludeSerial = true,
            IncludeStoreForward = true,
            IncludeTelemetry = true
        };

        public static BackupSelection RadioOnly => new()
        {
            IncludeLora = true,
            IncludeChannels = true,
            IncludeSecurity = true
        };
    }
}
