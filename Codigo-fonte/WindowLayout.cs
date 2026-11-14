using System;
using System.Collections.Generic;
#if DIAGNOSTICS
using System.Diagnostics;
#endif
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class WindowLayout
{
    private const string WindowProperty = "Splinter.WindowLayout";
    private const int WindowStyle = -20;
    private const long ToolWindow = 0x80;
    private const long NoActivate = 0x08000000;
    private const uint AsyncPlacement = 4;
    private const int DwmCloaked = 14;
    private const int DwmFrameBounds = 9;
    private const int FormatVersion = 3;
    private const uint NearestMonitor = 2;
    private const uint ShowWithoutActivation = 4;
    private const uint Maximized = 3;
    private const uint InvalidateWindowAndFrame = 0x0001 | 0x0004 | 0x0080 | 0x0400;
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Splinter", "windows.bin");

#if DIAGNOSTICS
    internal static void Report()
    {
        EnumWindows((window, unused) =>
        {
            if (!IsWindowVisible(window))
                return true;
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                {
                    var placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
                    GetWindowPlacement(window, ref placement);
                    MonitorInfo monitor = GetMonitor(MonitorFromWindow(window, NearestMonitor));
                    Rect frame;
                    TryGetVisibleFrame(window, out frame);
                    Console.WriteLine("{0} {1:X} normal=({2},{3}) state={4} eligible={5} token={6:X} monitor=({7},{8}) frame={9},{10},{11},{12}",
                        process.ProcessName, window.ToInt64(), placement.Left, placement.Top,
                        placement.ShowCommand, IsApplicationWindow(window), GetProp(window, WindowProperty).ToInt64(),
                        monitor.Bounds.Left, monitor.Bounds.Top, frame.Left, frame.Top, frame.Right, frame.Bottom);
                }
            }
            catch (ArgumentException) { }
            return true;
        }, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
#endif

    internal static void Save()
    {
        var windows = new List<SavedWindow>();
        EnumWindows((window, unused) =>
        {
            if (!IsApplicationWindow(window))
                return true;

            var placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
            if (!GetWindowPlacement(window, ref placement))
                return true;

            IntPtr monitor = MonitorFromWindow(window, NearestMonitor);
            MonitorInfo display = GetMonitor(monitor);
            if (display.Bounds.Right <= display.Bounds.Left)
                return true;

            // A property belongs to this window instance, even if Windows reuses the handle later.
            var token = new IntPtr(BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) | 1L);
            Rect frame = new Rect();
            if (placement.ShowCommand == Maximized)
                TryGetVisibleFrame(window, out frame);
            if (SetProp(window, WindowProperty, token))
                windows.Add(new SavedWindow
                {
                    Handle = window, Token = token, Placement = placement,
                    MonitorX = display.Bounds.Left, MonitorY = display.Bounds.Top, VisibleFrame = frame
                });
            return true;
        }, IntPtr.Zero);

        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath));
        string temporaryPath = StoragePath + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temporaryPath)))
        {
            writer.Write(FormatVersion);
            writer.Write(windows.Count);
            foreach (SavedWindow window in windows)
                window.Write(writer);
        }

        if (File.Exists(StoragePath))
            File.Replace(temporaryPath, StoragePath, null);
        else
            File.Move(temporaryPath, StoragePath);
    }

    internal static void Restore()
    {
        if (!File.Exists(StoragePath))
            return;

        var windows = new List<SavedWindow>();
        using (var reader = new BinaryReader(File.OpenRead(StoragePath)))
        {
            int version = reader.ReadInt32();
            int count = reader.ReadInt32();
            int recordSize = version == 1 ? 56 : version == 2 ? 64 : 80;
            if (version < 1 || version > FormatVersion || count < 0
                || reader.BaseStream.Length != 8 + (long)recordSize * count)
                throw new InvalidDataException("As telas voltaram ao modo estendido, mas o registro de posições das janelas está inválido.");

            for (int index = 0; index < count; index++)
                windows.Add(SavedWindow.Read(reader, version));
        }

        if (windows.Count != 0)
        {
            Thread.Sleep(400);
            foreach (SavedWindow window in windows)
                RestoreWindow(window, true);

            // Display notifications can cause Windows or an application to relocate windows
            // after the first move. Check the actual monitor while the desktop settles.
            for (int pass = 0; pass < 12; pass++)
            {
                Thread.Sleep(250);
                foreach (SavedWindow window in windows)
                    RestoreWindow(window, false);
            }
        }
        foreach (SavedWindow window in windows)
        {
            if (GetProp(window.Handle, WindowProperty) != window.Token)
                continue;

            // Repaint the edge as well as the contents once display changes have settled.
            if (window.Placement.ShowCommand == Maximized && IsZoomed(window.Handle))
                RedrawWindow(window.Handle, IntPtr.Zero, IntPtr.Zero, InvalidateWindowAndFrame);
            RemoveProp(window.Handle, WindowProperty);
        }
        File.Delete(StoragePath);
    }

    private static void RestoreWindow(SavedWindow window, bool firstPass)
    {
        if (GetProp(window.Handle, WindowProperty) != window.Token || !IsApplicationWindow(window.Handle))
            return;

        MonitorInfo current = GetMonitor(MonitorFromWindow(window.Handle, NearestMonitor));
        bool wrongMonitor = current.Bounds.Left != window.MonitorX || current.Bounds.Top != window.MonitorY;
        var actual = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
        if (!GetWindowPlacement(window.Handle, ref actual))
            return;

        Placement placement = window.Placement;
        bool wrongState = placement.ShowCommand != actual.ShowCommand;
        Rect frame;
        bool wrongFrame = placement.ShowCommand == Maximized && !wrongState
            && window.VisibleFrame.Right > window.VisibleFrame.Left
            && TryGetVisibleFrame(window.Handle, out frame)
            && !frame.Matches(window.VisibleFrame);
        bool wrongPosition = actual.Left != placement.Left || actual.Top != placement.Top
            || actual.Right != placement.Right || actual.Bottom != placement.Bottom;
        bool isMaximized = placement.ShowCommand == Maximized;
        if (!wrongMonitor && !wrongFrame && !(firstPass && (wrongState || (!isMaximized && wrongPosition))))
            return;

        placement.Flags |= AsyncPlacement;
        if (isMaximized)
        {
            // Keep the original restore rectangle in the snapshot. Translate it only if
            // needed to put a maximized window back on its original monitor.
            var normalBounds = new Rect
            {
                Left = placement.Left, Top = placement.Top, Right = placement.Right, Bottom = placement.Bottom
            };
            var destinationPoint = new Point { X = window.MonitorX, Y = window.MonitorY };
            IntPtr destination = MonitorFromPoint(destinationPoint, NearestMonitor);
            IntPtr normalMonitor = MonitorFromRect(ref normalBounds, NearestMonitor);
            if (normalMonitor != destination)
            {
                MonitorInfo target = GetMonitor(destination);
                MonitorInfo source = GetMonitor(normalMonitor);
                int dx = target.WorkArea.Left - source.WorkArea.Left;
                int dy = target.WorkArea.Top - source.WorkArea.Top;
                placement.Left += dx;
                placement.Right += dx;
                placement.Top += dy;
                placement.Bottom += dy;
            }

            // Place the restored window on the destination before maximizing it there.
            Placement restored = placement;
            restored.ShowCommand = ShowWithoutActivation;
            SetWindowPlacement(window.Handle, ref restored);
        }
        SetWindowPlacement(window.Handle, ref placement);
    }

    private static bool TryGetVisibleFrame(IntPtr window, out Rect bounds)
    {
        if (DwmGetFrameBounds(window, DwmFrameBounds, out bounds, Marshal.SizeOf(typeof(Rect))) == 0)
            return true;

        bounds = new Rect();
        return false;
    }

    private static MonitorInfo GetMonitor(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        GetMonitorInfo(monitor, ref info);
        return info;
    }

    private static bool IsApplicationWindow(IntPtr window)
    {
        if (!IsWindowVisible(window) || window == GetShellWindow())
            return false;

        long style = GetWindowLongPtr(window, WindowStyle).ToInt64();
        if ((style & (ToolWindow | NoActivate)) != 0)
            return false;

        int cloaked;
        return DwmGetWindowAttribute(window, DwmCloaked, out cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private sealed class SavedWindow
    {
        internal IntPtr Handle, Token;
        internal Placement Placement;
        internal int MonitorX, MonitorY;
        internal Rect VisibleFrame;

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Handle.ToInt64());
            writer.Write(Token.ToInt64());
            writer.Write(Placement.Flags);
            writer.Write(Placement.ShowCommand);
            writer.Write(Placement.MinX);
            writer.Write(Placement.MinY);
            writer.Write(Placement.MaxX);
            writer.Write(Placement.MaxY);
            writer.Write(Placement.Left);
            writer.Write(Placement.Top);
            writer.Write(Placement.Right);
            writer.Write(Placement.Bottom);
            writer.Write(MonitorX);
            writer.Write(MonitorY);
            writer.Write(VisibleFrame.Left);
            writer.Write(VisibleFrame.Top);
            writer.Write(VisibleFrame.Right);
            writer.Write(VisibleFrame.Bottom);
        }

        internal static SavedWindow Read(BinaryReader reader, int version)
        {
            var window = new SavedWindow
            {
                Handle = new IntPtr(reader.ReadInt64()),
                Token = new IntPtr(reader.ReadInt64()),
                Placement = new Placement
                {
                    Length = Marshal.SizeOf(typeof(Placement)),
                    Flags = reader.ReadUInt32(),
                    ShowCommand = reader.ReadUInt32(),
                    MinX = reader.ReadInt32(),
                    MinY = reader.ReadInt32(),
                    MaxX = reader.ReadInt32(),
                    MaxY = reader.ReadInt32(),
                    Left = reader.ReadInt32(),
                    Top = reader.ReadInt32(),
                    Right = reader.ReadInt32(),
                    Bottom = reader.ReadInt32()
                }
            };
            if (version == 1)
            {
                var bounds = new Rect
                {
                    Left = window.Placement.Left, Top = window.Placement.Top,
                    Right = window.Placement.Right, Bottom = window.Placement.Bottom
                };
                MonitorInfo monitor = GetMonitor(MonitorFromRect(ref bounds, NearestMonitor));
                window.MonitorX = monitor.Bounds.Left;
                window.MonitorY = monitor.Bounds.Top;
            }
            else
            {
                window.MonitorX = reader.ReadInt32();
                window.MonitorY = reader.ReadInt32();
            }
            if (version >= 3)
                window.VisibleFrame = new Rect
                {
                    Left = reader.ReadInt32(),
                    Top = reader.ReadInt32(),
                    Right = reader.ReadInt32(),
                    Bottom = reader.ReadInt32()
                };
            return window;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left, Top, Right, Bottom;

        internal bool Matches(Rect other)
        {
            return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Bounds, WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect bounds, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point position, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        internal int Length;
        internal uint Flags, ShowCommand;
        internal int MinX, MinY, MaxX, MaxY;
        internal int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr unused);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr unused);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr window, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr window, string name);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetFrameBounds(IntPtr window, int attribute, out Rect bounds, int size);
}
