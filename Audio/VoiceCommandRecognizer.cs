using System.IO;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace TwitchStudioNative.Audio;

public sealed class VoiceCommandRecognizer : IDisposable
{
    private WaveInEvent? _capture;
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private VoiceCommandSettings _settings = new();
    private readonly Dictionary<string, VoiceCommandRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastAudioDebugAt = DateTimeOffset.MinValue;
    private string _lastPartial = "";
    private string? _lastTriggeredActionId;
    private DateTimeOffset _lastTriggeredAt = DateTimeOffset.MinValue;

    public event Action<VoiceCommandRule, float>? CommandRecognized;
    public event Action<VoiceCommandDebugEvent>? DebugChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? Error;

    public void Start(VoiceCommandSettings settings)
    {
        Stop();
        _settings = settings;
        _rules.Clear();
        _lastPartial = "";

        if (!settings.Enabled)
        {
            StatusChanged?.Invoke(LocalizationManager.Text("voice.status.disabled"));
            return;
        }

        var modelPath = ResolveModelPath(settings.VoskModelPath);
        if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
        {
            StatusChanged?.Invoke(LocalizationManager.Text("voice.status.noModel"));
            DebugChanged?.Invoke(new VoiceCommandDebugEvent("", 0, "error", null, null, DateTimeOffset.Now, Error: "Vosk model folder was not found."));
            return;
        }

        try
        {
            foreach (var rule in settings.Rules.Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.Phrase)))
            {
                _rules[NormalizePhrase(rule.Phrase)] = rule;
            }

            Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(modelPath);
            _recognizer = settings.UseGrammar && !string.IsNullOrWhiteSpace(CommandGrammarJson())
                ? new VoskRecognizer(_model, 16000.0f, CommandGrammarJson())
                : new VoskRecognizer(_model, 16000.0f);
            _recognizer.SetWords(false);

            var deviceNumber = settings.DeviceNumber ?? 0;
            if (WaveInEvent.DeviceCount == 0)
            {
                throw new InvalidOperationException("Microphone input devices were not found.");
            }

            if (deviceNumber < 0 || deviceNumber >= WaveInEvent.DeviceCount)
            {
                deviceNumber = 0;
            }

