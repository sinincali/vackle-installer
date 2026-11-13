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
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vackle;

public sealed record ReleaseFile(string Sha256, long Size);
public sealed record Release(string Version, string Runtime, string Notes, Dictionary<string, ReleaseFile> Files, string? Commit);
public sealed record Reusable(string Runtime, string Exe);
public sealed record Latest(Release Release, object? Document, Dictionary<string, string> Assets);

public interface IFeed
{
    Task<(object? Document, Dictionary<string, string> Assets)> LatestAsync();
    Task<byte[]> DownloadAsync(string url);
    Task<string> ChangesAsync(string from, string to);
}

public static class Updater
{
    static readonly Regex Semver = new(@"^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$");
    static readonly Regex Sha = new("^[0-9a-f]{7,40}$");
    static readonly Dictionary<string, long> SizeLimits = new() { ["Vackle.exe"] = 256L * 1024 * 1024, ["injector.cjs"] = 8L * 1024 * 1024, ["renderer.js"] = 16L * 1024 * 1024 };

    public static readonly HttpClient Http = CreateClient(true);
    public static readonly HttpClient HttpManual = CreateClient(false);

    static HttpClient CreateClient(bool redirects)
    {
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vackle-Updater");
        return client;
    }

    public static bool IsVersion(string version) => Semver.IsMatch(version);

    public static int CompareVersions(string a, string b)
    {
        var left = Semver.Match(a); var right = Semver.Match(b);
        if (!left.Success || !right.Success) throw new VackleException($"Invalid version: {(left.Success ? b : a)}");
        for (var index = 1; index <= 3; index++)
        {
            var difference = int.Parse(left.Groups[index].Value).CompareTo(int.Parse(right.Groups[index].Value));
            if (difference != 0) return Math.Sign(difference);
        }
        return 0;
    }

