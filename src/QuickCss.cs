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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Vackle;

public static class QuickCss
{
    const int Limit = 524288;

    static string PathOf(string root) => Path.Combine(root, "quickcss.css");

    static string Stamp(string path) => new FileInfo(path).LastWriteTimeUtc.Ticks.ToString();

    public static object? Handle(string root, object? payload)
    {
        if (payload is not JsonObject request) throw new VackleException("Invalid QuickCSS request");
        var path = PathOf(root);
        Files.SafeTree(root, path);
        switch (request.Text("op"))
        {
            case "open" when request.Text("text") is { } text && text.Length <= Limit:
                Files.AtomicWrite(path, text);
                Open(path);
                return Json.Object(("stamp", Stamp(path)));
            case "write" when request.Text("text") is { } text && text.Length <= Limit:
                Files.AtomicWrite(path, text);
                return Json.Object(("stamp", Stamp(path)));
            case "read" when File.Exists(path):
                var stamp = Stamp(path);
                if (request.Text("stamp") == stamp) return Json.Object(("stamp", stamp));
                if (new FileInfo(path).Length > Limit * 4) throw new VackleException("The QuickCSS file is too large");
                return Json.Object(("text", Files.ReadText(path)), ("stamp", stamp));
            case "read": return null;
            default: throw new VackleException("Invalid QuickCSS request");
        }
    }

    static string? Code()
    {
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs\Microsoft VS Code", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Microsoft VS Code" })
        {
            var candidate = Path.Combine(folder, "Code.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static void Open(string path)
    {
        var program = Code() ?? Path.Combine(Environment.SystemDirectory, "notepad.exe");
        try { Process.Start(new ProcessStartInfo(program, "\"" + path + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (Win32Exception) { throw new VackleException("No editor could be started"); }
    }
}
