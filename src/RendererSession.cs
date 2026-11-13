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
using System.Threading;
using System.Threading.Tasks;

namespace Vackle;

public sealed class RendererSession(Cdp cdp, string root, string source, string session, string version, string hostVersion,
    Action? onRestart = null, Updates? updates = null, Action<string>? onHandoff = null)
{
    const string TrustCheck = "location.origin==='https://flackle.com' && window.top===window && Boolean(window.__TAURI_INTERNALS__)";
    static readonly Regex[] AppUris = [
        new(@"^spotify:(track|album|artist|playlist|user|episode|show):[A-Za-z0-9]{1,64}$"),
        new(@"^steam://openurl/https://(store\.steampowered\.com|steamcommunity\.com)/[A-Za-z0-9\-._~/?=&%#+]{0,400}$"),
        new(@"^com\.epicgames\.launcher://store/p/[A-Za-z0-9-]{1,100}$"),
        new(@"^tidal://(track|album|artist|playlist|mix|video)/[A-Za-z0-9-]{1,64}$")
    ];

    readonly Dictionary<string, string> sheets = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> navigations = new(StringComparer.Ordinal);
    readonly string binding = "vackle_" + Guid.NewGuid().ToString("N");
    readonly object queueLock = new();
    Task tail = Task.CompletedTask;
    int outstanding;
    string? scriptId;
    JsonObject? config;
    Action? removeListener;

    public Cdp Cdp => cdp;

    public static bool IsAppUri(string uri) => AppUris.Any(pattern => pattern.IsMatch(uri));

    string SourceText() => $"if(location.origin===\"https://flackle.com\"&&window.top===window){{window.__VACKLE_CONFIG__={Json.Write(config)};{source}\n}}";

    public async Task InitializeAsync()
    {
        if (await cdp.EvaluateAsync(TrustCheck).ConfigureAwait(false) is not true) throw new VackleException("Target is not the confirmed Flackle Tauri renderer");
        config = Json.Object(("version", version), ("hostVersion", hostVersion), ("session", session), ("binding", binding), ("settings", Settings.Load(root)));
        await cdp.SendAsync("Runtime.enable").ConfigureAwait(false);
        await cdp.SendAsync("Page.enable").ConfigureAwait(false);
        await cdp.SendAsync("DOM.enable").ConfigureAwait(false);
        await cdp.SendAsync("CSS.enable").ConfigureAwait(false);
        await cdp.SendAsync("Runtime.addBinding", Json.Object(("name", binding))).ConfigureAwait(false);
        removeListener = cdp.On(OnEvent);
        await ReplaceScriptAsync().ConfigureAwait(false);
        await EnsureAsync().ConfigureAwait(false);
    }

    bool AdblockEnabled => Settings.AdblockEnabled(config?.Object("settings"));

    Task<JsonObject> AutoAttachAsync() =>
        cdp.SendAsync("Target.setAutoAttach", Json.Object(("autoAttach", AdblockEnabled), ("waitForDebuggerOnStart", AdblockEnabled), ("flatten", true)));

    async Task ReplaceScriptAsync()
    {
        if (scriptId is not null) await cdp.SendAsync("Page.removeScriptToEvaluateOnNewDocument", Json.Object(("identifier", scriptId))).ConfigureAwait(false);
        var result = await cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument", Json.Object(("source", SourceText()))).ConfigureAwait(false);
        scriptId = result.Text("identifier") ?? throw new VackleException("Document bootstrap registration failed");
        await AutoAttachAsync().ConfigureAwait(false);
    }

    void OnEvent(ProtocolEvent protocolEvent)
    {
        if (protocolEvent.SessionId is not null) return;
        var parameters = protocolEvent.Params;
        if (protocolEvent.Method == "Page.frameStartedNavigating" && parameters.Text("frameId") is { } frame && parameters.Text("url") is { } url)
            lock (navigations) if (navigations.Count < 256) navigations[frame] = url;
        if (protocolEvent.Method == "Target.attachedToTarget")
        {
            _ = Web.AttachFrameAsync(cdp, parameters, AdblockEnabled, navigations).ContinueWith(task => Console.Error.WriteLine($"[Vackle:adblock] Frame attach failed: {task.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
            return;
        }
        if (protocolEvent.Method == "Runtime.executionContextsCleared") { lock (sheets) sheets.Clear(); lock (navigations) navigations.Clear(); }
        if (protocolEvent.Method != "Runtime.bindingCalled" || parameters.Text("name") != binding || parameters.Text("payload") is not { } payload || parameters.Number("executionContextId") is not { } context) return;
        Interlocked.Increment(ref outstanding);
        lock (queueLock) tail = Chain(tail, payload, (int)context);
    }

    async Task Chain(Task previous, string payload, int context)
    {
        await Task.Yield();
        await previous.ConfigureAwait(false);
        try { await RequestAsync(payload, context).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"[Vackle:bridge] Request failed: {error.Message}"); }
        finally { Interlocked.Decrement(ref outstanding); }
    }

    Task ResolveAsync(double id, string? error, object? result, bool hasResult, int context) =>
        cdp.EvaluateAsync($"window.__VACKLE__?.resolve({Json.Write(id)},{Json.Write(error)}{(hasResult ? "," + Json.Write(result) : "")})", context);

    async Task RequestAsync(string raw, int context)
    {
        if (raw.Length > 3300000) throw new VackleException("Bridge request too large");
        if (await cdp.EvaluateAsync(TrustCheck, context).ConfigureAwait(false) is not true) throw new VackleException("Bridge request from untrusted context");
        if (Json.Parse(raw) is not JsonObject request || request.Number("id") is not { } id || id != Math.Floor(id) || id <= 0 || id > 9007199254740991)
            throw new VackleException("Invalid bridge request");
        var method = request.Text("method");
        request.TryGetValue("payload", out var payload);
        string? staged = null;
        Func<Task<object?>>? lookup = method switch
        {
            "branding" when payload is string video => async () => await Web.BrandingAsync(video).ConfigureAwait(false),
            "translate" => async () => await Web.TranslateAsync(payload).ConfigureAwait(false),
            "sound" => async () => await Web.SoundAsync(payload).ConfigureAwait(false),
            "image" => async () => await Web.ImageAsync(payload).ConfigureAwait(false),
            "update-status" when updates is not null => async () => await updates.StatusAsync(payload is true).ConfigureAwait(false),
            "update-install" when updates is not null && onHandoff is not null => async () => { staged = await updates.StageAsync().ConfigureAwait(false); return null; },
            _ => null
        };
        if (lookup is not null)
        {
            _ = Task.Run(async () =>
            {
                string? failure = null; object? result = null;
                try { result = await lookup().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { failure = error.Message; }
                try
                {
                    await ResolveAsync(id, failure, result, true, context).ConfigureAwait(false);
                    if (staged is not null) onHandoff?.Invoke(staged);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"[Vackle:bridge] Lookup reply failed: {error.Message}"); }
            });
            return;
        }
        string? failed = null;
        var restart = false;
        try
        {
            if (method == "save")
            {
                var settings = Settings.Validate(payload);
                Settings.Save(root, settings);
                if (config is not null) config["settings"] = settings;
                await ReplaceScriptAsync().ConfigureAwait(false);
            }
            else if (method == "style")
            {
                if (payload is not JsonObject style || !Settings.IsIdentifier(style.TryGetValue("id", out var styleId) ? styleId : null) || style.Text("text") is not { } text || text.Length > 524288)
                    throw new VackleException("Invalid stylesheet operation");
                var key = (string)styleId!;
                string? sheet;
                lock (sheets) sheets.TryGetValue(key, out sheet);
                if (sheet is null)
                {
                    lock (sheets) if (sheets.Count >= 256) throw new VackleException("Too many stylesheets");
                    var tree = await cdp.SendAsync("Page.getFrameTree").ConfigureAwait(false);
                    var frame = tree.Object("frameTree")?.Object("frame")?.Text("id") ?? throw new VackleException("Cannot identify the main frame");
                    var created = await cdp.SendAsync("CSS.createStyleSheet", Json.Object(("frameId", frame), ("force", true))).ConfigureAwait(false);
                    sheet = created.Text("styleSheetId") ?? throw new VackleException("Cannot create inspector stylesheet");
                    lock (sheets) sheets[key] = sheet;
                }
                await cdp.SendAsync("CSS.setStyleSheetText", Json.Object(("styleSheetId", sheet), ("text", text))).ConfigureAwait(false);
            }
            else if (method == "uri" && payload is string uri)
            {
                if (!IsAppUri(uri)) throw new VackleException("Unsupported app link");
                using var process = Win.Start(Path.Combine(Win.WindowsDirectory(), "explorer.exe"), "\"" + uri + "\"", Win.WindowsDirectory(), hidden: true);
            }
            else if (method == "motion" && payload is bool reduce)
                await cdp.SendAsync("Emulation.setEmulatedMedia", Json.Object(("features", new List<object?> { Json.Object(("name", "prefers-reduced-motion"), ("value", reduce ? "reduce" : "")) }))).ConfigureAwait(false);
            else if (method == "update-skip" && payload is string skipped && updates is not null) updates.Skip(skipped);
            else if (method == "restart" && payload is null && request.ContainsKey("payload") && onRestart is not null) { Settings.Load(root); restart = true; }
            else throw new VackleException("Unknown bridge operation");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failed = error.Message; Console.Error.WriteLine($"[Vackle:bridge] {failed}"); }
        await ResolveAsync(id, failed, null, false, context).ConfigureAwait(false);
        if (restart && failed is null) onRestart?.Invoke();
    }

    public async Task<object?> EnsureAsync()
    {
        var state = await cdp.EvaluateAsync("({origin:location.origin,native:Boolean(window.__TAURI_INTERNALS__),loaded:Boolean(window.__VACKLE__),ready:Boolean(window.__VACKLE__?.ready),session:window.__VACKLE__?.session,version:window.__VACKLE__?.version,hostVersion:window.__VACKLE__?.hostVersion,plugins:window.__VACKLE__?.plugins()})").ConfigureAwait(false);
        if (state is JsonObject value && value.Text("origin") == "https://flackle.com" && value.Bool("native") == true && value.Bool("loaded") != true)
            await cdp.EvaluateAsync(SourceText(), null, false).ConfigureAwait(false);
        return state;
    }

    public async Task DisposeAsync()
    {
        if (cdp.Connected)
        {
            if (scriptId is not null) await cdp.SendAsync("Page.removeScriptToEvaluateOnNewDocument", Json.Object(("identifier", scriptId))).ConfigureAwait(false);
            await cdp.EvaluateAsync("window.__VACKLE__?.shutdown()").ConfigureAwait(false);
            for (var waited = 0; Volatile.Read(ref outstanding) > 0 && waited < 100; waited++) await Task.Delay(100).ConfigureAwait(false);
            List<string> current;
            lock (sheets) current = sheets.Values.ToList();
            foreach (var sheet in current) await cdp.SendAsync("CSS.setStyleSheetText", Json.Object(("styleSheetId", sheet), ("text", ""))).ConfigureAwait(false);
            await cdp.SendAsync("Emulation.setEmulatedMedia", Json.Object(("features", new List<object?> { Json.Object(("name", "prefers-reduced-motion"), ("value", "")) }))).ConfigureAwait(false);
            await cdp.SendAsync("Runtime.removeBinding", Json.Object(("name", binding))).ConfigureAwait(false);
            await cdp.SendAsync("Target.setAutoAttach", Json.Object(("autoAttach", false), ("waitForDebuggerOnStart", false), ("flatten", true))).ConfigureAwait(false);
        }
        removeListener?.Invoke();
        cdp.Close();
    }
}
