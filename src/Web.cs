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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vackle;

public static class Web
{
    static readonly Regex Language = new(@"^[a-z]{2,3}(-[A-Za-z]{2,4})?$");
    static readonly string[] EmbedOrigins = ["https://www.youtube-nocookie.com", "https://www.youtube.com"];
    static readonly Lazy<string> AdguardSource = new(() =>
    {
        using var stream = typeof(Web).Assembly.GetManifestResourceStream("adguard.js") ?? throw new VackleException("adguard.js is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    static async Task<string> GetTextAsync(HttpRequestMessage request, int timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        using var response = await Updater.Http.SendAsync(request, cancel.Token).ConfigureAwait(false);
        return $"{(int)response.StatusCode}\n{await response.Content.ReadAsStringAsync().ConfigureAwait(false)}";
    }

    static (int Status, object? Body) Split(string text)
    {
        var newline = text.IndexOf('\n');
        var status = int.Parse(text.Substring(0, newline));
        object? body;
        try { body = Json.Parse(text.Substring(newline + 1)); } catch (FormatException) { body = null; }
        return (status, body);
    }

    static bool IsLanguage(object? value, bool auto) => value is string text && (auto && text == "auto" || Language.IsMatch(text));

    public static async Task<JsonObject> TranslateAsync(object? payload)
    {
        if (payload is not JsonObject request || request.Text("text") is not { } text || text.Trim().Length == 0 || text.Length > 5000 || !IsLanguage(request.TryGetValue("from", out var fromRaw) ? fromRaw : null, true) || !IsLanguage(request.TryGetValue("to", out var to) ? to : null, false))
            throw new VackleException("Invalid translation request");
        var from = request.Text("from")!;
        var query = string.Join("&", new[] { ("params.client", "gtx"), ("dataTypes", "TRANSLATION"), ("key", "AIzaSyDLEeFI5OtFBwYBIoK_jj5m32rZK5CkCXA"),
            ("query.sourceLanguage", from), ("query.targetLanguage", (string)to!), ("query.text", text) }.Select(pair => Uri.EscapeDataString(pair.Item1) + "=" + Uri.EscapeDataString(pair.Item2)));
        var (status, body) = Split(await GetTextAsync(new HttpRequestMessage(HttpMethod.Get, "https://translate-pa.googleapis.com/v1/translate?" + query), 8000).ConfigureAwait(false));
        if (status < 200 || status >= 300) throw new VackleException($"Google Translate returned {status}");
        if (body is not JsonObject data || data.Text("translation") is not { } translation) throw new VackleException("Google Translate returned no translation");
        var source = data.Text("sourceLanguage");
        return Json.Object(("sourceLanguage", source is not null && IsLanguage(source, false) ? source : from), ("text", translation.Length > 20000 ? translation.Substring(0, 20000) : translation));
    }

    public static JsonObject PickBranding(object? response)
    {
        JsonObject? First(string key)
        {
            var entry = (response as JsonObject)?.Array(key)?.FirstOrDefault() as JsonObject;
            return entry is not null && entry.Bool("original") != true && (entry.Bool("locked") == true || entry.Number("votes") is >= 0) ? entry : null;
        }
        var data = response as JsonObject;
        var title = First("titles")?.Text("title");
        double? random = data?.Number("randomTime") is { } randomTime && data.Number("videoDuration") is { } duration ? randomTime * duration : null;
        var thumbnail = First("thumbnails");
        double? time = thumbnail is not null && thumbnail.TryGetValue("timestamp", out var stamp) && stamp is not null ? stamp as double? : random;
        return Json.Object(
            ("title", title is not null && title.Trim().Length > 0 ? Cut(Regex.Replace(title, @"(^|\s)>(\S)", "$1$2"), 300) : null),
            ("thumbnail", time is { } value && !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 ? value : null));
    }

    static string Cut(string text, int length) => text.Length > length ? text.Substring(0, length) : text;

    public static async Task<JsonObject> BrandingAsync(string videoId)
    {
        if (!Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$")) throw new VackleException("Invalid YouTube video ID");
        var (status, data) = Split(await GetTextAsync(new HttpRequestMessage(HttpMethod.Get, $"https://sponsor.ajay.app/api/branding?videoID={videoId}"), 5000).ConfigureAwait(false));
        if ((status < 200 || status >= 300) && status != 404) throw new VackleException($"DeArrow returned {status}");
        var branding = PickBranding(data);
        if (branding["thumbnail"] is not null || data is not JsonObject response || response.Number("videoDuration") is not null) return branding;
        double? length = null;
        try { length = await VideoLengthAsync(videoId).ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException) { }
        var copy = new JsonObject();
        foreach (var entry in response) copy[entry.Key] = entry.Value;
        copy["videoDuration"] = length;
        branding["thumbnail"] = PickBranding(copy)["thumbnail"];
        return branding;
    }

    static async Task<double?> VideoLengthAsync(string videoId)
    {
        var body = Json.Write(Json.Object(("videoId", videoId), ("context", Json.Object(("client", Json.Object(("clientName", "WEB"), ("clientVersion", "2.20240101.00.00")))))));
        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var (status, data) = Split(await GetTextAsync(request, 5000).ConfigureAwait(false));
        var raw = status >= 200 && status < 300 ? (data as JsonObject)?.Object("videoDetails")?.TryGetValue("lengthSeconds", out var seconds) == true ? seconds : null : null;
        var number = raw switch { double value => value, string text when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed, _ => double.NaN };
        return !double.IsNaN(number) && !double.IsInfinity(number) && number > 0 ? number : null;
    }

    public static bool PrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int a = bytes[0], b = bytes[1];
            return a == 0 || a == 10 || a == 127 || a >= 224 || a == 100 && b >= 64 && b < 128 || a == 169 && b == 254 || a == 172 && b >= 16 && b < 32 || a == 192 && b == 168 || a == 198 && (b == 18 || b == 19);
        }
        var text = address.ToString().ToLowerInvariant();
        return address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) || (bytes[0] & 0xfe) == 0xfc || bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80 ||
            text.StartsWith("64:ff9b:", StringComparison.Ordinal);
    }