    public static Release VerifyRelease(object? document, string key)
    {
        if (string.IsNullOrEmpty(key)) throw new VackleException("This build has no release signing key, so updates are disabled");
        if (document is not JsonObject signed || signed.Text("manifest") is not { } manifest || signed.Text("signature") is not { } signature || manifest.Length > 65536)
            throw new VackleException("Release manifest is malformed");
        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(signature); } catch (FormatException) { throw new VackleException("Release signature is invalid"); }
        if (!Ed25519.Verify(Ed25519.PublicKeyFromSpki(Convert.FromBase64String(key)), Encoding.UTF8.GetBytes(manifest), signatureBytes))
            throw new VackleException("Release signature is invalid");
        var value = Json.Parse(manifest) as JsonObject;
        var commit = value?.ContainsKey("commit") == true ? value["commit"] : null;
        if (value is null || value.Number("schema") != 1 || value.Text("version") is not { } version || !Semver.IsMatch(version) ||
            value.Text("runtime") is not { } runtime || !Regex.IsMatch(runtime, @"^[\w.+-]{1,64}$") || value.Text("notes") is not { } notes || notes.Length > 20000 ||
            commit is not null && (commit is not string commitText || !Sha.IsMatch(commitText)) || value.Object("files") is not { } files ||
            !files.Keys.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(Installer.RuntimeFiles.OrderBy(name => name, StringComparer.Ordinal)))
            throw new VackleException("Release manifest has an unexpected shape");
        var result = new Dictionary<string, ReleaseFile>();
        foreach (var name in Installer.RuntimeFiles)
        {
            if (files.Object(name) is not { } file || file.Text("sha256") is not { } sha || !Regex.IsMatch(sha, "^[a-f0-9]{64}$") || file.Number("size") is not { } size ||
                size != Math.Floor(size) || size < 1 || size > SizeLimits[name]) throw new VackleException($"Release entry for {name} is invalid");
            result[name] = new ReleaseFile(sha, (long)size);
        }
        return new Release(version, runtime, notes, result, commit as string);
    }

    public static async Task<byte[]> FetchBytesAsync(string url, string accept, TimeSpan? timeout = null)
    {
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(new Uri(url).LocalPath);
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        using var response = await Http.SendAsync(request, cancel.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new VackleException($"Download failed ({(int)response.StatusCode}) for {new Uri(url).AbsolutePath}");
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    static async Task<object?> FetchJsonAsync(string url, string accept) => Json.Parse(Encoding.UTF8.GetString(await FetchBytesAsync(url, accept).ConfigureAwait(false)));

    public static IFeed GitHub(string repository)
    {
        if (!Regex.IsMatch(repository, @"^[\w.-]+/[\w.-]+$")) throw new VackleException("Update repository is not configured");
        return new GitHubFeed(repository);
    }

    sealed class GitHubFeed(string repository) : IFeed
    {
        public async Task<(object? Document, Dictionary<string, string> Assets)> LatestAsync()
        {
            if (await FetchJsonAsync($"https://api.github.com/repos/{repository}/releases/latest", "application/vnd.github+json").ConfigureAwait(false) is not JsonObject release || release.Array("assets") is not { } list)
                throw new VackleException("GitHub returned an unexpected release");
            var assets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in list)
                if (item is JsonObject asset && asset.Text("name") is { } name && asset.Text("browser_download_url") is { } url && url.StartsWith("https://github.com/", StringComparison.Ordinal)) assets[name] = url;
            if (!assets.TryGetValue("vackle-release.json", out var manifestUrl)) throw new VackleException("The latest release has no vackle-release.json");
            return (await FetchJsonAsync(manifestUrl, "application/octet-stream").ConfigureAwait(false), assets);
        }

        public Task<byte[]> DownloadAsync(string url) => FetchBytesAsync(url, "application/octet-stream");

        public async Task<string> ChangesAsync(string from, string to)
        {
            if (await FetchJsonAsync($"https://api.github.com/repos/{repository}/compare/{from}...{to}", "application/vnd.github+json").ConfigureAwait(false) is not JsonObject compare || compare.Array("commits") is not { } commits)
                throw new VackleException("GitHub returned an unexpected comparison");
            return string.Join("\n\n", commits.Select(entry => (entry as JsonObject)?.Object("commit")?.Text("message")?.Trim() ?? "").Where(message => message.Length > 0).Reverse());
        }
    }

    public static IFeed Directory(string location)
    {
        var baseUri = Uri.TryCreate(location, UriKind.Absolute, out var parsed) && !parsed.IsFile ? parsed : new Uri(Path.GetFullPath(location).TrimEnd('\\') + "\\");
        if (!baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)) baseUri = new Uri(baseUri.AbsoluteUri + "/");
        return new DirectoryFeed(baseUri);
    }

    sealed class DirectoryFeed(Uri baseUri) : IFeed
    {
        string Url(string name) => new Uri(baseUri, name).AbsoluteUri;
        public async Task<(object? Document, Dictionary<string, string> Assets)> LatestAsync() =>
            (await FetchJsonAsync(Url("vackle-release.json"), "application/json").ConfigureAwait(false), Installer.RuntimeFiles.ToDictionary(name => name, Url));
        public Task<byte[]> DownloadAsync(string url) => FetchBytesAsync(url, "application/octet-stream");
        public Task<string> ChangesAsync(string from, string to) => Task.FromResult("");
    }

    public static Reusable? ReusableExe(string root)
    {
        try
        {
            return Files.ReadJson(Path.Combine(root, "installed", "manifest.json")) is JsonObject manifest && manifest.Number("schema") == 2 && manifest.Text("runtime") is { } runtime &&
                manifest.Object("files")?.Text("Vackle.exe") is { } exe ? new Reusable(runtime, exe) : null;
        }
        catch (Exception error) when (error is IOException || error is FormatException || error is UnauthorizedAccessException) { return null; }
    }

    public static (Release Release, Dictionary<string, byte[]> Files) ReadStaged(string directory, string key, Reusable? reuse)
    {
        var release = VerifyRelease(Files.ReadJson(Path.Combine(directory, "vackle-release.json")), key);
        var files = new Dictionary<string, byte[]>();
        foreach (var name in Installer.RuntimeFiles)
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, name));
            var kept = name == "Vackle.exe" && reuse is not null && reuse.Runtime == release.Runtime && Files.Hash(bytes) == reuse.Exe;
            if (!kept && (bytes.Length != release.Files[name].Size || Files.Hash(bytes) != release.Files[name].Sha256)) throw new VackleException($"Staged {name} does not match the signed release");
            files[name] = bytes;
        }
        return (release, files);
    }

    public static void CleanStaged(string root, string? keep = null, TimeSpan? olderThan = null)
    {
        var limit = olderThan ?? TimeSpan.FromMinutes(10);
        var updates = Path.Combine(root, "updates");
        Files.SafeTree(root, updates);
        var leftovers = System.IO.Directory.Exists(root) ? System.IO.Directory.EnumerateDirectories(root).Where(path => Regex.IsMatch(Path.GetFileName(path), "^(stage|backup|failed)-[0-9a-f-]{36}$")) : [];
        var staged = System.IO.Directory.Exists(updates) ? System.IO.Directory.EnumerateDirectories(updates) : [];
        foreach (var path in leftovers.Concat(staged).ToList())
        {
            if (keep is not null && string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (DateTime.UtcNow - System.IO.Directory.GetLastWriteTimeUtc(path) >= limit) Files.RemoveTree(path); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
        }
    }

    public sealed class State
    {
        public string? Skipped;
        public List<string> Failed = [];
        public (string From, string To)? Updated;
    }

    public static State ReadState(string root)
    {
        var path = Path.Combine(root, "updates.json");
        Files.SafeTree(root, path);
        var state = new State();
        if (!File.Exists(path)) return state;
        JsonObject? value;
        try { value = Files.ReadJson(path) as JsonObject; } catch (FormatException) { return state; }
        if (value?.Number("schema") != 1) return state;
        state.Skipped = value.Text("skipped");
        state.Failed = value.Array("failed")?.OfType<string>().ToList() ?? [];
        if (state.Failed.Count > 32) state.Failed = state.Failed.Skip(state.Failed.Count - 32).ToList();
        if (value.Object("updated") is { } updated && updated.Text("from") is { } from && updated.Text("to") is { } to) state.Updated = (from, to);
        return state;
    }

    public static void UpdateState(string root, Action<State> change)
    {
        var state = ReadState(root);
        change(state);
        Files.AtomicWrite(Path.Combine(root, "updates.json"), Json.Write(Json.Object(("schema", 1.0), ("skipped", state.Skipped), ("failed", state.Failed.Cast<object?>().ToList()),
            ("updated", state.Updated is { } updated ? Json.Object(("from", updated.From), ("to", updated.To)) : null)), true));
    }
}

