using System.Text.Json.Serialization;

namespace TwitchStudioNative;

[JsonConverter(typeof(JsonStringEnumConverter<AnimationState>))]
public enum AnimationState
{
    idle,
    speaking,
    muted,
    deafened,
    disconnected
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionStatus>))]
public enum ConnectionStatus
{
    disconnected,
    connecting,
    connected,
    error
}

public record DiscordUser
{
    public string Id { get; init; } = "";
    public string Username { get; init; } = "";
    public string? Discriminator { get; init; }
    public string? GlobalName { get; init; }
    public string DisplayName { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public bool? Bot { get; init; }
}

public sealed record VoiceUser : DiscordUser
{
    public AnimationState State { get; init; } = AnimationState.idle;
    public string? ActiveActionId { get; init; }
    public bool IsStreaming { get; init; }
    public bool IsVideoEnabled { get; init; }
    public string UpdatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed record VoiceChannel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? GuildId { get; init; }
    public List<VoiceUser> Users { get; init; } = [];
}

public sealed record VoiceDeviceSettings
{
    public string? DeviceId { get; init; }
    public double? Volume { get; init; }
}

public sealed record VoiceSettingsSnapshot
{
    public bool IsMuted { get; init; }
    public bool IsDeafened { get; init; }
    public VoiceDeviceSettings? Input { get; init; }
    public VoiceDeviceSettings? Output { get; init; }
    public string UpdatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed record OverlayAsset
{
    public string Id { get; init; } = "";
    public string UserId { get; init; } = "";
    public AnimationState State { get; init; }
    public string FileName { get; init; } = "";
    public string MimeType { get; init; } = "";
    public string Url { get; init; } = "";
    public long Version { get; init; }
    public long? SizeBytes { get; init; }
    public string ImportedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed record OverlayAnimation
{
    public AnimationState State { get; init; }
    public List<OverlayAsset> Frames { get; init; } = [];
    public int FrameDurationMs { get; init; } = 120;
    public string RenderingMode { get; init; } = "webgl";
    public string UpdatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed record CustomOverlayAnimation
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string TriggerPhrase { get; init; } = "";
    public List<OverlayAsset> Frames { get; init; } = [];
    public int FrameDurationMs { get; init; } = 120;
    public string RenderingMode { get; init; } = "webgl";
    public string UpdatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed record UserOverlaySettings
{
    public string UserId { get; init; } = "";
    public double Scale { get; init; } = 1;
    public double Opacity { get; init; } = 1;
    public double PositionX { get; init; }
    public double PositionY { get; init; }
    public int TransitionMs { get; init; } = 180;
    public Dictionary<AnimationState, OverlayAsset> Assets { get; init; } = [];
    public Dictionary<AnimationState, OverlayAnimation> Animations { get; init; } = [];
    public Dictionary<string, CustomOverlayAnimation> CustomAnimations { get; init; } = [];
}

public sealed record DiscordConnectionSettings
{
    public string? ClientId { get; init; }
    public string IntegrationMode { get; init; } = "rpc";
}

public sealed record LocalMicrophoneSettings
{
    public bool Enabled { get; init; } = true;
    public bool Muted { get; init; }
    public int? DeviceNumber { get; init; }
    public string? MuteHotKey { get; init; }
    public string MuteHotKeyModifiers { get; init; } = "";
    public string DetectionMode { get; init; } = "rms";
    public double SpeakingThreshold { get; init; } = 0.025;
    public double SilenceThreshold { get; init; } = 0.014;
    public int AttackMs { get; init; } = 80;
    public int ReleaseMs { get; init; } = 280;
}

public sealed record VoiceCommandSettings
{
    public const string BundledVoskModelPath = @"Vosk\vosk-model-small-ru-0.22";

    public bool Enabled { get; init; }
    public int? DeviceNumber { get; init; }
    public string? VoskModelPath { get; init; } = BundledVoskModelPath;
    public bool UseGrammar { get; init; }
    public int HoldMs { get; init; } = 2200;
    public List<VoiceCommandRule> Rules { get; init; } = [];
}

public sealed record VoiceCommandRule
{
    public string Phrase { get; init; } = "";
    public string ActionId { get; init; } = "";
    public string ActionName { get; init; } = "";
    public string? HotKey { get; init; }
    public string HotKeyModifiers { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

public sealed record AppConfig
{
    public string? Language { get; init; }
    public bool DebugMode { get; init; }
    public bool MinimizeToTray { get; init; }
    public int OverlayPort { get; init; } = 3847;
    public DiscordConnectionSettings Discord { get; init; } = new();
    public LocalMicrophoneSettings LocalMicrophone { get; init; } = new();
    public VoiceCommandSettings VoiceCommands { get; init; } = new();
    public string? SelectedChannelId { get; init; }
    public string? SelectedAuthenticatedUserId { get; init; }
    public DiscordUser? SelectedAuthenticatedUser { get; init; }
    public Dictionary<string, DiscordUser> KnownUsers { get; init; } = [];
    public Dictionary<string, UserOverlaySettings> Overlays { get; init; } = [];
}

public sealed record ConnectionStatusUpdate(ConnectionStatus Status, string? Message = null);

public sealed record VoiceSnapshot
{
    public List<VoiceChannel> Channels { get; init; } = [];
    public string? SelectedChannelId { get; init; }
    public string? AuthenticatedUserId { get; init; }
    public DiscordUser? AuthenticatedUser { get; init; }
}

public static class VoiceStateResolver
{
    public static AnimationState Resolve(bool isConnected, bool isDeafened, bool isMuted, bool isSpeaking)
    {
        if (!isConnected) return AnimationState.disconnected;
        if (isDeafened) return AnimationState.deafened;
        if (isMuted) return AnimationState.muted;
        return isSpeaking ? AnimationState.speaking : AnimationState.idle;
    }
}
