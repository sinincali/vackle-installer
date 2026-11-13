/*
 * Vackle, a modification for Flackle's desktop app
 * Copyright (c) 2026 Xrm0s and contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vackle;

public sealed record ProtocolEvent(string Method, JsonObject Params, string? SessionId);
public sealed record Target(string Id, string Type, string Url, string WebSocketDebuggerUrl);

public interface ICdp
{
    Task<JsonObject> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null);
}

public sealed class Cdp : ICdp, IDisposable
{
    readonly ClientWebSocket socket;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    readonly List<Action<ProtocolEvent>> listeners = [];
    readonly SemaphoreSlim sendLock = new(1, 1);
    int sequence;

    Cdp(ClientWebSocket socket)
    {
        this.socket = socket;
        _ = Task.Run(ReceiveLoop);
    }

    public bool Connected => socket.State == WebSocketState.Open;

    public static async Task<Cdp> ConnectAsync(string url, int port)
    {
        var parsed = new Uri(url);
        if (parsed.Scheme != "ws" || parsed.Host is not ("127.0.0.1" or "localhost" or "[::1]") || parsed.Port != port) throw new VackleException("Refusing non-local CDP connection");
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        using var timeout = new CancellationTokenSource(5000);
        try { await socket.ConnectAsync(parsed, timeout.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException || error is WebSocketException) { socket.Dispose(); throw new VackleException("CDP connection failed"); }
        return new Cdp(socket);
    }

    public Action On(Action<ProtocolEvent> listener)
    {
        lock (listeners) listeners.Add(listener);
        return () => { lock (listeners) listeners.Remove(listener); };
    }

    async Task ReceiveLoop()
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                Dispatch(text);
            }
        }
        catch (Exception error) when (error is WebSocketException || error is ObjectDisposedException || error is OperationCanceledException) { }
        foreach (var request in pending.Values) request.TrySetException(new VackleException("Flackle debugging connection closed"));
        pending.Clear();
    }

    void Dispatch(string text)
    {
        try
        {
            if (Json.Parse(text) is not JsonObject value) throw new FormatException("Malformed CDP message");
            if (value.Number("id") is { } id)
            {
                if (!pending.TryRemove((int)id, out var request)) return;
                if (value.Object("error") is { } error) request.TrySetException(new VackleException(error.TryGetValue("message", out var detail) ? Convert.ToString(detail) ?? "" : ""));
                else request.TrySetResult(value.Object("result") ?? new JsonObject());
            }
            else if (value.Text("method") is { } method && value.Object("params") is { } parameters)
            {
                var protocolEvent = new ProtocolEvent(method, parameters, value.Text("sessionId"));
                Action<ProtocolEvent>[] current;
                lock (listeners) current = listeners.ToArray();
                foreach (var listener in current) listener(protocolEvent);
            }
        }
        catch (FormatException error) { Console.Error.WriteLine($"[Vackle:CDP] Invalid message: {error.Message}"); }
    }

    public async Task<JsonObject> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null)
    {
        if (!Connected) throw new VackleException("CDP disconnected");
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        var message = Json.Object(("id", (double)id), ("method", method), ("params", parameters ?? new JsonObject()));
        if (sessionId is not null) message["sessionId"] = sessionId;
        var bytes = Encoding.UTF8.GetBytes(Json.Write(message));
        await sendLock.WaitAsync().ConfigureAwait(false);
        try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is WebSocketException || error is ObjectDisposedException) { pending.TryRemove(id, out _); throw new VackleException("CDP disconnected"); }
        finally { sendLock.Release(); }
        var finished = await Task.WhenAny(completion.Task, Task.Delay(10000)).ConfigureAwait(false);
        if (finished != completion.Task) { pending.TryRemove(id, out _); throw new VackleException($"CDP timeout: {method}"); }
        return await completion.Task.ConfigureAwait(false);
    }

    public async Task<object?> EvaluateAsync(string expression, int? contextId = null, bool awaitPromise = true)
    {
        var parameters = Json.Object(("expression", expression), ("returnByValue", true), ("awaitPromise", awaitPromise));
        if (contextId is not null) parameters["contextId"] = (double)contextId.Value;
        var result = await SendAsync("Runtime.evaluate", parameters).ConfigureAwait(false);
        if (result.ContainsKey("exceptionDetails")) throw new VackleException("Renderer evaluation failed");
        return result.Object("result") is { } value && value.TryGetValue("value", out var inner) ? inner : null;
    }

    public void Close()
    {
        try { socket.Abort(); } catch (ObjectDisposedException) { }
        socket.Dispose();
    }

    public void Dispose() => Close();

    public static async Task<List<Target>> TargetsAsync(int port)
    {
        using var cancel = new CancellationTokenSource(2000);
        using var response = await Updater.HttpManual.GetAsync($"http://127.0.0.1:{port}/json/list", cancel.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new VackleException($"WebView2 target discovery failed: {(int)response.StatusCode}");
        if (Json.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)) is not List<object?> list) throw new VackleException("Invalid debugging target list");
        return list.OfType<JsonObject>().Where(entry => entry.Text("id") is not null && entry.Text("type") == "page" && entry.Text("url") is not null && entry.Text("webSocketDebuggerUrl") is not null)
            .Select(entry => new Target(entry.Text("id")!, "page", entry.Text("url")!, entry.Text("webSocketDebuggerUrl")!)).ToList();
    }

    public static bool FlackleTarget(Target target) =>
        target.Type == "page" && Uri.TryCreate(target.Url, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == "flackle.com" && url.IsDefaultPort;
}