public sealed class Updates(string root, string current, IFeed? feed, string key, string? commit)
{
    readonly TimeSpan maxAge = TimeSpan.FromMinutes(10);
    readonly Dictionary<string, string> changelogs = new(StringComparer.Ordinal);
    readonly SemaphoreSlim gate = new(1, 1);
    (DateTime At, Latest Value)? cached;

    public string Current => current;
    public bool Enabled => feed is not null && !string.IsNullOrEmpty(key);

    public async Task<Latest> LatestAsync(bool force = false)
    {
        if (feed is null) throw new VackleException("Updates are not configured for this build");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!force && cached is { } hit && DateTime.UtcNow - hit.At < maxAge) return hit.Value;
            var (document, assets) = await feed.LatestAsync().ConfigureAwait(false);
            var latest = new Latest(Updater.VerifyRelease(document, key), document, assets);
            cached = (DateTime.UtcNow, latest);
            return latest;
        }
        finally { gate.Release(); }
    }

    public async Task<JsonObject> StatusAsync(bool force = false)
    {
        var state = Updater.ReadState(root);
        var result = Json.Object(("current", current), ("available", null), ("updated", state.Updated is { } updated ? Json.Object(("from", updated.From), ("to", updated.To)) : null), ("error", null));
        if (state.Updated is not null) Updater.UpdateState(root, value => value.Updated = null);
        if (!Enabled) return result;
        try
        {
            var release = (await LatestAsync(force).ConfigureAwait(false)).Release;
            if (Updater.CompareVersions(release.Version, current) > 0 && state.Skipped != release.Version && !state.Failed.Contains(release.Version))
                result["available"] = Json.Object(("version", release.Version), ("notes", await ChangesAsync(release).ConfigureAwait(false)));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { result["error"] = error.Message; }
        return result;
    }

    async Task<string> ChangesAsync(Release release)
    {
        if (string.IsNullOrEmpty(commit) || release.Commit is null || feed is null) return release.Notes;
        if (changelogs.TryGetValue(release.Commit, out var known)) return known;
        string listed;
        try { listed = await feed.ChangesAsync(commit!, release.Commit).ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException) { listed = ""; }
        if (listed.Length > 0) changelogs[release.Commit] = listed;
        return listed.Length > 0 ? listed : release.Notes;
    }

    public void Skip(string version)
    {
        if (!Updater.IsVersion(version)) throw new VackleException("Invalid version");
        Updater.UpdateState(root, state => state.Skipped = version);
    }

    public async Task<string> StageAsync(bool allowSame = false)
    {
        var latest = await LatestAsync(true).ConfigureAwait(false);
        var release = latest.Release;
        if (!allowSame && Updater.CompareVersions(release.Version, current) <= 0) throw new VackleException("No newer Vackle release is available");
        var directory = Path.Combine(root, "updates", $"{release.Version}-{Guid.NewGuid()}");
        Files.SafeTree(root, directory);
        System.IO.Directory.CreateDirectory(directory);
        var reuse = Updater.ReusableExe(root);
        try
        {
            foreach (var name in Installer.RuntimeFiles)
            {
                var expected = release.Files[name];
                var target = Path.Combine(directory, name);
                byte[]? local = null;
                foreach (var candidate in new[] { Path.Combine(root, "installed", name), name == "Vackle.exe" ? Program.ExecutablePath : null })
                {
                    if (candidate is null || !File.Exists(candidate)) continue;
                    var sameRuntime = name == "Vackle.exe" && reuse?.Runtime == release.Runtime && candidate.StartsWith(Path.Combine(root, "installed"), StringComparison.OrdinalIgnoreCase);
                    if (!sameRuntime && new FileInfo(candidate).Length != expected.Size) continue;
                    var bytes = File.ReadAllBytes(candidate);
                    if (Files.Hash(bytes) == expected.Sha256 || sameRuntime && Files.Hash(bytes) == reuse!.Exe) { local = bytes; break; }
                }
                if (local is null)
                {
                    if (!latest.Assets.TryGetValue(name, out var url)) throw new VackleException($"The release is missing {name}");
                    local = await feed!.DownloadAsync(url).ConfigureAwait(false);
                    if (local.Length != expected.Size || Files.Hash(local) != expected.Sha256) throw new VackleException($"Downloaded {name} does not match the signed release");
                }
                Files.WriteNew(target, local);
            }
            Files.WriteNew(Path.Combine(directory, "vackle-release.json"), Encoding.UTF8.GetBytes(Json.Write(latest.Document)));
            return directory;
        }
        catch { Files.RemoveTree(directory); throw; }
    }
}
