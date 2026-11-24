using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UserRemoveTool.Core.ServiceDesk;

public sealed class CdpException(string message) : Exception(message);

/// <summary>Minimal Chrome DevTools Protocol client (flat sessions) over the built-in WebSocket client.</summary>
internal sealed class CdpClient : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private int _nextId;

    public static async Task<CdpClient> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var client = new CdpClient();
        try
        {
            client._socket.Options.Proxy = null; // loopback only
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client._socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            _ = client.ReceiveLoopAsync();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<JsonNode?> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (sessionId is not null) message["sessionId"] = sessionId;
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            if (_closed.IsCancellationRequested) throw new CdpException("connection closed");
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
            return await completion.Task.WaitAsync(CallTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new CdpException($"{method} timed out"); }
        catch (WebSocketException ex) { throw new CdpException(ex.Message); }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, _closed.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var node = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
                message.SetLength(0);
                if (node?["id"] is not { } id || !_pending.TryGetValue((int)id, out var completion)) continue;
                if (node["error"] is { } error) completion.TrySetException(new CdpException((string?)error["message"] ?? "error"));
                else completion.TrySetResult(node["result"]);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            _closed.Cancel();
            foreach (var pending in _pending.Values) pending.TrySetException(new CdpException("connection closed"));
        }
    }

    public void Dispose()
    {
        _closed.Cancel();
        _socket.Dispose();
    }
}
