// Win32 side of the CS2 client test harness (README.md). Compiled by Cs2Client.psm1 with Add-Type; not part of any project
// (tools/ is outside every project's folder).
//
// THE MECHANISM: CS2 runs on a Win32 desktop of its own ("Cs2Harness" in WinSta0) that is created and never switched to. A window
// there cannot reach the user's screen, foreground or input queue. Adapted from StationeersPlus' TestRig
// (TestRig/src/TestRig.Core/Infrastructure/Win32/DesktopProcessLauncher.cs), whose measurements carry over:
//   - CreateProcessW does NOT fail when lpDesktop names a desktop that does not exist: the process silently lands on the caller's
//     desktop. So the desktop is created (CreateDesktopW opens an existing one) immediately before every launch.
//   - A desktop dies when its last handle closes and no window is on it, and a booting game has neither for its first seconds. So
//     the child gets an inheritable desktop handle of its own (0xC0000142 at 0.02 s otherwise).
//   - bInheritHandles is all-or-nothing: the child would inherit every inheritable handle of the caller - the tool call's stdout
//     pipe included, which then never reaches EOF while the game lives. PROC_THREAD_ATTRIBUTE_HANDLE_LIST names the desktop handle
//     and nothing else.
// SwitchDesktop, SetForegroundWindow, ShowWindow, SetWindowPos, BringWindowToTop, SetActiveWindow and AttachThreadInput are never
// imported here. SetThreadDesktop is, for helper threads only: a thread of THIS process attaches itself to the hidden desktop to
// PrintWindow or PostMessage a window there (neither works across desktops). It changes nothing about the desktop the user sees.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Cs2Harness
{
    public sealed class WindowInfo
    {
        public long Handle;
        public uint Pid;
        public string Title = "";
        public string Class = "";
        public bool Visible;
        public int X, Y, Width, Height;
        public int ClientWidth, ClientHeight;
        public override string ToString() => $"0x{Handle:X} pid={Pid} '{Title}' [{Class}] visible={Visible} {X},{Y} {Width}x{Height} client {ClientWidth}x{ClientHeight}";
    }

    public sealed class AudioSessionInfo
    {
        public uint Pid;
        public string Device = "";
        public int State;          // 0 inactive, 1 active, 2 expired
        public float Peak;         // 0..1: what the session is putting out right now
        public float Volume;       // the session's slider in Windows' mixer, 0..1 (read only)
        public bool Muted;         // the session's mute in Windows' mixer (read only)
        public override string ToString() => $"pid={Pid} state={State} peak={Peak:0.000000} volume={Volume:0.00} muted={Muted} device='{Device}'";
    }

    public static class Native
    {
        // ---------------------------------------------------------------- the desktop and the launch

        /// <summary>Starts exe on WinSta0\desktop (created if missing), never switching to it, handing the child nothing but its own
        /// desktop handle. env: extra or overriding variables (a null value removes one); the rest is this process's environment.
        /// Breaks away from the caller's job when the job allows it. Returns the pid.</summary>
        public static uint StartOnDesktop(string desktop, string exePath, string commandLine, string workDir, IDictionary<string, string> env)
        {
            if (string.IsNullOrWhiteSpace(desktop)) throw new ArgumentException("a desktop name is required: without one the game would land on the user's desktop");
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
            IntPtr hDesk = CreateDesktopW(desktop, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, ref sa);
            if (hDesk == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktopW('" + desktop + "') failed - refusing to launch onto the user's desktop");

            IntPtr cmdBuf = IntPtr.Zero, deskBuf = IntPtr.Zero, envBuf = IntPtr.Zero, attrList = IntPtr.Zero, handleArray = IntPtr.Zero;
            try
            {
                // CreateProcessW writes into lpCommandLine, so it is a private buffer, with room to spare.
                var chars = new char[commandLine.Length + 64];
                commandLine.CopyTo(0, chars, 0, commandLine.Length);
                cmdBuf = Marshal.AllocHGlobal(chars.Length * 2);
                deskBuf = Marshal.StringToHGlobalUni(@"WinSta0\" + desktop);

                // Environment block: this process's, with the overrides, sorted case-insensitively, double-NUL terminated.
                var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string)e.Value;
                if (env != null) foreach (var kv in env) { if (kv.Value == null) vars.Remove(kv.Key); else vars[kv.Key] = kv.Value; }
                var sb = new StringBuilder();
                foreach (var kv in vars) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
                sb.Append('\0');
                envBuf = Marshal.StringToHGlobalUni(sb.ToString());

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attrList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
                handleArray = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(handleArray, hDesk);
                if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute(HANDLE_LIST)");

                var si = new STARTUPINFOEXW();
                si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
                si.StartupInfo.lpDesktop = deskBuf;
                si.StartupInfo.dwFlags = STARTF_USESHOWWINDOW;
                si.StartupInfo.wShowWindow = SW_SHOWNOACTIVATE; // belt and braces; the desktop is the mechanism
                si.lpAttributeList = attrList;

                // The error mode is inherited: no "missing DLL" / GP-fault / critical-error box from the loader or WER, which Windows
                // could put on the interactive desktop rather than the game's. Below-normal priority: the user works on this PC.
                SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
                uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | BELOW_NORMAL_PRIORITY_CLASS;
                PROCESS_INFORMATION pi;
                Marshal.Copy(chars, 0, cmdBuf, chars.Length);
                bool ok = CreateProcessW(exePath, cmdBuf, IntPtr.Zero, IntPtr.Zero, true, flags | CREATE_BREAKAWAY_FROM_JOB, envBuf, workDir, ref si, out pi);
                int err = Marshal.GetLastWin32Error();
                if (!ok && err == 5) // the job forbids breaking away: launch inside it
                {
                    Marshal.Copy(chars, 0, cmdBuf, chars.Length);
                    ok = CreateProcessW(exePath, cmdBuf, IntPtr.Zero, IntPtr.Zero, true, flags, envBuf, workDir, ref si, out pi);
                    err = Marshal.GetLastWin32Error();
                }
                if (!ok) throw new Win32Exception(err, "CreateProcessW failed for " + exePath);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally
            {
                if (cmdBuf != IntPtr.Zero) Marshal.FreeHGlobal(cmdBuf);
                if (deskBuf != IntPtr.Zero) Marshal.FreeHGlobal(deskBuf);
                if (envBuf != IntPtr.Zero) Marshal.FreeHGlobal(envBuf);
                if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
                if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
                CloseDesktop(hDesk); // the child holds its own copy now
            }
        }

        /// <summary>Starts a console helper (the watchdog) detached: no window, no console, and nothing inherited - in particular not
        /// the caller's pipes, which would keep a tool call open for the helper's whole life.</summary>
        public static uint StartDetached(string exePath, string commandLine, string workDir)
        {
            var chars = new char[commandLine.Length + 64];
            commandLine.CopyTo(0, chars, 0, commandLine.Length);
            IntPtr cmdBuf = Marshal.AllocHGlobal(chars.Length * 2);
            try
            {
                var si = new STARTUPINFOEXW();
                si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOW>();
                si.StartupInfo.dwFlags = STARTF_USESHOWWINDOW;
                si.StartupInfo.wShowWindow = 0; // SW_HIDE
                PROCESS_INFORMATION pi;
                uint flags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP | BELOW_NORMAL_PRIORITY_CLASS;
                Marshal.Copy(chars, 0, cmdBuf, chars.Length);
                bool ok = CreateProcessW(exePath, cmdBuf, IntPtr.Zero, IntPtr.Zero, false, flags | CREATE_BREAKAWAY_FROM_JOB, IntPtr.Zero, workDir, ref si, out pi);
                int err = Marshal.GetLastWin32Error();
                if (!ok && err == 5)
                {
                    Marshal.Copy(chars, 0, cmdBuf, chars.Length);
                    ok = CreateProcessW(exePath, cmdBuf, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workDir, ref si, out pi);
                    err = Marshal.GetLastWin32Error();
                }
                if (!ok) throw new Win32Exception(err, "CreateProcessW failed for " + exePath);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally { Marshal.FreeHGlobal(cmdBuf); }
        }

        /// <summary>Puts a running process (and every child it starts later) into a new job that kills them all when its last handle
        /// closes - that is, when the CALLING process dies, however it dies. The watchdog calls this on its cs2, so the game can never
        /// outlive its guard. The handle is deliberately kept open (leaked) for the caller's whole life. Returns "ok" or the error.</summary>
        public static string TieToThisProcess(uint pid)
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return "CreateJobObject failed: " + Marshal.GetLastWin32Error();
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                return "SetInformationJobObject failed: " + Marshal.GetLastWin32Error();
            IntPtr proc = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, false, pid);
            if (proc == IntPtr.Zero) return "OpenProcess failed: " + Marshal.GetLastWin32Error();
            try { return AssignProcessToJobObject(job, proc) ? "ok" : "AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error(); }
            finally { CloseHandle(proc); }
        }

        // ---------------------------------------------------------------- windows and focus

        /// <summary>Every top-level window on the named desktop (empty if the desktop does not exist).</summary>
        public static List<WindowInfo> WindowsOnDesktop(string desktop)
        {
            var list = new List<WindowInfo>();
            IntPtr hDesk = OpenDesktopW(desktop, 0, false, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
            if (hDesk == IntPtr.Zero) return list;
            try { EnumDesktopWindows(hDesk, (h, l) => { list.Add(Describe(h)); return true; }, IntPtr.Zero); }
            finally { CloseDesktop(hDesk); }
            return list;
        }

        /// <summary>Top-level windows of a process on THIS process's desktop (the user's, when run from their session).</summary>
        public static List<WindowInfo> WindowsOfPidOnOwnDesktop(uint pid)
        {
            var list = new List<WindowInfo>();
            EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) list.Add(Describe(h)); return true; }, IntPtr.Zero);
            return list;
        }

        static WindowInfo Describe(IntPtr h)
        {
            var w = new WindowInfo { Handle = h.ToInt64() };
            uint pid; GetWindowThreadProcessId(h, out pid); w.Pid = pid;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity); w.Title = sb.ToString();
            sb.Clear(); GetClassNameW(h, sb, sb.Capacity); w.Class = sb.ToString();
            w.Visible = IsWindowVisible(h);
            RECT r; if (GetWindowRect(h, out r)) { w.X = r.Left; w.Y = r.Top; w.Width = r.Right - r.Left; w.Height = r.Bottom - r.Top; }
            RECT c; if (GetClientRect(h, out c)) { w.ClientWidth = c.Right - c.Left; w.ClientHeight = c.Bottom - c.Top; }
            return w;
        }

        /// <summary>The pid owning the user's foreground window (0: none).</summary>
        public static uint ForegroundPid()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            uint pid; GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        /// <summary>What GetForegroundWindow answers for a thread ON the hidden desktop - the call SDL3 makes to decide whether the
        /// game window has focus. Measured: always 0, so the game never has keyboard focus and posted keys are ignored.</summary>
        public static uint ForegroundPidSeenFromDesktop(string desktop)
        {
            uint pid = 0;
            OnDesktop(desktop, () => { IntPtr h = GetForegroundWindow(); if (h != IntPtr.Zero) GetWindowThreadProcessId(h, out pid); }, e => { });
            return pid;
        }

        /// <summary>The name of the desktop that receives input right now ("Default" while the user's session is in use).</summary>
        public static string InputDesktopName()
        {
            IntPtr h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
            if (h == IntPtr.Zero) return "(unreadable: " + Marshal.GetLastWin32Error() + ")";
            try
            {
                var sb = new StringBuilder(256); int needed;
                return GetUserObjectInformationW(h, 2 /* UOI_NAME */, sb, sb.Capacity * 2, out needed) ? sb.ToString() : "?";
            }
            finally { CloseDesktop(h); }
        }

        /// <summary>Asks a window on the hidden desktop to close: WM_CLOSE, posted (no activation, no wait). CS2's SDL window takes it
        /// as a normal quit - it disconnects, saves its config and exits in about half a second. A message cannot cross desktops, so it
        /// is posted from a thread that attaches itself to that desktop. Returns "ok" or the error.</summary>
        public static string PostClose(string desktop, long handle)
        {
            string result = "not run";
            OnDesktop(desktop, () => { result = PostMessageW(new IntPtr(handle), 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero) ? "ok" : "PostMessage failed: " + Marshal.GetLastWin32Error(); }, e => result = e);
            return result;
        }

        /// <summary>Runs work on a fresh thread attached to the named desktop (SetThreadDesktop on that thread only).</summary>
        static void OnDesktop(string desktop, Action work, Action<string> onError)
        {
            var t = new Thread(() =>
            {
                IntPtr hDesk = OpenDesktopW(desktop, 0, false, GENERIC_ALL);
                if (hDesk == IntPtr.Zero) { onError("OpenDesktop failed: " + Marshal.GetLastWin32Error()); return; }
                try
                {
                    if (!SetThreadDesktop(hDesk)) { onError("SetThreadDesktop failed: " + Marshal.GetLastWin32Error()); return; }
                    work();
                }
                catch (Exception e) { onError(e.GetType().Name + ": " + e.Message); }
                finally { CloseDesktop(hDesk); }
            });
            t.Start();
            t.Join();
        }

        // ---------------------------------------------------------------- capture

        /// <summary>PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT) of a window on the hidden desktop into a PNG, scaled down to
        /// maxWidth when wider (0 keeps the full size). PW_RENDERFULLCONTENT makes DWM hand over what the game's swap chain last
        /// presented, which a plain WM_PRINT cannot. Returns "ok WxH (from WxH) mean=<0..255>" or the error; a mean near 0 is a
        /// black frame.</summary>
        public static string CaptureWindow(string desktop, long handle, string path, int maxWidth)
        {
            string result = "not run";
            OnDesktop(desktop, () =>
            {
                var h = new IntPtr(handle);
                RECT c; GetClientRect(h, out c);
                int w = c.Right - c.Left, ht = c.Bottom - c.Top;
                if (w <= 0 || ht <= 0) { result = "empty client area (window gone or minimised)"; return; }
                using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok = PrintWindow(h, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
                        g.ReleaseHdc(hdc);
                        if (!ok) { result = "PrintWindow failed: " + Marshal.GetLastWin32Error(); return; }
                    }
                    var overlay = SteamOverlay(bmp);     // at full size, before any scaling: such a frame is never written
                    if (overlay != null) { result = $"overlay {overlay} (from {w}x{ht})"; return; }
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                    if (maxWidth > 0 && w > maxWidth)
                    {
                        int nh = (int)Math.Round((double)ht * maxWidth / w);
                        using (var small = new Bitmap(maxWidth, nh, PixelFormat.Format32bppArgb))
                        {
                            using (var g = Graphics.FromImage(small))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(bmp, 0, 0, maxWidth, nh);
                            }
                            small.Save(path, ImageFormat.Png);
                            result = $"ok {maxWidth}x{nh} (from {w}x{ht}) mean={MeanLuma(small):0.0}";
                        }
                    }
                    else
                    {
                        bmp.Save(path, ImageFormat.Png);
                        result = $"ok {w}x{ht} (from {w}x{ht}) mean={MeanLuma(bmp):0.0}";
                    }
                }
            }, e => result = e);
            return result;
        }

        /// <summary>Crops a region (in the image's own pixels) out of a PNG into another PNG, scaled by factor (1 = as is).</summary>
        public static string Crop(string src, string dst, int x, int y, int width, int height, double factor)
        {
            using (var bmp = new Bitmap(src))
            {
                x = Math.Max(0, Math.Min(x, bmp.Width - 1)); y = Math.Max(0, Math.Min(y, bmp.Height - 1));
                width = Math.Min(width, bmp.Width - x); height = Math.Min(height, bmp.Height - y);
                int ow = Math.Max(1, (int)Math.Round(width * factor)), oh = Math.Max(1, (int)Math.Round(height * factor));
                using (var outBmp = new Bitmap(ow, oh, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(outBmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(bmp, new Rectangle(0, 0, ow, oh), new Rectangle(x, y, width, height), GraphicsUnit.Pixel);
                    }
                    outBmp.Save(dst, ImageFormat.Png);
                    return $"ok {ow}x{oh}";
                }
            }
        }

        // ---------------------------------------------------------------- the Steam overlay

        /// <summary>Where the Steam overlay's Back to Game button is in a frame, "x0,y0,x1,y1", or null - os2-deploy docshots' (Client/DocShots/Frames.cs)
        /// steam_overlay, number for number. In the top right corner (the right 14 %, the top 12 %): a square of the button's grey (#23262E,
        /// within 10 a channel), 2 % to 8 % of the frame's height a side and at least half of it that grey, with light pixels in it (every channel
        /// at least 170) of which at least 70 % lie along its two diagonals - the X. The overlay (Shift+Tab, or opened by Steam itself: once it came
        /// up with the client's start and stayed in every frame) shows the account's own name: CaptureWindow never writes a frame with it.</summary>
        public static string SteamOverlay(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int x0 = (int)(w * 0.86), rh = (int)(h * 0.12), rw = w - x0;
            if (rw <= 0 || rh <= 0) return null;
            var data = bmp.LockBits(new Rectangle(x0, 0, rw, rh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            var buf = new byte[stride * rh];
            try { Marshal.Copy(data.Scan0, buf, 0, buf.Length); }
            finally { bmp.UnlockBits(data); }
            // BGRA in memory
            var mask = new bool[rw * rh];
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    int p = y * stride + x * 4;
                    mask[y * rw + x] = Math.Abs(buf[p + 2] - 35) <= 10 && Math.Abs(buf[p + 1] - 38) <= 10 && Math.Abs(buf[p] - 46) <= 10;
                }
            var seen = new bool[mask.Length];
            double lo = 0.02 * h, hi = 0.08 * h;
            var stack = new Stack<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start]) continue;
                stack.Push(start);
                seen[start] = true;
                int n = 0, bx0 = start % rw, bx1 = bx0, by0 = start / rw, by1 = by0;
                while (stack.Count > 0)         // the grey's 4-connected piece
                {
                    int i = stack.Pop();
                    n++;
                    int x = i % rw, y = i / rw;
                    bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y);
                    if (x + 1 < rw && mask[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                    if (x > 0 && mask[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                    if (y + 1 < rh && mask[i + rw] && !seen[i + rw]) { seen[i + rw] = true; stack.Push(i + rw); }
                    if (y > 0 && mask[i - rw] && !seen[i - rw]) { seen[i - rw] = true; stack.Push(i - rw); }
                }
                int bw = bx1 - bx0 + 1, bh = by1 - by0 + 1;
                if (!(lo <= bw && bw <= hi && lo <= bh && bh <= hi && 0.8 <= (double)bw / bh && (double)bw / bh <= 1.25 && n >= 0.5 * bw * bh)) continue;
                double cx = (bx0 + bx1) / 2.0, cy = (by0 + by1) / 2.0;
                int light = 0, on = 0;
                for (int y = by0; y <= by1; y++)
                    for (int x = bx0; x <= bx1; x++)
                    {
                        int p = y * stride + x * 4;
                        if (Math.Min(Math.Min(buf[p], buf[p + 1]), buf[p + 2]) < 170) continue;
                        light++;
                        if (Math.Abs(Math.Abs(x - cx) - Math.Abs(y - cy)) <= 0.12 * bw) on++;
                    }
                if (light < 0.03 * bw * bh) continue;
                if (on >= 0.7 * light) return $"{x0 + bx0},{by0},{x0 + bx1 + 1},{by1 + 1}";
            }
            return null;
        }

        /// <summary>SteamOverlay of a picture on disk (a PNG the harness or docshots wrote), for a check after the fact.</summary>
        public static string SteamOverlayIn(string path)
        {
            using (var bmp = new Bitmap(path)) return SteamOverlay(bmp);
        }

        static double MeanLuma(Bitmap bmp)
        {
            double sum = 0; int n = 0;
            for (int gy = 0; gy < 36; gy++)
                for (int gx = 0; gx < 64; gx++)
                {
                    var p = bmp.GetPixel(Math.Min(bmp.Width - 1, gx * bmp.Width / 64), Math.Min(bmp.Height - 1, gy * bmp.Height / 36));
                    sum += 0.299 * p.R + 0.587 * p.G + 0.114 * p.B; n++;
                }
            return sum / n;
        }

        // ---------------------------------------------------------------- audio (read only)

        /// <summary>Every audio session of the process on every active render device. Read only: the peak meter is how silence is
        /// proven. Nothing is muted or turned down here, because Windows remembers a per-app volume and mute for the app's next start
        /// - the user's own CS2.</summary>
        public static List<AudioSessionInfo> AudioSessions(uint pid)
        {
            var list = new List<AudioSessionInfo>();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDeviceCollection devices;
            Check(enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out devices));
            int count; Check(devices.GetCount(out count));
            for (int i = 0; i < count; i++)
            {
                IMMDevice dev; if (devices.Item(i, out dev) != 0) continue;
                string id; dev.GetId(out id);
                var iid = typeof(IAudioSessionManager2).GUID;
                object o;
                if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) continue;
                IAudioSessionEnumerator sessions;
                if (((IAudioSessionManager2)o).GetSessionEnumerator(out sessions) != 0) continue;
                int n; sessions.GetCount(out n);
                for (int j = 0; j < n; j++)
                {
                    IAudioSessionControl2 s;
                    if (sessions.GetSession(j, out s) != 0) continue;
                    uint p; s.GetProcessId(out p);
                    if (p != pid) continue;
                    var info = new AudioSessionInfo { Pid = p, Device = id };
                    int st; s.GetState(out st); info.State = st;
                    try { float pk; ((IAudioMeterInformation)s).GetPeakValue(out pk); info.Peak = pk; } catch { info.Peak = -1; }
                    try { var v = (ISimpleAudioVolume)s; float vol; bool m; v.GetMasterVolume(out vol); v.GetMute(out m); info.Volume = vol; info.Muted = m; } catch { }
                    list.Add(info);
                }
            }
            return list;
        }

        static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }

        // ---------------------------------------------------------------- P/Invoke

        const uint GENERIC_ALL = 0x10000000;
        const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_ENUMERATE = 0x0040;
        const int STARTF_USESHOWWINDOW = 0x1;
        const short SW_SHOWNOACTIVATE = 4;
        const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_BREAKAWAY_FROM_JOB = 0x01000000,
                   CREATE_NO_WINDOW = 0x08000000, CREATE_NEW_PROCESS_GROUP = 0x00000200, BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000, PROCESS_SET_QUOTA = 0x0100, PROCESS_TERMINATE = 0x0001;
        const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
        const uint PW_CLIENTONLY = 0x1, PW_RENDERFULLCONTENT = 0x2;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, ref SECURITY_ATTRIBUTES sa);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr h);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder info, int length, out int needed);
        [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr hDesk, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
        [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessW(string app, IntPtr cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFOEXW si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr sa, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFOW
        {
            public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }

        // Core Audio (mmdeviceapi.h, audiopolicy.h, endpointvolume.h): only the leading methods each call needs, in vtable order.
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator { [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices); }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection { [PreserveSig] int GetCount(out int count); [PreserveSig] int Item(int index, out IMMDevice device); }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }
        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator { [PreserveSig] int GetCount(out int count); [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session); }
        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
        }
        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation { [PreserveSig] int GetPeakValue(out float peak); }
        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
