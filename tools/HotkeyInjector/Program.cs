using System.Runtime.InteropServices;

// 全局热键注入器 / 自检探针（v0.3.1 收入仓库）。
//
// 为什么需要它：判断「快捷键到底有没有生效」这件事，最容易把【测试脚本的缺陷】
// 当成【产品缺陷】。本仓库就踩过一次：RegisterHotKey 11/11 全部成功、
// 按 Ctrl+Alt+C 却毫无反应，排查半天才发现是脚本的 SendInput 一个键都没注入出去。
//
// 那个坑的具体内容（值得记住，因为它无声无息）：
//   x64 上 sizeof(INPUT) = 40（type 4 + 对齐填充 4 + 联合体 32）。
//   联合体若只按 KEYBDINPUT（24 字节）声明，托管侧 Marshal.SizeOf 算出 32，
//   而 Win32 要求 cbSize 必须等于 40 —— 不等时 SendInput 【直接返回 0】，
//   不抛异常、不写日志、看起来就是「热键功能坏了」。
// 所以本工具：① 联合体按 MOUSEINPUT 补齐到 32，② 每次注入都检查返回值。
//
// 用法：
//   dotnet run --project tools/HotkeyInjector -- inject <VK十六进制> [次数] [首次延迟ms]
//   dotnet run --project tools/HotkeyInjector -- probe <MODS十六进制> <VK十六进制> [Id十六进制]
//
// probe 模式自己注册热键并等命中，用来先确认「系统注入链路通不通」，
// 再怀疑被测程序。

if (args.Length > 0 && args[0] == "inject")
{
    return Injector.Inject(args);
}

return Probe.Run(args);

internal static class Injector
{
    internal static int Inject(string[] args)
    {
        int vk = Convert.ToInt32(args.Length > 1 ? args[1] : "43", 16);
        int repeat = args.Length > 2 ? int.Parse(args[2]) : 1;
        int firstDelay = args.Length > 3 ? int.Parse(args[3]) : 1000;

        Console.WriteLine($"INPUT_SIZE={Native.SizeOfInput}");
        if (Native.SizeOfInput != 40)
        {
            Console.WriteLine("FATAL sizeof(INPUT) != 40，注入必然被系统丢弃");
            return 2;
        }

        for (int n = 0; n < repeat; n++)
        {
            Thread.Sleep(n == 0 ? firstDelay : 700);
            Native.Key(0x11, false);           // VK_CONTROL
            Native.Key(0x12, false);           // VK_MENU (Alt)
            Native.Key(vk, false);
            Thread.Sleep(60);
            Native.Key(vk, true);
            Native.Key(0x12, true);
            Native.Key(0x11, true);
        }

        Thread.Sleep(400);
        Console.WriteLine($"INJECTED vk=0x{vk:X2} repeat={repeat}");
        return 0;
    }
}

internal static class Probe
{
    const uint WM_HOTKEY = 0x0312;
    const uint WM_QUIT = 0x0012;

    static int _hits;
    static readonly WndProc _proc = WndProcStatic;

    internal static int Run(string[] args)
    {
        var tcs = new TaskCompletionSource<int>();
        var thread = new Thread(() => tcs.SetResult(RunCore(args)));
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task.Result;
    }

    static int RunCore(string[] args)
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = "WinCanvasHotkeyProbe",
        };

        if (Native.RegisterClassExW(ref wc) == 0)
        {
            Console.WriteLine($"CLASS_REGISTER_FAILED err={Marshal.GetLastWin32Error()}");
            return 2;
        }

        IntPtr hwnd = Native.CreateWindowExW(0, "WinCanvasHotkeyProbe", "probe", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"CREATE_WINDOW_FAILED err={Marshal.GetLastWin32Error()}");
            return 2;
        }

        uint mods = Convert.ToUInt32(args.Length > 1 ? args[1] : "3", 16);
        uint vk = Convert.ToUInt32(args.Length > 2 ? args[2] : "43", 16);
        int id = args.Length > 3 ? int.Parse(args[3], System.Globalization.NumberStyles.HexNumber) : 0x9999;

        if (!Native.RegisterHotKey(hwnd, id, mods, vk))
        {
            // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED：说明这个组合正被别的进程（很可能就是被测应用）占着，
            // 此时应改用 inject 模式去打它，而不是让探针抢注。
            Console.WriteLine($"REGISTER_FAILED err={Marshal.GetLastWin32Error()} mods=0x{mods:X} vk=0x{vk:X}");
            return 3;
        }

        _hits = 0;

        var sender = new Thread(() =>
        {
            Thread.Sleep(1000);
            for (int n = 0; n < 3; n++)
            {
                Native.Key(0x11, false); Native.Key(0x12, false); Native.Key((int)vk, false);
                Thread.Sleep(60);
                Native.Key((int)vk, true); Native.Key(0x12, true); Native.Key(0x11, true);
                Thread.Sleep(600);
            }

            Thread.Sleep(1200);
            Native.PostMessageW(hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        });
        sender.IsBackground = true;
        sender.Start();

        DateTime deadline = DateTime.Now.AddSeconds(15);
        while (DateTime.Now < deadline && Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_HOTKEY) { _hits++; Console.WriteLine($"  GOT_WM_HOTKEY id=0x{msg.wParam.ToInt64():X}"); }
            if (msg.message == WM_QUIT) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        Native.UnregisterHotKey(hwnd, id);
        Console.WriteLine($"PROBE_HITS={_hits}");
        return _hits > 0 ? 0 : 1;
    }

    static IntPtr WndProcStatic(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_HOTKEY) return IntPtr.Zero;   // 计数在消息循环里做，这里只需吞掉
        return Native.DefWindowProcW(h, m, w, l);
    }
}

delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

[StructLayout(LayoutKind.Sequential)]
struct WNDCLASSEX
{
    public uint cbSize, style;
    [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public IntPtr hInstance, hIcon, hCursor, hbrBackground;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName, lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }

// MOUSEINPUT 决定联合体尺寸：缺了它，INPUT 在 x64 上会算成 32 而不是 40
[StructLayout(LayoutKind.Sequential)]
struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public UIntPtr extra; }

[StructLayout(LayoutKind.Explicit, Size = 32)]
struct INPUTU { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }

[StructLayout(LayoutKind.Sequential)]
struct INPUT { public uint type; public INPUTU u; }

[StructLayout(LayoutKind.Sequential)]
struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time, ptX, ptY; }

internal static class Native
{
    /// <summary>托管侧算出的 INPUT 尺寸；探针与注入器都用来自证 cbSize 传对了。</summary>
    internal static int SizeOfInput => Marshal.SizeOf<INPUT>();

    [DllImport("user32.dll", SetLastError = true)] internal static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] internal static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] internal static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] internal static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint n, INPUT[] p, int size);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandleW(string? n);

    /// <summary>发一个键（up=false 按下）。返回值必须检查：0 表示系统根本没接。</summary>
    internal static void Key(int vk, bool up)
    {
        var i = new INPUT { type = 1 };
        i.u.ki.vk = (ushort)vk;
        if (up) i.u.ki.flags = 2;   // KEYEVENTF_KEYUP

        if (SendInput(1u, [i], SizeOfInput) != 1)
        {
            Console.WriteLine($"  SENDINPUT_FAIL size={SizeOfInput} err={Marshal.GetLastWin32Error()}");
        }
    }
}
