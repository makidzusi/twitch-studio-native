using System.Text.Json.Nodes;

namespace TwitchStudioNative.Discord;

public sealed class DiscordVoiceProvider : IAsyncDisposable
{
    public const string DefaultClientId = "1482927951921025216";

    private readonly DiscordConnectionSettings _settings;
    private readonly DiscordIpcClient _client = new();
    private readonly HashSet<string> _speaking = [];
    private readonly Dictionary<string, JsonObject> _entries = [];
    private readonly HashSet<string> _subscriptions = [];

    private VoiceSnapshot _snapshot = new();
    private VoiceSettingsSnapshot? _voiceSettings;
    private ConnectionStatusUpdate _status = new(ConnectionStatus.disconnected, "RPC provider is stopped");
    private bool _localMicActive;
    private bool _localMicMuted;
    private bool _localMicSpeaking;
    private string? _voiceCommandActionId;
    private bool _authenticatedUserMutedInDiscord;
    private bool _isRestarting;

    public event Action<ConnectionStatusUpdate>? StatusChanged;
    public event Action<VoiceSnapshot>? SnapshotChanged;
    public event Action<string, VoiceUser>? UserStateChanged;
    public event Action<VoiceSettingsSnapshot>? VoiceSettingsChanged;
    public event Action<string>? Log;

    public ConnectionStatusUpdate Status => _status;
    public VoiceSnapshot Snapshot => _snapshot;
    public VoiceSettingsSnapshot? VoiceSettings => _voiceSettings;

    public void SetLocalMicrophoneState(bool isActive, bool isMuted, bool isSpeaking)
    {
        if (_localMicActive == isActive && _localMicMuted == isMuted && _localMicSpeaking == isSpeaking)
        {
            return;
        }

        _localMicActive = isActive;
        _localMicMuted = isMuted;
        _localMicSpeaking = isSpeaking;
        _snapshot = _snapshot with { Channels = ApplyLocalMicrophoneOverride(_snapshot.Channels) };
        PublishSnapshot();
        PublishAuthenticatedUserState();
    }

    public void SetVoiceCommandAction(string? actionId)
    {
        actionId = string.IsNullOrWhiteSpace(actionId) ? null : actionId;
        if (_voiceCommandActionId == actionId)
        {
            return;
        }

        _voiceCommandActionId = actionId;
        _snapshot = _snapshot with { Channels = ApplyLocalMicrophoneOverride(_snapshot.Channels) };
        PublishSnapshot();
        PublishAuthenticatedUserState();
    }

    public DiscordVoiceProvider(DiscordConnectionSettings settings)
    {
        _settings = settings;
        _client.Dispatch += HandleDispatch;
        _client.Log += message => Log?.Invoke(message);
        _client.Disconnected += message =>
        {
            SetStatus(ConnectionStatus.disconnected, message);
            _subscriptions.Clear();
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            SetStatus(ConnectionStatus.connecting, attempt == 1
                ? "Connecting to Discord RPC"
                : $"Connecting to Discord RPC, retry {attempt}/{maxAttempts}");
            try
            {
                await _client.LoginAsync(EffectiveClientId(_settings), cancellationToken);
                ApplyAuthenticatedUser();
                await SubscribeBaseAsync(cancellationToken);
                await RefreshAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (attempt < maxAttempts && IsTransientStartupDisconnect(error))
            {
                Log?.Invoke($"Discord RPC startup retry after transient disconnect: {error.Message}");
                await _client.ResetAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(450 * attempt), cancellationToken);
            }
            catch (Exception error)
            {
                SetStatus(ConnectionStatus.error, error.Message);
                return;
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var selectedChannel = await _client.TryRequestAsync("GET_SELECTED_VOICE_CHANNEL", new JsonObject(), null, cancellationToken);
            if (selectedChannel.Error is not null)
            {
                SetStatus(ConnectionStatus.error, selectedChannel.Error);
                return;
            }

            var channel = selectedChannel.Data ?? new JsonObject();
            if (channel.Count == 0 || channel["id"] is null)
            {
                _snapshot = _snapshot with { Channels = [], SelectedChannelId = null };
                PublishSnapshot();
                SetStatus(ConnectionStatus.connected, "Connected to Discord RPC, no active voice channel");
                return;
            }

            if (!await JoinChannelAsync(channel["id"]!.GetValue<string>(), cancellationToken))
            {
                return;
            }

            await RefreshVoiceSettingsAsync(cancellationToken);
        }
        catch (Exception error)
        {
            SetStatus(ConnectionStatus.error, error.Message);
        }
    }

    public Task ReconnectAsync(CancellationToken cancellationToken) => RestartAsync(cancellationToken);

    private async Task<bool> JoinChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var channelResponse = await _client.TryRequestAsync("GET_CHANNEL", new JsonObject { ["channel_id"] = channelId }, null, cancellationToken);
        if (channelResponse.Error is not null)
        {
            SetStatus(ConnectionStatus.error, channelResponse.Error);
            return false;
        }

        var channel = channelResponse.Data ?? new JsonObject();
        var voiceChannel = ToVoiceChannel(channel);

        _entries.Clear();
        foreach (var entry in VoiceStateEntries(channel))
        {
            var id = entry["user"]?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
            {
                _entries[id] = entry;
            }
        }

        _snapshot = new VoiceSnapshot
        {
            Channels = ApplyLocalMicrophoneOverride([voiceChannel]),
            SelectedChannelId = voiceChannel.Id,
            AuthenticatedUserId = _snapshot.AuthenticatedUserId,
            AuthenticatedUser = _snapshot.AuthenticatedUser
        };
        await SubscribeSelectedAsync(voiceChannel.Id, cancellationToken);
        PublishSnapshot();
        SetStatus(ConnectionStatus.connected, $"Connected to Discord RPC: {voiceChannel.Name}");

        foreach (var user in voiceChannel.Users)
        {
            UserStateChanged?.Invoke(voiceChannel.Id, user);
        }

        return true;
    }

