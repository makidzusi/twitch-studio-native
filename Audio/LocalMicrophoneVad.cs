using NAudio.Wave;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TwitchStudioNative.Audio;

public sealed class LocalMicrophoneVad : IDisposable
{
    private WaveInEvent? _capture;
    private LocalMicrophoneSettings _settings = new();
    private bool _isSpeaking;
    private int _speechMs;
    private int _silenceMs;
    private InferenceSession? _silero;
    private DenseTensor<float>? _sileroState;
    private DenseTensor<long>? _sileroSampleRate;
    private readonly List<float> _neuralSamples = [];

    public event Action<bool>? SpeakingChanged;
    public event Action<double>? LevelChanged;
    public event Action<string>? Error;

    public static IReadOnlyList<MicrophoneDevice> GetDevices()
    {
        var devices = new List<MicrophoneDevice>();
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            var capabilities = WaveInEvent.GetCapabilities(index);
            devices.Add(new MicrophoneDevice(index, capabilities.ProductName));
        }

        return devices;
    }

    public void Start(LocalMicrophoneSettings settings)
    {
        Stop();
        _settings = settings;
        if (!settings.Enabled || settings.Muted)
        {
            PublishSpeaking(false);
            return;
        }

        var deviceNumber = settings.DeviceNumber ?? 0;
        if (WaveInEvent.DeviceCount == 0)
        {
            Error?.Invoke("Microphone input devices were not found.");
            return;
        }

        if (deviceNumber < 0 || deviceNumber >= WaveInEvent.DeviceCount)
        {
            deviceNumber = 0;
        }

        try
        {
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

        _speechMs = 0;
        _silenceMs = 0;
        _neuralSamples.Clear();
        _silero?.Dispose();
        _silero = null;
        _sileroState = null;
        _sileroSampleRate = null;
        PublishSpeaking(false);
        LevelChanged?.Invoke(0);
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        double sum = 0;
        var sampleCount = 0;
        for (var offset = 0; offset + 1 < e.BytesRecorded; offset += 2)
        {
            var sample = BitConverter.ToInt16(e.Buffer, offset) / 32768.0;
            sum += sample * sample;
            sampleCount++;
        }

        if (sampleCount == 0)
        {
            return;
        }

        var rms = Math.Sqrt(sum / sampleCount);
        LevelChanged?.Invoke(rms);
        var bufferMs = Math.Max(1, e.BytesRecorded / 2 * 1000 / 16000);

        if (string.Equals(_settings.DetectionMode, "neural", StringComparison.OrdinalIgnoreCase) && TryProcessNeural(e.Buffer, e.BytesRecorded))
        {
            return;
        }

        if (!_isSpeaking)
        {
            if (rms >= _settings.SpeakingThreshold)
            {
                _speechMs += bufferMs;
                if (_speechMs >= _settings.AttackMs)
                {
                    _silenceMs = 0;
                    PublishSpeaking(true);
                }
            }
            else
            {
                _speechMs = 0;
            }
            return;
        }

        if (rms <= _settings.SilenceThreshold)
        {
            _silenceMs += bufferMs;
            if (_silenceMs >= _settings.ReleaseMs)
            {
                _speechMs = 0;
                PublishSpeaking(false);
            }
        }
        else
        {
            _silenceMs = 0;
        }
    }

    private void PublishSpeaking(bool speaking)
    {
        if (_isSpeaking == speaking)
        {
            return;
        }

        _isSpeaking = speaking;
        SpeakingChanged?.Invoke(speaking);
    }

    public void Dispose() => Stop();

    private bool TryProcessNeural(byte[] buffer, int bytesRecorded)
    {
        try
        {
            _silero ??= CreateSileroSession();
            _sileroState ??= new DenseTensor<float>(new[] { 2, 1, 128 });
            _sileroSampleRate ??= new DenseTensor<long>(new long[] { 16000 }, new[] { 1 });
        }
        catch (Exception error)
        {
            Error?.Invoke($"Neural VAD model unavailable, using RMS: {error.Message}");
            _settings = _settings with { DetectionMode = "rms" };
            return false;
        }

        for (var offset = 0; offset + 1 < bytesRecorded; offset += 2)
        {
            _neuralSamples.Add(BitConverter.ToInt16(buffer, offset) / 32768f);
        }

        const int frameSamples = 512;
        while (_neuralSamples.Count >= frameSamples)
        {
            var frame = _neuralSamples.GetRange(0, frameSamples).ToArray();
            _neuralSamples.RemoveRange(0, frameSamples);

            var input = new DenseTensor<float>(frame, new[] { 1, frameSamples });
            using var results = _silero.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input", input),
                NamedOnnxValue.CreateFromTensor("state", _sileroState),
                NamedOnnxValue.CreateFromTensor("sr", _sileroSampleRate)
            });
            foreach (var result in results)
            {
                if (result.Name == "stateN")
                {
                    _sileroState = result.AsTensor<float>().ToDenseTensor();
                }
                else if (result.Name == "output")
                {
                    var probability = result.AsEnumerable<float>().FirstOrDefault();
                    ProcessVoiceProbability(probability, frameSamples * 1000 / 16000);
                }
            }
        }

        return true;
    }

    private void ProcessVoiceProbability(float probability, int frameMs)
    {
        const double speechThreshold = 0.62;
        const double silenceThreshold = 0.38;
        if (!_isSpeaking)
        {
            if (probability >= speechThreshold)
            {
                _speechMs += frameMs;
                if (_speechMs >= _settings.AttackMs)
                {
                    _silenceMs = 0;
                    PublishSpeaking(true);
                }
            }
            else
            {
                _speechMs = 0;
            }
            return;
        }

        if (probability <= silenceThreshold)
        {
            _silenceMs += frameMs;
            if (_silenceMs >= _settings.ReleaseMs)
            {
                _speechMs = 0;
                PublishSpeaking(false);
            }
        }
        else
        {
            _silenceMs = 0;
        }
    }

    private static InferenceSession CreateSileroSession()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "Vad", "silero_vad_v5.onnx");
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("silero_vad_v5.onnx was not found.", modelPath);
        }

        return new InferenceSession(modelPath);
    }
}

public sealed record MicrophoneDevice(int DeviceNumber, string Name)
{
    public string DisplayName => $"{DeviceNumber + 1}. {Name}";
}
