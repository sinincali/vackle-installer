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
using System.Text.RegularExpressions;

namespace Vackle;

public sealed record Manifest(int Schema, string Version, Installation Host, Dictionary<string, string> Files, string? Runtime, string? Commit);

public static class Installer
{
    public static readonly string[] RuntimeFiles = ["Vackle.exe", "injector.cjs", "renderer.js"];
    static readonly string[] LegacyFiles = ["injector.cjs", "launch.cmd", "renderer.js"];

    static string[] FileNames(int schema) => schema == 1 ? LegacyFiles : RuntimeFiles.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public static string DataDirectory()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(local)) throw new VackleException("LOCALAPPDATA is unavailable");
        return Path.Combine(local, "Vackle");
    }

    public static Manifest ReadManifest(string root, string folder = "installed")
    {
        var path = Path.Combine(root, folder, "manifest.json");
        Files.SafeTree(root, path);
        var value = Files.ReadJson(path) as JsonObject;
        var schema = value?.Number("schema");
        var host = Installation.FromJson(value?.Object("host"));
        if (value is null || schema != 1 && schema != 2 || value.Text("version") is not { } version || host is null || value.Object("files") is not { } files)
            throw new VackleException("Invalid Vackle installation manifest");
        if (schema == 2) Updater.CompareVersions(version, version);
        if (value.TryGetValue("runtime", out var runtime) && runtime is not null && (runtime is not string runtimeText || runtimeText.Length > 64))
            throw new VackleException("Invalid Vackle installation manifest");
        var names = files.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(FileNames((int)schema!)) || files.Values.Any(entry => entry is not string hash || !Regex.IsMatch(hash, "^[a-f0-9]{64}$")))
            throw new VackleException("Invalid installed file manifest");
        var commit = value.Text("commit");
        return new Manifest((int)schema, version, host, files.ToDictionary(entry => entry.Key, entry => (string)entry.Value!), runtime as string, commit is not null && Regex.IsMatch(commit, "^[0-9a-f]{7,40}$") ? commit : null);
    }

    static void WriteManifest(string path, Manifest manifest)
    {
        var files = new JsonObject();
        foreach (var entry in manifest.Files) files[entry.Key] = entry.Value;
        var json = Json.Object(("schema", (double)manifest.Schema), ("version", manifest.Version), ("host", manifest.Host.ToJson()), ("files", files), ("runtime", manifest.Runtime));
        if (manifest.Commit is not null) json["commit"] = manifest.Commit;
        Files.WriteNew(path, System.Text.Encoding.UTF8.GetBytes(Json.Write(json, true)));
    }

    public static int? LockOwner(string root)
    {
        int? pid = null;
        try { if (Files.ReadJson(Path.Combine(root, "launcher.lock")) is JsonObject owner && owner.Number("pid") is { } number) pid = (int)number; }
        catch (Exception error) when (error is IOException || error is FormatException || error is UnauthorizedAccessException) { }
        if (pid is null) return null;
        return Win.Processes().Any(item => item.Pid == pid && Regex.IsMatch(item.Path, @"\\(vackle|node)\.exe$", RegexOptions.IgnoreCase)) ? pid : null;
    }

    public static void ClearStaleLock(string root)
    {
        var path = Path.Combine(root, "launcher.lock");
        Files.SafeTree(root, path);
        if (!File.Exists(path)) return;
        if (LockOwner(root) is not null) throw new VackleException("Vackle launcher is already running. Quit Flackle completely, including its tray icon, and try again.");
        File.Delete(path);
    }

    static void CheckFolder(string root, string folder, Manifest manifest)
    {
        var directory = Path.Combine(root, folder);
        var names = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var expected = FileNames(manifest.Schema).Concat(["manifest.json"]).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(expected)) throw new VackleException("Unexpected files in Vackle installation; refusing automatic replacement");
        foreach (var name in names) Files.SafeTree(root, Path.Combine(directory, name!));
    }

    public static Manifest VerifyFiles(string root, string folder = "installed")
    {
        var manifest = ReadManifest(root, folder);
        if (manifest.Schema != 2) throw new VackleException("This Vackle installation is from an older installer. Run the latest Vackle.exe to upgrade it.");
        CheckFolder(root, folder, manifest);
        foreach (var entry in manifest.Files)
            if (Files.Hash(File.ReadAllBytes(Path.Combine(root, folder, entry.Key))) != entry.Value) throw new VackleException($"Installed file changed: {entry.Key}. Run Vackle.exe and choose Repair.");
        return manifest;
    }

    public sealed record Options(bool Repair, string Version, string Runtime, string? Commit, Action? AfterStaging = null);

    public static string Install(string root, Installation host, IReadOnlyDictionary<string, byte[]> files, Options options)
    {
        Files.SafeTree(root);
        Updater.CompareVersions(options.Version, options.Version);
        ClearStaleLock(root);
        Directory.CreateDirectory(root);
        var installed = Path.Combine(root, "installed");
        var hashes = RuntimeFiles.ToDictionary(name => name, name => Files.Hash(files[name]));
        if (Directory.Exists(installed))
        {
            var previous = ReadManifest(root);
            if (!string.Equals(Path.GetFullPath(previous.Host.Executable), Path.GetFullPath(host.Executable), StringComparison.OrdinalIgnoreCase))
                throw new VackleException("Vackle is installed for a different Flackle location; uninstall it first");
            if (previous.Schema == 2 && Updater.CompareVersions(options.Version, previous.Version) < 0)
                throw new VackleException($"Vackle {previous.Version} is already installed, which is newer than {options.Version}. Download the latest Vackle.exe instead.");
            CheckFolder(root, "installed", previous);
            if (!options.Repair && previous.Schema == 2 && previous.Version == options.Version && previous.Host.Sha256 == host.Sha256)
            {
                VerifyFiles(root);
                if (RuntimeFiles.All(name => previous.Files[name] == hashes[name])) return "unchanged";
            }
        }
        var stage = Path.Combine(root, $"stage-{Guid.NewGuid()}");
        var backup = Path.Combine(root, $"backup-{Guid.NewGuid()}");
        var moved = false;
        var committed = false;
        try
        {
            Directory.CreateDirectory(stage);
            foreach (var name in RuntimeFiles) Files.WriteNew(Path.Combine(stage, name), files[name]);
            WriteManifest(Path.Combine(stage, "manifest.json"), new Manifest(2, options.Version, host, hashes, options.Runtime, options.Commit));
            options.AfterStaging?.Invoke();
            if (Directory.Exists(installed)) { Files.RenameRetry(installed, backup); moved = true; }
            Files.RenameRetry(stage, installed);
            committed = true;
            VerifyFiles(root);
        }
        catch
        {
            if (committed) Files.RemoveTree(installed);
            if (moved && Directory.Exists(backup)) Files.RenameRetry(backup, installed);
            throw;
        }
        finally { if (Directory.Exists(stage)) Files.RemoveTree(stage); }
        if (moved)
        {
            var kept = Path.Combine(root, "previous");
            Manifest? old = null;
            try { old = ReadManifest(root, Path.GetFileName(backup)); } catch (Exception error) when (error is VackleException || error is IOException || error is FormatException) { }
            if (Directory.Exists(kept)) Files.RemoveTree(kept);
            if (old?.Schema == 2) Files.RenameRetry(backup, kept); else Files.RemoveTree(backup);
        }
        return "installed";
    }

    public static Manifest RestorePrevious(string root)
    {
        Files.SafeTree(root);
        var previous = VerifyFiles(root, "previous");
        var installed = Path.Combine(root, "installed");
        var failed = Path.Combine(root, $"failed-{Guid.NewGuid()}");
        if (Directory.Exists(installed)) Files.RenameRetry(installed, failed);
        try { Files.RenameRetry(Path.Combine(root, "previous"), installed); }
        catch { if (Directory.Exists(failed)) Files.RenameRetry(failed, installed); throw; }
        try { Files.RemoveTree(failed); } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
        VerifyFiles(root);
        return previous;
    }

    public static void Uninstall(string root)
    {
        Files.SafeTree(root);
        ClearStaleLock(root);
        if (!Directory.Exists(root)) { Console.WriteLine("Vackle is not installed."); return; }
        Manifest? manifest = null;
        try { manifest = ReadManifest(root); } catch (Exception error) when (error is VackleException || error is IOException || error is FormatException) { }
        if (manifest is not null) CheckFolder(root, "installed", manifest);
        Shortcuts.Remove(root, null, manifest?.Host);
        Files.RemoveTree(root);
    }
}