    private async Task RefreshVoiceSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _client.TryRequestAsync("GET_VOICE_SETTINGS", new JsonObject(), null, cancellationToken);
            if (settings.Error is not null)
            {
                return;
            }

            _voiceSettings = ToVoiceSettings(settings.Data ?? new JsonObject(), _voiceSettings);
            VoiceSettingsChanged?.Invoke(_voiceSettings);
        }
        catch
        {
            // Discord can deny this command while voice is reconnecting.
        }
    }

    private async Task SubscribeBaseAsync(CancellationToken cancellationToken)
    {
        foreach (var evt in new[] { "VOICE_CHANNEL_SELECT", "VOICE_SETTINGS_UPDATE", "VOICE_CONNECTION_STATUS", "SPEAKING_START", "SPEAKING_STOP" })
        {
            await SubscribeAsync(evt, null, cancellationToken);
        }
    }

    private async Task SubscribeSelectedAsync(string channelId, CancellationToken cancellationToken)
    {
        foreach (var evt in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" })
        {
            await SubscribeAsync(evt, channelId, cancellationToken);
        }
    }

    private async Task SubscribeAsync(string evt, string? channelId, CancellationToken cancellationToken)
    {
        var key = channelId is null ? evt : $"{evt}:{channelId}";
        if (!_subscriptions.Add(key))
        {
            return;
        }

        try
        {
            await _client.SubscribeAsync(evt, channelId, cancellationToken);
        }
        catch (Exception error)
        {
            _subscriptions.Remove(key);
            SetStatus(IsBrokenPipe(error.Message) ? ConnectionStatus.error : ConnectionStatus.connected, $"{evt} unavailable: {error.Message}");
        }
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        if (_isRestarting)
        {
            return;
        }

        _isRestarting = true;
        try
        {
            SetStatus(ConnectionStatus.connecting, "Discord RPC reconnecting");
            _subscriptions.Clear();
            _entries.Clear();
            _speaking.Clear();
            await _client.ResetAsync();
            await StartAsync(cancellationToken);
        }
        finally
        {
            _isRestarting = false;
        }
    }

    private static bool IsBrokenPipe(string message)
    {
        return message.Contains("Pipe is broken", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase)
               || message.Contains("disconnected", StringComparison.OrdinalIgnoreCase);
    }

    private static string EffectiveClientId(DiscordConnectionSettings settings)
        => string.IsNullOrWhiteSpace(settings.ClientId)
            ? DefaultClientId
            : settings.ClientId.Trim();

    private static bool IsTransientStartupDisconnect(Exception error)
    {
        var message = error.Message;
        return message.Contains("disconnected during IPC read", StringComparison.OrdinalIgnoreCase)
               || message.Contains("did not send READY", StringComparison.OrdinalIgnoreCase)
               || IsBrokenPipe(message);
    }

    private void HandleDispatch(JsonObject message)
    {
        var evt = message["evt"]?.GetValue<string>();
        var data = message["data"]?.AsObject();
        if (evt is null || data is null)
        {
            return;
        }

        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                _ = RefreshAsync(CancellationToken.None);
                break;
            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                HandleVoiceState(data, true);
                break;
            case "VOICE_STATE_DELETE":
                HandleVoiceState(data, false);
                break;
            case "SPEAKING_START":
                HandleSpeaking(data, true);
                break;
            case "SPEAKING_STOP":
                HandleSpeaking(data, false);
                break;
            case "VOICE_SETTINGS_UPDATE":
                _voiceSettings = ToVoiceSettings(data, _voiceSettings);
                VoiceSettingsChanged?.Invoke(_voiceSettings);
                break;
            case "VOICE_CONNECTION_STATUS":
                if (data["state"]?.GetValue<string>() is "VOICE_CONNECTED" or "CONNECTED")
                {
                    _ = RefreshAsync(CancellationToken.None);
                }
                break;
        }
    }

    private void HandleVoiceState(JsonObject entry, bool connected)
    {
        var channelId = entry["channel_id"]?.GetValue<string>() ?? _snapshot.SelectedChannelId;
        var userId = entry["user"]?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
        {
            _ = RefreshAsync(CancellationToken.None);
            return;
        }

        if (!connected)
        {
            _entries.Remove(userId);
            _speaking.Remove(userId);
        }
        else
        {
            _entries[userId] = entry;
        }

        PublishUser(channelId, entry, connected);
    }

    private void HandleSpeaking(JsonObject data, bool speaking)
    {
        var userId = data["user_id"]?.GetValue<string>() ?? data["userId"]?.GetValue<string>() ?? data["id"]?.GetValue<string>();
        var channelId = data["channel_id"]?.GetValue<string>() ?? data["channelId"]?.GetValue<string>() ?? _snapshot.SelectedChannelId;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }

        if (speaking) _speaking.Add(userId); else _speaking.Remove(userId);

        if (!_entries.TryGetValue(userId, out var entry))
        {
            var existing = _snapshot.Channels.SelectMany(channel => channel.Users).FirstOrDefault(user => user.Id == userId);
            entry = new JsonObject
            {
                ["user"] = new JsonObject { ["id"] = userId, ["username"] = existing?.Username ?? userId, ["global_name"] = existing?.GlobalName },
                ["nick"] = existing?.DisplayName ?? userId,
                ["voice_state"] = new JsonObject()
            };
            _entries[userId] = entry;
        }

        PublishUser(channelId, entry, true);
    }

    private void PublishUser(string channelId, JsonObject entry, bool connected)
    {
        var user = ApplyLocalMicrophoneOverride(ToVoiceUser(entry, connected));
        var channel = _snapshot.Channels.FirstOrDefault(item => item.Id == channelId);
        if (channel is not null)
        {
            var index = channel.Users.FindIndex(item => item.Id == user.Id);
            if (!connected)
            {
                channel.Users.RemoveAll(item => item.Id == user.Id);
            }
            else if (index >= 0)
            {
                channel.Users[index] = user;
            }
            else
            {
                channel.Users.Add(user);
            }
        }

        UserStateChanged?.Invoke(channelId, user);
        PublishSnapshot();
    }

    private VoiceChannel ToVoiceChannel(JsonObject channel)
    {
        var users = VoiceStateEntries(channel).Select(entry => ApplyLocalMicrophoneOverride(ToVoiceUser(entry, true))).ToList();
        return new VoiceChannel
        {
            Id = channel["id"]?.GetValue<string>() ?? "",
            Name = channel["name"]?.GetValue<string>() ?? "Voice channel",
            GuildId = channel["guild_id"]?.GetValue<string>(),
            Users = users
        };
    }

    private static IEnumerable<JsonObject> VoiceStateEntries(JsonObject channel)
    {
        return channel["voice_states"]?.AsArray()
            .Select(item => item as JsonObject)
            .Where(item => item?["user"]?["id"] is not null)
            .Select(item => item!)
            ?? [];
    }

    private VoiceUser ToVoiceUser(JsonObject entry, bool connected)
    {
        var user = entry["user"]?.AsObject();
        var userId = user?["id"]?.GetValue<string>() ?? $"unknown-{DisplayName(entry)}";
        var state = entry["voice_state"]?.AsObject();
        var muted = Bool(state, "mute") || Bool(state, "self_mute") || Bool(entry, "mute");
        var deafened = Bool(state, "deaf") || Bool(state, "self_deaf");
        var activity = VoiceStateResolver.Resolve(connected, deafened, muted, _speaking.Contains(userId));
        if (userId == _snapshot.AuthenticatedUserId)
        {
            _authenticatedUserMutedInDiscord = connected && muted;
        }

        return new VoiceUser
        {
            Id = userId,
            Username = user?["username"]?.GetValue<string>() ?? DisplayName(entry),
            Discriminator = user?["discriminator"]?.GetValue<string>(),
            GlobalName = user?["global_name"]?.GetValue<string>(),
            DisplayName = DisplayName(entry),
            AvatarUrl = AvatarUrl(user),
            Bot = user?["bot"]?.GetValue<bool>(),
            State = activity,
            IsStreaming = connected && Bool(state, "self_stream"),
            IsVideoEnabled = connected && Bool(state, "self_video"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private void ApplyAuthenticatedUser()
    {
        if (_client.CurrentUser is null)
        {
            return;
        }

        var user = _client.CurrentUser;
        var discordUser = new DiscordUser
        {
            Id = user["id"]?.GetValue<string>() ?? "",
            Username = user["username"]?.GetValue<string>() ?? "",
            Discriminator = user["discriminator"]?.GetValue<string>(),
            GlobalName = user["global_name"]?.GetValue<string>(),
            DisplayName = user["global_name"]?.GetValue<string>() ?? user["username"]?.GetValue<string>() ?? "",
            AvatarUrl = AvatarUrl(user),
            Bot = user["bot"]?.GetValue<bool>()
        };
        _snapshot = _snapshot with { AuthenticatedUserId = discordUser.Id, AuthenticatedUser = discordUser };
    }

    private static VoiceSettingsSnapshot ToVoiceSettings(JsonObject settings, VoiceSettingsSnapshot? previous)
    {
        return new VoiceSettingsSnapshot
        {
            IsMuted = settings["mute"]?.GetValue<bool>() ?? previous?.IsMuted ?? false,
            IsDeafened = settings["deaf"]?.GetValue<bool>() ?? previous?.IsDeafened ?? false,
            Input = ToDevice(settings["input"]?.AsObject()) ?? previous?.Input,
            Output = ToDevice(settings["output"]?.AsObject()) ?? previous?.Output,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private static VoiceDeviceSettings? ToDevice(JsonObject? device)
    {
        if (device is null) return null;
        return new VoiceDeviceSettings
        {
            DeviceId = device["device_id"]?.GetValue<string>() ?? device["device"]?.GetValue<string>(),
            Volume = device["volume"]?.GetValue<double>()
        };
    }

    private static bool Bool(JsonObject? obj, string key) => obj?[key]?.GetValue<bool>() ?? false;

    private static string DisplayName(JsonObject entry)
    {
        return entry["nick"]?.GetValue<string>()
               ?? entry["user"]?["global_name"]?.GetValue<string>()
               ?? entry["user"]?["username"]?.GetValue<string>()
               ?? entry["user"]?["id"]?.GetValue<string>()
               ?? "Unknown user";
    }

    private static string? AvatarUrl(JsonObject? user)
    {
        var avatar = user?["avatar"]?.GetValue<string>();
        var id = user?["id"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(avatar) || string.IsNullOrWhiteSpace(id)
            ? null
            : $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png";
    }

    private void PublishSnapshot() => SnapshotChanged?.Invoke(_snapshot);

    private List<VoiceChannel> ApplyLocalMicrophoneOverride(List<VoiceChannel> channels)
    {
        return channels.Select(channel => channel with
        {
            Users = channel.Users.Select(ApplyLocalMicrophoneOverride).ToList()
        }).ToList();
    }

    private VoiceUser ApplyLocalMicrophoneOverride(VoiceUser user)
    {
        if (_voiceCommandActionId is { } actionId && user.Id == _snapshot.AuthenticatedUserId)
        {
            return user with
            {
                ActiveActionId = actionId,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };
        }

        if (!_localMicActive
            || user.Id != _snapshot.AuthenticatedUserId
            || !_authenticatedUserMutedInDiscord)
        {
            return user;
        }

        return user with
        {
            State = _localMicMuted ? AnimationState.muted : _localMicSpeaking ? AnimationState.speaking : AnimationState.idle,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private void PublishAuthenticatedUserState()
    {
        var authenticatedUserId = _snapshot.AuthenticatedUserId;
        if (string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            return;
        }

        foreach (var channel in _snapshot.Channels)
        {
            var user = channel.Users.FirstOrDefault(item => item.Id == authenticatedUserId);
            if (user is not null)
            {
                UserStateChanged?.Invoke(channel.Id, user);
                return;
            }
        }
    }

    private void SetStatus(ConnectionStatus status, string? message)
    {
        _status = new ConnectionStatusUpdate(status, message);
        Log?.Invoke($"Status: {status} {message}");
        StatusChanged?.Invoke(_status);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
