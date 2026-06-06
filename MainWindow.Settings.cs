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
    private Border BuildSettingsPage()
    {
        RefreshMicrophoneDevices();
        Detach(_microphoneSelect);
        Detach(_vadModeSelect);
        Detach(_localMicMute);
        Detach(_vadStatus);
        Detach(_vadLevel);
        Detach(_vadSpeakingThreshold);
        Detach(_vadSilenceThreshold);
        Detach(_vadSpeakingSlider);
        Detach(_vadSilenceSlider);
        Detach(_vadAttackMs);
        Detach(_vadReleaseMs);
        Detach(_captureMuteHotKey);
        Detach(_discordClientId);
        Detach(_overlayPort);
        Detach(_overlayServerStatus);
        var stack = new StackPanel { MaxWidth = 520 };
        stack.Children.Add(new TextBlock { Text = T("settings.localMic"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(Description(T("settings.localMicDescription")));

        var enabled = new CheckBox { Content = T("settings.localVadEnabled"), Foreground = Brush("#F4F4F5"), IsChecked = _config.LocalMicrophone.Enabled, Margin = new Thickness(0, 0, 0, 12) };
        enabled.Checked += (_, _) => SaveLocalMicrophoneSettings(settings => settings with { Enabled = true });
        enabled.Unchecked += (_, _) => SaveLocalMicrophoneSettings(settings => settings with { Enabled = false });
        stack.Children.Add(enabled);

        stack.Children.Add(Label(T("settings.vadMode")));
        stack.Children.Add(Description(T("settings.vadModeDescription")));
        _vadModeSelect.ItemsSource = new[]
        {
            new VadModeOption("rms", T("settings.vadRms")),
            new VadModeOption("neural", T("settings.vadNeural"))
        };
        _vadModeSelect.DisplayMemberPath = nameof(VadModeOption.Label);
        _vadModeSelect.SelectedValuePath = nameof(VadModeOption.Value);
        _vadModeSelect.SelectedValue = _config.LocalMicrophone.DetectionMode;
        _vadModeSelect.MinHeight = 34;
        _vadModeSelect.Margin = new Thickness(0, 6, 0, 12);
        stack.Children.Add(_vadModeSelect);

        stack.Children.Add(Label(T("voice.microphone")));
        _microphoneSelect.ItemsSource = _microphoneDevices;
        _microphoneSelect.DisplayMemberPath = nameof(MicrophoneDevice.DisplayName);
        _microphoneSelect.SelectedValuePath = nameof(MicrophoneDevice.DeviceNumber);
        _microphoneSelect.SelectedValue = _config.LocalMicrophone.DeviceNumber ?? 0;
        _microphoneSelect.MinHeight = 34;
        _microphoneSelect.Margin = new Thickness(0, 6, 0, 12);
        if (!_settingsControlsConfigured)
        {
            _microphoneSelect.SelectionChanged += (_, _) =>
            {
                if (_microphoneSelect.SelectedValue is int deviceNumber)
                {
                    SaveLocalMicrophoneSettings(settings => settings with { DeviceNumber = deviceNumber });
                }
            };
            _vadModeSelect.SelectionChanged += (_, _) =>
            {
                if (_vadModeSelect.SelectedValue is string mode)
                {
                    SaveLocalMicrophoneSettings(settings => settings with { DetectionMode = mode });
                }
            };
            _localMicMute.Click += LocalMicMute_Click;
            _captureMuteHotKey.Click += (_, _) =>
            {
                _isCapturingMuteHotKey = true;
                _captureMuteHotKey.Content = T("settings.captureHotkey");
                Focus();
            };
            _vadSpeakingSlider.ValueChanged += (_, _) =>
            {
                if (!_vadSpeakingSlider.IsKeyboardFocusWithin && !_vadSpeakingSlider.IsMouseCaptureWithin) return;
                _vadSpeakingThreshold.Text = _vadSpeakingSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);
            };
            _vadSilenceSlider.ValueChanged += (_, _) =>
            {
                if (!_vadSilenceSlider.IsKeyboardFocusWithin && !_vadSilenceSlider.IsMouseCaptureWithin) return;
                _vadSilenceThreshold.Text = _vadSilenceSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);
            };
            _settingsControlsConfigured = true;
        }
        stack.Children.Add(_microphoneSelect);

        _localMicMute.Content = _config.LocalMicrophone.Muted ? T("settings.unmuteMic") : T("settings.muteMic");
        _localMicMute.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(_localMicMute);
        _captureMuteHotKey.Content = T("settings.muteHotkey", HotKeyLabel());
        _captureMuteHotKey.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(_captureMuteHotKey);

        _vadStatus.Foreground = Brush("#A1A1AA");
        _vadStatus.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(_vadStatus);
        _vadLevel.Minimum = 0;
        _vadLevel.Maximum = 0.12;
        _vadLevel.Height = 12;
        _vadLevel.Background = Brushes.Transparent;
        _vadLevel.Foreground = Brush("#60A5FA");
        stack.Children.Add(BuildVadLevelMeter());

        stack.Children.Add(SettingsSliderInput(T("settings.vadSpeakingThreshold"), T("settings.vadSpeakingThresholdDescription"), _vadSpeakingThreshold, _vadSpeakingSlider, _config.LocalMicrophone.SpeakingThreshold));
        stack.Children.Add(SettingsSliderInput(T("settings.vadSilenceThreshold"), T("settings.vadSilenceThresholdDescription"), _vadSilenceThreshold, _vadSilenceSlider, _config.LocalMicrophone.SilenceThreshold));
        stack.Children.Add(SettingsInput(T("settings.vadAttack"), T("settings.vadAttackDescription"), _vadAttackMs, _config.LocalMicrophone.AttackMs.ToString()));
        stack.Children.Add(SettingsInput(T("settings.vadRelease"), T("settings.vadReleaseDescription"), _vadReleaseMs, _config.LocalMicrophone.ReleaseMs.ToString()));

        var save = Button(T("settings.saveVad"));
        save.Click += (_, _) => SaveVadSettingsFromInputs();
        stack.Children.Add(save);

        stack.Children.Add(new TextBlock { Text = T("settings.application"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 28, 0, 14) });
        var minimizeToTray = new CheckBox
        {
            Content = T("settings.minimizeToTray"),
            Foreground = Brush("#F4F4F5"),
            IsChecked = _config.MinimizeToTray,
            Margin = new Thickness(0, 0, 0, 12)
        };
        minimizeToTray.Checked += (_, _) => SaveMinimizeToTray(true);
        minimizeToTray.Unchecked += (_, _) => SaveMinimizeToTray(false);
        stack.Children.Add(minimizeToTray);
        stack.Children.Add(Description(T("settings.minimizeToTrayDescription")));

        var debugMode = new CheckBox
        {
            Content = "Debug mode",
            Foreground = Brush("#F4F4F5"),
            IsChecked = _config.DebugMode,
            Margin = new Thickness(0, 0, 0, 12)
        };
        debugMode.Checked += (_, _) => SaveDebugMode(true);
        debugMode.Unchecked += (_, _) => SaveDebugMode(false);
        stack.Children.Add(debugMode);
        stack.Children.Add(Description(T("settings.debugDescription")));

        stack.Children.Add(new TextBlock { Text = "Discord", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 28, 0, 14) });
        stack.Children.Add(SettingsInput("Client ID", "ID приложения из Discord Developer Portal. По умолчанию используется встроенный Client ID.", _discordClientId, DiscordClientIdText()));
        var saveDiscord = Button(T("settings.saveDiscordClient"));
        saveDiscord.Margin = new Thickness(0, 0, 0, 12);
        saveDiscord.Click += SaveDiscordClientId_Click;
        stack.Children.Add(saveDiscord);
        stack.Children.Add(Description(T("settings.discordClientDescription")));

        stack.Children.Add(new TextBlock { Text = T("settings.overlay"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 28, 0, 14) });
        stack.Children.Add(SettingsInput(T("settings.port"), T("settings.portDescription"), _overlayPort, _config.OverlayPort.ToString(CultureInfo.InvariantCulture)));
        var savePort = Button(T("settings.savePort"));
        savePort.Margin = new Thickness(0, 0, 0, 12);
        savePort.Click += SaveOverlayPort_Click;
        stack.Children.Add(savePort);
        _overlayServerStatus.Foreground = _server is null ? Brush("#F97316") : Brush("#A1A1AA");
        _overlayServerStatus.TextWrapping = TextWrapping.Wrap;
        _overlayServerStatus.Text = OverlayServerStatusText();
        stack.Children.Add(_overlayServerStatus);
        UpdateVadStatus();
        return Panel(new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
    }

    private void RefreshMicrophoneDevices()
    {
        _microphoneDevices.Clear();
        foreach (var device in LocalMicrophoneVad.GetDevices())
        {
            _microphoneDevices.Add(device);
        }
    }

    private async void SaveDebugMode(bool enabled)
    {
        if (_config.DebugMode == enabled)
        {
            return;
        }

        _config = _config with { DebugMode = enabled };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        _logNav.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled && _activePage == "log")
        {
            ShowPage("settings");
        }
        else if (_activePage == "commands")
        {
            ShowPage("commands");
        }
    }

    private async void SaveMinimizeToTray(bool enabled)
    {
        if (_config.MinimizeToTray == enabled)
        {
            return;
        }

        _config = _config with { MinimizeToTray = enabled };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        if (!enabled)
        {
            HideTrayIcon();
        }
    }

    private void NormalizeOverlayPort()
    {
        var port = NormalizePort(_config.OverlayPort);
        if (port != _config.OverlayPort)
        {
            _config = _config with { OverlayPort = port };
            App.CurrentConfig = _config;
        }
    }

    private async void SaveOverlayPort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_overlayPort.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            _overlayServerStatus.Foreground = Brush("#F97316");
            _overlayServerStatus.Text = T("settings.portInvalid");
            return;
        }

        var port = NormalizePort(parsed);
        _overlayPort.Text = port.ToString(CultureInfo.InvariantCulture);
        if (port == _config.OverlayPort && _server is not null)
        {
            _overlayServerStatus.Foreground = Brush("#A1A1AA");
            _overlayServerStatus.Text = OverlayServerStatusText();
            return;
        }

        _config = _config with { OverlayPort = port };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        await StartOverlayServerAsync();
        RenderSelectedUser(updatePreview: true);
        _overlayServerStatus.Foreground = _server is null ? Brush("#F97316") : Brush("#A1A1AA");
        _overlayServerStatus.Text = OverlayServerStatusText();
    }

    private async Task StartOverlayServerAsync()
    {
        if (_voiceProvider is null)
        {
            return;
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }

        OverlayServer? server = null;
        try
        {
            server = new OverlayServer(_storage, _voiceProvider, _config.OverlayPort);
            await server.StartAsync(_shutdown.Token);
            _server = server;
            AddLog($"Overlay server started: {server.BaseUrl}");
        }
        catch (IOException error)
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
            AddLog($"Overlay server failed on port {_config.OverlayPort}: {error.Message}");
            _discordMessage.Text = T("discord.overlayUnavailable", _config.OverlayPort);
        }
        catch (InvalidOperationException error)
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
            AddLog($"Overlay server failed on port {_config.OverlayPort}: {error.Message}");
            _discordMessage.Text = T("discord.overlayUnavailable", _config.OverlayPort);
        }

        RenderSelectedUser(updatePreview: true);
    }

    private string OverlayBaseUrl => _server?.BaseUrl ?? $"http://localhost:{_config.OverlayPort}";
    private string OverlayApiBaseUrl => $"http://127.0.0.1:{_config.OverlayPort}";

    private string OverlayServerStatusText()
        => _server is null
            ? T("settings.overlayStopped", _config.OverlayPort)
            : T("settings.overlayStarted", _server.BaseUrl);

    private static int NormalizePort(int port) => Math.Clamp(port, 1, 65535);

    private string DiscordClientIdText()
        => string.IsNullOrWhiteSpace(_config.Discord.ClientId)
            ? DiscordVoiceProvider.DefaultClientId
            : _config.Discord.ClientId.Trim();

    private async void SaveDiscordClientId_Click(object sender, RoutedEventArgs e)
    {
        var clientId = _discordClientId.Text.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = DiscordVoiceProvider.DefaultClientId;
            _discordClientId.Text = clientId;
        }

        if (clientId.Length is < 17 or > 20 || clientId.Any(character => !char.IsDigit(character)))
        {
            _discordMessage.Text = T("settings.discordClientInvalid");
            return;
        }

        var storedClientId = clientId == DiscordVoiceProvider.DefaultClientId ? null : clientId;
        if (_config.Discord.ClientId == storedClientId)
        {
            _discordMessage.Text = T("settings.discordClientAlreadySaved");
            return;
        }

        _config = _config with
        {
            Discord = _config.Discord with { ClientId = storedClientId }
        };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        await RecreateDiscordProviderAsync();
        _discordMessage.Text = T("settings.discordClientSaved");
    }

    private async void SaveLocalMicrophoneSettings(Func<LocalMicrophoneSettings, LocalMicrophoneSettings> update)
    {
        _config = _config with { LocalMicrophone = update(_config.LocalMicrophone) };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        _localMicMute.Content = _config.LocalMicrophone.Muted ? T("settings.unmuteMic") : T("settings.muteMic");
        _dashboardLocalMicMute.Content = _localMicMute.Content;
        _captureMuteHotKey.Content = T("settings.muteHotkey", HotKeyLabel());
        RegisterMuteHotKey();
        UpdateLocalMicrophoneCapture();
        PublishLocalMicState();
        UpdateVadStatus();
    }

    private void SaveVadSettingsFromInputs()
    {
        var current = _config.LocalMicrophone;
        var speaking = double.TryParse(_vadSpeakingThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpeaking)
            ? Math.Max(0.001, parsedSpeaking)
            : current.SpeakingThreshold;
        var silence = double.TryParse(_vadSilenceThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSilence)
            ? Math.Max(0.001, parsedSilence)
            : current.SilenceThreshold;
        var attack = int.TryParse(_vadAttackMs.Text, out var parsedAttack) ? Math.Max(0, parsedAttack) : current.AttackMs;
        var release = int.TryParse(_vadReleaseMs.Text, out var parsedRelease) ? Math.Max(0, parsedRelease) : current.ReleaseMs;

        if (silence > speaking)
        {
            silence = speaking;
        }

        SaveLocalMicrophoneSettings(settings => settings with
        {
            SpeakingThreshold = speaking,
            SilenceThreshold = silence,
            AttackMs = attack,
            ReleaseMs = release
        });
        _vadSpeakingSlider.Value = Math.Clamp(speaking, _vadSpeakingSlider.Minimum, _vadSpeakingSlider.Maximum);
        _vadSilenceSlider.Value = Math.Clamp(silence, _vadSilenceSlider.Minimum, _vadSilenceSlider.Maximum);
        _vadSpeakingThreshold.Text = speaking.ToString("0.000", CultureInfo.InvariantCulture);
        _vadSilenceThreshold.Text = silence.ToString("0.000", CultureInfo.InvariantCulture);
        if (_activePage == "settings")
        {
            ShowPage("settings");
        }
    }

    private void LocalMicMute_Click(object sender, RoutedEventArgs e)
    {
        SaveLocalMicrophoneSettings(settings => settings with { Muted = !settings.Muted });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_isCapturingVoiceCommandHotKey)
        {
            e.Handled = true;
            var commandKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (commandKey is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return;
            }

            _isCapturingVoiceCommandHotKey = false;
            SaveSelectedVoiceCommandHotKey(commandKey, Keyboard.Modifiers);
            return;
        }

        if (!_isCapturingMuteHotKey)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        _isCapturingMuteHotKey = false;
        SaveLocalMicrophoneSettings(settings => settings with
        {
            MuteHotKey = key.ToString(),
            MuteHotKeyModifiers = modifiers.ToString()
        });
    }

    private string HotKeyLabel()
    {
        return string.IsNullOrWhiteSpace(_config.LocalMicrophone.MuteHotKey)
            ? T("common.notAssigned")
            : string.IsNullOrWhiteSpace(_config.LocalMicrophone.MuteHotKeyModifiers) || _config.LocalMicrophone.MuteHotKeyModifiers == "None"
                ? _config.LocalMicrophone.MuteHotKey
                : $"{_config.LocalMicrophone.MuteHotKeyModifiers}+{_config.LocalMicrophone.MuteHotKey}";
    }

    private static string VoiceCommandHotKeyLabel(VoiceCommandRule? rule)
    {
        if (rule is null || string.IsNullOrWhiteSpace(rule.HotKey))
        {
            return T("common.notAssigned");
        }

        return string.IsNullOrWhiteSpace(rule.HotKeyModifiers) || rule.HotKeyModifiers == "None"
            ? rule.HotKey
            : $"{rule.HotKeyModifiers}+{rule.HotKey}";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmHotKey = 0x0312;
        if (msg == wmHotKey && wParam.ToInt32() == LocalMuteHotKeyId)
        {
            SaveLocalMicrophoneSettings(settings => settings with { Muted = !settings.Muted });
            handled = true;
        }
        else if (msg == wmHotKey)
        {
            var hotKeyId = wParam.ToInt32();
            var index = hotKeyId - VoiceCommandHotKeyBaseId;
            var rules = _config.VoiceCommands.Rules.Where(rule => !string.IsNullOrWhiteSpace(rule.HotKey)).ToList();
            if (index >= 0 && index < rules.Count)
            {
                TriggerVoiceCommand(rules[index], "hotkey");
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void RegisterMuteHotKey()
    {
        if (_hwndSource is null)
        {
            return;
        }

        UnregisterMuteHotKey();
        if (string.IsNullOrWhiteSpace(_config.LocalMicrophone.MuteHotKey)
            || !Enum.TryParse<Key>(_config.LocalMicrophone.MuteHotKey, out var key))
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return;
        }

        _ = RegisterHotKey(
            _hwndSource.Handle,
            LocalMuteHotKeyId,
            ModifiersFromString(_config.LocalMicrophone.MuteHotKeyModifiers),
            (uint)virtualKey);
    }

    private void RegisterVoiceCommandHotKeys()
    {
        if (_hwndSource is null)
        {
            return;
        }

        UnregisterVoiceCommandHotKeys();
        var rules = _config.VoiceCommands.Rules.Where(rule => !string.IsNullOrWhiteSpace(rule.HotKey)).ToList();
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            if (!Enum.TryParse<Key>(rule.HotKey, out var key))
            {
                continue;
            }

            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0)
            {
                continue;
            }

            _ = RegisterHotKey(
                _hwndSource.Handle,
                VoiceCommandHotKeyBaseId + index,
                ModifiersFromString(rule.HotKeyModifiers),
                (uint)virtualKey);
        }
    }

    private void UnregisterVoiceCommandHotKeys()
    {
        if (_hwndSource is null)
        {
            return;
        }

        for (var index = 0; index < 100; index++)
        {
            _ = UnregisterHotKey(_hwndSource.Handle, VoiceCommandHotKeyBaseId + index);
        }
    }

    private void UnregisterMuteHotKey()
    {
        if (_hwndSource is not null)
        {
            _ = UnregisterHotKey(_hwndSource.Handle, LocalMuteHotKeyId);
        }
    }

    private static uint ModifiersFromString(string value)
    {
        uint modifiers = 0;
        if (value.Contains("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0001;
        if (value.Contains("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0002;
        if (value.Contains("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0004;
        if (value.Contains("Windows", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0008;
        return modifiers;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void UpdateLocalMicrophoneCapture()
    {
        var settings = _config.LocalMicrophone;
        var shouldListen = settings.Enabled && _authenticatedUserMutedInDiscord && !settings.Muted;
        if (shouldListen)
        {
            _localVad.Start(settings);
        }
        else
        {
            _localVad.Stop();
            _localMicSpeaking = false;
        }

        PublishLocalMicState();
        UpdateVadStatus();
    }

    private void PublishLocalMicState()
    {
        _voiceProvider?.SetLocalMicrophoneState(
            _config.LocalMicrophone.Enabled && _authenticatedUserMutedInDiscord,
            _config.LocalMicrophone.Muted,
            _localMicSpeaking);
    }

    private void UpdateVadStatus(string? error = null)
    {
        string text;
        if (!string.IsNullOrWhiteSpace(error))
        {
            text = T("settings.error", error);
        }
        else if (!_config.LocalMicrophone.Enabled)
        {
            text = T("settings.vadOff");
        }
        else if (!_authenticatedUserMutedInDiscord)
        {
            text = T("settings.vadWaitingDiscordMute");
        }
        else if (_config.LocalMicrophone.Muted)
        {
            text = T("settings.localMicMuted");
        }
        else
        {
            text = _localMicSpeaking ? T("settings.speechDetected") : T("settings.listeningMic");
        }

        _vadStatus.Text = text;
        _dashboardVadStatus.Text = text;
        _dashboardHotKey.Text = T("hotkey.label", HotKeyLabel());
        _dashboardLocalMicMute.Content = _config.LocalMicrophone.Muted ? T("settings.unmuteMic") : T("settings.muteMic");
        _localMicMute.Content = _dashboardLocalMicMute.Content;
    }
}
