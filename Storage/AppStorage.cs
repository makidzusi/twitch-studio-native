using System.IO;
using System.Text.Json;

namespace TwitchStudioNative.Storage;

public sealed class AppStorage
{
    private readonly SemaphoreSlim _configLock = new(1, 1);

    public string DataDir { get; }
    public string AssetsDir { get; }
    private string ConfigPath => Path.Combine(DataDir, "config.json");

    public AppStorage()
    {
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TwitchStudioNative");
        AssetsDir = Path.Combine(DataDir, "assets");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(AssetsDir);
    }

    public async Task<AppConfig> ReadConfigAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigPath))
        {
            var config = new AppConfig();
            await WriteConfigAsync(config, cancellationToken);
            return config;
        }

        await using var stream = File.OpenRead(ConfigPath);
        return await JsonSerializer.DeserializeAsync<AppConfig>(stream, Json.Options, cancellationToken)
               ?? new AppConfig();
    }

    public async Task<AppConfig> WriteConfigAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _configLock.WaitAsync(cancellationToken);
        try
        {
            var temp = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, config, Json.Options, cancellationToken);
            }

            File.Move(temp, ConfigPath, true);
            return config;
        }
        finally
        {
            _configLock.Release();
        }
    }
}
