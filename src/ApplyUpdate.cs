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
using System.Threading;

namespace Vackle;

public interface IApplyDeps
{
    bool Alive(int pid);
    int Launch(string executable);
    object? Receipt();
    void Stop(int launcherPid, Installation host);
    void Delay(int milliseconds);
    Installation ValidateHost(string directory);
    void Shortcuts(Installation host);
    void Notify(string message);
}

public static class ApplyUpdate
{
    public static string Run(string root, string staged, int waitPid, string key, IApplyDeps deps, int exitTimeout = 60000, int healthTimeout = 120000)
    {
        var executable = Path.Combine(root, "installed", "Vackle.exe");
        for (var deadline = DateTime.UtcNow.AddMilliseconds(exitTimeout); deps.Alive(waitPid);)
        {
            if (DateTime.UtcNow > deadline) throw new VackleException("The running Vackle launcher did not close, so the update was not applied");
            deps.Delay(200);
        }
        var previous = Installer.ReadManifest(root);
        string Refuse(string reason, string? version = null)
        {
            Console.Error.WriteLine($"Update refused: {reason}");
            if (version is not null) Updater.UpdateState(root, state => { if (!state.Failed.Contains(version)) state.Failed.Add(version); });
            deps.Launch(executable);
            deps.Notify($"Vackle could not update: {reason}\n\nVackle {previous.Version} is still installed.");
            return "refused";
        }
        Release release;
        System.Collections.Generic.Dictionary<string, byte[]> files;
        try { (release, files) = Updater.ReadStaged(staged, key, previous.Runtime is not null ? new Reusable(previous.Runtime, previous.Files.TryGetValue("Vackle.exe", out var exe) ? exe : "") : null); }
        catch (Exception error) when (error is VackleException || error is IOException || error is FormatException) { return Refuse(error.Message); }
        if (Updater.CompareVersions(release.Version, previous.Version) <= 0) return Refuse($"{release.Version} is not newer than {previous.Version}");
        Installation host;
        try
        {
            host = deps.ValidateHost(previous.Host.Directory);
            Installer.Install(root, host, files, new Installer.Options(false, release.Version, release.Runtime, release.Commit));
        }
        catch (Exception error) when (error is VackleException || error is IOException || error is UnauthorizedAccessException)
        {
            var busy = error is IOException || error is UnauthorizedAccessException;
            return Refuse(error.Message, busy ? null : release.Version);
        }
        try { deps.Shortcuts(host); } catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"Shortcut update failed: {error.Message}"); }
        Updater.UpdateState(root, state => { state.Updated = (previous.Version, release.Version); state.Skipped = null; });
        var launcher = deps.Launch(executable);
        var healthy = false;
        for (var deadline = DateTime.UtcNow.AddMilliseconds(healthTimeout); DateTime.UtcNow < deadline && deps.Alive(launcher);)
        {
            object? receipt = null;
            try { receipt = deps.Receipt(); } catch (Exception error) when (error is IOException || error is FormatException || error is UnauthorizedAccessException) { }
            if (receipt is JsonObject value && value.Text("version") == release.Version && value.Number("launcherPid") == launcher) { healthy = true; break; }
            deps.Delay(500);
        }
        if (healthy)
        {
            Updater.CleanStaged(root, staged, TimeSpan.Zero);
            return "updated";
        }
        Console.Error.WriteLine($"Vackle {release.Version} did not become healthy; restoring {previous.Version}");
        deps.Stop(launcher, host);
        Installer.RestorePrevious(root);
        Updater.UpdateState(root, state => { state.Updated = null; if (!state.Failed.Contains(release.Version)) state.Failed.Add(release.Version); });
        deps.Launch(executable);
        deps.Notify($"Vackle {release.Version} did not start correctly, so Vackle {previous.Version} was restored.\n\nYou will not be asked to install {release.Version} again.");
        return "rolled-back";
    }
}

public sealed class SystemApplyDeps(string root) : IApplyDeps
{
    public bool Alive(int pid) => Win.Alive(pid);

    public int Launch(string executable)
    {
        try
        {
            using var process = Win.Start(executable, "launch-hidden", root, new System.Collections.Generic.Dictionary<string, string?> { ["VACKLE_SUPERVISED"] = "1" });
            return process.Id;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception || error is VackleException) { return -1; }
    }

    public object? Receipt() => Files.ReadJson(Path.Combine(root, "runtime.json"));

    public void Stop(int launcherPid, Installation host)
    {
        Win.Kill(launcherPid);
        foreach (var item in Win.Processes()) if (string.Equals(item.Path, host.Executable, StringComparison.OrdinalIgnoreCase)) Win.Kill(item.Pid);
        Thread.Sleep(1500);
    }

    public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
    public Installation ValidateHost(string directory) => Detection.Validate(directory);
    public void Shortcuts(Installation host) => Vackle.Shortcuts.Install(root, host);
    public void Notify(string message) => Win.MessageBox(message, false);
}
