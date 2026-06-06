using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using TwitchStudioNative.Audio;
using TwitchStudioNative.Discord;
using TwitchStudioNative.Server;
using TwitchStudioNative.Storage;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using TitleBar = Wpf.Ui.Controls.TitleBar;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;
using Ellipse = System.Windows.Shapes.Ellipse;
using Forms = System.Windows.Forms;

namespace TwitchStudioNative;

public sealed partial class MainWindow
{
    private Border BuildVoiceCommandsPage()
    {
        _isBuildingVoiceCommandsPage = true;
        RefreshMicrophoneDevices();
        SyncVoiceCommandRules();
        Detach(_voiceCommandRulesList);
        Detach(_voiceCommandMicrophoneSelect);
        Detach(_voiceCommandModelPath);
        Detach(_voiceCommandOpenModels);
        Detach(_voiceCommandUseGrammar);
        Detach(_voiceCommandPhrase);
        Detach(_voiceCommandActionName);
        Detach(_voiceCommandHoldMs);
        Detach(_voiceCommandFrameDurationMs);
        Detach(_voiceCommandToggle);
        Detach(_voiceCommandAdd);
        Detach(_voiceCommandSave);
        Detach(_voiceCommandRun);
        Detach(_voiceCommandCaptureHotKey);
        Detach(_voiceCommandUpload);
        Detach(_voiceCommandDelete);
        Detach(_voiceCommandStatus);
        Detach(_voiceCommandAnimationInfo);
        Detach(_voiceCommandDebug);
        Detach(_voiceCommandEventsList);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var leftHeader = new StackPanel();
        leftHeader.Children.Add(new TextBlock { Text = T("voice.title"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        leftHeader.Children.Add(Description(T("voice.description")));
        _voiceCommandToggle.Content = _config.VoiceCommands.Enabled ? T("voice.disable") : T("voice.enable");
        _voiceCommandToggle.Margin = new Thickness(0, 0, 0, 12);
        _voiceCommandToggle.Click -= VoiceCommandToggle_Click;
        _voiceCommandToggle.Click += VoiceCommandToggle_Click;
        leftHeader.Children.Add(_voiceCommandToggle);

        _voiceCommandStatus.Foreground = Brush("#A1A1AA");
        _voiceCommandStatus.Margin = new Thickness(0, 0, 0, 14);
        leftHeader.Children.Add(_voiceCommandStatus);

        _voiceCommandAdd.Margin = new Thickness(0, 0, 0, 12);
        _voiceCommandAdd.Click -= VoiceCommandAdd_Click;
        _voiceCommandAdd.Click += VoiceCommandAdd_Click;
        leftHeader.Children.Add(_voiceCommandAdd);
        left.Children.Add(leftHeader);

        ConfigureVoiceCommandRulesList();
        _voiceCommandRulesList.SelectionChanged -= VoiceCommandRulesList_SelectionChanged;
        _voiceCommandRulesList.SelectionChanged += VoiceCommandRulesList_SelectionChanged;
        Grid.SetRow(_voiceCommandRulesList, 1);
        left.Children.Add(_voiceCommandRulesList);

        if (_config.DebugMode)
        {
            var debugPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            debugPanel.Children.Add(new TextBlock { Text = T("voice.events"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            _voiceCommandEventsList.ItemsSource = _voiceCommandEvents;
            _voiceCommandEventsList.MinHeight = 150;
            _voiceCommandEventsList.MaxHeight = 220;
            _voiceCommandEventsList.Background = Brush("#101012");
            _voiceCommandEventsList.BorderBrush = Brush("#2A2A2D");
            _voiceCommandEventsList.Foreground = Brush("#D4D4D8");
            _voiceCommandEventsList.FontFamily = new FontFamily("Consolas");
            _voiceCommandEventsList.FontSize = 12;
            debugPanel.Children.Add(_voiceCommandEventsList);
            Grid.SetRow(debugPanel, 2);
            left.Children.Add(debugPanel);
        }
        grid.Children.Add(Panel(left));

        var form = new StackPanel { MaxWidth = 640 };
        form.Children.Add(new TextBlock { Text = T("voice.rule"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });

        form.Children.Add(Label(T("voice.microphone")));
        _voiceCommandMicrophoneSelect.ItemsSource = _microphoneDevices;
        _voiceCommandMicrophoneSelect.DisplayMemberPath = nameof(MicrophoneDevice.DisplayName);
        _voiceCommandMicrophoneSelect.SelectedValuePath = nameof(MicrophoneDevice.DeviceNumber);
        _voiceCommandMicrophoneSelect.SelectedValue = _config.VoiceCommands.DeviceNumber ?? 0;
        _voiceCommandMicrophoneSelect.MinHeight = 34;
        _voiceCommandMicrophoneSelect.Margin = new Thickness(0, 6, 0, 12);
        _voiceCommandMicrophoneSelect.SelectionChanged -= VoiceCommandMicrophoneSelect_SelectionChanged;
        _voiceCommandMicrophoneSelect.SelectionChanged += VoiceCommandMicrophoneSelect_SelectionChanged;
        form.Children.Add(_voiceCommandMicrophoneSelect);

        form.Children.Add(SettingsInput(T("voice.modelPath"), T("voice.modelDescription", VoiceCommandSettings.BundledVoskModelPath), _voiceCommandModelPath, VoiceCommandModelPathText()));
        _voiceCommandModelPath.LostFocus -= VoiceCommandModelPath_Commit;
        _voiceCommandModelPath.LostFocus += VoiceCommandModelPath_Commit;
        _voiceCommandModelPath.KeyDown -= VoiceCommandModelPath_KeyDown;
        _voiceCommandModelPath.KeyDown += VoiceCommandModelPath_KeyDown;
        _voiceCommandOpenModels.Content = T("voice.openModelFolder");
        _voiceCommandOpenModels.Margin = new Thickness(0, 0, 0, 12);
        _voiceCommandOpenModels.Click -= VoiceCommandOpenModels_Click;
        _voiceCommandOpenModels.Click += VoiceCommandOpenModels_Click;
        form.Children.Add(_voiceCommandOpenModels);

        _voiceCommandUseGrammar.Content = T("voice.useGrammar");
        _voiceCommandUseGrammar.Foreground = Brush("#F4F4F5");
        _voiceCommandUseGrammar.IsChecked = _config.VoiceCommands.UseGrammar;
        _voiceCommandUseGrammar.Margin = new Thickness(0, 0, 0, 12);
        _voiceCommandUseGrammar.Checked -= VoiceCommandUseGrammar_Changed;
        _voiceCommandUseGrammar.Checked += VoiceCommandUseGrammar_Changed;
        _voiceCommandUseGrammar.Unchecked -= VoiceCommandUseGrammar_Changed;
        _voiceCommandUseGrammar.Unchecked += VoiceCommandUseGrammar_Changed;
        form.Children.Add(_voiceCommandUseGrammar);

        form.Children.Add(SettingsInput(T("voice.phrase"), T("voice.phraseDescription"), _voiceCommandPhrase, ""));
        form.Children.Add(SettingsInput(T("voice.action"), T("voice.actionDescription"), _voiceCommandActionName, ""));

        form.Children.Add(SettingsInput(T("voice.returnMs"), T("voice.returnDescription"), _voiceCommandHoldMs, _config.VoiceCommands.HoldMs.ToString()));
        form.Children.Add(SettingsInput(T("voice.frameDuration"), T("voice.frameDurationDescription"), _voiceCommandFrameDurationMs, "120"));

        _voiceCommandAnimationInfo.Foreground = Brush("#A1A1AA");
        _voiceCommandAnimationInfo.TextWrapping = TextWrapping.Wrap;
        _voiceCommandAnimationInfo.Margin = new Thickness(0, 0, 0, 12);
        form.Children.Add(_voiceCommandAnimationInfo);

        if (_config.DebugMode)
        {
            form.Children.Add(Label(T("voice.debug")));
            _voiceCommandDebug.Foreground = Brush("#A1A1AA");
            _voiceCommandDebug.TextWrapping = TextWrapping.Wrap;
            _voiceCommandDebug.Margin = new Thickness(0, 4, 0, 12);
            form.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderBrush = Brush("#2A2A2D"),
                BorderThickness = new Thickness(1),
                Background = Brush("#121214"),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 12),
                Child = _voiceCommandDebug
            });
        }

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        _voiceCommandSave.Margin = new Thickness(0, 0, 10, 0);
        _voiceCommandRun.Margin = new Thickness(0, 0, 10, 0);
        _voiceCommandCaptureHotKey.Margin = new Thickness(0, 0, 10, 0);
        _voiceCommandUpload.Margin = new Thickness(0, 0, 10, 0);
        _voiceCommandSave.Margin = new Thickness(0, 0, 10, 10);
        _voiceCommandRun.Margin = new Thickness(0, 0, 10, 10);
        _voiceCommandCaptureHotKey.Margin = new Thickness(0, 0, 10, 10);
        _voiceCommandUpload.Margin = new Thickness(0, 0, 10, 10);
        _voiceCommandDelete.Margin = new Thickness(0, 0, 0, 10);
        _voiceCommandDelete.IsEnabled = _voiceCommandRulesList.SelectedItem is not null;
        _voiceCommandSave.Click -= VoiceCommandSave_Click;
        _voiceCommandSave.Click += VoiceCommandSave_Click;
        _voiceCommandRun.Click -= VoiceCommandRun_Click;
        _voiceCommandRun.Click += VoiceCommandRun_Click;
        _voiceCommandCaptureHotKey.Click -= VoiceCommandCaptureHotKey_Click;
        _voiceCommandCaptureHotKey.Click += VoiceCommandCaptureHotKey_Click;
        _voiceCommandUpload.Click -= VoiceCommandUpload_Click;
        _voiceCommandUpload.Click += VoiceCommandUpload_Click;
        _voiceCommandDelete.Click -= VoiceCommandDelete_Click;
        _voiceCommandDelete.Click += VoiceCommandDelete_Click;
        buttons.Children.Add(_voiceCommandSave);
        buttons.Children.Add(_voiceCommandRun);
        buttons.Children.Add(_voiceCommandCaptureHotKey);
        buttons.Children.Add(_voiceCommandUpload);
        buttons.Children.Add(_voiceCommandDelete);
        form.Children.Add(buttons);

        var right = Panel(new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        if (_voiceCommandRulesList.SelectedItem is null && _voiceCommandRules.Count > 0)
        {
            _voiceCommandRulesList.SelectedItem = _voiceCommandRules[0];
        }
        PopulateVoiceCommandForm(_voiceCommandRulesList.SelectedItem as VoiceCommandRule);
        UpdateVoiceCommandDebug();
        UpdateVoiceCommandStatus();
        _isBuildingVoiceCommandsPage = false;
        return Panel(grid);
    }

    private void SyncVoiceCommandRules()
    {
        _voiceCommandRules.Clear();
        foreach (var rule in _config.VoiceCommands.Rules.Select(NormalizeVoiceCommandRule))
        {
            _voiceCommandRules.Add(rule);
        }
    }

    private void NormalizeVoiceCommandSettings()
    {
        var modelPath = string.IsNullOrWhiteSpace(_config.VoiceCommands.VoskModelPath)
            ? VoiceCommandSettings.BundledVoskModelPath
            : _config.VoiceCommands.VoskModelPath;
        var rules = _config.VoiceCommands.Rules.Count == 0
            ? new List<VoiceCommandRule> { DefaultVoiceCommandRule() }
            : _config.VoiceCommands.Rules.Select(NormalizeVoiceCommandRule).ToList();
        _config = _config with
        {
            VoiceCommands = _config.VoiceCommands with
            {
                VoskModelPath = modelPath,
                Rules = rules
            }
        };
        App.CurrentConfig = _config;
    }

    private static VoiceCommandRule DefaultVoiceCommandRule() => new()
    {
        Phrase = T("voice.defaultPhrase"),
        ActionId = "hello",
        ActionName = T("voice.defaultActionName")
    };

    private static VoiceCommandRule NormalizeVoiceCommandRule(VoiceCommandRule rule)
    {
        var phrase = rule.Phrase.Trim();
        var actionId = string.IsNullOrWhiteSpace(rule.ActionId) ? CreateActionId(phrase) : rule.ActionId;
        var actionName = string.IsNullOrWhiteSpace(rule.ActionName) ? phrase : rule.ActionName;
        return rule with { Phrase = phrase, ActionId = actionId, ActionName = actionName };
    }

    private async void SaveVoiceCommandSettings(Func<VoiceCommandSettings, VoiceCommandSettings> update, bool restartCapture = true)
    {
        var nextSettings = update(_config.VoiceCommands);
        if (nextSettings == _config.VoiceCommands)
        {
            return;
        }

        _config = _config with { VoiceCommands = nextSettings };
        App.CurrentConfig = _config;
        SyncVoiceCommandRules();
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        if (restartCapture)
        {
            RestartVoiceCommandCapture();
        }
        RegisterVoiceCommandHotKeys();
        UpdateVoiceCommandStatus();
    }

    private void VoiceCommandToggle_Click(object sender, RoutedEventArgs e)
    {
        SaveVoiceCommandSettings(settings => settings with { Enabled = !settings.Enabled });
        _voiceCommandToggle.Content = !_config.VoiceCommands.Enabled ? T("voice.enable") : T("voice.disable");
    }

    private void VoiceCommandMicrophoneSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isBuildingVoiceCommandsPage)
        {
            return;
        }

        if (_voiceCommandMicrophoneSelect.SelectedValue is int deviceNumber)
        {
            SaveVoiceCommandSettings(settings => settings with { DeviceNumber = deviceNumber });
        }
    }

    private void VoiceCommandModelPath_Commit(object sender, RoutedEventArgs e)
    {
        if (_isBuildingVoiceCommandsPage)
        {
            return;
        }

        SaveVoiceCommandSettings(settings => settings with { VoskModelPath = _voiceCommandModelPath.Text.Trim() });
    }

    private void VoiceCommandModelPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SaveVoiceCommandSettings(settings => settings with { VoskModelPath = _voiceCommandModelPath.Text.Trim() });
        _voiceCommandModelPath.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void VoiceCommandUseGrammar_Changed(object sender, RoutedEventArgs e)
    {
        if (_isBuildingVoiceCommandsPage)
        {
            return;
        }

        SaveVoiceCommandSettings(settings => settings with { UseGrammar = _voiceCommandUseGrammar.IsChecked == true });
    }

    private void VoiceCommandOpenModels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var modelPath = ResolveVoiceCommandModelPath(_voiceCommandModelPath.Text);
            if (Directory.Exists(modelPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = modelPath,
                    UseShellExecute = true
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "https://alphacephei.com/vosk/models",
                UseShellExecute = true
            });
        }
        catch (Exception error)
        {
            UpdateVoiceCommandStatus(T("voice.status.openFailed", error.Message));
        }
    }

    private string VoiceCommandModelPathText()
    {
        return string.IsNullOrWhiteSpace(_config.VoiceCommands.VoskModelPath)
            ? VoiceCommandSettings.BundledVoskModelPath
            : _config.VoiceCommands.VoskModelPath;
    }

    private static string ResolveVoiceCommandModelPath(string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? VoiceCommandSettings.BundledVoskModelPath : path.Trim();
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private void VoiceCommandAdd_Click(object sender, RoutedEventArgs e)
    {
        _voiceCommandRulesList.SelectedItem = null;
        _voiceCommandPhrase.Text = "";
        _voiceCommandActionName.Text = "";
        _voiceCommandHoldMs.Text = _config.VoiceCommands.HoldMs.ToString();
        _voiceCommandFrameDurationMs.Text = "120";
        _voiceCommandAnimationInfo.Text = T("voice.form.new");
        _voiceCommandRun.IsEnabled = false;
        _voiceCommandCaptureHotKey.IsEnabled = false;
        _voiceCommandCaptureHotKey.Content = T("hotkey.none");
        _voiceCommandUpload.IsEnabled = false;
        _voiceCommandDelete.IsEnabled = false;
        _voiceCommandPhrase.Focus();
        UpdateVoiceCommandDebug();
    }

    private void VoiceCommandRulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateVoiceCommandForm(_voiceCommandRulesList.SelectedItem as VoiceCommandRule);
        UpdateVoiceCommandDebug();
    }

    private void PopulateVoiceCommandForm(VoiceCommandRule? rule)
    {
        _voiceCommandPhrase.Text = rule?.Phrase ?? "";
        _voiceCommandActionName.Text = rule?.ActionName ?? "";
        _voiceCommandHoldMs.Text = _config.VoiceCommands.HoldMs.ToString();
        var animation = rule is null ? null : CustomAnimationForRule(rule);
        _voiceCommandFrameDurationMs.Text = (animation?.FrameDurationMs ?? 120).ToString();
        _voiceCommandAnimationInfo.Text = rule is null
            ? T("voice.form.empty")
            : animation?.Frames.Count > 0
                ? T("asset.frames", animation.Frames.Count, animation.Frames[0].FileName)
                : T("voice.form.noAnimation");
        _voiceCommandCaptureHotKey.Content = T("hotkey.label", VoiceCommandHotKeyLabel(rule));
        _voiceCommandRun.IsEnabled = rule is not null;
        _voiceCommandCaptureHotKey.IsEnabled = rule is not null;
        _voiceCommandUpload.IsEnabled = rule is not null || !string.IsNullOrWhiteSpace(_voiceCommandPhrase.Text);
        _voiceCommandDelete.IsEnabled = rule is not null;
    }

    private void UpdateVoiceCommandDebug()
    {
        if (_lastVoiceCommandDebug is null && _lastVoiceCommandAudioDebug is null && _lastVoiceCommandStartupDebug is null)
        {
            _voiceCommandDebug.Text = T("voice.debug.none");
            return;
        }

        var lines = new List<string>();
        if (_lastVoiceCommandStartupDebug is not null)
        {
            lines.Add(T("voice.debug.recognizer"));
            lines.Add(T("voice.debug.mic", _lastVoiceCommandStartupDebug.DeviceNumber.HasValue ? _lastVoiceCommandStartupDebug.DeviceNumber.Value + 1 : "?", _lastVoiceCommandStartupDebug.DeviceName ?? "unknown"));
            lines.Add($"Speech input: {_lastVoiceCommandStartupDebug.SpeechInput ?? "unknown"}");
        }

        if (!string.IsNullOrWhiteSpace(_lastVoiceCommandDebug?.Error))
        {
            lines.Add(T("voice.debug.error", _lastVoiceCommandDebug.Error));
        }

        if (_lastVoiceCommandAudioDebug is not null)
        {
            var level = _lastVoiceCommandAudioDebug.AudioLevel ?? 0;
            lines.Add(T("voice.debug.audio", level, _lastVoiceCommandAudioDebug.BytesRecorded ?? 0, _lastVoiceCommandAudioDebug.At));
        }

        if (_lastVoiceCommandDebug is null)
        {
            lines.Add(T("voice.debug.noEvents"));
            _voiceCommandDebug.Text = string.Join("\n", lines);
            return;
        }

        var selected = _voiceCommandRulesList.SelectedItem as VoiceCommandRule;
        var selectedPhrase = selected?.Phrase ?? _voiceCommandPhrase.Text.Trim();
        var appliesToSelected = !string.IsNullOrWhiteSpace(selectedPhrase)
            && _lastVoiceCommandDebug.MatchedPhrase is not null
            && selectedPhrase.Equals(_lastVoiceCommandDebug.MatchedPhrase, StringComparison.OrdinalIgnoreCase);
        var result = _lastVoiceCommandDebug.Result switch
        {
            "recognized" => appliesToSelected ? T("voice.debug.recognizedSelected") : T("voice.debug.recognizedOther"),
            "recognized-partial" => appliesToSelected ? T("voice.debug.partialSelected") : T("voice.debug.partialOther"),
            "unknown" => "Vosk вернул [unk]",
            "dictation" => T("voice.debug.dictation"),
            "hypothesis" => T("voice.debug.hypothesis"),
            "weak" => T("voice.debug.weak"),
            "rejected" => T("voice.debug.rejected"),
            _ => _lastVoiceCommandDebug.Result
        };
        var heard = string.IsNullOrWhiteSpace(_lastVoiceCommandDebug.Text) ? T("voice.debug.noText") : _lastVoiceCommandDebug.Text;
        var command = _lastVoiceCommandDebug.MatchedPhrase is null
            ? T("voice.debug.noneValue")
            : string.IsNullOrWhiteSpace(_lastVoiceCommandDebug.ActionName)
                ? _lastVoiceCommandDebug.MatchedPhrase
                : $"{_lastVoiceCommandDebug.MatchedPhrase} -> {_lastVoiceCommandDebug.ActionName}";
        lines.Add($"{result}");
        lines.Add(T("voice.debug.heard", heard));
        lines.Add(T("voice.debug.command", command));
        lines.Add(T("voice.debug.confidence", _lastVoiceCommandDebug.Confidence));
        lines.Add(T("voice.debug.time", _lastVoiceCommandDebug.At));
        _voiceCommandDebug.Text = string.Join("\n", lines);
    }

    private void ApplyVoiceCommandDebug(VoiceCommandDebugEvent debug)
    {
        if (debug.Result == "audio")
        {
            _lastVoiceCommandAudioDebug = debug;
        }
        else if (debug.Result == "listening")
        {
            _lastVoiceCommandStartupDebug = debug;
        }
        else
        {
            _lastVoiceCommandDebug = debug;
            AddVoiceCommandEvent(debug);
        }

        UpdateVoiceCommandDebug();
    }

    private void AddVoiceCommandEvent(VoiceCommandDebugEvent debug)
    {
        var text = string.IsNullOrWhiteSpace(debug.Text) ? "<no text>" : debug.Text;
        var confidence = debug.Result == "hypothesis" ? "" : $" {debug.Confidence:P0}";
        var match = debug.MatchedPhrase is null ? "" : $" -> {debug.MatchedPhrase}";
        _voiceCommandEvents.Insert(0, $"[{debug.At:HH:mm:ss}] {debug.Result}{confidence}: {text}{match}");
        while (_voiceCommandEvents.Count > 40)
        {
            _voiceCommandEvents.RemoveAt(_voiceCommandEvents.Count - 1);
        }
    }

    private void VoiceCommandSave_Click(object sender, RoutedEventArgs e)
    {
        var phrase = _voiceCommandPhrase.Text.Trim();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            UpdateVoiceCommandStatus(T("voice.status.noPhrase"));
            return;
        }

        var actionName = string.IsNullOrWhiteSpace(_voiceCommandActionName.Text)
            ? phrase
            : _voiceCommandActionName.Text.Trim();
        var holdMs = int.TryParse(_voiceCommandHoldMs.Text, out var parsedHoldMs)
            ? Math.Clamp(parsedHoldMs, 250, 30000)
            : _config.VoiceCommands.HoldMs;
        var selected = _voiceCommandRulesList.SelectedItem as VoiceCommandRule;
        var rules = _config.VoiceCommands.Rules.ToList();
        var actionId = string.IsNullOrWhiteSpace(selected?.ActionId) ? CreateActionId(phrase) : selected.ActionId;
        var nextRule = new VoiceCommandRule
        {
            Phrase = phrase,
            ActionId = actionId,
            ActionName = actionName,
            HotKey = selected?.HotKey,
            HotKeyModifiers = selected?.HotKeyModifiers ?? "",
            Enabled = true
        };
        var index = selected is null
            ? -1
            : rules.FindIndex(rule => rule.Phrase.Equals(selected.Phrase, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            rules[index] = nextRule;
        }
        else
        {
            rules.RemoveAll(rule => rule.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase));
            rules.Add(nextRule);
        }

        SaveVoiceCommandSettings(settings => settings with { Rules = rules, HoldMs = holdMs });
        _voiceCommandRulesList.SelectedItem = _voiceCommandRules.FirstOrDefault(rule => rule.ActionId == actionId)
            ?? _voiceCommandRules.FirstOrDefault(rule => rule.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase));
        EnsureCustomAnimation(nextRule);
        PopulateVoiceCommandForm(_voiceCommandRulesList.SelectedItem as VoiceCommandRule ?? nextRule);
    }

    private void VoiceCommandRun_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceCommandRulesList.SelectedItem is VoiceCommandRule rule)
        {
            TriggerVoiceCommand(rule, "button");
        }
    }

    private void VoiceCommandCaptureHotKey_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceCommandRulesList.SelectedItem is not VoiceCommandRule)
        {
            return;
        }

        _isCapturingVoiceCommandHotKey = true;
        _isCapturingMuteHotKey = false;
        _voiceCommandCaptureHotKey.Content = T("voice.captureHotkey");
        Focus();
    }

    private async void SaveSelectedVoiceCommandHotKey(Key key, ModifierKeys modifiers)
    {
        if (_voiceCommandRulesList.SelectedItem is not VoiceCommandRule selected)
        {
            return;
        }

        var rules = _config.VoiceCommands.Rules.Select(rule => rule.ActionId == selected.ActionId
            ? rule with { HotKey = key.ToString(), HotKeyModifiers = modifiers.ToString() }
            : rule).ToList();
        _config = _config with { VoiceCommands = _config.VoiceCommands with { Rules = rules } };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        SyncVoiceCommandRules();
        var updated = _voiceCommandRules.FirstOrDefault(rule => rule.ActionId == selected.ActionId);
        _voiceCommandRulesList.SelectedItem = updated;
        PopulateVoiceCommandForm(updated);
        RegisterVoiceCommandHotKeys();
    }

    private async void VoiceCommandUpload_Click(object sender, RoutedEventArgs e)
    {
        var rule = EnsureSelectedVoiceCommandRule();
        if (rule is null)
        {
            return;
        }

        var userId = _voiceProvider?.Snapshot.AuthenticatedUserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            UpdateVoiceCommandStatus(T("voice.status.noUser"));
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg|All files|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var settings = _config.Overlays.TryGetValue(userId, out var existing)
            ? existing
            : new UserOverlaySettings { UserId = userId };
        var frames = new List<OverlayAsset>();
        foreach (var fileName in dialog.FileNames)
        {
            var id = Guid.NewGuid().ToString("N");
            var extension = Path.GetExtension(fileName);
            var storedName = $"{id}{extension}";
            var storedPath = Path.Combine(_storage.AssetsDir, storedName);
            File.Copy(fileName, storedPath, true);
            frames.Add(new OverlayAsset
            {
                Id = id,
                UserId = userId,
                State = AnimationState.idle,
                FileName = Path.GetFileName(fileName),
                MimeType = MimeFromExtension(extension),
                Url = $"/assets/{storedName}",
                Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SizeBytes = new FileInfo(storedPath).Length,
                ImportedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        var frameDuration = int.TryParse(_voiceCommandFrameDurationMs.Text, out var parsed)
            ? Math.Max(16, parsed)
            : 120;
        var customAnimations = new Dictionary<string, CustomOverlayAnimation>(settings.CustomAnimations, StringComparer.OrdinalIgnoreCase)
        {
            [rule.ActionId] = new CustomOverlayAnimation
            {
                Id = rule.ActionId,
                Name = string.IsNullOrWhiteSpace(rule.ActionName) ? rule.Phrase : rule.ActionName,
                TriggerPhrase = rule.Phrase,
                Frames = frames,
                FrameDurationMs = frameDuration,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        settings = settings with { CustomAnimations = customAnimations };
        _config = _config with { Overlays = new Dictionary<string, UserOverlaySettings>(_config.Overlays) { [userId] = settings } };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        await SaveServerConfigAsync();
        PopulateVoiceCommandForm(rule);
        UpdateVoiceCommandStatus(T("voice.status.loaded", rule.Phrase));
    }

    private void VoiceCommandDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceCommandRulesList.SelectedItem is not VoiceCommandRule selected)
        {
            return;
        }

        SaveVoiceCommandSettings(settings => settings with
        {
            Rules = settings.Rules
                .Where(rule => !rule.Phrase.Equals(selected.Phrase, StringComparison.OrdinalIgnoreCase))
                .ToList()
        });
        _voiceCommandRulesList.SelectedItem = null;
        PopulateVoiceCommandForm(null);
    }

    private async void RestartVoiceCommandCapture()
    {
        var settings = _config.VoiceCommands;
        var version = Interlocked.Increment(ref _voiceCommandRestartVersion);
        UpdateVoiceCommandStatus(settings.Enabled ? T("voice.status.starting") : T("voice.status.disabled"));

        await _voiceCommandRestartLock.WaitAsync();
        try
        {
            if (version != _voiceCommandRestartVersion)
            {
                return;
            }

            await Task.Run(() =>
            {
                if (settings.Enabled)
                {
                    _voiceCommands.Start(settings);
                }
                else
                {
                    _voiceCommands.Stop();
                }
            });

            if (!settings.Enabled)
            {
                _voiceProvider?.SetVoiceCommandAction(null);
            }
        }
        catch (Exception error)
        {
            AddLog($"Voice commands restart failed: {error.Message}");
            UpdateVoiceCommandStatus(T("voice.status.startFailed", error.Message));
        }
        finally
        {
            _voiceCommandRestartLock.Release();
        }
    }

    private void UpdateVoiceCommandStatus(string? text = null)
    {
        _voiceCommandStatus.Text = text
            ?? (_config.VoiceCommands.Enabled ? T("voice.status.ready") : T("voice.status.disabled"));
    }

    private void ApplyVoiceCommand(VoiceCommandRule rule, float confidence)
    {
        TriggerVoiceCommand(rule, $"voice {confidence:P0}");
    }

    private void TriggerVoiceCommand(VoiceCommandRule rule, string source)
    {
        if (string.IsNullOrWhiteSpace(_voiceProvider?.Snapshot.AuthenticatedUserId))
        {
            UpdateVoiceCommandStatus(T("voice.status.commandNoUser"));
            return;
        }

        if (DateTimeOffset.Now < _voiceCommandActionLockedUntil)
        {
            AddLog($"Voice command '{rule.Phrase}' ignored while action animation is locked.");
            return;
        }

        _voiceProvider.SetVoiceCommandAction(rule.ActionId);
        var label = string.IsNullOrWhiteSpace(rule.ActionName) ? rule.Phrase : rule.ActionName;
        UpdateVoiceCommandStatus(T("voice.status.command", rule.Phrase, label, source));
        AddLog($"Voice command '{rule.Phrase}' -> {rule.ActionId} via {source}");

        var durationMs = VoiceCommandAnimationDurationMs(rule);
        _voiceCommandActionLockedUntil = DateTimeOffset.Now.AddMilliseconds(durationMs);
        _voiceCommandResetTimer?.Stop();
        _voiceCommandResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(durationMs)
        };
        _voiceCommandResetTimer.Tick += (_, _) =>
        {
            _voiceCommandResetTimer?.Stop();
            _voiceCommandActionLockedUntil = DateTimeOffset.MinValue;
            _voiceProvider?.SetVoiceCommandAction(null);
            UpdateVoiceCommandStatus();
        };
        _voiceCommandResetTimer.Start();
    }

    private int VoiceCommandAnimationDurationMs(VoiceCommandRule rule)
    {
        var animation = CustomAnimationForRule(rule);
        if (animation is null || animation.Frames.Count == 0)
        {
            return Math.Max(250, _config.VoiceCommands.HoldMs);
        }

        return Math.Max(250, Math.Max(1, animation.Frames.Count) * Math.Max(16, animation.FrameDurationMs));
    }

    private VoiceCommandRule? EnsureSelectedVoiceCommandRule()
    {
        if (_voiceCommandRulesList.SelectedItem is VoiceCommandRule selected)
        {
            return selected;
        }

        var phrase = _voiceCommandPhrase.Text.Trim();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            UpdateVoiceCommandStatus(T("voice.status.noPhrase"));
            return null;
        }

        VoiceCommandSave_Click(this, new RoutedEventArgs());
        return _config.VoiceCommands.Rules.FirstOrDefault(rule => rule.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private CustomOverlayAnimation? CustomAnimationForRule(VoiceCommandRule rule)
    {
        var userId = _voiceProvider?.Snapshot.AuthenticatedUserId;
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(rule.ActionId)
            || !_config.Overlays.TryGetValue(userId, out var settings))
        {
            return null;
        }

        return settings.CustomAnimations.TryGetValue(rule.ActionId, out var animation) ? animation : null;
    }

    private void EnsureCustomAnimation(VoiceCommandRule rule)
    {
        var userId = _voiceProvider?.Snapshot.AuthenticatedUserId;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(rule.ActionId))
        {
            return;
        }

        var settings = _config.Overlays.TryGetValue(userId, out var existing)
            ? existing
            : new UserOverlaySettings { UserId = userId };
        if (settings.CustomAnimations.ContainsKey(rule.ActionId))
        {
            return;
        }

        settings = settings with
        {
            CustomAnimations = new Dictionary<string, CustomOverlayAnimation>(settings.CustomAnimations, StringComparer.OrdinalIgnoreCase)
            {
                [rule.ActionId] = new CustomOverlayAnimation
                {
                    Id = rule.ActionId,
                    Name = string.IsNullOrWhiteSpace(rule.ActionName) ? rule.Phrase : rule.ActionName,
                    TriggerPhrase = rule.Phrase,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                }
            }
        };
        _config = _config with { Overlays = new Dictionary<string, UserOverlaySettings>(_config.Overlays) { [userId] = settings } };
    }

    private static string CreateActionId(string phrase)
    {
        var safe = new string(phrase.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        safe = string.Join('-', safe.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
    }
}
