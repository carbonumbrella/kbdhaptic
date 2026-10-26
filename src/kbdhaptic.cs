// kbdhaptic - touch-keyboard haptic feedback for MSI Claw (Windows 11), tray edition
//
// Trigger: on-screen-keyboard key taps arrive as injected keydowns to a low-level
// KEYBOARD hook (mouse taps on the keyboard do not). On each injected keydown, while
// the touch keyboard is visible (IFrameworkInputPane), fire a short XInput pulse.
//
// Threads:
//  - UI/STA thread runs the WinForms message loop (tray icon + settings) AND hosts the
//    keyboard hook (the loop pumps messages, so the hook fires). The hook only dedups
//    auto-repeat and enqueues; it never calls COM/XInput (that fails inside a hook).
//  - Worker/STA thread owns IFrameworkInputPane and fires the pulse; motor off by timer.
//
// Tray menu: Settings (sliders for click strength, bass, pulse length + Test), Enabled,
// Exit. Changes apply live and persist to kbdhaptic.ini.
//
// Build: csc /target:winexe /platform:x64 /win32manifest:kbdhaptic.manifest ^
//        /r:<fw>\System.Windows.Forms.dll /r:<fw>\System.Drawing.dll /out:kbdhaptic.exe kbdhaptic.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class KbdHaptic
{
    // ---- config (live) ----
    private static int _controllerIndex = 0;
    private static int _clickIntensity = 35;
    private static int _bassIntensity = 35;
    private static int _pulseMs = 5;
    private static bool _injectedOnly = true;
    private static bool _requireKeyboardVisible = true;
    private static bool _enabled = true;
    private static bool _gamepadButtonsInExplorer = true; // A/B/X/Y actions while an Explorer window is active
    private static bool _debug = false;

    // Auto-rotate: this Claw's sensor exposes only a Simple Device Orientation sensor
    // (no accelerometer/fusion sensor), so Windows' own screen auto-rotation never
    // engages. We read that sensor directly and rotate the display ourselves. The
    // Windows "Auto-rotate" quick-setting still acts as the lock (AutoRotation\Enable).
    private static bool _autoRotate = true;
    // Map SimpleOrientation (NotRotated=0, 90ccw=1, 180=2, 270ccw=3) -> display
    // orientation DMDO (0,1,2,3). Default {0,3,2,1} is calibrated for the Claw 8 panel
    // (its two portrait orientations are swapped vs the naive mapping); adjustable from
    // the ini for other panels.
    private static int[] _rotateMap = { 0, 3, 2, 1 };

    // ---- win32 ----
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public UIntPtr dwExtraInfo; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, string lParam);
    private const int EM_SETCUEBANNER = 0x1501;
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // message loop for the dedicated hook thread
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int pt_x; public int pt_y; }
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    // ---- display rotation ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion; public ushort dmDriverVersion; public ushort dmSize; public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX; public int dmPositionY; public uint dmDisplayOrientation; public uint dmDisplayFixedOutput;
        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels; public uint dmBitsPerPel; public uint dmPelsWidth; public uint dmPelsHeight;
        public uint dmDisplayFlags; public uint dmDisplayFrequency;
        public uint dmICMMethod; public uint dmICMIntent; public uint dmMediaType; public uint dmDitherType;
        public uint dmReserved1; public uint dmReserved2; public uint dmPanningWidth; public uint dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lp);
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYORIENTATION = 0x80;
    private const uint CDS_UPDATEREGISTRY = 0x01;
    private const uint DMDO_DEFAULT = 0, DMDO_90 = 1, DMDO_180 = 2, DMDO_270 = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed; public ushort wRightMotorSpeed; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger; public byte bRightTrigger; public short sThumbLX; public short sThumbLY; public short sThumbRX; public short sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);
    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    private const ushort XINPUT_GAMEPAD_A = 0x1000;
    private const ushort XINPUT_GAMEPAD_B = 0x2000;
    private const ushort XINPUT_GAMEPAD_X = 0x4000;
    private const ushort XINPUT_GAMEPAD_Y = 0x8000;
    private const byte VK_BACK = 0x08;   // Backspace = Back in Explorer
    private const byte VK_RETURN = 0x0D;
    private const byte VK_MENU = 0x12;   // Alt
    private const byte VK_UP = 0x26;
    private const byte VK_APPS = 0x5D;   // context-menu key
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [ComImport, Guid("D5120AA3-46BA-44C5-822D-CA8092C1FC72")]
    private class FrameworkInputPane { }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5752238B-24F0-495A-82F1-2FD593056796")]
    private interface IFrameworkInputPane
    {
        void Advise([MarshalAs(UnmanagedType.IUnknown)] object pHandler, out uint pdwCookie);
        void AdviseWithHWND(IntPtr hwnd, [MarshalAs(UnmanagedType.IUnknown)] object pHandler, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void Location(out RECT prcInputPaneScreenLocation);
    }

    // ---- state ----
    private static HookProc _proc;
    private static IntPtr _hook = IntPtr.Zero;
    private static IFrameworkInputPane _fip;
    private static readonly object _motorLock = new object();
    // Motor-off runs on MotorLoop (its own above-normal thread), not a thread-pool timer:
    // under heavy load the pool was starved so "motor off" was delayed by seconds, leaving
    // the motor buzzing continuously. _motorDeadline is a monotonic-clock ms deadline.
    private static volatile int _motorDeadline;            // 0 = motor off
    private static readonly AutoResetEvent _pulseSignal = new AutoResetEvent(false);
    // Work items are enqueue timestamps (ms on a monotonic clock), not vkCodes: a pulse
    // only cares WHEN the tap happened, so stale items can be dropped under load.
    private static readonly BlockingCollection<long> _work = new BlockingCollection<long>();
    private static readonly HashSet<uint> _down = new HashSet<uint>();
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    // Cached keyboard-visible state so the shell COM Location() call is not made on every
    // keydown (it can stall under load and back the queue up, firing late burst pulses).
    // Init well in the past (not long.MinValue, which overflows the age subtraction and
    // would wedge the cache at "not visible") so the first query runs immediately.
    private static long _visTick = -100000;
    private static bool _visCached;
    // A pulse older than this (ms) when it reaches the worker is skipped: too late to feel
    // in sync, and firing it would be the out-of-time buzzing seen under high load.
    private const long StalePulseMs = 60;
    private static string _iniPath, _logPath;

    [STAThread]
    private static int Main()
    {
        // hidden: render the settings window to a PNG offscreen (no window shown), for layout checks
        var cmd = Environment.GetCommandLineArgs();
        if (cmd.Length >= 3 && cmd[1] == "--layoutshot")
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var f = new SettingsForm())
                {
                    // Show off-screen without stealing focus, then capture the whole
                    // window (frame + client) via PrintWindow(PW_RENDERFULLCONTENT).
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-32000, -32000);
                    f.ShowInTaskbar = false;
                    IntPtr prevFg = GetForegroundWindow();
                    f.Show();
                    Application.DoEvents();
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = g.GetHdc();
                            PrintWindow(f.Handle, hdc, 2); // PW_RENDERFULLCONTENT
                            g.ReleaseHdc(hdc);
                        }
                        bmp.Save(cmd[2], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    f.Close();
                    if (prevFg != IntPtr.Zero) SetForegroundWindow(prevFg);
                }
            }
            catch { }
            return 0;
        }

        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _iniPath = Path.Combine(dir, "kbdhaptic.ini");
        _logPath = Path.Combine(dir, "kbdhaptic.log");

        bool createdNew;
        var mtx = new Mutex(false, "Local\\kbdhaptic_singleton", out createdNew);
        if (!createdNew) return 0;
        GC.KeepAlive(mtx);

        LoadOrCreateConfig();
        SetMotors(0, 0);

        // Motor off runs here, on its own above-normal thread (see _motorDeadline).
        var motor = new Thread(MotorLoop) { IsBackground = true };
        try { motor.Priority = ThreadPriority.AboveNormal; } catch { }
        motor.Start();

        var worker = new Thread(WorkLoop) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        try { worker.Priority = ThreadPriority.AboveNormal; } catch { }
        worker.Start();

        var poll = new Thread(PollLoop) { IsBackground = true };
        poll.Start();

        var rot = new Thread(RotateLoop) { IsBackground = true };
        rot.Start();

        AppDomain.CurrentDomain.ProcessExit += (s, e) => SetMotors(0, 0);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => SetMotors(0, 0);

        // Keyboard hook on its own above-normal thread with its own message loop, so the
        // WinForms UI pump cannot starve it under load (which dropped/delayed taps).
        var hookThread = new Thread(HookThread) { IsBackground = true };
        try { hookThread.Priority = ThreadPriority.AboveNormal; } catch { }
        hookThread.Start();
        Log("started");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());

        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        SetMotors(0, 0);
        return 0;
    }

    // Keyboard hook owner: installs WH_KEYBOARD_LL and pumps its own message queue. The
    // hook callback runs on this thread, isolated from the UI pump.
    private static void HookThread()
    {
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { Log("SetWindowsHookEx failed err=" + Marshal.GetLastWin32Error()); return; }
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    // Motor-off timer on a dedicated above-normal thread, driven by the monotonic clock
    // so it cannot be starved by the thread pool. Guarantees the motor stops within a few
    // ms of its deadline even under heavy CPU load.
    private static void MotorLoop()
    {
        while (true)
        {
            _pulseSignal.WaitOne(100);
            int dl;
            while ((dl = _motorDeadline) != 0)
            {
                int now = (int)_clock.ElapsedMilliseconds;
                if (now >= dl) { SetMotors(0, 0); _motorDeadline = 0; if (_debug) Log("motor off"); break; }
                Thread.Sleep(Math.Min(dl - now, 8));
            }
        }
    }

    private static void Realize(Control c)
    {
        IntPtr h = c.Handle; GC.KeepAlive(h);
        foreach (Control ch in c.Controls) Realize(ch);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = wParam.ToInt32();
            try
            {
                if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                {
                    var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool injected = (k.flags & LLKHF_INJECTED) != 0;
                    bool firstPress = _down.Add(k.vkCode);
                    if (_enabled && firstPress && (!_injectedOnly || injected))
                        _work.Add(_clock.ElapsedMilliseconds);
                }
                else if (m == WM_KEYUP || m == WM_SYSKEYUP)
                {
                    var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    _down.Remove(k.vkCode);
                }
            }
            catch { }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void WorkLoop()
    {
        try { _fip = (IFrameworkInputPane)new FrameworkInputPane(); }
        catch (Exception ex) { Log("FrameworkInputPane create failed: " + ex.Message); return; }

        foreach (var t0 in _work.GetConsumingEnumerable())
        {
            try
            {
                // Coalesce any backlog that piled up while we were busy: keep only the
                // newest tap, drop the rest. Under normal typing the queue is empty so
                // this is one pulse per key; under load it collapses a burst to one.
                long newest = t0; long t; int drained = 0;
                while (_work.TryTake(out t)) { newest = t; drained++; }

                long age = _clock.ElapsedMilliseconds - newest;
                if (age > StalePulseMs) { if (_debug) Log("skip stale pulse age=" + age + "ms drained=" + drained); continue; }

                bool visible = KeyboardVisibleCached();
                if (_debug) Log("pulse age=" + age + "ms drained=" + drained + " kbdVisible=" + visible);
                if (visible) Pulse();
            }
            catch (Exception ex) { Log("work error: " + ex.Message); }
        }
    }

    // Keyboard-visible via the shell COM interface, but queried at most ~10x/sec. The
    // COM call can block under load; calling it per keydown backs the queue up, so the
    // result is cached for a short window.
    private static bool KeyboardVisibleCached()
    {
        if (!_requireKeyboardVisible) return true;
        long now = _clock.ElapsedMilliseconds;
        if (now - _visTick > 100)
        {
            try { RECT r; _fip.Location(out r); _visCached = !(r.left == 0 && r.top == 0 && r.right == 0 && r.bottom == 0); }
            catch { /* keep last cached value */ }
            _visTick = now;
        }
        return _visCached;
    }

    // Public for the Test button.
    internal static void Pulse()
    {
        ushort right = (ushort)(Clamp(_clickIntensity, 0, 100) * 65535 / 100);
        ushort left = (ushort)(Clamp(_bassIntensity, 0, 100) * 65535 / 100);
        SetMotors(left, right);
        // Hand the motor-off deadline to MotorLoop (hard-capped at 60ms so a lost wakeup
        // can never leave the motor on for seconds).
        _motorDeadline = (int)_clock.ElapsedMilliseconds + Clamp(_pulseMs, 1, 60);
        _pulseSignal.Set();
    }

    private static void SetMotors(ushort left, ushort right)
    {
        lock (_motorLock)
        {
            var v = new XINPUT_VIBRATION { wLeftMotorSpeed = left, wRightMotorSpeed = right };
            try { XInputSetState((uint)_controllerIndex, ref v); } catch { }
        }
    }

    private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

    // Poll the gamepad: map A/B/X/Y to Explorer actions, but only while a file
    // Explorer window is foreground. XInput is read only when Explorer is in front,
    // so idle CPU stays ~0. Each action fires on the button's rising edge and gives
    // the same vibration pulse as a keyboard tap.
    private static void PollLoop()
    {
        ushort prev = 0;
        var sb = new System.Text.StringBuilder(64);
        while (true)
        {
            try
            {
                if (_gamepadButtonsInExplorer && IsExplorerForeground(sb))
                {
                    XINPUT_STATE st;
                    if (XInputGetState((uint)_controllerIndex, out st) == 0)
                    {
                        ushort now = st.Gamepad.wButtons;
                        ushort pressed = (ushort)(now & ~prev); // newly pressed this tick
                        if ((pressed & XINPUT_GAMEPAD_A) != 0) { Tap(VK_RETURN); Pulse(); }           // открыть
                        if ((pressed & XINPUT_GAMEPAD_Y) != 0) { TapWith(VK_MENU, VK_UP); Pulse(); }   // уровень вверх
                        if ((pressed & XINPUT_GAMEPAD_B) != 0) { Tap(VK_BACK); Pulse(); }               // назад
                        if ((pressed & XINPUT_GAMEPAD_X) != 0) { Tap(VK_APPS); Pulse(); }              // контекстное меню
                        prev = now;
                    }
                    else prev = 0;
                }
                else prev = 0;
            }
            catch { }
            Thread.Sleep(20);
        }
    }

    private static bool IsExplorerForeground(System.Text.StringBuilder sb)
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        sb.Length = 0;
        GetClassName(h, sb, sb.Capacity);
        // File Explorer windows use CabinetWClass; the desktop (Progman/WorkerW) is excluded.
        return sb.ToString() == "CabinetWClass";
    }

    // Read the Simple Device Orientation sensor (WinRT, via reflection so the in-box
    // csc build needs no winmd reference) and rotate the display to match. Faceup /
    // Facedown are ignored (keep the last orientation). A new orientation must persist
    // for two polls before it is applied, to avoid flicker mid-rotation. The Windows
    // quick-setting "Auto-rotate" is honored as the lock via AutoRotation\Enable.
    private static void RotateLoop()
    {
        object sensor = null;
        MethodInfo getCur = null;
        int candidate = -1, stable = 0, lastApplied = -1;
        while (true)
        {
            try
            {
                if (!_autoRotate) { Thread.Sleep(600); continue; }
                // Locked unless the Windows auto-rotate feature is provisioned (the
                // Enable-AutoRotate.reg flags). Without it there is no quick-settings tile
                // / lock, so we do not rotate on our own.
                if (!IsAutoRotateProvisioned()) { Thread.Sleep(800); lastApplied = -1; continue; }
                if (!IsRotationUnlocked()) { Thread.Sleep(600); lastApplied = -1; continue; }

                if (sensor == null)
                {
                    Type t = Type.GetType("Windows.Devices.Sensors.SimpleOrientationSensor, Windows, ContentType=WindowsRuntime");
                    if (t != null) { sensor = t.GetMethod("GetDefault").Invoke(null, null); if (sensor != null) getCur = sensor.GetType().GetMethod("GetCurrentOrientation"); }
                    if (sensor == null) { Thread.Sleep(1000); continue; }
                }

                int ori = Convert.ToInt32(getCur.Invoke(sensor, null)); // 0..5
                if (ori >= 4) { Thread.Sleep(400); continue; } // Faceup / Facedown -> ignore

                int target = _rotateMap[ori & 3] & 3;
                if (target == candidate) { if (stable < 2) stable++; }
                else { candidate = target; stable = 1; }

                if (stable >= 2 && target != lastApplied)
                {
                    if (SetDisplayOrientation((uint)target)) { lastApplied = target; Log("rotated ori=" + ori + " -> dmdo=" + target); }
                }
            }
            catch (Exception ex) { Log("rotate error: " + ex.Message); sensor = null; }
            Thread.Sleep(400);
        }
    }

    // The Windows auto-rotate feature is provisioned when SlateEnable+SensorPresent are
    // set (Enable-AutoRotate.reg). If they are zero/absent, the auto-rotate option is
    // locked: no quick-settings tile exists and we must not rotate the screen ourselves.
    private static bool IsAutoRotateProvisioned()
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation"))
            {
                if (k == null) return false;
                object slate = k.GetValue("SlateEnable");
                object sensor = k.GetValue("SensorPresent");
                return slate != null && Convert.ToInt32(slate) != 0
                    && sensor != null && Convert.ToInt32(sensor) != 0;
            }
        }
        catch { return false; }
    }

    // The quick-setting lock lives in AutoRotation\Enable (1 = auto-rotate on). Missing
    // value is treated as enabled.
    private static bool IsRotationUnlocked()
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation"))
            {
                if (k == null) return true;
                object v = k.GetValue("Enable");
                if (v == null) return true;
                return Convert.ToInt32(v) != 0;
            }
        }
        catch { return true; }
    }

    private static bool SetDisplayOrientation(uint dmdo)
    {
        string dev = null;
        try { dev = Screen.PrimaryScreen.DeviceName; } catch { }
        var dm = new DEVMODE();
        dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
        if (EnumDisplaySettings(dev, ENUM_CURRENT_SETTINGS, ref dm) == 0 || dm.dmPelsWidth == 0) return false;
        if (dm.dmDisplayOrientation == dmdo) return true; // already there

        // Landscape baseline (long edge = width) from the current mode.
        uint lw = dm.dmPelsWidth, lh = dm.dmPelsHeight;
        if (dm.dmDisplayOrientation == DMDO_90 || dm.dmDisplayOrientation == DMDO_270) { uint t = lw; lw = lh; lh = t; }

        bool portrait = (dmdo == DMDO_90 || dmdo == DMDO_270);
        dm.dmPelsWidth = portrait ? lh : lw;
        dm.dmPelsHeight = portrait ? lw : lh;
        dm.dmDisplayOrientation = dmdo;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYORIENTATION;
        return ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
    }

    private static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void TapWith(byte modifier, byte vk)
    {
        keybd_event(modifier, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(modifier, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---- config ----
    private static void LoadOrCreateConfig()
    {
        if (!File.Exists(_iniPath)) { SaveConfig(); return; }
        foreach (var raw in File.ReadAllLines(_iniPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            int semi = line.IndexOf(';'); if (semi >= 0) line = line.Substring(0, semi).Trim();
            int eq = line.IndexOf('='); if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant();
            string v = line.Substring(eq + 1).Trim();
            switch (k)
            {
                case "controllerindex": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _controllerIndex); break;
                case "clickintensity": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _clickIntensity); break;
                case "bassintensity": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _bassIntensity); break;
                case "pulsems": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _pulseMs); break;
                case "injectedonly": _injectedOnly = IsTrue(v); break;
                case "requirekeyboardvisible": _requireKeyboardVisible = IsTrue(v); break;
                case "enabled": _enabled = IsTrue(v); break;
                case "gamepadbuttonsinexplorer": _gamepadButtonsInExplorer = IsTrue(v); break;
                case "gamepadenterinexplorer": _gamepadButtonsInExplorer = IsTrue(v); break; // legacy key
                case "autorotate": _autoRotate = IsTrue(v); break;
                case "rotatemap": ParseRotateMap(v); break;
                case "debug": _debug = IsTrue(v); break;
            }
        }
    }

    private static bool IsTrue(string v) { return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1"; }

    private static void ParseRotateMap(string v)
    {
        var parts = v.Split(',');
        if (parts.Length != 4) return;
        var m = new int[4];
        for (int i = 0; i < 4; i++)
        {
            int n;
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return;
            m[i] = n & 3;
        }
        _rotateMap = m;
    }

    internal static void SaveConfig()
    {
        try
        {
            File.WriteAllText(_iniPath,
                "; kbdhaptic config\r\n" +
                "controllerIndex=" + _controllerIndex + "        ; 0..3\r\n" +
                "clickIntensity=" + _clickIntensity + "          ; high-freq motor, 0..100\r\n" +
                "bassIntensity=" + _bassIntensity + "            ; low-freq motor, 0..100\r\n" +
                "pulseMs=" + _pulseMs + "                        ; motor-on duration\r\n" +
                "injectedOnly=" + (_injectedOnly ? "true" : "false") + "        ; only on-screen-keyboard keys\r\n" +
                "requireKeyboardVisible=" + (_requireKeyboardVisible ? "true" : "false") + " ; only while keyboard shown\r\n" +
                "enabled=" + (_enabled ? "true" : "false") + "              ; master on/off\r\n" +
                "gamepadButtonsInExplorer=" + (_gamepadButtonsInExplorer ? "true" : "false") + " ; A/B/X/Y actions in Explorer\r\n" +
                "autoRotate=" + (_autoRotate ? "true" : "false") + "            ; rotate screen from the orientation sensor\r\n" +
                "rotateMap=" + string.Join(",", Array.ConvertAll(_rotateMap, x => x.ToString())) + "          ; sensor->display orientation map (calibration)\r\n" +
                "debug=" + (_debug ? "true" : "false") + "                  ; write kbdhaptic.log\r\n");
        }
        catch { }
    }

    private static void Log(string msg)
    {
        if (!_debug) return;
        try { File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n"); } catch { }
    }

    // ---- live accessors for the UI ----
    internal static int ClickIntensity { get { return _clickIntensity; } set { _clickIntensity = value; } }
    internal static int BassIntensity { get { return _bassIntensity; } set { _bassIntensity = value; } }
    internal static int PulseMs { get { return _pulseMs; } set { _pulseMs = value; } }
    internal static bool Enabled { get { return _enabled; } set { _enabled = value; } }
    internal static bool GamepadButtonsInExplorer { get { return _gamepadButtonsInExplorer; } set { _gamepadButtonsInExplorer = value; } }
    internal static bool AutoRotate { get { return _autoRotate; } set { _autoRotate = value; } }
    internal static bool AutoRotateProvisioned { get { return IsAutoRotateProvisioned(); } }

    // ================= tray UI =================
    private sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private SettingsForm _form;
        private readonly ToolStripMenuItem _enabledItem;

        public TrayContext()
        {
            _enabledItem = new ToolStripMenuItem("Включено", null, (s, e) =>
            {
                Enabled = !Enabled; _enabledItem.Checked = Enabled; SaveConfig();
            });
            _enabledItem.Checked = Enabled;

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Настройки", null, (s, e) => ShowSettings()));
            menu.Items.Add(_enabledItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (s, e) => ExitApp()));

            _icon = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "kbdhaptic",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (s, e) => ShowSettings();
        }

        private void ShowSettings()
        {
            if (_form == null || _form.IsDisposed) _form = new SettingsForm();
            _form.Show();
            _form.WindowState = FormWindowState.Normal;
            _form.BringToFront();
            _form.Activate();
        }

        private void ExitApp()
        {
            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        }

    }

    // A keyboard with vibration motion lines on both sides (matches src/kbdhaptic.ico).
    // Drawn on a 64-unit grid then scaled to 32px.
    private static Icon MakeIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform(32f / 64f, 32f / 64f);
            var key = Color.FromArgb(0x2D, 0x8C, 0xF0);
            using (var b = new SolidBrush(key))
            using (var wb = new SolidBrush(Color.White))
            using (var pen = new Pen(key, 3.2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
                g.DrawLine(pen, 7f, 20f, 7f, 44f);         // left outer tall
                g.DrawLine(pen, 12.5f, 25f, 12.5f, 39f);   // left inner short
                g.DrawLine(pen, 51.5f, 25f, 51.5f, 39f);   // right inner short
                g.DrawLine(pen, 57f, 20f, 57f, 44f);       // right outer tall
                using (var body = RoundRect(18f, 19f, 28f, 26f, 4f)) g.FillPath(b, body);
                foreach (float ky in new[] { 23f, 29.5f })
                    foreach (float kx in new[] { 22.5f, 27.5f, 32.5f, 37.5f })
                        using (var k = RoundRect(kx, ky, 4f, 4.5f, 1f)) g.FillPath(wb, k);
                using (var sb = RoundRect(24f, 37.5f, 16f, 3f, 1f)) g.FillPath(wb, sb);
            }
        }
        return Icon.FromHandle(bmp.GetHicon()); // handle kept for process lifetime
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private sealed class SettingsForm : Form
    {
        private readonly float _s; // scale factor from system DPI
        private TextBox _testBox;
        private int S(int px) { return (int)Math.Round(px * _s); }

        public SettingsForm()
        {
            uint dpi = 96; try { dpi = GetDpiForSystem(); } catch { }
            _s = dpi / 96f;

            AutoScaleMode = AutoScaleMode.None; // manual scaling
            Font = new Font(Font.FontFamily, 9f * _s);
            Text = "kbdhaptic";
            FormBorderStyle = FormBorderStyle.FixedDialog; // shows the title-bar icon
            MaximizeBox = false; MinimizeBox = false;
            ShowIcon = true; Icon = MakeIcon();
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true; // stay visible when focus moves (e.g. to the touch keyboard)

            int W = S(380);

            // --- Вибрация ---
            var gbVibro = new GroupBox { Text = "Вибрация", Left = S(10), Top = S(8), Width = W, Height = S(234) };
            AddSlider(gbVibro, "Малый мотор (R)", 0, 100, ClickIntensity, 28, v => { ClickIntensity = v; SaveConfig(); });
            AddSlider(gbVibro, "Большой мотор (L)", 0, 100, BassIntensity, 94, v => { BassIntensity = v; SaveConfig(); });
            AddSlider(gbVibro, "Длина импульса, мс", 1, 40, PulseMs, 160, v => { PulseMs = v; SaveConfig(); });
            Controls.Add(gbVibro);

            // --- Проводник ---
            var gbExpl = new GroupBox { Text = "Проводник", Left = S(10), Top = S(250), Width = W, Height = S(88) };
            var gp = new CheckBox { Text = "Кнопки геймпада в проводнике", Left = S(14), Top = S(28), AutoSize = true, Checked = GamepadButtonsInExplorer };
            gp.CheckedChanged += (s, e) => { GamepadButtonsInExplorer = gp.Checked; SaveConfig(); };
            var hint = new Label { Text = "A открыть · Y вверх · B назад · X меню", Left = S(14), Top = S(56), AutoSize = true, ForeColor = SystemColors.GrayText, Font = new Font(Font.FontFamily, 8f * _s) };
            gbExpl.Controls.Add(gp); gbExpl.Controls.Add(hint);
            Controls.Add(gbExpl);

            // --- Экран ---
            bool rotProv = AutoRotateProvisioned;
            var gbScreen = new GroupBox { Text = "Экран", Left = S(10), Top = S(346), Width = W, Height = S(96) };
            var ar = new CheckBox { Text = "Автоповорот экрана", Left = S(14), Top = S(26), AutoSize = true, Checked = AutoRotate && rotProv, Enabled = rotProv };
            ar.CheckedChanged += (s, e) => { AutoRotate = ar.Checked; SaveConfig(); };
            var arHint = new Label
            {
                Text = rotProv ? "Поворот экрана по датчику ориентации"
                               : "Отключено: запустите Enable-AutoRotate.reg, затем перезагрузка",
                Left = S(14), Top = S(50), Width = W - S(28), Height = S(40), AutoSize = false,
                ForeColor = SystemColors.GrayText, Font = new Font(Font.FontFamily, 8f * _s)
            };
            gbScreen.Controls.Add(ar); gbScreen.Controls.Add(arHint);
            Controls.Add(gbScreen);

            // --- Проверка ---
            var gbTest = new GroupBox { Text = "Проверка", Left = S(10), Top = S(454), Width = W, Height = S(108) };
            var test = new Button { Text = "Тест импульса", Left = S(14), Top = S(30), Width = W - S(28), Height = S(32) };
            test.Click += (s, e) => Pulse();
            _testBox = new TextBox { Left = S(14), Top = S(70), Width = W - S(28), Height = S(26) };
            _testBox.HandleCreated += (s, e) =>
                SendMessage(_testBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Печатайте здесь для проверки");
            gbTest.Controls.Add(test); gbTest.Controls.Add(_testBox);
            Controls.Add(gbTest);

            // --- Закрыть ---
            var close = new Button { Text = "Закрыть", Left = S(10) + W - S(120), Top = S(572), Width = S(120), Height = S(34) };
            close.Click += (s, e) => Hide();
            Controls.Add(close);

            ClientSize = new Size(S(400), S(618));
        }

        private void AddSlider(Control parent, string label, int min, int max, int val, int top, Action<int> onChange)
        {
            val = Clamp(val, min, max);
            int w = parent.Width;
            var lbl = new Label { Text = label, Left = S(14), Top = S(top), Width = w - S(90), Height = S(22), AutoSize = false };
            var valLbl = new Label { Text = val.ToString(), Left = w - S(74), Top = S(top), Width = S(60), Height = S(22), TextAlign = ContentAlignment.MiddleRight };
            var sl = new TouchSlider(min, max, val, S(22)) { Left = S(14), Top = S(top + 22), Width = w - S(28), Height = S(38) };
            sl.ValueChanged += v => { valLbl.Text = v.ToString(); onChange(v); };
            parent.Controls.Add(lbl); parent.Controls.Add(valLbl); parent.Controls.Add(sl);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            e.Cancel = true; // keep the process alive in tray; just hide
            Hide();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible && _testBox != null) _testBox.Clear(); // wipe test text on any hide
            base.OnVisibleChanged(e);
        }
    }

    // Touch-friendly slider: tap or drag anywhere on the track sets the value.
    // A plain Control (not TrackBar) so touch is promoted to mouse and drag works.
    private sealed class TouchSlider : Control
    {
        private readonly int _min, _max, _thumb;
        private int _val;
        public event Action<int> ValueChanged;

        public TouchSlider(int min, int max, int val, int thumbW)
        {
            _min = min; _max = max; _thumb = Math.Max(10, thumbW);
            _val = Clamp(val, min, max);
            DoubleBuffered = true; TabStop = false;
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) SetFromX(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (e.Button == MouseButtons.Left) SetFromX(e.X); base.OnMouseMove(e); }

        private void SetFromX(int x)
        {
            int track = Math.Max(1, Width - _thumb);
            int xx = Clamp(x - _thumb / 2, 0, track);
            int v = _min + (int)Math.Round((double)xx / track * (_max - _min));
            if (v != _val) { _val = v; Invalidate(); if (ValueChanged != null) ValueChanged(_val); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int track = Math.Max(1, Width - _thumb);
            int tx = (int)Math.Round((double)(_val - _min) / (_max - _min) * track);
            int cy = Height / 2;
            int th = Math.Max(4, _thumb / 3);
            int x0 = _thumb / 2;
            using (var bg = new SolidBrush(Color.FromArgb(0x60, 0x60, 0x60)))
                g.FillRectangle(bg, x0, cy - th / 2, track, th);
            using (var fb = new SolidBrush(Color.FromArgb(0x2D, 0x8C, 0xF0)))
                g.FillRectangle(fb, x0, cy - th / 2, tx, th);
            using (var tb = new SolidBrush(Color.White))
            using (var tp = new Pen(Color.FromArgb(0x2D, 0x8C, 0xF0), 2f))
            {
                g.FillEllipse(tb, tx, cy - _thumb / 2, _thumb, _thumb);
                g.DrawEllipse(tp, tx, cy - _thumb / 2, _thumb, _thumb);
            }
        }
    }
}
