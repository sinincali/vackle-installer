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
using System.Globalization;
using System.Text;

namespace Vackle;

public sealed class JsonObject : Dictionary<string, object?>
{
    public JsonObject() : base(StringComparer.Ordinal) { }
    public string? Text(string key) => TryGetValue(key, out var value) ? value as string : null;
    public JsonObject? Object(string key) => TryGetValue(key, out var value) ? value as JsonObject : null;
    public List<object?>? Array(string key) => TryGetValue(key, out var value) ? value as List<object?> : null;
    public double? Number(string key) => TryGetValue(key, out var value) && value is double number ? number : null;
    public bool? Bool(string key) => TryGetValue(key, out var value) && value is bool flag ? flag : null;
}

public static class Json
{
    public static object? Parse(string text)
    {
        var reader = new Reader(text);
        reader.Space();
        var value = reader.Value(0);
        reader.Space();
        if (reader.Position != text.Length) throw new FormatException("Unexpected text after JSON value");
        return value;
    }

    public static JsonObject ParseObject(string text) => Parse(text) as JsonObject ?? throw new FormatException("Expected a JSON object");

    public static string Write(object? value, bool indent = false)
    {
        var builder = new StringBuilder();
        Append(builder, value, indent ? 0 : -1);
        return builder.ToString();
    }

    public static JsonObject Object(params (string Key, object? Value)[] entries)
    {
        var result = new JsonObject();
        foreach (var (key, value) in entries) result[key] = value;
        return result;
    }

    static void Append(StringBuilder builder, object? value, int depth)
    {
        switch (value)
        {
            case null: builder.Append("null"); break;
            case bool flag: builder.Append(flag ? "true" : "false"); break;
            case string text: Quote(builder, text); break;
            case int number: builder.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case long number: builder.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case double number: builder.Append(Number(number)); break;
            case IDictionary<string, object?> map:
                if (map.Count == 0) { builder.Append("{}"); break; }
                builder.Append('{');
                var first = true;
                foreach (var entry in map)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    if (depth >= 0) Break(builder, depth + 1);
                    Quote(builder, entry.Key);
                    builder.Append(depth >= 0 ? ": " : ":");
                    Append(builder, entry.Value, depth >= 0 ? depth + 1 : -1);
                }
                Break(builder, depth);
                builder.Append('}');
                break;
            case IEnumerable<object?> list:
                var items = new List<object?>(list);
                if (items.Count == 0) { builder.Append("[]"); break; }
                builder.Append('[');
                for (var index = 0; index < items.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    if (depth >= 0) Break(builder, depth + 1);
                    Append(builder, items[index], depth >= 0 ? depth + 1 : -1);
                }
                Break(builder, depth);
                builder.Append(']');
                break;
            default: throw new ArgumentException($"Cannot write {value.GetType().Name} as JSON");
        }
    }

    static void Break(StringBuilder builder, int depth)
    {
        if (depth < 0) return;
        builder.Append('\n').Append(' ', depth * 2);
    }

    static string Number(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) return "null";
        if (Math.Floor(number) == number && Math.Abs(number) < 1e15) return ((long)number).ToString(CultureInfo.InvariantCulture);
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        var exponent = text.IndexOf('E');
        if (exponent < 0) return text;
        var power = int.Parse(text.Substring(exponent + 1), CultureInfo.InvariantCulture);
        return text.Substring(0, exponent) + "e" + (power < 0 ? "-" : "+") + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
    }

    static void Quote(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    var lone = char.IsHighSurrogate(character) ? index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])
                        : char.IsLowSurrogate(character) && (index == 0 || !char.IsHighSurrogate(text[index - 1]));
                    if (character < 0x20 || lone) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    sealed class Reader(string text)
    {
        public int Position;

        public void Space()
        {
            while (Position < text.Length && (text[Position] == ' ' || text[Position] == '\t' || text[Position] == '\n' || text[Position] == '\r')) Position++;
        }

        public object? Value(int depth)
        {
            if (depth > 256) throw new FormatException("JSON nests too deeply");
            if (Position >= text.Length) throw new FormatException("Unexpected end of JSON");
            var character = text[Position];
            if (character == '{') return ObjectValue(depth);
            if (character == '[') return ArrayValue(depth);
            if (character == '"') return StringValue();
            if (character == '-' || character >= '0' && character <= '9') return NumberValue();
            if (Literal("true")) return true;
            if (Literal("false")) return false;
            if (Literal("null")) return null;
            throw new FormatException($"Unexpected character in JSON at {Position}");
        }

        bool Literal(string word)
        {
            if (string.CompareOrdinal(text, Position, word, 0, word.Length) != 0) return false;
            Position += word.Length;
            return true;
        }

        JsonObject ObjectValue(int depth)
        {
            var result = new JsonObject();
            Position++;
            Space();
            if (Peek('}')) { Position++; return result; }
            while (true)
            {
                Space();
                if (!Peek('"')) throw new FormatException("Expected a property name");
                var key = StringValue();
                Space();
                Expect(':');
                Space();
                result[key] = Value(depth + 1);
                Space();
                if (Peek(',')) { Position++; continue; }
                Expect('}');
                return result;
            }
        }

        List<object?> ArrayValue(int depth)
        {
            var result = new List<object?>();
            Position++;
            Space();
            if (Peek(']')) { Position++; return result; }
            while (true)
            {
                Space();
                result.Add(Value(depth + 1));
                Space();
                if (Peek(',')) { Position++; continue; }
                Expect(']');
                return result;
            }
        }

        string StringValue()
        {
            Position++;
            var builder = new StringBuilder();
            while (true)
            {
                if (Position >= text.Length) throw new FormatException("Unterminated JSON string");
                var character = text[Position++];
                if (character == '"') return builder.ToString();
                if (character < 0x20) throw new FormatException("Control character in JSON string");
                if (character != '\\') { builder.Append(character); continue; }
                if (Position >= text.Length) throw new FormatException("Unterminated JSON escape");
                var escape = text[Position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (Position + 4 > text.Length || !int.TryParse(text.Substring(Position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            throw new FormatException("Invalid JSON unicode escape");
                        builder.Append((char)code);
                        Position += 4;
                        break;
                    default: throw new FormatException("Invalid JSON escape");
                }
            }
        }

        double NumberValue()
        {
            var start = Position;
            if (Peek('-')) Position++;
            if (Peek('0')) Position++;
            else if (Digits() == 0) throw new FormatException("Invalid JSON number");
            if (Peek('.')) { Position++; if (Digits() == 0) throw new FormatException("Invalid JSON number"); }
            if (Peek('e') || Peek('E'))
            {
                Position++;
                if (Peek('+') || Peek('-')) Position++;
                if (Digits() == 0) throw new FormatException("Invalid JSON number");
            }
            return double.Parse(text.Substring(start, Position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        int Digits()
        {
            var start = Position;
            while (Position < text.Length && text[Position] >= '0' && text[Position] <= '9') Position++;
            return Position - start;
        }

        bool Peek(char character) => Position < text.Length && text[Position] == character;

        void Expect(char character)
        {
            if (!Peek(character)) throw new FormatException($"Expected '{character}' in JSON at {Position}");
            Position++;
        }
    }
}