            _capture = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 30
            };
            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null)
                {
                    Error?.Invoke(args.Exception.Message);
                }
            };
            _capture.StartRecording();

            DebugChanged?.Invoke(new VoiceCommandDebugEvent(
                "",
                0,
                "listening",
                null,
                null,
                DateTimeOffset.Now,
                DeviceNumber: deviceNumber,
                DeviceName: WaveInEvent.GetCapabilities(deviceNumber).ProductName,
                AudioLevel: 0,
                BytesRecorded: 0,
                SpeechInput: $"Vosk: {modelPath} ({(settings.UseGrammar ? "grammar" : "free text")})"));
            StatusChanged?.Invoke(_rules.Count == 0 ? "Vosk слушает, команд нет" : $"Vosk слушает команды: {_rules.Count}");
        }
        catch (Exception error)
        {
            Error?.Invoke(error.Message);
            Stop();
        }
    }

    public void Stop()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= Capture_DataAvailable;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }

        _recognizer?.Dispose();
        _recognizer = null;
        _model?.Dispose();
        _model = null;
        StatusChanged?.Invoke(_settings.Enabled ? LocalizationManager.Text("voice.status.stopped") : LocalizationManager.Text("voice.status.disabled"));
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            PublishAudioDebug(e.Buffer, e.BytesRecorded);
            if (_recognizer is null || e.BytesRecorded <= 0)
            {
                return;
            }

            if (_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
            {
                ProcessRecognitionJson(_recognizer.Result(), "recognized");
            }
            else
            {
                ProcessRecognitionJson(_recognizer.PartialResult(), "hypothesis");
            }
        }
        catch (Exception error)
        {
            Error?.Invoke(error.Message);
        }
    }

    private void ProcessRecognitionJson(string json, string resultType)
    {
        var text = TextFromJson(json, resultType == "hypothesis" ? "partial" : "text");
        text = NormalizePhrase(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text == "[unk]")
        {
            DebugChanged?.Invoke(new VoiceCommandDebugEvent(text, 0, "unknown", null, null, DateTimeOffset.Now));
            return;
        }

        if (resultType == "hypothesis")
        {
            if (text == _lastPartial)
            {
                return;
            }

            _lastPartial = text;
            DebugChanged?.Invoke(new VoiceCommandDebugEvent(text, 0, "hypothesis", null, null, DateTimeOffset.Now));
            var partialMatch = MatchRule(text);
            if (partialMatch is not null && CanTrigger(partialMatch))
            {
                TriggerMatch(partialMatch, text, "recognized-partial", 0.75f);
            }

            return;
        }

        _lastPartial = "";
        var matched = MatchRule(text);
        if (matched is null)
        {
            DebugChanged?.Invoke(new VoiceCommandDebugEvent(text, 0, "dictation", null, null, DateTimeOffset.Now));
            StatusChanged?.Invoke($"Vosk распознал: {text}");
            return;
        }

        if (CanTrigger(matched))
        {
            TriggerMatch(matched, text, "recognized", 1);
        }
    }

    private bool CanTrigger(VoiceCommandRule rule)
    {
        var now = DateTimeOffset.Now;
        var cooldownMs = Math.Max(800, _settings.HoldMs);
        if (_lastTriggeredActionId == rule.ActionId && (now - _lastTriggeredAt).TotalMilliseconds < cooldownMs)
        {
            return false;
        }

        _lastTriggeredActionId = rule.ActionId;
        _lastTriggeredAt = now;
        return true;
    }

    private void TriggerMatch(VoiceCommandRule rule, string text, string resultType, float confidence)
    {
        DebugChanged?.Invoke(new VoiceCommandDebugEvent(text, confidence, resultType, rule.Phrase, rule.ActionName, DateTimeOffset.Now));
        CommandRecognized?.Invoke(rule, confidence);
    }

    private VoiceCommandRule? MatchRule(string text)
    {
        if (_rules.TryGetValue(text, out var exact))
        {
            return exact;
        }

        foreach (var (phrase, rule) in _rules)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase)
                || phrase.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }

    private void PublishAudioDebug(byte[] buffer, int bytesRecorded)
    {
        var now = DateTimeOffset.Now;
        if ((now - _lastAudioDebugAt).TotalMilliseconds < 300)
        {
            return;
        }

        _lastAudioDebugAt = now;
        double sum = 0;
        var sampleCount = 0;
        for (var offset = 0; offset + 1 < bytesRecorded; offset += 2)
        {
            var sample = BitConverter.ToInt16(buffer, offset) / 32768.0;
            sum += sample * sample;
            sampleCount++;
        }

        var rms = sampleCount == 0 ? 0 : Math.Sqrt(sum / sampleCount);
        DebugChanged?.Invoke(new VoiceCommandDebugEvent("", 0, "audio", null, null, now, AudioLevel: rms, BytesRecorded: bytesRecorded));
    }

    private string CommandGrammarJson()
    {
        if (_rules.Count == 0)
        {
            return "";
        }

        var phrases = _rules.Keys.Concat(["[unk]"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return JsonSerializer.Serialize(phrases);
    }

    private static string TextFromJson(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value)
                ? value.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    public void Dispose() => Stop();

    private static string ResolveModelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = VoiceCommandSettings.BundledVoskModelPath;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private static string NormalizePhrase(string phrase) => string.Join(
        ' ',
        phrase.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public sealed record VoiceCommandDebugEvent(
    string Text,
    float Confidence,
    string Result,
    string? MatchedPhrase,
    string? ActionName,
    DateTimeOffset At,
    string? CultureName = null,
    int? DeviceNumber = null,
    string? DeviceName = null,
    double? AudioLevel = null,
    int? BytesRecorded = null,
    string? SpeechInput = null,
    string? Error = null);
