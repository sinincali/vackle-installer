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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace Vackle;

public sealed record ShortcutLocations(string Desktop, string Programs, string Pinned)
{
    public IEnumerable<string> All => [Desktop, Programs, Pinned];

    public static ShortcutLocations Current() => new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"));
}

public sealed record ShortcutLink(string Path, string Target, string Arguments);
public sealed record ShortcutEntry(string Path, string? Original, string InstalledHash);

public static class Shortcuts
{
    static string Key(string path) => System.IO.Path.GetFullPath(path).ToLowerInvariant();

    public static string LaunchTarget(string root) => System.IO.Path.Combine(root, "installed", "Vackle.exe");

    public static bool VackleLaunch(ShortcutLink link, string root)
    {
        if (link.Target.Length > 0 && Key(link.Target) == Key(LaunchTarget(root)) && link.Arguments.Trim() == "launch-hidden") return true;
        var encoded = Regex.Match(link.Arguments, @"-EncodedCommand\s+([A-Za-z0-9+/=]+)\s*$", RegexOptions.IgnoreCase);
        if (!encoded.Success) return false;
        string script;
        try { script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded.Groups[1].Value)).ToLowerInvariant(); }
        catch (FormatException) { return false; }
        var installed = System.IO.Path.Combine(root, "installed").ToLowerInvariant();
        return script.Contains("launch-hidden") && (script.Contains(installed) || script.Contains(installed.Replace("'", "''")));
    }

    static void CheckPath(string path, ShortcutLocations locations)
    {
        var folder = locations.All.FirstOrDefault(location => Files.Inside(location, path));
        if (folder is null || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) throw new VackleException("Shortcut path is outside the current user's launch locations");
        Files.SafeTree(folder, path);
    }

    static List<ShortcutEntry>? Receipt(string root, ShortcutLocations locations)
    {
        var path = System.IO.Path.Combine(root, "shortcuts.json");
        Files.SafeTree(root, path);
        if (!File.Exists(path)) return null;
        if (Files.ReadJson(path) is not JsonObject value || value.Number("schema") != 1 || value.Array("entries") is not { } entries || entries.Count > 128)
            throw new VackleException("Invalid shortcut backup");
        var seen = new HashSet<string>();
        var result = new List<ShortcutEntry>();
        foreach (var item in entries)
        {
            if (item is not JsonObject entry || entry.Text("path") is not { } entryPath || entry.Text("installedHash") is not { } installedHash || !Regex.IsMatch(installedHash, "^[a-f0-9]{64}$") ||
                !entry.ContainsKey("original") || entry["original"] is not null && (entry["original"] is not string original || original.Length > 2097152 || !Regex.IsMatch(original, "^[A-Za-z0-9+/]*={0,2}$")))
                throw new VackleException("Invalid shortcut backup entry");
            CheckPath(entryPath, locations);
            if (!seen.Add(Key(entryPath))) throw new VackleException("Duplicate shortcut backup path");
            result.Add(new ShortcutEntry(entryPath, entry["original"] as string, installedHash));
        }
        return result;
    }

    static string ReceiptJson(IEnumerable<ShortcutEntry> entries) =>
        Json.Write(Json.Object(("schema", 1.0), ("entries", entries.Select(entry => (object?)Json.Object(("path", entry.Path), ("original", entry.Original), ("installedHash", entry.InstalledHash))).ToList())), true);

    static void Replace(string path, byte[] bytes)
    {
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".vackle-{Guid.NewGuid()}.lnk");
        try { Files.WriteNew(temporary, bytes); Files.RenameRetry(temporary, path, true); }
        finally { Files.TryDelete(temporary); }
    }

    public static List<ShortcutLink> Scan(ShortcutLocations locations)
    {
        var links = new List<ShortcutLink>();
        foreach (var folder in locations.All)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
            {
                try { var (target, arguments) = Read(file); links.Add(new ShortcutLink(file, target, arguments)); }
                catch (Exception error) when (error is COMException || error is IOException || error is UnauthorizedAccessException) { }
            }
        }
        return links;
    }

    static byte[] Build(string root, string target, string arguments, string directory, string icon, string description)
    {
        var stage = System.IO.Path.Combine(root, $"shortcut-{Guid.NewGuid()}.lnk");
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target); link.SetArguments(arguments); link.SetWorkingDirectory(directory); link.SetIconLocation(icon, 0); link.SetDescription(description); link.SetShowCmd(1);
                ((IPersistFile)link).Save(stage, true);
            }
            finally { Marshal.ReleaseComObject(link); }
            return File.ReadAllBytes(stage);
        }
        finally { Files.TryDelete(stage); }
    }

    static (string Target, string Arguments) Read(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(32768);
            link.GetArguments(arguments, arguments.Capacity);
            return (target.ToString(), arguments.ToString());
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    static byte[] PlainShortcut(string root, Installation host) => Build(root, host.Executable, "", host.Directory, host.Executable, "Flackle");

    public static List<string> Install(string root, Installation host, ShortcutLocations? locations = null)
    {
        locations ??= ShortcutLocations.Current();
        Files.SafeTree(root);
        Directory.CreateDirectory(root);
        var previous = Receipt(root, locations);
        var candidates = Scan(locations);
        var paths = new List<string>();
        void AddPath(string path) { if (!paths.Any(existing => Key(existing) == Key(path))) paths.Add(path); }
        foreach (var entry in previous ?? []) if (File.Exists(entry.Path)) AddPath(entry.Path);
        var adopted = new HashSet<string>();
        foreach (var candidate in candidates)
        {
            if (candidate.Target.Length > 0 && Key(candidate.Target) == Key(host.Executable)) AddPath(candidate.Path);
            else if (VackleLaunch(candidate, root) && !paths.Any(existing => Key(existing) == Key(candidate.Path))) { AddPath(candidate.Path); adopted.Add(Key(candidate.Path)); }
        }
        var desktop = System.IO.Path.Combine(locations.Desktop, "Flackle.lnk");
        if (previous is null && !File.Exists(desktop)) AddPath(desktop);
        if (!paths.Any(path => Files.Inside(locations.Programs, path)))
        {
            var path = System.IO.Path.Combine(locations.Programs, "Flackle (Vackle).lnk");
            if (File.Exists(path)) throw new VackleException("A Flackle (Vackle) shortcut already exists and is not owned by Vackle");
            AddPath(path);
        }
        var links = candidates.GroupBy(candidate => Key(candidate.Path)).ToDictionary(group => group.Key, group => group.First());
        var bytes = Build(root, LaunchTarget(root), "launch-hidden", root, host.Executable, "Flackle with Vackle");
        var plans = new List<(ShortcutEntry Entry, byte[]? Before)>();
        foreach (var path in paths)
        {
            CheckPath(path, locations);
            var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var saved = previous?.FirstOrDefault(entry => entry.Path == path);
            links.TryGetValue(Key(path), out var link);
            if (before is not null && saved is not null && !(link is not null && (VackleLaunch(link, root) || link.Target.Length > 0 && Key(link.Target) == Key(host.Executable))))
            {
                Console.Error.WriteLine($"Leaving a shortcut you changed alone: {path}");
                continue;
            }
            var original = saved is not null ? saved.Original : adopted.Contains(Key(path)) ? Convert.ToBase64String(PlainShortcut(root, host)) : before is null ? null : Convert.ToBase64String(before);
            plans.Add((new ShortcutEntry(path, original, Files.Hash(bytes)), before));
        }
        var receipt = System.IO.Path.Combine(root, "shortcuts.json");
        Files.AtomicWrite(receipt, ReceiptJson(plans.Select(plan => plan.Entry)));
        var committed = new List<(ShortcutEntry Entry, byte[]? Before)>();
        try
        {
            foreach (var plan in plans)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(plan.Entry.Path)!);
                CheckPath(plan.Entry.Path, locations);
                var current = File.Exists(plan.Entry.Path) ? File.ReadAllBytes(plan.Entry.Path) : null;
                if ((current is null ? null : Files.Hash(current)) != (plan.Before is null ? null : Files.Hash(plan.Before))) throw new VackleException("Shortcut changed during installation");
                Replace(plan.Entry.Path, bytes);
                committed.Add(plan);
            }
        }
        catch
        {
            committed.Reverse();
            foreach (var plan in committed) { if (plan.Before is not null) Replace(plan.Entry.Path, plan.Before); else File.Delete(plan.Entry.Path); }
            if (previous is not null) Files.AtomicWrite(receipt, ReceiptJson(previous)); else File.Delete(receipt);
            throw;
        }
        return plans.Select(plan => plan.Entry.Path).ToList();
    }

    public static List<string> Repair(string root, Installation host, ShortcutLocations? locations = null)
    {
        locations ??= ShortcutLocations.Current();
        var plain = Scan(locations).Where(link => link.Target.Length > 0 && Key(link.Target) == Key(host.Executable)).Select(link => link.Path).ToList();
        if (plain.Count == 0) return plain;
        Install(root, host, locations);
        return plain;
    }

    public static List<string> Verify(string root, ShortcutLocations? locations = null)
    {
        locations ??= ShortcutLocations.Current();
        var saved = Receipt(root, locations);
        var links = Scan(locations).GroupBy(link => Key(link.Path)).ToDictionary(group => group.Key, group => group.First());
        foreach (var entry in saved ?? [])
            if (!links.TryGetValue(Key(entry.Path), out var link) || !VackleLaunch(link, root)) throw new VackleException($"Launch shortcut changed or missing: {entry.Path}. Run Vackle.exe and choose Repair.");
        return saved?.Select(entry => entry.Path).ToList() ?? [];
    }

    public static void Remove(string root, ShortcutLocations? locations = null, Installation? host = null)
    {
        var path = System.IO.Path.Combine(root, "shortcuts.json");
        Files.SafeTree(root, path);
        locations ??= ShortcutLocations.Current();
        List<ShortcutEntry>? saved;
        try { saved = Receipt(root, locations); } catch (Exception error) when (error is VackleException || error is FormatException || error is IOException) { saved = null; }
        foreach (var link in Scan(locations))
        {
            if (!VackleLaunch(link, root)) continue;
            CheckPath(link.Path, locations);
            var entry = saved?.FirstOrDefault(item => Key(item.Path) == Key(link.Path));
            var original = entry is not null ? entry.Original : host is not null ? Convert.ToBase64String(PlainShortcut(root, host)) : null;
            if (original is null) File.Delete(link.Path); else Replace(link.Path, Convert.FromBase64String(original));
        }
        Files.TryDelete(path);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr data, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
