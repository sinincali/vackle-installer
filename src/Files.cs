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
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Vackle;

public static class Files
{
    static readonly UTF8Encoding Utf8 = new(false);

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
    }

    public static bool Inside(string folder, string path)
    {
        var relative = Relative(folder, path);
        return relative.Length > 0 && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    public static string Relative(string folder, string path)
    {
        var from = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var to = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return "";
        if (to.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return to.Substring(from.Length + 1);
        if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) return to;
        var a = from.Split(Path.DirectorySeparatorChar); var b = to.Split(Path.DirectorySeparatorChar);
        var common = 0;
        while (common < a.Length && common < b.Length && string.Equals(a[common], b[common], StringComparison.OrdinalIgnoreCase)) common++;
        return string.Join(Path.DirectorySeparatorChar.ToString(), Enumerable.Repeat("..", a.Length - common).Concat(b.Skip(common)));
    }

    public static void SafeTree(string root, string? target = null)
    {
        target ??= root;
        var full = Path.GetFullPath(root);
        var relative = Relative(full, target);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new VackleException("Path escapes Vackle data directory");
        var current = full;
        foreach (var component in new[] { "" }.Concat(relative.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)))
        {
            if (component.Length > 0) current = Path.Combine(current, component);
            if (!Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new VackleException($"Refusing linked Vackle path: {current}");
        }
    }

    public static string ReadText(string path) => File.ReadAllText(path, Utf8);

    public static object? ReadJson(string path) => Json.Parse(ReadText(path).TrimStart('﻿'));

    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid()}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) { var bytes = Utf8.GetBytes(content); stream.Write(bytes, 0, bytes.Length); }
            RenameRetry(temporary, path, true);
        }
        finally { TryDelete(temporary); }
    }

    public static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes, 0, bytes.Length);
    }

    public static void RenameRetry(string from, string to, bool replace = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else if (replace && File.Exists(to)) File.Replace(from, to, null, true);
                else File.Move(from, to);
                return;
            }
            catch (Exception error) when (attempt < 9 && (error is UnauthorizedAccessException || error is IOException))
            {
                Thread.Sleep(100 * (1 << Math.Min(attempt, 5)));
            }
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void RemoveTree(string path)
    {
        if (!Directory.Exists(path)) { TryDelete(path); return; }
        for (var attempt = 0; ; attempt++)
        {
            try { ClearReadOnly(path); Directory.Delete(path, true); return; }
            catch (Exception error) when (attempt < 9 && (error is UnauthorizedAccessException || error is IOException)) { Thread.Sleep(200); }
        }
    }

    static void ClearReadOnly(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, FileAttributes.Normal);
    }
}

public sealed class VackleException(string message) : Exception(message);
