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
using System.Text.RegularExpressions;

namespace Vackle;

public static class Settings
{
    static readonly Regex Identifier = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    static readonly string[] Toolboxes = ["titlebar", "panel", "profile", "hidden"];

    public static bool IsIdentifier(object? value) => value is string text && Identifier.IsMatch(text);

    public static JsonObject Defaults() => Json.Object(("schema", 1.0), ("plugins", new JsonObject()),
        ("css", Json.Object(("enabled", false), ("text", ""), ("filename", ""))), ("themes", new List<object?>()), ("toolbox", "titlebar"));

    public static JsonObject Validate(object? value)
    {
        if (value is not JsonObject input || input.Number("schema") != 1 || input.Object("plugins") is not { } pluginsInput || input.Object("css") is not { } css)
            throw new VackleException("Invalid Vackle settings schema");
        if (css.Bool("enabled") is not { } cssEnabled || css.Text("text") is not { } cssText || cssText.Length > 524288 || css.Text("filename") is not { } filename || filename.Length > 256)
            throw new VackleException("Invalid custom CSS settings (limit: 512 KiB)");
        if (pluginsInput.Count > 128) throw new VackleException("Too many plugin configurations");
        var plugins = new JsonObject();
        foreach (var entry in pluginsInput)
        {
            if (!IsIdentifier(entry.Key) || entry.Value is not JsonObject state || state.Bool("enabled") is not { } enabled || state.Object("values") is not { } valuesInput)
                throw new VackleException($"Invalid plugin configuration: {entry.Key}");
            var values = new JsonObject();
            foreach (var setting in valuesInput)
            {
                var ok = setting.Value is bool || setting.Value is double number && !double.IsNaN(number) && !double.IsInfinity(number) || setting.Value is string text && text.Length <= 4096;
                if (!IsIdentifier(setting.Key) || !ok) throw new VackleException($"Invalid setting: {entry.Key}.{setting.Key}");
                values[setting.Key] = setting.Value;
            }
            plugins[entry.Key] = Json.Object(("enabled", enabled), ("values", values));
        }
        var themes = new List<object?>();
        if (input.ContainsKey("themes"))
        {
            if (input.Array("themes") is not { } list || list.Count > 24) throw new VackleException("Invalid theme list");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var bytes = 0;
            foreach (var item in list)
            {
                if (item is not JsonObject theme || theme.Text("id") is not { } id || !IsIdentifier(id) || ids.Contains(id) || theme.Text("name") is not { } name ||
                    name.Length > 256 || theme.Bool("enabled") is not { } on || theme.Text("css") is not { } themeCss) throw new VackleException("Invalid theme");
                bytes += themeCss.Length;
                if (bytes > 524288) throw new VackleException("Themes exceed 512 KiB total");
                ids.Add(id);
                themes.Add(Json.Object(("id", id), ("name", name), ("enabled", on), ("css", themeCss)));
            }
        }
        var toolbox = input.TryGetValue("toolbox", out var raw) && raw is not null ? raw : "titlebar";
        if (toolbox is not string location || Array.IndexOf(Toolboxes, location) < 0) throw new VackleException("Invalid toolbox location");
        return Json.Object(("schema", 1.0), ("plugins", plugins), ("css", Json.Object(("enabled", cssEnabled), ("text", cssText), ("filename", filename))), ("themes", themes), ("toolbox", location));
    }

    public static JsonObject Load(string root)
    {
        var path = Path.Combine(root, "settings.json");
        Files.SafeTree(root, path);
        return File.Exists(path) ? Validate(Files.ReadJson(path)) : Defaults();
    }

    public static void Save(string root, object? value)
    {
        var path = Path.Combine(root, "settings.json");
        Files.SafeTree(root, path);
        Files.AtomicWrite(path, Json.Write(Validate(value), true));
    }

    public static bool AdblockEnabled(JsonObject? settings) => settings?.Object("plugins")?.Object("youtube-adblock")?.Bool("enabled") == true;
}
