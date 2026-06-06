using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TwitchStudioNative.Discord;

public sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcResponse>> _pending = [];
    private readonly HttpClient _http = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readCts;
    private TaskCompletionSource<JsonObject>? _ready;

    public event Action<JsonObject>? Dispatch;
    public event Action<string>? Log;
    public event Action<string>? Disconnected;
    public string ClientId { get; private set; } = "";
    public JsonObject? CurrentUser { get; private set; }
    public bool IsConnected => _pipe is { IsConnected: true };

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        ClientId = clientId;
        _ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pipe = await ConnectPipeAsync(cancellationToken);
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token), CancellationToken.None);
        await SendRawAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, cancellationToken);
        await WaitForReadyAsync(cancellationToken);
    }

    public async Task LoginAsync(string clientId, CancellationToken cancellationToken)
    {
        await ConnectAsync(clientId, cancellationToken);
        var codeVerifier = CreateCodeVerifier();
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var authorization = await TryRequestAsync(
            "AUTHORIZE",
            new JsonObject
            {
                ["client_id"] = clientId,
                ["scopes"] = new JsonArray("rpc", "rpc.voice.read"),
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256"
            },
            null,
            cancellationToken);

        if (authorization.Error is not null)
        {
            throw new InvalidOperationException($"Discord RPC authorize failed: {authorization.Error}");
        }

        var authorized = authorization.Data ?? new JsonObject();
        var code = authorized["code"]?.GetValue<string>() ?? throw new InvalidOperationException("Discord did not return an OAuth code.");
        var accessToken = await ExchangeTokenAsync(clientId, code, codeVerifier, cancellationToken);
        var authentication = await TryRequestAsync(
            "AUTHENTICATE",
            new JsonObject { ["access_token"] = accessToken },
            null,
            cancellationToken);

        if (authentication.Error is not null)
        {
            throw new InvalidOperationException($"Discord RPC authenticate failed: {authentication.Error}");
        }

        var authenticated = authentication.Data ?? new JsonObject();
        CurrentUser = authenticated["user"]?.AsObject();
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _ready.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Discord RPC did not send READY after IPC handshake.");
        }
    }

    public async Task<JsonObject> RequestAsync(string command, JsonObject? args, string? evt, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(command, args, evt, cancellationToken);
        if (response.Error is not null)
        {
            throw new InvalidOperationException(response.Error);
        }

        return response.Data ?? new JsonObject();
    }

    public Task<RpcResponse> TryRequestAsync(string command, JsonObject? args, string? evt, CancellationToken cancellationToken)
        => SendRequestAsync(command, args, evt, cancellationToken);

    private Task<RpcResponse> SendRequestAsync(string command, JsonObject? args, string? evt, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var payload = new JsonObject
        {
            ["cmd"] = command,
            ["args"] = args ?? new JsonObject(),
            ["nonce"] = nonce
        };

        if (!string.IsNullOrWhiteSpace(evt))
        {
            payload["evt"] = evt;
        }

        var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(nonce, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        _ = SendRawAsync(OpFrame, payload, cancellationToken).ContinueWith(task =>
        {
            if (task.IsFaulted && _pending.TryRemove(nonce, out var pending))
            {
                pending.TrySetResult(new RpcResponse(null, task.Exception!.GetBaseException().Message));
            }
        }, TaskScheduler.Default);

        return tcs.Task;
    }

    public Task SubscribeAsync(string evt, string? channelId, CancellationToken cancellationToken)
    {
        JsonObject args = new();
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            args["channel_id"] = channelId;
        }

        return RequestAsync("SUBSCRIBE", args, evt, cancellationToken);
    }

    private async Task<string> ExchangeTokenAsync(string clientId, string code, string codeVerifier, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = "http://127.0.0.1"
        });

        using var response = await _http.PostAsync("https://discord.com/api/oauth2/token", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(body)!.AsObject();
        return json["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Discord did not return an access token.");
    }

    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64Url(bytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var index = 0; index < 10; index++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1500, cancellationToken);
                return pipe;
            }
            catch (Exception error) when (error is TimeoutException or IOException)
            {
                lastError = error;
                await pipe.DisposeAsync();
            }
        }

        throw new InvalidOperationException("Discord IPC pipe was not found. Start Discord desktop client.", lastError);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8];
        while (!cancellationToken.IsCancellationRequested && _pipe is { IsConnected: true } pipe)
        {
            try
            {
                await ReadExactlyAsync(pipe, header, cancellationToken);
                var op = BitConverter.ToInt32(header, 0);
                var length = BitConverter.ToInt32(header, 4);
                var payload = new byte[length];
                await ReadExactlyAsync(pipe, payload, cancellationToken);

                if (op == OpPing)
                {
                    await SendRawAsync(OpPong, JsonNode.Parse(Encoding.UTF8.GetString(payload))?.AsObject() ?? new JsonObject(), cancellationToken);
                    continue;
                }

                if (op != OpFrame)
                {
                    continue;
                }

                var message = JsonNode.Parse(Encoding.UTF8.GetString(payload))!.AsObject();
                HandleMessage(message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                var message = error is EndOfStreamException
                    ? "Discord RPC disconnected during IPC read."
                    : error.Message;
                CompletePendingWithError(message);
                Disconnected?.Invoke(message);
                return;
            }
        }
    }

    private void CompletePendingWithError(string message)
    {
        _ready?.TrySetException(new InvalidOperationException(message));
        foreach (var item in _pending)
        {
            if (_pending.TryRemove(item.Key, out var pending))
            {
                pending.TrySetResult(new RpcResponse(null, message));
            }
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private void HandleMessage(JsonObject message)
    {
        Log?.Invoke($"Discord <= {message.ToJsonString(Json.Options)}");
        var nonce = message["nonce"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(nonce) && _pending.TryRemove(nonce, out var pending))
        {
            if (message["evt"]?.GetValue<string>() == "ERROR")
            {
                var error = message["data"]?["message"]?.GetValue<string>() ?? "Discord RPC error";
                pending.TrySetResult(new RpcResponse(null, error));
            }
            else
            {
                pending.TrySetResult(new RpcResponse(message["data"]?.AsObject() ?? new JsonObject(), null));
            }
            return;
        }

        if (message["cmd"]?.GetValue<string>() == "DISPATCH" && message["evt"]?.GetValue<string>() == "READY")
        {
            CurrentUser = message["data"]?["user"]?.AsObject();
            _ready?.TrySetResult(message["data"]?.AsObject() ?? new JsonObject());
        }

        Dispatch?.Invoke(message);
    }

    private async Task SendRawAsync(int op, JsonObject payload, CancellationToken cancellationToken)
    {
        if (_pipe is not { IsConnected: true } pipe)
        {
            throw new InvalidOperationException("Discord IPC is not connected.");
        }

        var body = Encoding.UTF8.GetBytes(payload.ToJsonString(Json.Options));
        if (op == OpFrame)
        {
            Log?.Invoke($"Discord => {payload.ToJsonString(Json.Options)}");
        }
        var header = new byte[8];
        BitConverter.GetBytes(op).CopyTo(header, 0);
        BitConverter.GetBytes(body.Length).CopyTo(header, 4);
        await pipe.WriteAsync(header, cancellationToken);
        await pipe.WriteAsync(body, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        if (_pipe is { IsConnected: true })
        {
            try { await SendRawAsync(OpClose, new JsonObject(), CancellationToken.None); } catch { }
        }
        _pipe?.Dispose();
        _readCts?.Dispose();
        _http.Dispose();
    }

    public async Task ResetAsync()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
        CurrentUser = null;
        CompletePendingWithError("Discord RPC connection was reset.");
        _ready = null;
    }

    public sealed record RpcResponse(JsonObject? Data, string? Error);
}
