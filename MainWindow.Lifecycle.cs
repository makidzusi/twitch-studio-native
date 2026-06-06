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
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _config = await _storage.ReadConfigAsync(_shutdown.Token);
        var language = LocalizationManager.ResolveDefaultLanguage(_config.Language);
        LocalizationManager.SetLanguage(language);
        _config = _config with { Language = language };
        NormalizeOverlayPort();
        NormalizeVoiceCommandSettings();
        App.CurrentConfig = _config;
        _languageSelect.SelectedValue = language;
        ApplyLanguage();
        _logNav.Visibility = _config.DebugMode ? Visibility.Visible : Visibility.Collapsed;
        SyncKnownUsers(_config.KnownUsers.Values.Select(ToVoiceUser).OrderBy(user => user.DisplayName).ToList());
        RenderSelectedUser(updatePreview: true);
        _localVad.SpeakingChanged += speaking => Dispatcher.Invoke(() =>
        {
            _localMicSpeaking = speaking;
            PublishLocalMicState();
            UpdateVadStatus();
        });
        _localVad.LevelChanged += level => Dispatcher.Invoke(() => _vadLevel.Value = level);
        _localVad.Error += message => Dispatcher.Invoke(() =>
        {
            AddLog($"Local microphone VAD: {message}");
            UpdateVadStatus(message);
        });
        _voiceCommands.CommandRecognized += (rule, confidence) => Dispatcher.Invoke(() => ApplyVoiceCommand(rule, confidence));
        _voiceCommands.DebugChanged += debug => Dispatcher.Invoke(() => ApplyVoiceCommandDebug(debug));
        _voiceCommands.StatusChanged += message => Dispatcher.Invoke(() => UpdateVoiceCommandStatus(message));
        _voiceCommands.Error += message => Dispatcher.Invoke(() =>
        {
            AddLog($"Voice commands: {message}");
            UpdateVoiceCommandStatus(T("voice.status.error", message));
        });
        _voiceProvider = CreateDiscordProvider();
        await StartOverlayServerAsync();
        UpdateDiscordStatus(ConnectionStatus.disconnected, T("discord.status.disconnected"));
        UpdateDiscordMessageForLanguage();
        RestartVoiceCommandCapture();
    }

    private DiscordVoiceProvider CreateDiscordProvider()
    {
        var provider = new DiscordVoiceProvider(_config.Discord);
        provider.Log += message => Dispatcher.Invoke(() => AddLog(message));
        provider.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            UpdateDiscordStatus(status.Status, StatusLabel(status.Status));
            _discordMessage.Text = status.Message ?? "";
            _reconnectDiscord.Content = status.Status == ConnectionStatus.connected ? T("discord.reconnect") : T("discord.connect");
        });
        provider.SnapshotChanged += snapshot => Dispatcher.Invoke(() => ApplySnapshot(snapshot));
        provider.VoiceSettingsChanged += settings => Dispatcher.Invoke(() =>
        {
            _authenticatedUserMutedInDiscord = settings.IsMuted;
            UpdateLocalMicrophoneCapture();
        });
        return provider;
    }

    private async Task RecreateDiscordProviderAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }

        if (_voiceProvider is not null)
        {
            await _voiceProvider.DisposeAsync();
        }

        _voiceProvider = CreateDiscordProvider();
        _authenticatedUserMutedInDiscord = false;
        UpdateLocalMicrophoneCapture();
        await StartOverlayServerAsync();
        UpdateDiscordStatus(ConnectionStatus.disconnected, T("discord.status.disconnected"));
        _reconnectDiscord.Content = T("discord.connect");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        RegisterMuteHotKey();
        RegisterVoiceCommandHotKeys();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_isRestoringFromTray || !_config.MinimizeToTray || WindowState != WindowState.Minimized)
        {
            return;
        }

        EnsureTrayIcon();
        Hide();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(T("tray.open"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(T("tray.exit"), null, (_, _) =>
        {
            _trayIcon!.Visible = false;
            Close();
        });

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Twitch Studio Native",
            Icon = TrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static System.Drawing.Icon TrayIcon()
    {
        var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrWhiteSpace(path)
            ? System.Drawing.SystemIcons.Application
            : System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application;
    }

    private void RestoreFromTray()
    {
        _isRestoringFromTray = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _isRestoringFromTray = false;
        HideTrayIcon();
    }

    private void HideTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
    }

    private void AddLog(string message)
    {
        _logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (_logs.Count > 1000)
        {
            _logs.RemoveAt(0);
        }

        _logList.ScrollIntoView(_logs[^1]);
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _shutdown.Cancel();
        UnregisterMuteHotKey();
        UnregisterVoiceCommandHotKeys();
        _hwndSource?.RemoveHook(WndProc);
        StateChanged -= MainWindow_StateChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _voiceCommandResetTimer?.Stop();
        _voiceCommands.Dispose();
        _localVad.Dispose();
        if (_server is not null) await _server.DisposeAsync();
        if (_voiceProvider is not null) await _voiceProvider.DisposeAsync();
    }

    private void ApplySnapshot(VoiceSnapshot snapshot)
    {
        var users = snapshot.Channels.SelectMany(channel => channel.Users).OrderBy(user => user.DisplayName).ToList();
        var authenticatedUser = string.IsNullOrWhiteSpace(snapshot.AuthenticatedUserId)
            ? null
            : users.FirstOrDefault(user => user.Id == snapshot.AuthenticatedUserId);
        if (_voiceProvider?.VoiceSettings is null && authenticatedUser is not null)
        {
            _authenticatedUserMutedInDiscord = authenticatedUser.State == AnimationState.muted;
            UpdateLocalMicrophoneCapture();
        }
        RememberKnownUsers(users);
        if (_activePage == "members")
        {
            _isSyncingUsers = true;
            try
            {
                SyncUsers(users);
                SyncSelectedItems();
            }
            finally
            {
                _isSyncingUsers = false;
            }

            return;
        }

        var selectedId = _selectedUser?.Id;
        VoiceUser? selectedListItem = null;
        var selectionChanged = false;
        var shouldClearSelection = false;

        _isSyncingUsers = true;
        try
        {
            SyncUsers(users);

            var nextSelected = users.FirstOrDefault(user => user.Id == selectedId) ?? users.FirstOrDefault();
            if (nextSelected is null)
            {
                shouldClearSelection = _selectedUser is not null;
                _selectedUser = null;
                if (_usersList.SelectedItem is not null)
                {
                    _usersList.SelectedItem = null;
                }
            }
            else
            {
                selectionChanged = nextSelected.Id != _selectedUser?.Id;
                selectedListItem = _users.FirstOrDefault(user => user.Id == nextSelected.Id) ?? nextSelected;
                _selectedUser = selectedListItem;
                if (!ReferenceEquals(_usersList.SelectedItem, selectedListItem))
                {
                    _usersList.SelectedItem = selectedListItem;
                }
            }
        }
        finally
        {
            _isSyncingUsers = false;
        }

        if (selectionChanged)
        {
            RenderSelectedUser(updatePreview: true);
        }
        else if (shouldClearSelection)
        {
            RenderSelectedUser(updatePreview: true);
        }
    }

    private void SyncUsers(IReadOnlyList<VoiceUser> users)
    {
        for (var index = _users.Count - 1; index >= 0; index--)
        {
            if (users.All(user => user.Id != _users[index].Id))
            {
                _users.RemoveAt(index);
            }
        }

        for (var index = 0; index < users.Count; index++)
        {
            var user = users[index];
            var existingIndex = -1;
            for (var currentIndex = 0; currentIndex < _users.Count; currentIndex++)
            {
                if (_users[currentIndex].Id == user.Id)
                {
                    existingIndex = currentIndex;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                _users.Insert(Math.Min(index, _users.Count), user);
                continue;
            }

            if (!Equals(_users[existingIndex], user))
            {
                _users[existingIndex] = user;
            }

            if (existingIndex != index && index < _users.Count)
            {
                _users.Move(existingIndex, index);
            }
        }
    }

    private void RememberKnownUsers(IReadOnlyList<VoiceUser> users)
    {
        var known = new Dictionary<string, DiscordUser>(_config.KnownUsers);
        var changed = false;
        foreach (var user in users)
        {
            var remembered = ToDiscordUser(user);
            if (!known.TryGetValue(user.Id, out var existing) || !Equals(existing, remembered))
            {
                known[user.Id] = remembered;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        _config = _config with { KnownUsers = known };
        App.CurrentConfig = _config;
        SyncKnownUsers(known.Values.Select(ToVoiceUser).OrderBy(user => user.DisplayName).ToList());
        _ = _storage.WriteConfigAsync(_config, _shutdown.Token);
    }

    private void SyncKnownUsers(IReadOnlyList<VoiceUser> users)
    {
        var wasSyncing = _isSyncingUsers;
        _isSyncingUsers = true;
        try
        {
            for (var index = _knownUsers.Count - 1; index >= 0; index--)
            {
                if (users.All(user => user.Id != _knownUsers[index].Id))
                {
                    _knownUsers.RemoveAt(index);
                }
            }

            for (var index = 0; index < users.Count; index++)
            {
                var user = users[index];
                var existingIndex = -1;
                for (var currentIndex = 0; currentIndex < _knownUsers.Count; currentIndex++)
                {
                    if (_knownUsers[currentIndex].Id == user.Id)
                    {
                        existingIndex = currentIndex;
                        break;
                    }
                }

                if (existingIndex < 0)
                {
                    _knownUsers.Insert(Math.Min(index, _knownUsers.Count), user);
                    continue;
                }

                if (!Equals(_knownUsers[existingIndex], user))
                {
                    _knownUsers[existingIndex] = user;
                }

                if (existingIndex != index && index < _knownUsers.Count)
                {
                    _knownUsers.Move(existingIndex, index);
                }
            }
        }
        finally
        {
            _isSyncingUsers = wasSyncing;
        }
    }

    private static DiscordUser ToDiscordUser(VoiceUser user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Discriminator = user.Discriminator,
        GlobalName = user.GlobalName,
        DisplayName = user.DisplayName,
        AvatarUrl = user.AvatarUrl,
        Bot = user.Bot
    };

    private static VoiceUser ToVoiceUser(DiscordUser user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Discriminator = user.Discriminator,
        GlobalName = user.GlobalName,
        DisplayName = user.DisplayName,
        AvatarUrl = user.AvatarUrl,
        Bot = user.Bot,
        State = AnimationState.idle
    };

    private async void ReconnectDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceProvider is null)
        {
            return;
        }

        _reconnectDiscord.IsEnabled = false;
        try
        {
            await _voiceProvider.ReconnectAsync(_shutdown.Token);
        }
        finally
        {
            _reconnectDiscord.IsEnabled = true;
        }
    }

    private async void ExportOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUser is null)
        {
            return;
        }

        if (_server is null)
        {
            AddLog("Overlay export failed: local overlay server is not running.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Overlay archive|*.zip",
            FileName = $"{SanitizeFileName(_selectedUser.Id)}-overlay.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            var url = $"{OverlayApiBaseUrl}/api/overlays/{Uri.EscapeDataString(_selectedUser.Id)}/export";
            await using var responseStream = await client.GetStreamAsync(url, _shutdown.Token);
            await using var fileStream = File.Create(dialog.FileName);
            await responseStream.CopyToAsync(fileStream, _shutdown.Token);
        }
        catch (Exception error)
        {
            AddLog($"Overlay export failed: {error.Message}");
        }
    }

    private async void ImportOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUser is null)
        {
            return;
        }

        if (_server is null)
        {
            AddLog("Overlay import failed: local overlay server is not running.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Overlay archive|*.zip|All files|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            await using var fileStream = File.OpenRead(dialog.FileName);
            using var content = new StreamContent(fileStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            var url = $"{OverlayApiBaseUrl}/api/overlays/{Uri.EscapeDataString(_selectedUser.Id)}/import";
            using var response = await client.PostAsync(url, content, _shutdown.Token);
            response.EnsureSuccessStatusCode();
            _config = await _storage.ReadConfigAsync(_shutdown.Token);
            App.CurrentConfig = _config;
            RenderStateRows();
            UpdatePreview(force: true);
        }
        catch (Exception error)
        {
            AddLog($"Overlay import failed: {error.Message}");
        }
    }

    private async Task SaveFrameDurationAsync(AnimationState state)
    {
        if (_selectedUser is null || !_durationInputs.TryGetValue(state, out var input))
        {
            return;
        }

        if (!int.TryParse(input.Text, out var parsed))
        {
            RenderStateRows();
            return;
        }

        var frameDuration = Math.Max(16, parsed);
        input.Text = frameDuration.ToString();
        var settings = _config.Overlays.TryGetValue(_selectedUser.Id, out var existing)
            ? existing
            : new UserOverlaySettings { UserId = _selectedUser.Id };
        settings.Animations.TryGetValue(state, out var animation);
        if (animation is not null && animation.FrameDurationMs == frameDuration)
        {
            return;
        }

        animation ??= new OverlayAnimation { State = state };
        settings = settings with
        {
            Animations = new Dictionary<AnimationState, OverlayAnimation>(settings.Animations)
            {
                [state] = animation with
                {
                    FrameDurationMs = frameDuration,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                }
            }
        };
        _config = _config with { Overlays = new Dictionary<string, UserOverlaySettings>(_config.Overlays) { [_selectedUser.Id] = settings } };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        await SaveServerConfigAsync();
        UpdatePreview(force: true);
    }

    private async void ImportAsset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUser is null || sender is not FrameworkElement { Tag: AnimationState state }) return;
        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg|All files|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var settings = _config.Overlays.TryGetValue(_selectedUser.Id, out var existing) ? existing : new UserOverlaySettings { UserId = _selectedUser.Id };
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
                UserId = _selectedUser.Id,
                State = state,
                FileName = Path.GetFileName(fileName),
                MimeType = MimeFromExtension(extension),
                Url = $"/assets/{storedName}",
                Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SizeBytes = new FileInfo(storedPath).Length,
                ImportedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }
        var frameDuration = int.TryParse(_durationInputs[state].Text, out var parsed) ? Math.Max(16, parsed) : 120;
        settings = settings with
        {
            Assets = new Dictionary<AnimationState, OverlayAsset>(settings.Assets) { [state] = frames[0] },
            Animations = new Dictionary<AnimationState, OverlayAnimation>(settings.Animations)
            {
                [state] = new OverlayAnimation { State = state, Frames = frames, FrameDurationMs = frameDuration, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") }
            }
        };
        _config = _config with { Overlays = new Dictionary<string, UserOverlaySettings>(_config.Overlays) { [_selectedUser.Id] = settings } };
        App.CurrentConfig = _config;
        await _storage.WriteConfigAsync(_config, _shutdown.Token);
        await SaveServerConfigAsync();
        RenderStateRows();
        UpdatePreview(force: true);
    }

    private async Task SaveServerConfigAsync()
    {
        if (_server is null)
        {
            return;
        }

        using var client = new HttpClient();
        var content = new StringContent(JsonSerializer.Serialize(_config, Json.Options), System.Text.Encoding.UTF8, "application/json");
        await client.PutAsync($"{OverlayApiBaseUrl}/api/config", content, _shutdown.Token);
    }
}
