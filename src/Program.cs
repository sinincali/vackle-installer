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
using System.Text;
using System.Threading.Tasks;

namespace Vackle;

public static class Program
{
    static readonly string[] Hidden = ["launch-hidden", "apply-update"];

    public static string ExecutablePath => Process.GetCurrentProcess().MainModule!.FileName;

    [STAThread]
    static int Main(string[] args)
    {
        var command = args.FirstOrDefault();
        var hidden = command is not null && Hidden.Contains(command);
        string? root = null;
        try
        {
            root = Installer.DataDirectory();
            if (hidden) RedirectToLog(root, command == "apply-update" ? "update.log" : "launcher.log");
            else Win.AttachConsole();
            Run(root, args).GetAwaiter().GetResult();
            return Environment.ExitCode;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[Vackle] {error.Message}");
            var supervised = Environment.GetEnvironmentVariable("VACKLE_SUPERVISED") == "1" && DateTime.Now - Process.GetCurrentProcess().StartTime < TimeSpan.FromMinutes(3);
            if (hidden && !supervised) Win.MessageBox(error.Message, false);
            return 1;
        }
    }

    static void RedirectToLog(string root, string name)
    {
        var path = Path.Combine(root, name);
        Files.SafeTree(root, path);
        Directory.CreateDirectory(root);
        var writer = new LogWriter(path);
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    sealed class LogWriter(string path) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Write(value.ToString());
        public override void WriteLine(string? value)
        {
            lock (this) File.AppendAllText(path, $"[{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}] {value}\n", new UTF8Encoding(false));
        }
        public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) WriteLine(value!.TrimEnd('\n')); }
    }

    static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static IFeed? Feed()
    {
        var feed = Environment.GetEnvironmentVariable("VACKLE_UPDATE_FEED");
        if (!string.IsNullOrEmpty(feed)) return Updater.Directory(feed);
        return BuildInfo.Repository.Length > 0 ? Updater.GitHub(BuildInfo.Repository) : null;
    }

    public static Updates UpdatesFor(string root, Manifest? manifest) => new(root, manifest?.Version ?? "0.0.0", Feed(), BuildInfo.PublicKey, manifest?.Commit);

    internal static bool InsideData(string root) => Files.Inside(root, ExecutablePath);

    static bool Popup(string message, bool question = false) => Win.MessageBox(message, question) == 6;

    sealed record Payload(Dictionary<string, byte[]> Files, string Version, string Runtime, string? Commit);

    static async Task<Payload> ReleasePayloadAsync(string root, string? local)
    {
        if (local is not null)
        {
            if (Files.ReadJson(Path.Combine(local, "vackle-local.json")) is not JsonObject info || info.Text("version") is not { } version || info.Text("runtime") is not { } runtime)
                throw new VackleException("vackle-local.json is missing or invalid");
            return new Payload(Installer.RuntimeFiles.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(local, name))), version, runtime, info.Text("commit"));
        }
        var updates = UpdatesFor(root, null);
        if (!updates.Enabled) throw new VackleException("This Vackle.exe has no release source configured");
        var staged = await updates.StageAsync(true).ConfigureAwait(false);
        try
        {
            var (release, files) = Updater.ReadStaged(staged, BuildInfo.PublicKey, null);
            if (!release.Runtime.StartsWith("native-", StringComparison.Ordinal)) throw new VackleException($"Vackle {release.Version} on GitHub is an older build this installer can't set up. Try again after the next release.");
            return new Payload(files, release.Version, release.Runtime, release.Commit);
        }
        finally { Files.RemoveTree(staged); }
    }

    internal static async Task InstallAsync(string root, Installation host, bool repair, string? local)
    {
        if (InsideData(root)) throw new VackleException("This is the installed copy of Vackle. Run the Vackle.exe you downloaded to install, repair or update.");
        Console.WriteLine(local is null ? "Downloading the latest Vackle release…" : $"Installing local build from {local}…");
        var payload = await ReleasePayloadAsync(root, local).ConfigureAwait(false);
        var outcome = Installer.Install(root, host, payload.Files, new Installer.Options(repair, payload.Version, payload.Runtime, payload.Commit));
        var shortcuts = Shortcuts.Install(root, host);
        Console.WriteLine(outcome == "unchanged" ? $"Vackle {payload.Version} is already installed and up to date." : $"Vackle {payload.Version} installed for Flackle {host.Version}.");
        Console.WriteLine($"Shortcuts: {string.Join(", ", shortcuts)}");
    }

    internal static void StartInstalled(string root)
    {
        try { using var child = Win.Start(Path.Combine(root, "installed", "Vackle.exe"), "launch-hidden", root); }
        catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"Could not start Flackle: {error.Message}"); }
    }

    internal static async Task CloseFlackleAsync(string root, Installation host)
    {
        foreach (var item in Win.Processes()) if (string.Equals(item.Path, host.Executable, StringComparison.OrdinalIgnoreCase)) Win.Kill(item.Pid);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (!LauncherRunning(root) && !Win.Processes().Any(item => string.Equals(item.Path, host.Executable, StringComparison.OrdinalIgnoreCase))) return;
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new VackleException("Flackle did not close. Quit it from the tray icon and try again.");
    }

    static bool LauncherRunning(string root)
    {
        try { Installer.ClearStaleLock(root); return false; } catch (VackleException) { return true; }
    }

    internal static bool Running(string root, Installation host) => LauncherRunning(root) || Win.Processes().Any(item => string.Equals(item.Path, host.Executable, StringComparison.OrdinalIgnoreCase));

    static async Task<bool> FlackleChangedAsync(string root, string reason)
    {
        var manifest = Installer.ReadManifest(root);
        var updates = UpdatesFor(root, manifest);
        var status = await updates.StatusAsync(true).ConfigureAwait(false);
        var available = status.Object("available");
        if (available is not null && Popup($"Flackle was updated and Vackle {manifest.Version} does not support it yet.\n\nVackle {available.Text("version")} is available. Install it now?", true))
        {
            Launcher.HandOff(await updates.StageAsync().ConfigureAwait(false));
            return true;
        }
        Popup(available is not null ? "Flackle will open without Vackle this time." :
            $"Flackle was updated and Vackle does not support this version yet ({reason}).\n\nFlackle will open without Vackle. Vackle will offer an update as soon as a compatible version is released.");
        using var child = Win.Start(manifest.Host.Executable, "", manifest.Host.Directory);
        return true;
    }

    static async Task Run(string root, string[] args)
    {
        var command = args.FirstOrDefault();
        if (command is null)
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new InstallerWindow(root));
            return;
        }
        var rest = args.Skip(1).ToArray();
        switch (command)
        {
            case "install":
            case "repair":
                await InstallAsync(root, Detection.Select(Option(rest, "--path")), command == "repair", Option(rest, "--local")).ConfigureAwait(false);
                break;
            case "uninstall":
                if (InsideData(root)) throw new VackleException("Run the Vackle.exe you downloaded to uninstall.");
                Installer.Uninstall(root);
                Console.WriteLine("Vackle removed completely. Flackle application files were never modified.");
                break;
            case "launch":
            case "launch-hidden":
                Directory.SetCurrentDirectory(root);
                try { await Launcher.LaunchAsync(root, UpdatesFor(root, Installer.ReadManifest(root))).ConfigureAwait(false); }
                catch (VackleException error) when (command == "launch-hidden")
                {
                    if (error.Message.IndexOf("product metadata", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bool handled;
                        try { handled = await FlackleChangedAsync(root, error.Message).ConfigureAwait(false); } catch (Exception inner) when (inner is not OutOfMemoryException) { handled = false; }
                        if (handled) return;
                    }
                    if (!error.Message.Contains("without a verified Vackle session")) throw;
                    await CloseFlackleAsync(root, Installer.ReadManifest(root).Host).ConfigureAwait(false);
                    await Launcher.LaunchAsync(root, UpdatesFor(root, Installer.ReadManifest(root))).ConfigureAwait(false);
                }
                break;
            case "apply-update":
                var staged = Option(rest, "--stage");
                if (staged is null || !int.TryParse(Option(rest, "--pid"), out var pid)) throw new VackleException("apply-update needs --stage and --pid");
                Files.SafeTree(root, staged);
                Console.WriteLine($"Update finished: {ApplyUpdate.Run(root, staged, pid, BuildInfo.PublicKey, new SystemApplyDeps(root))}");
                break;
            case "verify":
                Console.WriteLine("Live Vackle verification: " + Json.Write(await Launcher.VerifyRuntimeAsync(root).ConfigureAwait(false), true));
                break;
            case "status":
                if (Directory.Exists(Path.Combine(root, "installed")))
                {
                    var manifest = Installer.VerifyFiles(root);
                    var host = Detection.Validate(manifest.Host.Directory);
                    var shortcuts = Shortcuts.Verify(root);
                    var status = await UpdatesFor(root, manifest).StatusAsync(true).ConfigureAwait(false);
                    Console.WriteLine($"Vackle {manifest.Version} installed for Flackle {host.Version}: {host.Directory}");
                    Console.WriteLine($"{shortcuts.Count} persistent launch shortcuts verified.");
                    Console.WriteLine(status.Object("available") is { } available ? $"Update available: {available.Text("version")}" : status.Text("error") is { } error ? $"Update check failed: {error}" :
                        BuildInfo.Repository.Length > 0 || Environment.GetEnvironmentVariable("VACKLE_UPDATE_FEED") is { Length: > 0 } ? "Vackle is up to date." : "Updates are not configured for this build.");
                }
                else
                {
                    var host = Detection.Select(Option(rest, "--path"));
                    Console.WriteLine($"Flackle {host.Version}: {host.Directory}\nVackle is not installed.");
                }
                break;
            default:
                throw new VackleException($"Unknown command: {command}");
        }
    }
}
