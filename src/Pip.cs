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
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vackle;

public sealed class Pip : IDisposable
{
    readonly CancellationTokenSource stop = new();

    public Pip()
    {
        new Thread(Watch) { IsBackground = true, Name = "Vackle picture-in-picture" }.Start();
    }

    void Watch()
    {
        while (!stop.IsCancellationRequested)
        {
            try { Place(); } catch (Exception error) when (error is not OutOfMemoryException) { }
            stop.Token.WaitHandle.WaitOne(400);
        }
    }

    static void Place()
    {
        var window = IntPtr.Zero;
        while ((window = FindWindowExW(IntPtr.Zero, window, "Chrome_WidgetWin_1", "Picture in picture")) != IntPtr.Zero)
        {
            if (!GetWindowRect(window, out var rect) || rect.Left > -30000) continue;
            GetWindowThreadProcessId(window, out var pid);
            if (!Win.CommandLine((int)pid).Contains("com.flackle")) return;
            var main = Process.GetProcessesByName("flackle-desktop").Select(process => { using (process) return process.MainWindowHandle; }).FirstOrDefault(handle => handle != IntPtr.Zero);
            var monitor = main != IntPtr.Zero ? MonitorFromWindow(main, 2) : MonitorFromPoint(new PointStruct(), 1);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info)) return;
            ShowWindow(window, 9);
            SetWindowPos(window, new IntPtr(-1), info.Work.Right - (rect.Right - rect.Left) - 24, info.Work.Bottom - (rect.Bottom - rect.Top) - 24, 0, 0, 0x41);
            return;
        }
    }

    public void Dispose() => stop.Cancel();

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct PointStruct { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public int Flags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(PointStruct point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
