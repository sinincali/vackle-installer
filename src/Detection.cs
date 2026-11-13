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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Vackle;

public sealed record Installation(string Directory, string Executable, string Version, string Sha256)
{
    public JsonObject ToJson() => Json.Object(("directory", Directory), ("executable", Executable), ("version", Version), ("sha256", Sha256));

    public static Installation? FromJson(object? value) =>
        value is JsonObject host && host.Text("directory") is { } directory && host.Text("executable") is { } executable && host.Text("version") is { } version && host.Text("sha256") is { } sha
            ? new Installation(directory, executable, version, sha) : null;
}

public static class Detection
{
    public static Installation Validate(string directory)
    {
        var canonical = RealPath(Path.GetFullPath(directory));
        var expected = Path.Combine(canonical, "flackle-desktop.exe");
        if (!File.Exists(expected)) throw new VackleException($"flackle-desktop.exe was not found in {canonical}");
        var executable = RealPath(expected);
        if (!string.Equals(Path.GetFileName(executable), "flackle-desktop.exe", StringComparison.OrdinalIgnoreCase) || !string.Equals(executable, expected, StringComparison.OrdinalIgnoreCase))
            throw new VackleException("Executable redirects outside the selected installation");
        var data = File.ReadAllBytes(executable);
        if (data.Length < 2 || data[0] != 0x4d || data[1] != 0x5a) throw new VackleException("Flackle product metadata does not match: not a Windows program");
        var info = FileVersionInfo.GetVersionInfo(executable);
        if (info.ProductName != "Flackle" || info.ProductVersion is null || !Regex.IsMatch(info.ProductVersion, @"^\d{1,6}\.\d{1,6}\.\d{1,6}$"))
            throw new VackleException("Flackle product metadata does not match");
        return new Installation(canonical, executable, info.ProductVersion, Files.Hash(data));
    }

    public static List<string> Candidates()
    {
        var candidates = new List<string>();
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") as string == "Flackle" && entry.GetValue("InstallLocation") is string location && location.Trim('"').Length > 0)
                    candidates.Add(location.Trim('"'));
            }
        }
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "Flackle"));
        return candidates.GroupBy(path => Path.GetFullPath(path).ToLowerInvariant()).Select(group => Path.GetFullPath(group.First()))
            .Where(path => File.Exists(Path.Combine(path, "flackle-desktop.exe"))).ToList();
    }

    public static Installation Select(string? explicitPath = null)
    {
        if (explicitPath is not null) return Validate(explicitPath);
        var candidates = Candidates();
        if (candidates.Count != 1) throw new VackleException(candidates.Count > 0 ? "Multiple Flackle installations found. Pass --path <directory>." : "Flackle not found. Pass --path <directory>.");
        return Validate(candidates[0]);
    }

    public static string RealPath(string path)
    {
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new VackleException($"Cannot open {path}");
        var builder = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, builder, builder.Capacity, 0);
        if (length <= 0 || length >= builder.Capacity) throw new VackleException($"Cannot resolve {path}");
        var result = builder.ToString(0, length);
        if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + result.Substring(8);
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, int size, int flags);
}
