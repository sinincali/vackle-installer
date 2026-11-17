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
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vackle;

public static class Badges
{
    const string Endpoint = "https://api.vackle.dev/v1/badges";
    static readonly Regex Digits = new("^[0-9]{1,24}$", RegexOptions.CultureInvariant);
    static readonly Regex Base64 = new("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);

    public static async Task<object?> SubmitAsync(object? payload, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
    {
        if (payload is not JsonObject request || request.Text("user") is not { } user || !Digits.IsMatch(user) || request.Text("image") is not { } image || image.Length > 80_000 || !Base64.IsMatch(image))
            throw new VackleException("Invalid badge request");
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint + "/submit") { Content = new StringContent(Json.Write(Json.Object(("user", user), ("image", image))), Encoding.UTF8, "application/json") };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await (send ?? ((request, token) => Updater.HttpManual.SendAsync(request, token)))(message, cancel.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        object? parsed = null;
        if (text.Length <= 100_000) { try { parsed = Json.Parse(text); } catch (FormatException) { } }
        return Json.Object(("status", (double)(int)response.StatusCode), ("body", parsed));
    }

    public static async Task<object?> FetchAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await (send ?? ((request, token) => Updater.HttpManual.SendAsync(request, token)))(message, cancel.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new VackleException($"The badge list returned {(int)response.StatusCode}");
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (text.Length > 500_000) throw new VackleException("The badge list is too large");
        try { return Json.Parse(text); }
        catch (FormatException) { throw new VackleException("The badge list was not understood"); }
    }
}