    public static string? AudioType(byte[] bytes)
    {
        string Text(int start, int end) => bytes.Length >= end ? Encoding.ASCII.GetString(bytes, start, end - start) : "";
        if (Text(0, 3) == "ID3" || bytes.Length > 1 && bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0) return "audio/mpeg";
        if (Text(0, 4) == "OggS") return "audio/ogg";
        if (Text(0, 4) == "RIFF" && Text(8, 12) == "WAVE") return "audio/wav";
        if (Text(0, 4) == "fLaC") return "audio/flac";
        if (Text(4, 8) == "ftyp") return "audio/mp4";
        return null;
    }

    public static string? ImageType(byte[] bytes)
    {
        string Text(int start, int end) => bytes.Length >= end ? Encoding.ASCII.GetString(bytes, start, end - start) : "";
        if (bytes.Length > 3 && bytes[0] == 0x89 && Text(1, 4) == "PNG") return "image/png";
        if (bytes.Length > 2 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (Text(0, 4) == "GIF8") return "image/gif";
        if (Text(0, 4) == "RIFF" && Text(8, 12) == "WEBP") return "image/webp";
        if (Text(4, 12) == "ftypavif") return "image/avif";
        return null;
    }

    sealed record Kind(string Accept, int Limit, Func<byte[], string?> Detect, string TooBig, string WrongType);
    static readonly Kind Sound = new("audio/*", 1024 * 1024, AudioType, "Sounds must be 1 MB or smaller", "That link isn't an mp3, ogg, wav, flac or m4a file");
    static readonly Kind Picture = new("image/*", 8 * 1024 * 1024, ImageType, "Images must be 8 MB or smaller", "That link isn't a png, jpg, gif, webp or avif image");

    public static Task<JsonObject> SoundAsync(object? payload) => DownloadAsync(payload, Sound);
    public static Task<JsonObject> ImageAsync(object? payload) => DownloadAsync(payload, Picture);

    static async Task<JsonObject> DownloadAsync(object? payload, Kind kind)
    {
        if (payload is not string link || link.Length > 2048 || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var url)) throw new VackleException("That link is not valid");
        for (var hop = 0; hop < 4; hop++)
        {
            if (url.Scheme != "https" || url.UserInfo.Length > 0 || url.Port != 443) throw new VackleException("Only https:// links are allowed");
            var host = url.Host.Trim('[', ']');
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
            else { try { addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false); } catch (SocketException) { addresses = []; } }
            if (addresses.Length == 0) throw new VackleException("That website could not be found");
            if (addresses.Any(PrivateAddress)) throw new VackleException("Links to local or private addresses are not allowed");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(kind.Accept);
            using var cancel = new CancellationTokenSource(10000);
            using var response = await Updater.HttpManual.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status >= 300 && status < 400)
            {
                var next = response.Headers.Location ?? throw new VackleException("That link redirects nowhere");
                url = next.IsAbsoluteUri ? next : new Uri(url, next);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new VackleException($"That link returned an error ({status})");
            if (response.Content.Headers.ContentLength > kind.Limit) throw new VackleException(kind.TooBig);
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new MemoryStream();
            var chunk = new byte[65536];
            for (int read; (read = await stream.ReadAsync(chunk, 0, chunk.Length, cancel.Token).ConfigureAwait(false)) > 0;)
            {
                if (buffer.Length + read > kind.Limit) throw new VackleException(kind.TooBig);
                buffer.Write(chunk, 0, read);
            }
            var bytes = buffer.ToArray();
            var type = kind.Detect(bytes) ?? throw new VackleException(kind.WrongType);
            return Json.Object(("type", type), ("data", Convert.ToBase64String(bytes)));
        }
        throw new VackleException("That link redirects too many times");
    }

    public static bool YoutubeEmbed(object? url) =>
        url is string text && Uri.TryCreate(text, UriKind.Absolute, out var parsed) && EmbedOrigins.Contains(parsed.GetLeftPart(UriPartial.Authority)) && parsed.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal);

    public static string AdblockSource => $"if({Json.Write(EmbedOrigins.Cast<object?>().ToList())}.includes(location.origin)&&location.pathname.startsWith(\"/embed/\")){{{AdguardSource.Value}\n}}";

    public static async Task AttachFrameAsync(ICdp cdp, JsonObject parameters, bool enabled, IDictionary<string, string> navigations)
    {
        if (parameters.Text("sessionId") is not { } session) return;
        var info = parameters.Object("targetInfo");
        var target = info?.Text("targetId") ?? "";
        string? pending;
        lock (navigations) { navigations.TryGetValue(target, out pending); navigations.Remove(target); }
        var url = info?.Text("url");
        var inject = enabled && info?.Text("type") == "iframe" && YoutubeEmbed(string.IsNullOrEmpty(url) ? pending : url);
        var waiting = parameters.Bool("waitingForDebugger") == true;
        try
        {
            if (inject)
            {
                await cdp.SendAsync("Page.enable", new JsonObject(), session).ConfigureAwait(false);
                await cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument", Json.Object(("source", AdblockSource)), session).ConfigureAwait(false);
                if (!waiting) await cdp.SendAsync("Runtime.evaluate", Json.Object(("expression", AdblockSource)), session).ConfigureAwait(false);
            }
        }
        finally
        {
            if (waiting) await cdp.SendAsync("Runtime.runIfWaitingForDebugger", new JsonObject(), session).ConfigureAwait(false);
            if (!inject) await cdp.SendAsync("Target.detachFromTarget", Json.Object(("sessionId", session))).ConfigureAwait(false);
        }
    }
}
