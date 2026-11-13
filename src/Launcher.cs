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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Vackle;

public static class Launcher
{
    public static async Task LaunchAsync(string root, Updates? updates)
    {
        try { Updater.CleanStaged(root); } catch (Exception error) when (error is IOException || error is VackleException || error is UnauthorizedAccessException) { }
        while (true)
        {
            var outcome = await SessionAsync(root, updates).ConfigureAwait(false);
            if (outcome == "exit") return;
            if (outcome != "restart") { HandOff(outcome); return; }
        }
    }

    public static void HandOff(string staged)
    {
        try
        {
            using var helper = Win.Start(Path.Combine(staged, "Vackle.exe"), $"apply-update --stage \"{staged}\" --pid {Process.GetCurrentProcess().Id}", staged, hidden: true);
            Console.WriteLine($"Handing off to the Vackle update in {staged}");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"[Vackle:update] Could not start the update: {error.Message}"); }
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static string Stamp(string path)
    {
        var info = new FileInfo(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    static bool LauncherAlive(string root) => Installer.LockOwner(root) is { } owner && owner != Process.GetCurrentProcess().Id;

    public static async Task<int?> RelaunchedHostAsync(string executable, int previousPid, string launchedStamp, Func<int, bool> owns)
    {
        var start = DateTime.UtcNow;
        for (var replaced = false; DateTime.UtcNow - start < TimeSpan.FromMinutes(2); await Task.Delay(1000).ConfigureAwait(false))
        {
            var items = Win.Processes();
            foreach (var item in items)
            {
                if (item.Pid == previousPid || !string.Equals(item.Path, executable, StringComparison.OrdinalIgnoreCase)) continue;
                bool owned;
                try { owned = owns(item.Pid); } catch (Exception error) when (error is not OutOfMemoryException) { owned = false; }
                if (owned) return item.Pid;
            }
            if (!replaced) { try { replaced = Stamp(executable) != launchedStamp; } catch (IOException) { replaced = true; } }
            var installing = items.Any(item => item.Parent == previousPid && !Regex.IsMatch(item.Path, @"msedgewebview2\.exe$", RegexOptions.IgnoreCase));
            if (!replaced && !installing && DateTime.UtcNow - start >= TimeSpan.FromSeconds(5)) return null;
        }
        return null;
    }

    static async Task ClearWebViewsAsync(Installation host)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            List<int> left;
            try { left = Win.HostWebViews(Path.GetFileName(host.Executable)); } catch (Exception error) when (error is not OutOfMemoryException) { left = []; }
            if (left.Count == 0) return;
            foreach (var pid in left) Win.Kill(pid);
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new VackleException("Flackle's old WebView2 processes would not close. Restart your PC and open Flackle again.");
    }

    static void Report(Installation host, int port, int hostPid, IEnumerable<string> failures)
    {
        Console.WriteLine($"Problems while waiting: {(failures.Any() ? string.Join(" | ", failures) : "none")}");
        Console.WriteLine($"Vackle asked Flackle {hostPid} for debug port {port}");
        try { foreach (var line in Win.LaunchReport(Path.GetFileName(host.Executable))) Console.WriteLine(line); }
        catch (Exception error) when (error is not OutOfMemoryException) { Console.WriteLine($"Could not read process details: {error.Message}"); }
    }

    static void KeepShortcuts(string root, Installation host)
    {
        try { var fixedLinks = Shortcuts.Repair(root, host); if (fixedLinks.Count > 0) Console.WriteLine($"Pointed Flackle shortcuts back at Vackle: {string.Join(", ", fixedLinks)}"); }
        catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"[Vackle:launcher] Shortcut repair failed: {error.Message}"); }
    }

    static async Task<string> SessionAsync(string root, Updates? updates)
    {
        var listed = Installer.VerifyFiles(root);
        var host = Detection.Validate(listed.Host.Directory);
        var elevated = Win.IsElevated();
        var running = Win.Processes().FirstOrDefault(item => string.Equals(item.Path, host.Executable, StringComparison.OrdinalIgnoreCase));
        if (running.Pid != 0)
        {
            for (var deadline = DateTime.UtcNow.AddMinutes(1); !await VerifiedAsync(root).ConfigureAwait(false); await Task.Delay(1000).ConfigureAwait(false))
                if (DateTime.UtcNow > deadline || !LauncherAlive(root))
                    throw new VackleException("Flackle is running without a verified Vackle session. Quit it completely, including its tray icon, and open Flackle again using the installed shortcut.");
            Console.WriteLine("Flackle is already running with Vackle.");
            try { using var focus = Win.Start(host.Executable, "", host.Directory); }
            catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"Unable to show Flackle: {error.Message}"); }
            return "exit";
        }
        var lockPath = Path.Combine(root, "launcher.lock");
        if (LauncherAlive(root)) { await Task.Delay(1000).ConfigureAwait(false); return "restart"; }
        Installer.ClearStaleLock(root);
        var session = Guid.NewGuid().ToString();
        try { Files.WriteNew(lockPath, Encoding.UTF8.GetBytes(Json.Write(Json.Object(("pid", (double)Process.GetCurrentProcess().Id), ("session", session))))); }
        catch (IOException) when (File.Exists(lockPath)) { await Task.Delay(1000).ConfigureAwait(false); return "restart"; }
        var stopping = false;
        var restarting = false;
        string? handoff = null;
        RendererSession? renderer = null;
        Process? child = null;
        var hostPid = 0;
        var adopted = false;
        bool HostAlive() => adopted ? Win.Alive(hostPid) : child is not null && !child.HasExited;
        Pip? pip = null;
        var receiptPath = Path.Combine(root, "runtime.json");
        ConsoleCancelEventHandler stop = (_, args) => { args.Cancel = true; stopping = true; };
        Console.CancelKeyPress += stop;
        try
        {
            Files.SafeTree(root, receiptPath);
            Files.TryDelete(receiptPath);
            var source = Files.ReadText(Path.Combine(root, "installed", "renderer.js"));
            var port = FreePort();
            var arguments = $"--remote-debugging-port={port} --remote-debugging-address=127.0.0.1 --disable-features=msEdgePipAlignedUI";
            var environment = new Dictionary<string, string?> { ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = arguments, ["WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER"] = null, ["WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER"] = null };
            await ClearWebViewsAsync(host).ConfigureAwait(false);
            var launchedStamp = Stamp(host.Executable);
            if (elevated)
            {
                try
                {
                    foreach (var entry in environment) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
                    hostPid = Win.StartUnelevated(host.Executable, host.Directory);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { Console.WriteLine($"Could not start Flackle without admin: {error.Message}"); hostPid = 0; }
            }
            if (hostPid != 0) { adopted = true; Console.WriteLine("Vackle is running as admin, so Flackle was started without admin."); }
            else
            {
                child = Win.Start(host.Executable, "", host.Directory, environment);
                hostPid = child.Id;
            }
            Console.WriteLine($"Launched Flackle {host.Version}. Waiting for the Vackle renderer handshake…");
            var deadline = DateTime.UtcNow.AddMinutes(1);
            var verifiedOwner = false;
            var lastFailure = "Renderer not yet available";
            var acknowledged = false;
            var reported = false;
            var previousReceipt = "";
            var previousFailure = "";
            var waitFailures = new List<string>();
            while (true)
            {
                while (!stopping && HostAlive())
                {
                    try
                    {
                        if (renderer is null || !renderer.Cdp.Connected)
                        {
                            var listedTargets = await Cdp.TargetsAsync(port).ConfigureAwait(false);
                            if (!verifiedOwner) { Win.VerifyPortOwner(port, hostPid); verifiedOwner = true; continue; }
                            var pages = listedTargets.Where(Cdp.FlackleTarget).ToList();
                            if (pages.Count > 1) throw new VackleException("Multiple Flackle pages detected; refusing ambiguous target");
                            if (pages.Count == 1)
                            {
                                var cdp = await Cdp.ConnectAsync(pages[0].WebSocketDebuggerUrl, port).ConfigureAwait(false);
                                renderer = new RendererSession(cdp, root, source, session, listed.Version, host.Version, () => { restarting = true; stopping = true; }, updates,
                                    staged => { handoff = staged; restarting = true; stopping = true; });
                                try { await renderer.InitializeAsync().ConfigureAwait(false); }
                                catch { cdp.Close(); renderer = null; throw; }
                            }
                        }
                        var state = renderer is null ? null : await renderer.EnsureAsync().ConfigureAwait(false) as JsonObject;
                        if (state is not null && state.Bool("ready") == true && state.Text("session") == session)
                        {
                            var receipt = Json.Object(("session", session), ("hostPid", (double)hostPid), ("launcherPid", (double)Process.GetCurrentProcess().Id), ("port", (double)port),
                                ("hostHash", host.Sha256), ("targetOrigin", state.Text("origin")), ("version", state.TryGetValue("version", out var v) ? v : null),
                                ("hostVersion", state.TryGetValue("hostVersion", out var hv) ? hv : null), ("plugins", state.TryGetValue("plugins", out var plugins) ? plugins : null));
                            var encoded = Json.Write(receipt);
                            if (encoded != previousReceipt)
                            {
                                var written = Json.ParseObject(encoded);
                                written["observedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                                Files.AtomicWrite(receiptPath, Json.Write(written, true));
                                previousReceipt = encoded;
                            }
                            if (!acknowledged) { Console.WriteLine($"Verified: Vackle {listed.Version} is ready inside the real Flackle desktop renderer. Keep this launcher running."); acknowledged = true; }
                            var pipActive = state.Array("plugins")?.OfType<JsonObject>().Any(plugin => plugin.Text("id") == "picture-in-picture" && plugin.Bool("active") == true) == true;
                            if (pipActive) pip ??= new Pip();
                            else { pip?.Dispose(); pip = null; }
                        }
                        else if (!acknowledged && DateTime.UtcNow > deadline) throw new VackleException("Runtime handshake did not complete");
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        lastFailure = error is System.Net.Http.HttpRequestException or TaskCanceledException ? "Renderer not yet available" : error.Message;
                        if (!acknowledged && !waitFailures.Contains(lastFailure)) waitFailures.Add(lastFailure);
                        if (DateTime.UtcNow > deadline && !acknowledged && !reported) { reported = true; Report(host, port, hostPid, waitFailures); Console.Error.WriteLine($"[Vackle:launcher] Still waiting for Flackle: {lastFailure}"); }
                        if (acknowledged && lastFailure != previousFailure) { Console.Error.WriteLine($"[Vackle:launcher] {lastFailure}"); previousFailure = lastFailure; }
                    }
                    await Task.Delay(acknowledged || reported ? 3000 : 250).ConfigureAwait(false);
                }
                if (!acknowledged && !stopping) Report(host, port, hostPid, waitFailures);
                if (!acknowledged && !stopping) throw new VackleException($"Flackle exited before loading Vackle: {lastFailure}");
                if (stopping) break;
                var next = await RelaunchedHostAsync(host.Executable, hostPid, launchedStamp, pid => { Win.VerifyPortOwner(port, pid); return true; }).ConfigureAwait(false);
                if (next is null) break;
                if (renderer is not null) { try { await renderer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException) { renderer.Cdp.Close(); } }
                renderer = null;
                hostPid = next.Value; adopted = true; previousReceipt = "";
                host = Detection.Validate(host.Directory);
                launchedStamp = Stamp(host.Executable);
                Console.WriteLine($"Flackle restarted itself (now {host.Version}). Reconnecting Vackle…");
                KeepShortcuts(root, host);
            }
        }
        finally
        {
            pip?.Dispose();
            Console.CancelKeyPress -= stop;
            if (renderer is not null)
            {
                try { await renderer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"[Vackle:launcher] Cleanup failed: {error.Message}"); renderer.Cdp.Close(); }
            }
            if (restarting && HostAlive())
            {
                try
                {
                    Win.Kill(hostPid);
                    for (var attempt = 0; attempt < 40 && Win.Alive(hostPid); attempt++) await Task.Delay(250).ConfigureAwait(false);
                    if (Win.Alive(hostPid)) throw new VackleException("Flackle did not exit for restart");
                }
                finally { Files.TryDelete(receiptPath); Files.TryDelete(lockPath); }
            }
            child?.Dispose();
            if (!restarting) KeepShortcuts(root, host);
            Files.TryDelete(receiptPath);
            Files.TryDelete(lockPath);
        }
        return handoff ?? (restarting ? "restart" : "exit");
    }

    static async Task<bool> VerifiedAsync(string root)
    {
        try { await VerifyRuntimeAsync(root).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    public static async Task<JsonObject> VerifyRuntimeAsync(string root)
    {
        var manifest = Installer.VerifyFiles(root);
        var host = Detection.Validate(manifest.Host.Directory);
        var path = Path.Combine(root, "runtime.json");
        Files.SafeTree(root, path);
        if (!File.Exists(path)) throw new VackleException("No runtime handshake. Open Flackle with Vackle first.");
        if (Files.ReadJson(path) is not JsonObject receipt || receipt.Text("hostHash") != host.Sha256 || receipt.Number("hostPid") is not { } hostPid ||
            receipt.Number("launcherPid") is not { } launcherPid || receipt.Number("port") is not { } port || receipt.Text("session") is not { } session ||
            port != Math.Floor(port) || port < 1 || port > 65535) throw new VackleException("Invalid runtime receipt");
        var items = Win.Processes();
        if (!items.Any(item => item.Pid == hostPid && string.Equals(item.Path, host.Executable, StringComparison.OrdinalIgnoreCase)) || !items.Any(item => item.Pid == launcherPid))
            throw new VackleException("Runtime receipt is stale");
        Win.VerifyPortOwner((int)port, (int)hostPid);
        var pages = (await Cdp.TargetsAsync((int)port).ConfigureAwait(false)).Where(Cdp.FlackleTarget).ToList();
        if (pages.Count != 1) throw new VackleException("Flackle renderer is unavailable or ambiguous");
        using var cdp = await Cdp.ConnectAsync(pages[0].WebSocketDebuggerUrl, (int)port).ConfigureAwait(false);
        var state = await cdp.EvaluateAsync("({ready:Boolean(window.__VACKLE__?.ready),session:window.__VACKLE__?.session,version:window.__VACKLE__?.version,hostVersion:window.__VACKLE__?.hostVersion,plugins:window.__VACKLE__?.plugins(),native:Boolean(window.__TAURI_INTERNALS__),origin:location.origin})").ConfigureAwait(false) as JsonObject;
        if (state is null || state.Bool("ready") != true || state.Bool("native") != true || state.Text("origin") != "https://flackle.com" || state.Text("session") != session ||
            state.Text("version") != manifest.Version || state.Text("hostVersion") != host.Version) throw new VackleException("Live Vackle renderer handshake failed");
        return state;
    }
}
