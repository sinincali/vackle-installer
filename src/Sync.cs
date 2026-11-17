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
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vackle;

public static class Sync
{
    const string Endpoint = "https://api.vackle.dev/v1/sync/";
    static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    static string StatePath(string root) => Path.Combine(root, "sync.dat");

    public static string? Load(string root)
    {
        var path = StatePath(root);
        Files.SafeTree(root, path);
        if (!File.Exists(path)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(path).Trim()), null, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
    }

    public static void Store(string root, string value)
    {
        if (value.Length == 0 || value.Length > 1024) throw new VackleException("Invalid sync state");
        var path = StatePath(root);
        Files.SafeTree(root, path);
        Files.AtomicWrite(path, Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)));
    }

    public static void Clear(string root)
    {
        var path = StatePath(root);
        Files.SafeTree(root, path);
        if (File.Exists(path)) File.Delete(path);
    }

    public static async Task<object?> HandleAsync(string root, object? payload, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
    {
        if (payload is not JsonObject request) throw new VackleException("Invalid sync request");
        switch (request.Text("op"))
        {
            case "load": return Load(root);
            case "clear": Clear(root); return null;
            case "store" when request.Text("value") is { } value: Store(root, value); return null;
            case "http": return await RelayAsync(request, send ?? ((message, cancel) => Updater.HttpManual.SendAsync(message, cancel))).ConfigureAwait(false);
            default: throw new VackleException("Invalid sync request");
        }
    }

    static async Task<object?> RelayAsync(JsonObject request, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var method = request.Text("method");
        if (method is not ("GET" or "PUT" or "DELETE") || request.Text("id") is not { } id || !Hex64.IsMatch(id) || request.Text("token") is not { } token || !Hex64.IsMatch(token))
            throw new VackleException("Invalid sync request");
        var body = request.Text("body");
        if (body is not null && (method != "PUT" || body.Length > 1_600_000)) throw new VackleException("Invalid sync request");
        using var message = new HttpRequestMessage(new HttpMethod(method), Endpoint + id);
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (body is not null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var response = await send(message, cancel.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        object? parsed = null;
        if (text.Length <= 2_000_000) { try { parsed = Json.Parse(text); } catch (FormatException) { } }
        return Json.Object(("status", (double)(int)response.StatusCode), ("body", parsed));
    }
}
