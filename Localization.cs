using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TwitchStudioNative;

public sealed record LanguageOption(string Code, string Name);

public static class LocalizationManager
{
    private const string DefaultLanguage = "ru-RU";
    private static readonly Dictionary<string, LocaleFile> Locales = new(StringComparer.OrdinalIgnoreCase);
    private static LocaleFile? CurrentLocale;

    public static IReadOnlyList<LanguageOption> Languages { get; private set; } = [];
    public static string CurrentCode => CurrentLocale?.Code ?? DefaultLanguage;

    public static void Load()
    {
        Locales.Clear();
        var directory = Path.Combine(AppContext.BaseDirectory, "Locales");
        if (!Directory.Exists(directory))
        {
            directory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Locales");
        }

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                TryLoad(file);
            }
        }

        Languages = Locales.Values
            .OrderBy(locale => locale.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(locale => new LanguageOption(locale.Code, locale.Name))
            .ToList();
        SetLanguage(ResolveDefaultLanguage(null));
    }

    public static string ResolveDefaultLanguage(string? configuredLanguage)
    {
        if (!string.IsNullOrWhiteSpace(configuredLanguage) && Locales.ContainsKey(configuredLanguage))
        {
            return configuredLanguage;
        }

        var culture = CultureInfo.CurrentUICulture;
        if (Locales.ContainsKey(culture.Name))
        {
            return culture.Name;
        }

        var neutral = Locales.Keys.FirstOrDefault(code => code.StartsWith(culture.TwoLetterISOLanguageName + "-", StringComparison.OrdinalIgnoreCase));
        return neutral ?? (Locales.ContainsKey(DefaultLanguage) ? DefaultLanguage : Locales.Keys.FirstOrDefault() ?? DefaultLanguage);
    }

    public static void SetLanguage(string languageCode)
    {
        if (Locales.TryGetValue(languageCode, out var locale))
        {
            CurrentLocale = locale;
        }
    }

    public static string Text(string key) => Text(key, Array.Empty<object?>());

    public static string Text(string key, params object?[] args)
    {
        var template = CurrentLocale?.Strings.TryGetValue(key, out var value) == true
            ? value
            : key;
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    private static void TryLoad(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var code = root.GetProperty("code").GetString();
            if (string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            var name = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? code
                : code;
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("strings", out var stringsElement))
            {
                foreach (var property in stringsElement.EnumerateObject())
                {
                    strings[property.Name] = property.Value.GetString() ?? "";
                }
            }

            Locales[code] = new LocaleFile(code, name, strings);
        }
        catch
        {
            // Broken user-added locale files are ignored so the app can still start.
        }
    }

    private sealed record LocaleFile(string Code, string Name, Dictionary<string, string> Strings);
}
