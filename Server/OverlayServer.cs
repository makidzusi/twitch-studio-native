using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TwitchStudioNative.Discord;
using TwitchStudioNative.Storage;

namespace TwitchStudioNative.Server;

public sealed class OverlayServer : IAsyncDisposable
{
    private readonly AppStorage _storage;
    private readonly DiscordVoiceProvider _voiceProvider;
    private readonly ConcurrentDictionary<WebSocket, string?> _sockets = [];
    private readonly Action<VoiceSnapshot> _snapshotChanged;
    private readonly Action<string, VoiceUser> _userStateChanged;
    private readonly Action<VoiceSettingsSnapshot> _voiceSettingsChanged;
    private readonly Action<ConnectionStatusUpdate> _statusChanged;
    private WebApplication? _app;
    private AppConfig _config = new();

    public int Port { get; }
    public string BaseUrl => $"http://localhost:{Port}";

    public OverlayServer(AppStorage storage, DiscordVoiceProvider voiceProvider, int port = 3847)
    {
        _storage = storage;
        _voiceProvider = voiceProvider;
        Port = port;
        _snapshotChanged = snapshot => _ = BroadcastAsync(new
        {
            type = "voice:snapshot",
            payload = new
            {
                channels = snapshot.Channels,
                selectedChannelId = snapshot.SelectedChannelId,
                authenticatedUserId = snapshot.AuthenticatedUserId,
                authenticatedUser = snapshot.AuthenticatedUser
            }
        }, excludeSubscribedSockets: true);
        _userStateChanged = (channelId, user) => _ = BroadcastUserStateAsync(channelId, user);
        _voiceSettingsChanged = settings => _ = BroadcastAsync(new { type = "voice:settings", payload = settings });
        _statusChanged = status => _ = BroadcastAsync(new { type = "discord:status", payload = status });
        _voiceProvider.SnapshotChanged += _snapshotChanged;
        _voiceProvider.UserStateChanged += _userStateChanged;
        _voiceProvider.VoiceSettingsChanged += _voiceSettingsChanged;
        _voiceProvider.StatusChanged += _statusChanged;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _config = await _storage.ReadConfigAsync(cancellationToken);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(_storage.AssetsDir),
            RequestPath = "/assets"
        });

        MapRoutes(_app);
        await _app.StartAsync(cancellationToken);
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/voice", () => Results.Json(new
        {
            channels = _voiceProvider.Snapshot.Channels,
            selectedChannelId = _voiceProvider.Snapshot.SelectedChannelId,
            authenticatedUserId = _voiceProvider.Snapshot.AuthenticatedUserId,
            authenticatedUser = _voiceProvider.Snapshot.AuthenticatedUser
        }, Json.Options));

        app.MapGet("/api/config", () => Results.Json(_config, Json.Options));
        app.MapPut("/api/config", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            _config = await JsonSerializer.DeserializeAsync<AppConfig>(request.Body, Json.Options, cancellationToken) ?? _config;
            _config = await _storage.WriteConfigAsync(_config, cancellationToken);
            await BroadcastAsync(new { type = "app:config", payload = _config }, cancellationToken);
            return Results.Json(_config, Json.Options);
        });

        app.MapPost("/api/assets/import", ImportAssetAsync);
        app.MapGet("/api/overlays/{userId}/export", ExportOverlayAsync);
        app.MapPost("/api/overlays/{userId}/import", ImportOverlayAsync);
        app.MapGet("/overlay/user/{userId}", (string userId) => Results.Content(RenderOverlayHtml(userId), "text/html; charset=utf-8"));
        app.Map("/", () => Results.Redirect("/api/voice"));

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _sockets.TryAdd(socket, null);
            await SendInitialAsync(socket, context.RequestAborted);
            await ReceiveLoopAsync(socket, context.RequestAborted);
            _sockets.TryRemove(socket, out _);
        });
    }

    private async Task<IResult> ImportAssetAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var userId = request.Query["userId"].ToString();
        var stateText = request.Query["state"].ToString();
        var mode = request.Query["mode"].ToString() == "append" ? "append" : "replace";
        var originalName = SanitizeFileName(request.Query["fileName"].ToString());
        var mimeType = request.ContentType?.Split(';')[0] ?? "";
        if (string.IsNullOrWhiteSpace(userId) || !Enum.TryParse<AnimationState>(stateText, out var state))
        {
            return Results.BadRequest(new { error = "A valid userId and state are required." });
        }

        var extension = MimeExtension(mimeType);
        if (extension is null)
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var id = Guid.NewGuid().ToString("N");
        var storedName = $"{id}{extension}";
        var storedPath = Path.Combine(_storage.AssetsDir, storedName);
        await using (var file = File.Create(storedPath))
        {
            await request.Body.CopyToAsync(file, cancellationToken);
        }

        var asset = new OverlayAsset
        {
            Id = id,
            UserId = userId,
            State = state,
            FileName = string.IsNullOrWhiteSpace(originalName) ? storedName : originalName,
            MimeType = mimeType,
            Url = $"/assets/{storedName}",
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SizeBytes = new FileInfo(storedPath).Length,
            ImportedAt = DateTimeOffset.UtcNow.ToString("O")
        };

        var settings = _config.Overlays.TryGetValue(userId, out var existing)
            ? existing
            : new UserOverlaySettings { UserId = userId };

        var frames = mode == "append" && settings.Animations.TryGetValue(state, out var animation)
            ? [.. animation.Frames, asset]
            : new List<OverlayAsset> { asset };

        settings = settings with
        {
            Assets = new Dictionary<AnimationState, OverlayAsset>(settings.Assets) { [state] = frames[0] },
            Animations = new Dictionary<AnimationState, OverlayAnimation>(settings.Animations)
            {
                [state] = new OverlayAnimation
                {
                    State = state,
                    Frames = frames,
                    FrameDurationMs = settings.Animations.TryGetValue(state, out var old) ? old.FrameDurationMs : 120,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                }
            }
        };

        _config = _config with { Overlays = new Dictionary<string, UserOverlaySettings>(_config.Overlays) { [userId] = settings } };
        _config = await _storage.WriteConfigAsync(_config, cancellationToken);
        await BroadcastAsync(new { type = "overlay:settings-update", payload = new { userId, settings } }, cancellationToken);
        await BroadcastAsync(new { type = "app:config", payload = _config }, cancellationToken);
        return Results.Json(new { asset, settings }, Json.Options);
    }

    private async Task<IResult> ExportOverlayAsync(string userId, CancellationToken cancellationToken)
    {
        if (!_config.Overlays.TryGetValue(userId, out var settings))
        {
            return Results.NotFound(new { error = "Overlay settings were not found." });
        }

        await using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new OverlayArchiveManifest(1, DateTimeOffset.UtcNow.ToString("O"), userId, settings);
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            await using (var entryStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(entryStream, manifest, Json.Options, cancellationToken);
            }

            foreach (var storedFileName in OverlayAssetFileNames(settings).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var sourcePath = Path.Combine(_storage.AssetsDir, storedFileName);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var assetEntry = archive.CreateEntry($"assets/{storedFileName}", CompressionLevel.NoCompression);
                await using var entryStream = assetEntry.Open();
                await using var fileStream = File.OpenRead(sourcePath);
                await fileStream.CopyToAsync(entryStream, cancellationToken);
            }
        }

        return Results.File(
            archiveStream.ToArray(),
            "application/zip",
            $"{SanitizeFileName(settings.UserId)}-overlay.zip");
    }

    private async Task<IResult> ImportOverlayAsync(string userId, HttpRequest request, CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        if (body.Length == 0)
        {
            return Results.BadRequest(new { error = "Archive body is required." });
        }

        body.Position = 0;
        try
        {
            using var archive = new ZipArchive(body, ZipArchiveMode.Read);
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                return Results.BadRequest(new { error = "Archive manifest is missing." });
            }

            OverlayArchiveManifest? manifest;
            await using (var manifestStream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<OverlayArchiveManifest>(manifestStream, Json.Options, cancellationToken);
            }

            if (manifest?.Version != 1 || manifest.Settings is null)
            {
                return Results.BadRequest(new { error = "Unsupported overlay archive." });
            }

            var assetMap = new Dictionary<string, OverlayAsset>();
            async Task<OverlayAsset> ImportAssetFromArchiveAsync(OverlayAsset asset)
            {
                if (assetMap.TryGetValue(asset.Id, out var existing) || assetMap.TryGetValue(asset.Url, out existing))
                {
                    return existing;
                }

                if (!asset.Url.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Archive contains an invalid asset URL.");
                }

                var sourceFileName = Path.GetFileName(asset.Url);
                var entry = archive.GetEntry($"assets/{sourceFileName}")
                            ?? throw new InvalidOperationException($"Archive asset is missing: {sourceFileName}");
                var extension = MimeExtension(asset.MimeType) ?? Path.GetExtension(sourceFileName);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".bin";
                }

                var assetId = Guid.NewGuid().ToString("N");
                var storedFileName = $"{assetId}{extension}";
                var storedPath = Path.Combine(_storage.AssetsDir, storedFileName);
                await using (var entryStream = entry.Open())
                await using (var fileStream = File.Create(storedPath))
                {
                    await entryStream.CopyToAsync(fileStream, cancellationToken);
                }

                var imported = asset with
                {
                    Id = assetId,
                    UserId = userId,
                    Url = $"/assets/{storedFileName}",
                    Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SizeBytes = new FileInfo(storedPath).Length,
                    ImportedAt = DateTimeOffset.UtcNow.ToString("O")
                };
                assetMap[asset.Id] = imported;
                assetMap[asset.Url] = imported;
                return imported;
            }

            var importedAnimations = new Dictionary<AnimationState, OverlayAnimation>();
            foreach (var (state, animation) in manifest.Settings.Animations)
            {
                var frames = new List<OverlayAsset>();
                foreach (var frame in animation.Frames)
                {
                    frames.Add(await ImportAssetFromArchiveAsync(frame));
                }

                importedAnimations[state] = animation with
                {
                    State = state,
                    Frames = frames,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                };
            }

            var importedAssets = new Dictionary<AnimationState, OverlayAsset>();
            foreach (var (state, asset) in manifest.Settings.Assets)
            {
                importedAssets[state] = await ImportAssetFromArchiveAsync(asset);
            }

            var importedCustomAnimations = new Dictionary<string, CustomOverlayAnimation>(StringComparer.OrdinalIgnoreCase);
            foreach (var (actionId, animation) in manifest.Settings.CustomAnimations)
            {
                var frames = new List<OverlayAsset>();
                foreach (var frame in animation.Frames)
                {
                    frames.Add(await ImportAssetFromArchiveAsync(frame));
                }

                importedCustomAnimations[actionId] = animation with
                {
                    Id = actionId,
                    Frames = frames,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                };
            }

            var settings = manifest.Settings with
            {
                UserId = userId,
                Assets = importedAssets,
                Animations = importedAnimations,
                CustomAnimations = importedCustomAnimations
            };

            _config = _config with { Overlays = new Dictionary<string, UserOverlaySettings>(_config.Overlays) { [userId] = settings } };
            _config = await _storage.WriteConfigAsync(_config, cancellationToken);
            await BroadcastAsync(new { type = "overlay:settings-update", payload = new { userId, settings } }, cancellationToken);
            await BroadcastAsync(new { type = "app:config", payload = _config }, cancellationToken);
            return Results.Json(new { settings }, Json.Options);
        }
        catch (Exception error)
        {
            return Results.BadRequest(new { error = error.Message });
        }
    }

    private static IEnumerable<string> OverlayAssetFileNames(UserOverlaySettings settings)
    {
        foreach (var asset in settings.Assets.Values)
        {
            if (asset.Url.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFileName(asset.Url);
            }
        }

        foreach (var animation in settings.Animations.Values)
        {
            foreach (var frame in animation.Frames)
            {
                if (frame.Url.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    yield return Path.GetFileName(frame.Url);
                }
            }
        }

        foreach (var animation in settings.CustomAnimations.Values)
        {
            foreach (var frame in animation.Frames)
            {
                if (frame.Url.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    yield return Path.GetFileName(frame.Url);
                }
            }
        }
    }

    private async Task SendInitialAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        await SendAsync(socket, new { type = "connection:status", payload = new ConnectionStatusUpdate(ConnectionStatus.connected, "Local overlay server connected") }, cancellationToken);
        await SendAsync(socket, new { type = "discord:status", payload = _voiceProvider.Status }, cancellationToken);
        await SendAsync(socket, new
        {
            type = "voice:snapshot",
            payload = new
            {
                channels = _voiceProvider.Snapshot.Channels,
                selectedChannelId = _voiceProvider.Snapshot.SelectedChannelId,
                authenticatedUserId = _voiceProvider.Snapshot.AuthenticatedUserId,
                authenticatedUser = _voiceProvider.Snapshot.AuthenticatedUser
            }
        }, cancellationToken);
        await SendAsync(socket, new { type = "app:config", payload = _config }, cancellationToken);
        if (_voiceProvider.VoiceSettings is not null)
        {
            await SendAsync(socket, new { type = "voice:settings", payload = _voiceProvider.VoiceSettings }, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                return;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(text, Json.Options);
                var type = json.GetProperty("type").GetString();
                if (type == "overlay:subscribe-user")
                {
                    var userId = json.GetProperty("payload").GetProperty("userId").GetString();
                    _sockets[socket] = userId;
                    var current = _voiceProvider.Snapshot.Channels
                        .SelectMany(channel => channel.Users.Select(user => (channel.Id, User: user)))
                        .FirstOrDefault(item => item.User.Id == userId);
                    if (current.User is not null)
                    {
                        await SendAsync(socket, new { type = "voice:user-state", payload = new { channelId = current.Id, user = current.User } }, cancellationToken);
                    }
                }
                else if (type == "voice:refresh")
                {
                    await _voiceProvider.RefreshAsync(cancellationToken);
                }
            }
            catch
            {
                await SendAsync(socket, new { type = "connection:status", payload = new ConnectionStatusUpdate(ConnectionStatus.error, "Invalid WebSocket message") }, cancellationToken);
            }
        }
    }

    private Task BroadcastUserStateAsync(string channelId, VoiceUser user, CancellationToken cancellationToken = default)
    {
        return BroadcastAsync(new { type = "voice:user-state", payload = new { channelId, user } }, cancellationToken, user.Id);
    }

    private async Task BroadcastAsync(object payload, CancellationToken cancellationToken = default, string? userFilter = null, bool excludeSubscribedSockets = false)
    {
        foreach (var (socket, subscribedUserId) in _sockets.ToArray())
        {
            if (socket.State != WebSocketState.Open)
            {
                _sockets.TryRemove(socket, out _);
                continue;
            }

            if (excludeSubscribedSockets && subscribedUserId is not null)
            {
                continue;
            }

            if (userFilter is not null && subscribedUserId is not null && subscribedUserId != userFilter)
            {
                continue;
            }

            await SendAsync(socket, payload, cancellationToken);
        }
    }

    private static Task SendAsync(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, Json.Options);
        return socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
    }

    private string RenderOverlayHtml(string userId)
    {
        var escaped = System.Net.WebUtility.HtmlEncode(userId);
        var jsUserId = JsonSerializer.Serialize(userId);
        return """
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Overlay {{escaped}}</title>
  <style>
    html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent}
    body{display:flex;align-items:center;justify-content:center;font-family:Segoe UI,system-ui,sans-serif}
    #root{--scale:1;--opacity:1;--x:0px;--y:0px;--transition:180ms;opacity:var(--opacity);transform:translate(var(--x),var(--y)) scale(var(--scale));transition:opacity var(--transition),transform var(--transition);display:flex;align-items:center;justify-content:center;width:100vw;height:100vh}
    img{display:none;max-width:100vw;max-height:100vh;transform-origin:center bottom}
    .fallback{min-width:220px;padding:22px 28px;border:2px solid #67e8f9;border-radius:8px;background:rgba(12,18,26,.72);box-shadow:0 18px 44px rgba(0,0,0,.35);color:#fff;text-align:center;font-weight:700}
    .state{display:block;margin-top:6px;color:#67e8f9;font-size:13px;text-transform:uppercase}
    [data-state=speaking][data-single-frame=true] img{animation:bounce 420ms ease-in-out infinite}
    [data-state=speaking] .fallback{border-color:#4ade80;transform:translateY(-4px)}
    [data-state=muted] .fallback,[data-state=deafened] .fallback{border-color:#fbbf24;filter:saturate(.75)}
    [data-state=disconnected]{opacity:calc(var(--opacity) * .55)}
    @keyframes bounce{0%,100%{transform:translateY(0)}45%{transform:translateY(-20px)}70%{transform:translateY(0)}}
  </style>
</head>
<body>
  <div id="root" data-state="idle">
    <img id="asset" alt="">
    <div class="fallback"><span id="name">User %%ESCAPED_USER_ID%%</span><span id="state" class="state">idle</span></div>
  </div>
  <script>
    const userId = %%JS_USER_ID%%;
    const root = document.getElementById('root');
    const image = document.getElementById('asset');
    const fallback = document.querySelector('.fallback');
    const nameEl = document.getElementById('name');
    const stateEl = document.getElementById('state');
    let settings = null, currentUser = null, frames = [], frameIndex = 0, frameTimer = 0, renderedKey = '', visibleSrc = '';
    function applySettings(next) {
      settings = next;
      if (!settings) return;
      root.style.setProperty('--scale', settings.scale ?? 1);
      root.style.setProperty('--opacity', settings.opacity ?? 1);
      root.style.setProperty('--x', (settings.positionX ?? 0) + 'px');
      root.style.setProperty('--y', (settings.positionY ?? 0) + 'px');
      root.style.setProperty('--transition', (settings.transitionMs ?? 180) + 'ms');
      render();
    }
    function animationFor(user) {
      if (!settings) return null;
      if (user.activeActionId && settings.customAnimations?.[user.activeActionId]) return settings.customAnimations[user.activeActionId];
      return settings.animations?.[user.state] || settings.animations?.idle || null;
    }
    function render() {
      if (!currentUser) return;
      const animation = animationFor(currentUser);
      const nextFrames = animation?.frames || [];
      const nextKey = JSON.stringify({
        user: currentUser.id,
        state: currentUser.state,
        action: currentUser.activeActionId || '',
        name: currentUser.displayName || currentUser.username || currentUser.id,
        frames: nextFrames.map(frame => [frame.url, frame.version]),
        duration: animation?.frameDurationMs || 120,
        scale: settings?.scale ?? 1,
        opacity: settings?.opacity ?? 1,
        x: settings?.positionX ?? 0,
        y: settings?.positionY ?? 0,
        transition: settings?.transitionMs ?? 180
      });
      if (nextKey === renderedKey) return;
      renderedKey = nextKey;
      root.dataset.state = currentUser.state;
      root.dataset.action = currentUser.activeActionId || '';
      root.dataset.singleFrame = nextFrames.length === 1 ? 'true' : 'false';
      nameEl.textContent = currentUser.displayName || currentUser.username || currentUser.id;
      stateEl.textContent = currentUser.activeActionId || currentUser.state;
      clearInterval(frameTimer);
      frames = nextFrames;
      if (!frames.length) { image.style.display='none'; fallback.style.display='block'; return; }
      frameIndex = 0;
      const draw = () => {
        const f = frames[frameIndex % frames.length];
        const nextSrc = f.url + '?v=' + encodeURIComponent(f.version || 0);
        frameIndex++;
        if (nextSrc === visibleSrc) return;
        const loader = new Image();
        loader.onload = () => {
          visibleSrc = nextSrc;
          image.src = nextSrc;
          image.style.display = 'block';
          fallback.style.display = 'none';
        };
        loader.onerror = () => {
          if (!visibleSrc) {
            image.style.display = 'none';
            fallback.style.display = 'block';
          }
        };
        loader.src = nextSrc;
      };
      draw();
      if (frames.length > 1) frameTimer = setInterval(draw, Math.max(16, animation.frameDurationMs || 120));
    }
    function connect() {
      const ws = new WebSocket('ws://' + location.host + '/ws');
      ws.onopen = () => ws.send(JSON.stringify({type:'overlay:subscribe-user',payload:{userId}}));
      ws.onmessage = event => {
        const message = JSON.parse(event.data);
        if (message.type === 'voice:user-state' && message.payload.user.id === userId) { currentUser = message.payload.user; render(); }
        if (message.type === 'voice:snapshot') {
          for (const channel of message.payload.channels || []) {
            const user = (channel.users || []).find(item => item.id === userId);
            if (user) { currentUser = user; render(); }
          }
        }
        if (message.type === 'app:config') applySettings(message.payload.overlays?.[userId]);
        if (message.type === 'overlay:settings-update' && message.payload.userId === userId) applySettings(message.payload.settings);
      };
      ws.onclose = () => setTimeout(connect, 1200);
    }
    window.__setPreviewUser = user => {
      currentUser = user;
      render();
    };
    connect();
  </script>
</body>
</html>
""".Replace("%%ESCAPED_USER_ID%%", escaped).Replace("%%JS_USER_ID%%", jsUserId);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim();
    }

    private static string? MimeExtension(string mime) => mime switch
    {
        "image/png" or "image/apng" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        _ => null
    };

    public async ValueTask DisposeAsync()
    {
        _voiceProvider.SnapshotChanged -= _snapshotChanged;
        _voiceProvider.UserStateChanged -= _userStateChanged;
        _voiceProvider.VoiceSettingsChanged -= _voiceSettingsChanged;
        _voiceProvider.StatusChanged -= _statusChanged;
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed record OverlayArchiveManifest(int Version, string ExportedAt, string UserId, UserOverlaySettings Settings);
}
