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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Vackle;

public readonly record struct ProcessInfo(int Pid, int Parent, string Path, string Name);
public readonly record struct Listener(int Pid, string Address);

public static class Win
{
    public static List<ProcessInfo> Processes()
    {
        var result = new List<ProcessInfo>();
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                result.Add(new ProcessInfo(entry.Pid, entry.Parent, ImagePath(entry.Pid), entry.ExeFile));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    public static string ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var builder = new StringBuilder(32768);
            var size = builder.Capacity;
            return QueryFullProcessImageNameW(handle, 0, builder, ref size) ? builder.ToString(0, size) : "";
        }
        finally { CloseHandle(handle); }
    }

    public static string CommandLine(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var length = 0;
            NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out length);
            if (length <= 0 || length > 1 << 20) return "";
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, 60, buffer, length, out _) != 0) return "";
                var text = Marshal.ReadInt16(buffer);
                var pointer = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return text > 0 && pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pointer, text / 2) : "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(handle); }
    }

    public static int SessionOf(int pid) => ProcessIdToSessionId(pid, out var session) ? session : -1;

    public static bool Alive(int pid)
    {
        var handle = OpenProcess(0x1000 | 0x100000, false, pid);
        if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
        try { return WaitForSingleObject(handle, 0) == 0x102; }
        finally { CloseHandle(handle); }
    }

    public static void Kill(int pid)
    {
        try { using var process = Process.GetProcessById(pid); process.Kill(); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static List<Listener> TcpListeners(int port)
    {
        var result = new List<Listener>();
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) != 0) continue;
                var count = Marshal.ReadInt32(buffer);
                var rowSize = family == 2 ? 24 : 56;
                for (var index = 0; index < count; index++)
                {
                    var row = buffer + 4 + index * rowSize;
                    string address; int localPort; int pid;
                    if (family == 2)
                    {
                        address = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 4))).ToString();
                        localPort = PortOf(Marshal.ReadInt32(row, 8));
                        pid = Marshal.ReadInt32(row, 20);
                    }
                    else
                    {
                        var bytes = new byte[16];
                        Marshal.Copy(row, bytes, 0, 16);
                        address = new IPAddress(bytes).ToString();
                        localPort = PortOf(Marshal.ReadInt32(row, 20));
                        pid = Marshal.ReadInt32(row, 52);
                    }
                    if (localPort == port) result.Add(new Listener(pid, address));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

    static int PortOf(int raw) => ((raw & 0xff) << 8) | ((raw >> 8) & 0xff);

    public static bool Descendant(int pid, int root, IReadOnlyList<ProcessInfo> items)
    {
        var visited = new HashSet<int>();
        while (visited.Add(pid))
        {
            if (pid == root) return true;
            var found = items.Where(item => item.Pid == pid).Take(1).ToList();
            if (found.Count == 0 || found[0].Parent == 0) return false;
            pid = found[0].Parent;
        }
        return false;
    }

    public static List<int> HostWebViews(string executableName)
    {
        var session = SessionOf(Process.GetCurrentProcess().Id);
        var pattern = new Regex("--webview-exe-name=\"?" + Regex.Escape(executableName) + "\"?(\\s|$)", RegexOptions.IgnoreCase);
        var items = Processes();
        var alive = new HashSet<int>(items.Select(item => item.Pid));
        return items.Where(item => string.Equals(item.Name, "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) && SessionOf(item.Pid) == session && !alive.Contains(item.Parent) &&
            pattern.IsMatch(CommandLine(item.Pid))).Select(item => item.Pid).ToList();
    }

    public static void VerifyPortOwner(int port, int hostPid)
    {
        var listeners = TcpListeners(port);
        var items = Processes();
        var host = items.FirstOrDefault(item => item.Pid == hostPid).Name?.ToLowerInvariant();
        bool HostWebView(int pid)
        {
            var command = CommandLine(pid);
            return !string.IsNullOrEmpty(host) && Regex.IsMatch(command, "\\\\msedgewebview2\\.exe\"?\\s", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(command, "--webview-exe-name=\"?" + Regex.Escape(host!) + "\"?(\\s|$)", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(command, $"--remote-debugging-port={port}(\\s|$)");
        }
        if (listeners.Count == 0 || listeners.Any(listener => listener.Address != "127.0.0.1" && listener.Address != "::1" ||
            !(Descendant(listener.Pid, hostPid, items) || HostWebView(listener.Pid)))) throw new VackleException("Debugging endpoint is not owned by launched Flackle on loopback");
    }

    public static List<string> LaunchReport(string executableName)
    {
        var lines = new List<string> { $"Running as admin: {IsElevated()}" };
        foreach (var process in Processes())
        {
            if (string.Equals(process.Name, executableName, StringComparison.OrdinalIgnoreCase))
                lines.Add($"Flackle {process.Pid} parent={process.Parent} session={SessionOf(process.Pid)} {(process.Path.Length > 0 ? process.Path : "path hidden (admin or other user)")}");
            else if (string.Equals(process.Name, "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
            {
                var command = CommandLine(process.Pid);
                if (!Regex.IsMatch(command, "--webview-exe-name=\"?" + Regex.Escape(executableName), RegexOptions.IgnoreCase) || command.Contains("--type=")) continue;
                var port = Regex.Match(command, "--remote-debugging-port=(\\d+)");
                lines.Add($"WebView2 {process.Pid} parent={process.Parent}: {(port.Success ? "debug port " + port.Groups[1].Value : "no debug flag")}");
            }
        }
        var policies = new[] { (Microsoft.Win32.Registry.LocalMachine, "HKLM"), (Microsoft.Win32.Registry.CurrentUser, "HKCU") }
            .Where(entry => { using var key = entry.Item1.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge\WebView2"); return key is not null; })
            .Select(entry => entry.Item2 + @":\SOFTWARE\Policies\Microsoft\Edge\WebView2").ToList();
        lines.Add("WebView2 policy: " + (policies.Count > 0 ? string.Join(", ", policies) : "none"));
        return lines;
    }

    public static int StartUnelevated(string executable, string directory)
    {
        if (!SaferCreateLevel(2, 0x20000, 1, out var level, IntPtr.Zero)) throw new Win32Exception();
        IntPtr token;
        try { if (!SaferComputeTokenFromLevel(level, IntPtr.Zero, out token, 0, IntPtr.Zero)) throw new Win32Exception(); }
        finally { SaferCloseLevel(level); }
        try
        {
            if (!ConvertStringSidToSid("S-1-16-8192", out var sid)) throw new Win32Exception();
            try
            {
                var label = new SidAndAttributes { Sid = sid, Attributes = 0x20 };
                if (!SetTokenInformation(token, 25, ref label, Marshal.SizeOf(label) + GetLengthSid(sid))) throw new Win32Exception();
            }
            finally { LocalFree(sid); }
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessAsUser(token, executable, "\"" + executable + "\"", IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, directory, ref startup, out var info))
                throw new Win32Exception();
            CloseHandle(info.Thread); CloseHandle(info.Process);
            return info.ProcessId;
        }
        finally { CloseHandle(token); }
    }

    public static int MessageBox(string text, bool question) => MessageBoxW(IntPtr.Zero, text, "Vackle", question ? 0x24u | 0x10000 : 0x40u | 0x10000);

    public static bool AttachConsole()
    {
        var output = GetStdHandle(-11);
        if (output != IntPtr.Zero && output != new IntPtr(-1)) return false;
        if (AttachConsole(-1)) { Reopen(); return true; }
        return false;
    }

    static void Reopen()
    {
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(output);
        Console.SetError(output);
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }

    public static Process Start(string executable, string arguments, string directory, IDictionary<string, string?>? environment = null, bool hidden = false)
    {
        var info = new ProcessStartInfo(executable, arguments) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = hidden };
        if (environment is not null) foreach (var entry in environment) { if (entry.Value is null) info.EnvironmentVariables.Remove(entry.Key); else info.EnvironmentVariables[entry.Key] = entry.Value; }
        return Process.Start(info) ?? throw new VackleException($"Could not start {System.IO.Path.GetFileName(executable)}");
    }

    public static string WindowsDirectory() => Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int Size; public int Usage; public int Pid; public IntPtr HeapId; public int ModuleId; public int Threads; public int Parent; public int Priority; public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [StructLayout(LayoutKind.Sequential)] struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, CharsX, CharsY, Fill, Flags; public short ShowWindow, Reserved2; public IntPtr Reserved3, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int type, IntPtr buffer, int length, out int returned);
    [DllImport("iphlpapi.dll")] static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, int reserved);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SaferCreateLevel(uint scope, uint level, uint flags, out IntPtr handle, IntPtr reserved);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SaferComputeTokenFromLevel(IntPtr level, IntPtr input, out IntPtr output, uint flags, IntPtr reserved);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SaferCloseLevel(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSid(string sid, out IntPtr result);
    [DllImport("advapi32.dll", SetLastError = true)] static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int type, ref SidAndAttributes info, int length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string application, string command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation info);
}
