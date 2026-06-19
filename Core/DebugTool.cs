
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DebugTool;

/// <summary>
/// Windows kernel32 P/Invoke for console window allocation and colored output.
/// </summary>
public static class ConsoleAllocator
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleTextAttribute(IntPtr hConsoleOutput, ushort wAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WriteConsoleW(IntPtr hConsoleOutput, string lpBuffer, uint nNumberOfCharsToWrite, out uint lpNumberOfCharsWritten, IntPtr lpReserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleWindowInfo(IntPtr hConsoleOutput, bool bAbsolute, ref SMALL_RECT lpConsoleWindow);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern COORD GetLargestConsoleWindowSize(IntPtr hConsoleOutput);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFOEX lpConsoleCurrentFontEx);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    public const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    public const uint ENABLE_INSERT_MODE = 0x0020;
    public const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ScrollConsoleScreenBuffer(IntPtr hConsoleOutput,
        [In] ref SMALL_RECT lpScrollRectangle,
        [In] ref SMALL_RECT lpClipRectangle,
        COORD dwDestinationOrigin,
        [In] ref CHAR_INFO lpFill);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCursorPosition(IntPtr hConsoleOutput, COORD dwCursorPosition);

    [StructLayout(LayoutKind.Sequential)]
    public struct CHAR_INFO
    {
        public char UnicodeChar;
        public ushort Attributes;
    }

    public const int STD_OUTPUT_HANDLE = -11;
    public const int STD_INPUT_HANDLE = -10;
    public const int STD_ERROR_HANDLE = -12;
    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // Console mode flags
    public const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CONSOLE_FONT_INFOEX
    {
        public int cbSize;
        public int nFont;
        public COORD dwFontSize;
        public int FontFamily;
        public int FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    public const int FW_NORMAL = 400;
    public const int FW_BOLD = 700;
    public const int FF_DONTCARE = 0;
    public const int TMPF_TRUETYPE = 4;

    // Console color constants
    public const ushort FOREGROUND_BLUE = 0x0001;
    public const ushort FOREGROUND_GREEN = 0x0002;
    public const ushort FOREGROUND_RED = 0x0004;
    public const ushort FOREGROUND_INTENSITY = 0x0008;
    public const ushort FOREGROUND_WHITE = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE;
    public const ushort FOREGROUND_YELLOW = FOREGROUND_RED | FOREGROUND_GREEN;
    public const ushort FOREGROUND_CYAN = FOREGROUND_GREEN | FOREGROUND_BLUE;
    public const ushort FOREGROUND_MAGENTA = FOREGROUND_RED | FOREGROUND_BLUE;

    private static IntPtr? _stdoutHandle;
    private static ushort _defaultAttributes = FOREGROUND_WHITE;

    public static IntPtr StdoutHandle
    {
        get
        {
            if (_stdoutHandle == null)
                _stdoutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
            return _stdoutHandle.Value;
        }
    }

    public static void CacheDefaultAttributes()
    {
        if (GetConsoleScreenBufferInfo(StdoutHandle, out var info))
            _defaultAttributes = info.wAttributes;
    }

    /// <summary>
    /// Apply a modern TrueType font and reasonable window dimensions so the console
    /// looks like a regular cmd.exe window instead of the bare AllocConsole default.
    /// </summary>
    public static void ApplyConsoleStyle(string title = "Runtime Debug")
    {
        SetConsoleTitle(title);

        IntPtr hInput = GetStdHandle(STD_INPUT_HANDLE);
        if (hInput != IntPtr.Zero && GetConsoleMode(hInput, out uint mode))
        {
            mode |= ENABLE_QUICK_EDIT_MODE;
            mode |= ENABLE_EXTENDED_FLAGS;
            SetConsoleMode(hInput, mode);
        }

        SetConsoleCP(65001);
        SetConsoleOutputCP(65001);
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var fontInfo = new CONSOLE_FONT_INFOEX();
        fontInfo.cbSize = Marshal.SizeOf(fontInfo);
        fontInfo.nFont = 0;
        fontInfo.dwFontSize = new COORD { X = 0, Y = 18 };
        fontInfo.FontFamily = FF_DONTCARE;
        fontInfo.FontWeight = FW_NORMAL;
        fontInfo.FaceName = "Consolas";
        SetCurrentConsoleFontEx(StdoutHandle, false, ref fontInfo);

        var largest = GetLargestConsoleWindowSize(StdoutHandle);
        short cols = (short)Math.Min(140, (int)largest.X);
        short rows = (short)Math.Min(20, (int)largest.Y);

        var bufferSize = new COORD { X = cols, Y = rows };
        SetConsoleScreenBufferSize(StdoutHandle, bufferSize);

        var windowRect = new SMALL_RECT { Left = 0, Top = 0, Right = (short)(cols - 1), Bottom = (short)(rows - 1) };
        SetConsoleWindowInfo(StdoutHandle, true, ref windowRect);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetConsoleTitle(string lpConsoleTitle);

    public static void SetColor(ushort color)
    {
        SetConsoleTextAttribute(StdoutHandle, color);
    }

    public static void ResetColor()
    {
        SetConsoleTextAttribute(StdoutHandle, _defaultAttributes);
    }
}

public class ConsoleRedirector : System.IO.TextWriter
{
    private readonly ushort _color;
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public ConsoleRedirector(ushort color)
    {
        _color = color;
    }

    public override void Write(string value)
    {
        if (!string.IsNullOrEmpty(value))
            RuntimeDebug.EnqueueRaw(value, _color); // 丢进队列，绝不阻塞当前线程
    }

    public override void WriteLine(string value) => Write(value + "\n");
    public override void Write(char value) => Write(value.ToString());
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
}

/// <summary>
/// Static console output with color-coded log levels and timestamp prefix.
/// Call RuntimeDebug.LogInfo("message") directly from anywhere.
/// </summary>
public static class RuntimeDebug
{
    // AppDomain keys — use BCL types only to avoid cross-assembly cast failures
    private const string KEY_INIT = "RuntimeDebug.Initialized";
    private const string KEY_OWNER = "RuntimeDebug.Owner";
    private const string KEY_QUEUE = "RuntimeDebug.Queue";
    private const string KEY_THREAD = "RuntimeDebug.WriterThread";

    private static ConcurrentDictionary<string, object> SharedData =>
        (ConcurrentDictionary<string, object>)AppDomain.CurrentDomain.GetData("RuntimeDebug.Shared")
        ?? InitSharedData();

    private static ConcurrentDictionary<string, object> InitSharedData()
    {
        var d = new ConcurrentDictionary<string, object>();
        AppDomain.CurrentDomain.SetData("RuntimeDebug.Shared", d);
        return d;
    }

    private static bool _initialized
    {
        get => SharedData.TryGetValue(KEY_INIT, out var v) && (bool)v;
        set => SharedData[KEY_INIT] = value;
    }

    private static bool _isOwner
    {
        get => SharedData.TryGetValue(KEY_OWNER, out var v) && (bool)v;
        set => SharedData[KEY_OWNER] = value;
    }

    private static bool _crashDetected
    {
        get => SharedData.TryGetValue("RuntimeDebug.CrashDetected", out var v) && (bool)v;
        set => SharedData["RuntimeDebug.CrashDetected"] = value;
    }

    private static BlockingCollection<(string, ushort)> _writeQueue
    {
        get
        {
            if (SharedData.TryGetValue(KEY_QUEUE, out var q))
                return (BlockingCollection<(string, ushort)>)q;
            var nq = new BlockingCollection<(string, ushort)>(new ConcurrentQueue<(string, ushort)>());
            SharedData[KEY_QUEUE] = nq;
            return nq;
        }
    }

    private static Thread _writerThread
    {
        get => SharedData.TryGetValue(KEY_THREAD, out var t) ? (Thread)t : null;
        set => SharedData[KEY_THREAD] = value;
    }

    // Per-level color for the [LEVEL] tag
    private const ushort TimestampColor = 0x0008;
    private const ushort WarnColor = ConsoleAllocator.FOREGROUND_YELLOW | ConsoleAllocator.FOREGROUND_INTENSITY;
    private const ushort ErrorColor = ConsoleAllocator.FOREGROUND_RED | ConsoleAllocator.FOREGROUND_INTENSITY;
    private const ushort ExceptionColor = ConsoleAllocator.FOREGROUND_MAGENTA | ConsoleAllocator.FOREGROUND_INTENSITY;
    private const ushort InfoColor = ConsoleAllocator.FOREGROUND_GREEN | ConsoleAllocator.FOREGROUND_INTENSITY;
    private const ushort DebugColor = ConsoleAllocator.FOREGROUND_CYAN;
    private const ushort CrashBannerColor = ConsoleAllocator.FOREGROUND_RED | ConsoleAllocator.FOREGROUND_BLUE | ConsoleAllocator.FOREGROUND_INTENSITY;

    public static bool IsInitialized => _initialized;
    public static bool IsCrashDetected => _crashDetected;

    public static void EnqueueRaw(string message, ushort color)
    {
        if (_initialized) _writeQueue.Add((message, color));
    }

    /// <summary>
    /// Allocates a new console window and binds stdout/stderr.
    /// Across multiple DLLs in the same process, only the first call opens a window;
    /// subsequent callers share the same queue and writer thread via AppDomain data.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        if (!ConsoleAllocator.AllocConsole())
        {
            if (!ConsoleAllocator.AttachConsole(ConsoleAllocator.ATTACH_PARENT_PROCESS))
                return;
        }

        ConsoleAllocator.ApplyConsoleStyle("Runtime Debug");

        Console.SetOut(new ConsoleRedirector(InfoColor));
        Console.SetError(new ConsoleRedirector(ErrorColor));

        ConsoleAllocator.CacheDefaultAttributes();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _writerThread = new Thread(ConsoleWriterLoop)
        {
            Name = "RuntimeDebug.Writer",
            IsBackground = true
        };
        _writerThread.Start();

        _initialized = true;
        _isOwner = true;
        WriteColoredLine("[RuntimeDebug] Console initialized.", InfoColor);
    }

    /// <summary>
    /// Releases the console window on normal shutdown.
    /// On crash, the console stays open until the user presses a key.
    /// </summary>
    public static void Shutdown()
    {
        if (!_initialized) return;
        if (!_isOwner) return; // only the owning DLL may shut down the console
        if (_crashDetected) return;

        WriteColoredLine("[RuntimeDebug] Console shutting down.", InfoColor);

        _writeQueue.CompleteAdding();
        _writerThread?.Join(2000);

        ConsoleAllocator.FreeConsole();
        _initialized = false;
    }

    private const short BUFFER_HEIGHT = 2000;

    private static void ConsoleWriterLoop()
    {
        var handle = ConsoleAllocator.StdoutHandle;
        try
        {
            foreach (var (text, color) in _writeQueue.GetConsumingEnumerable())
            {
                if (ConsoleAllocator.GetConsoleScreenBufferInfo(handle, out var info))
                {
                    if (info.dwSize.Y < BUFFER_HEIGHT)
                    {
                        var tempSize = new ConsoleAllocator.COORD { X = info.dwSize.X, Y = BUFFER_HEIGHT };
                        ConsoleAllocator.SetConsoleScreenBufferSize(handle, tempSize);
                    }

                    ConsoleAllocator.SetColor(color);
                    uint written;
                    ConsoleAllocator.WriteConsoleW(handle, text, (uint)text.Length, out written, IntPtr.Zero);

                    ConsoleAllocator.GetConsoleScreenBufferInfo(handle, out info);

                    short windowHeight = (short)(info.srWindow.Bottom - info.srWindow.Top + 1);

                    short finalBufferHeight = (short)(info.dwCursorPosition.Y + 1);

                    if (finalBufferHeight < windowHeight)
                    {
                        finalBufferHeight = windowHeight;
                    }
                    if (finalBufferHeight > BUFFER_HEIGHT)
                    {
                        finalBufferHeight = BUFFER_HEIGHT;
                    }

                    if (info.dwSize.Y != finalBufferHeight)
                    {
                        var finalSize = new ConsoleAllocator.COORD { X = info.dwSize.X, Y = finalBufferHeight };
                        ConsoleAllocator.SetConsoleScreenBufferSize(handle, finalSize);

                        ConsoleAllocator.GetConsoleScreenBufferInfo(handle, out info);
                    }

                    if (info.srWindow.Bottom < info.dwCursorPosition.Y)
                    {
                        if (info.srWindow.Bottom >= info.dwCursorPosition.Y - 3)
                        {
                            short targetTop = (short)(finalBufferHeight - windowHeight);
                            if (targetTop < 0) targetTop = 0;

                            var newWindow = new ConsoleAllocator.SMALL_RECT
                            {
                                Left = 0,
                                Top = targetTop,
                                Right = (short)(info.dwSize.X - 1),
                                Bottom = (short)(finalBufferHeight - 1)
                            };
                            ConsoleAllocator.SetConsoleWindowInfo(handle, true, ref newWindow);
                        }
                    }
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _crashDetected = true;

        var ex = e.ExceptionObject as Exception;
        bool isTerminating = e.IsTerminating;

        if (!_initialized) return;

        try
        {
            // Crash banner
            WriteLineRaw($"{"".PadRight(60, '=')}", CrashBannerColor);
            WriteLineRaw("  GAME CRASH DETECTED", CrashBannerColor);
            WriteLineRaw($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", CrashBannerColor);
            WriteLineRaw($"  Terminating: {isTerminating}", CrashBannerColor);
            if (ex != null)
            {
                WriteLineRaw($"  Exception: {ex.GetType().FullName}", CrashBannerColor);
                WriteLineRaw($"  Message: {ex.Message}", CrashBannerColor);
            }
            else if (e.ExceptionObject != null)
            {
                WriteLineRaw($"  Exception: {e.ExceptionObject}", CrashBannerColor);
            }
            WriteLineRaw($"{"".PadRight(60, '=')}", CrashBannerColor);
            WriteLineRaw("", InfoColor);

            // Full stack trace
            if (ex != null)
            {
                WriteLineRaw("--- Stack Trace ---", ErrorColor);
                WriteLineRaw(ex.StackTrace ?? "(no stack trace)", InfoColor);
                WriteLineRaw("", InfoColor);

                // Inner exceptions
                var inner = ex.InnerException;
                while (inner != null)
                {
                    WriteLineRaw($"--- Inner: {inner.GetType().FullName}: {inner.Message}", ErrorColor);
                    WriteLineRaw(inner.StackTrace ?? "(no stack trace)", InfoColor);
                    WriteLineRaw("", InfoColor);
                    inner = inner.InnerException;
                }
            }

            WriteLineRaw("The console will remain open. Press any key to close...", InfoColor);

            // Block until user acknowledges — keeps the process alive
            Console.ReadKey(intercept: true);
        }
        catch
        {
            // Best-effort: if console I/O fails, nothing more we can do
        }
    }

    public static void LogWarning(string message) => WriteLevelLine("WARN", message, WarnColor);
    public static void LogError(string message) => WriteLevelLine("ERROR", StripHexLines(message), ErrorColor);
    public static void LogInfo(string message) => WriteLevelLine("INFO", message, InfoColor);
    public static void LogDebug(string message) => WriteLevelLine("DEBUG", message, DebugColor);

    public static void LogException(Exception ex)
    {
        WriteLevelLine("EXCEPTION", StripHexLines(ex?.ToString() ?? "null"), ExceptionColor);
    }

    public static void LogException(string message)
    {
        WriteLevelLine("EXCEPTION", StripHexLines(message), ExceptionColor);
    }

    /// <summary>Removes native 0x... address lines from error/exception output.</summary>
    private static string StripHexLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lines = text.Split('\n');
        var sb = new System.Text.StringBuilder(text.Length);
        bool hasContent = false;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("0x"))
                continue;
            if (hasContent) sb.Append('\n');
            sb.Append(t);
            hasContent = true;
        }
        return sb.Length > 0 ? sb.ToString() : text;
    }

    /// <summary>
    /// Write raw text with a specific console color.
    /// </summary>
    public static void WriteColored(string message, ushort color)
    {
        WriteColoredLine(message, color);
    }

    private static void WriteLevelLine(string level, string message, ushort levelColor)
    {
        if (!_initialized) return;
        _writeQueue.Add(($"[{DateTime.Now:HH:mm:ss.fff}] ", TimestampColor));
        _writeQueue.Add(($"[{level}] {message}\n", levelColor));
    }

    private static void WriteColoredLine(string line, ushort color)
    {
        if (!_initialized) return;
        _writeQueue.Add((line + "\n", color));
    }

    private static void WriteLineRaw(string line, ushort color)
    {
        _writeQueue.Add((line + "\n", color));
    }
}

/// <summary>
/// MonoBehaviour that hooks Unity's log output via Application.logMessageReceived
/// and redirects to the debug console. Attach to a DontDestroyOnLoad GameObject.
/// </summary>
public class UnityLogInterceptor : MonoBehaviour
{
    private bool _loggedStart;

    /// <summary>
    /// Creates the interceptor on a persistent GameObject, initializes the console, and starts listening.
    /// </summary>
    public static UnityLogInterceptor Create()
    {
        var go = new GameObject("RuntimeDebug_Interceptor") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(go);
        var interceptor = go.AddComponent<UnityLogInterceptor>();
        return interceptor;
    }

    private void Awake()
    {
        RuntimeDebug.Initialize();
    }

    private void OnEnable()
    {
        Application.logMessageReceived += OnLogMessageReceived;
        if (!_loggedStart)
        {
            _loggedStart = true;
            RuntimeDebug.LogInfo("[UnityLogInterceptor] Capturing Unity log output via Application.logMessageReceived.");
        }
    }

    private void OnDisable()
    {
        Application.logMessageReceived -= OnLogMessageReceived;
    }

    private static void OnLogMessageReceived(string logString, string stackTrace, LogType type)
    {
        switch (type)
        {
            case LogType.Error:
            case LogType.Assert:
                RuntimeDebug.LogError(stackTrace.Length > 0 ? $"{logString}\n{stackTrace}" : logString);
                break;
            case LogType.Exception:
                RuntimeDebug.LogException(
                    stackTrace.Length > 0 ? $"{logString}\n{stackTrace}" : logString);
                break;
            case LogType.Warning:
                RuntimeDebug.LogWarning(logString);
                break;
            default:
                RuntimeDebug.LogInfo(logString);
                break;
        }
    }

    private void OnDestroy()
    {
        RuntimeDebug.Shutdown();
    }
}

/// <summary>
/// Dispatches actions from background threads or coroutines onto the Unity main thread.
/// Safe to call from any thread.
/// </summary>
public class MainThreadExecutor : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();
    private static MainThreadExecutor _instance;
    private static readonly object _initLock = new object();

    public static MainThreadExecutor Instance
    {
        get
        {
            EnsureExists();
            return _instance;
        }
    }

    /// <summary>
    /// Creates the singleton instance on a persistent GameObject if it doesn't exist.
    /// </summary>
    public static void EnsureExists()
    {
        if (_instance != null) return;

        lock (_initLock)
        {
            if (_instance != null) return;

            var go = new GameObject("RuntimeDebug_Executor") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadExecutor>();
        }
    }

    /// <summary>
    /// Enqueue an action to run on the main thread. Fire-and-forget.
    /// </summary>
    public static void Run(Action action)
    {
        if (action == null) return;
        _actionQueue.Enqueue(action);
    }

    /// <summary>
    /// Enqueue an action and return a Task that completes when the action has executed on the main thread.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        _actionQueue.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Enqueue an action and return a Task that completes when the action has executed on the main thread.
    /// </summary>
    public static Task RunAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        _actionQueue.Enqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Returns true if the caller is on the Unity main thread.
    /// </summary>
    public static bool IsMainThread => _instance != null && Thread.CurrentThread.ManagedThreadId == 1;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
    }

    private void Update()
    {
        // Process up to a reasonable batch per frame to avoid stalling
        for (int i = 0; i < 256; i++)
        {
            if (!_actionQueue.TryDequeue(out var action))
                break;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                RuntimeDebug.LogError($"[MainThreadExecutor] Unhandled exception in queued action: {ex}");
            }
        }
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }
}

#pragma warning restore CS0612
#pragma warning restore CS0618
