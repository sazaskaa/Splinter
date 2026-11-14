using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Splinter
{
    private const string Aoc = "AOC2355";
    private const string Asus = "AUS2782";
    private const uint AllPaths = 1;
    private const uint ActivePaths = 2;
    private const uint PathActive = 1;
    private const uint Extend = 4;
    private const uint SuppliedConfig = 0x20;
    private const uint Validate = 0x40;
    private const uint Apply = 0x80;
    private const uint InvalidIndex = uint.MaxValue;
    private const int InsufficientBuffer = 122;
    private const string ResumeEvent = @"Local\Splinter.RestoreDisplayPort";

    [STAThread]
    private static int Main(string[] args)
    {
        string command = args.Length == 0 ? "--toggle" : args[0];
        try
        {
            using (var mutex = new Mutex(false, @"Local\Splinter.MonitorSwitch"))
            {
                try
                {
                    if (!mutex.WaitOne(0))
                        return 0;
                }
                catch (AbandonedMutexException)
                {
                    // Windows granted ownership after the previous process exited.
                }

                InputSession session;
                try
                {
                    if ((command == "--toggle" || command == "--normal") && RequestReturn())
                        return 0;
                    session = Run(command);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }

                if (session != null)
                {
                    using (session)
                    {
                        session.ReturnRequested.WaitOne();
                        try { mutex.WaitOne(); }
                        catch (AbandonedMutexException) { }
                        try
                        {
                            session.ReturnRequested.Dispose();
                            RestoreExtended(ReadConfiguration(ActivePaths), session.Input);
                        }
                        finally
                        {
                            mutex.ReleaseMutex();
                        }
                    }
                }
                return 0;
            }
        }
        catch (Exception error)
        {
            if (args.Length == 0)
                MessageBox.Show(error.Message, "Splinter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                Console.WriteLine(error.Message);
            return 1;
        }
    }

    private static bool RequestReturn()
    {
        EventWaitHandle signal;
        if (!EventWaitHandle.TryOpenExisting(ResumeEvent, out signal))
            return false;

        using (signal)
            return signal.Set();
    }

    private static InputSession Run(string command)
    {
        Configuration current = ReadConfiguration(ActivePaths);
#if DIAGNOSTICS
        if (command == "--windows")
        {
            WindowLayout.Report();
            return null;
        }
        if (command == "--input")
        {
            using (MonitorInput input = IsExtended(current) ? MonitorInput.OpenPrimary() : null)
                Console.WriteLine(input == null ? null : input.Read());
            return null;
        }
#endif
        if (command == "--status")
        {
            foreach (Path path in current.Paths)
            {
                Mode source = current.Modes[path.Source.ModeIndex];
                Console.WriteLine("{0}: {1}x{2} em ({3}, {4}), {5:F2} Hz",
                    MonitorId(path.Target), source.Width, source.Height, source.X, source.Y,
                    (double)path.Target.RefreshRate.Numerator / path.Target.RefreshRate.Denominator);
            }
            return null;
        }

        bool aocOnly = IsAocOnly(current);
        if (command == "--normal" || (command == "--toggle" && aocOnly))
        {
            RestoreExtended(current, null);
            return null;
        }

        if (command != "--ps5" && command != "--toggle" && command != "--validate")
            throw new InvalidOperationException("Comando desconhecido.");
        if (aocOnly)
            return null;
        if (!IsExtended(current))
            throw new InvalidOperationException(
                "Nas configurações de tela do Windows, estenda as telas com o Asus como principal e o AOC à esquerda. Depois abra o Splinter novamente.");

        Configuration single = AocConfiguration(current);
        Set(single, Validate);
        if (command == "--validate")
            return null;

        MonitorInput inputControl = MonitorInput.OpenPrimary();
        if (inputControl == null)
            throw new InvalidOperationException("O Windows não conseguiu abrir o controle DDC/CI do Asus.");

        var session = new InputSession(inputControl);
        bool switchRequested = false;
        try
        {
            WindowLayout.Save();
            switchRequested = true;
            if (!inputControl.Set(MonitorInput.Hdmi))
                throw new InvalidOperationException("O Asus não aceitou o comando para HDMI. Confira se DDC/CI está habilitado no monitor.");
            Set(single, Apply);
            VerifyOrRestore(current, IsAocOnly, "O Windows não confirmou que apenas o AOC está ativo.");
            return session;
        }
        catch
        {
            try
            {
                if (switchRequested)
                    RestoreExtended(ReadConfiguration(ActivePaths), inputControl);
            }
            finally
            {
                session.Dispose();
            }
            throw;
        }
    }

    private static void RestoreExtended(Configuration previous, MonitorInput retainedInput)
    {
        if (retainedInput != null && retainedInput.Set(MonitorInput.DisplayPort))
            Thread.Sleep(350);

        if (!IsExtended(previous))
        {
            for (int attempt = 0; attempt < 8 && !AsusAvailable(); attempt++)
                Thread.Sleep(250);
            if (!AsusAvailable())
                throw new InvalidOperationException(
                    "O Windows ainda não detectou o Asus. Selecione DisplayPort no Asus e abra o Splinter novamente.");

            Check(SetDisplayConfig(0, null, 0, null, Apply | Extend), "restaurar as telas estendidas");
            VerifyOrRestore(previous, IsExtended, "O Windows não confirmou o Asus como principal com o AOC à esquerda.");
        }

        // Reopen the channel if Windows invalidated the old handle during the topology change.
        bool confirmed = false;
        for (int attempt = 0; attempt < 6 && !confirmed; attempt++)
        {
            using (MonitorInput input = MonitorInput.OpenPrimary())
            {
                if (input != null)
                {
                    confirmed = input.Read() == MonitorInput.DisplayPort;
                    if (!confirmed)
                        input.Set(MonitorInput.DisplayPort);
                }
            }
            if (!confirmed)
                Thread.Sleep(250);
        }
        WindowLayout.Restore();
        if (!confirmed)
            throw new InvalidOperationException("O Windows restaurou as duas telas, mas não confirmou a entrada do Asus. Se necessário, selecione DisplayPort no monitor.");
    }

    private static bool AsusAvailable()
    {
        return ReadConfiguration(AllPaths).Paths.Any(
            path => path.Target.Available != 0 && MonitorId(path.Target) == Asus);
    }

    private sealed class InputSession : IDisposable
    {
        internal readonly MonitorInput Input;
        internal readonly EventWaitHandle ReturnRequested;

        internal InputSession(MonitorInput input)
        {
            Input = input;
            try
            {
                ReturnRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ResumeEvent);
            }
            catch
            {
                Input.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            ReturnRequested.Dispose();
            Input.Dispose();
        }
    }

    private static void VerifyOrRestore(Configuration previous, Func<Configuration, bool> verify, string message)
    {
        try
        {
            if (verify(ReadConfiguration(ActivePaths)))
                return;
        }
        catch (Win32Exception)
        {
            // Attempt to restore the previous layout if Windows cannot confirm the new one.
        }

        Set(previous, Apply);
        throw new InvalidOperationException(message + " O Splinter reaplicou a configuração anterior.");
    }

    private static bool IsAocOnly(Configuration config)
    {
        return config.Paths.Length == 1 && MonitorId(config.Paths[0].Target) == Aoc;
    }

    private static bool IsExtended(Configuration config)
    {
        if (config.Paths.Length != 2)
            return false;

        bool asusPrimary = false;
        bool aocLeft = false;
        foreach (Path path in config.Paths)
        {
            if (path.Source.ModeIndex >= config.Modes.Length)
                return false;

            Mode source = config.Modes[path.Source.ModeIndex];
            string id = MonitorId(path.Target);
            asusPrimary |= id == Asus && source.X == 0 && source.Y == 0;
            aocLeft |= id == Aoc && source.X < 0;
        }
        return asusPrimary && aocLeft;
    }

    private static Configuration AocConfiguration(Configuration current)
    {
        Path path = current.Paths.Single(item => MonitorId(item.Target) == Aoc);
        Mode source = current.Modes[path.Source.ModeIndex];
        source.X = 0;
        source.Y = 0;
        var modes = new[] { source };
        path.Source.ModeIndex = 0;
        if (path.Target.ModeIndex != InvalidIndex)
        {
            modes = new[] { source, current.Modes[path.Target.ModeIndex] };
            path.Target.ModeIndex = 1;
        }
        path.Flags |= PathActive;
        return new Configuration(new[] { path }, modes);
    }

    private static Configuration ReadConfiguration(uint flags)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            uint pathCount, modeCount;
            Check(GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount), "consultar as telas");
            var paths = new Path[pathCount];
            var modes = new Mode[modeCount];
            int result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == InsufficientBuffer)
                continue;

            Check(result, "ler a configuração de tela");
            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            return new Configuration(paths, modes);
        }
        throw new InvalidOperationException("A lista de telas está mudando. Aguarde alguns segundos e tente novamente.");
    }

    private static string MonitorId(Target target)
    {
        var name = new TargetName
        {
            Type = 2,
            Size = (uint)Marshal.SizeOf(typeof(TargetName)),
            Adapter = target.Adapter,
            Id = target.Id
        };
        if (DisplayConfigGetDeviceInfo(ref name) != 0 || string.IsNullOrEmpty(name.DevicePath))
            return "";

        string[] segments = name.DevicePath.Split('#');
        return segments.Length > 1 ? segments[1].ToUpperInvariant() : "";
    }

    private static void Set(Configuration config, uint operation)
    {
        Check(SetDisplayConfig((uint)config.Paths.Length, config.Paths,
            (uint)config.Modes.Length, config.Modes, SuppliedConfig | operation),
            operation == Validate ? "validar o modo PS5" : "aplicar a configuração de tela");
    }

    private static void Check(int error, string operation)
    {
        if (error != 0)
            throw new Win32Exception(error, string.Format("O Windows não conseguiu {0} (código {1}). {2}",
                operation, error, new Win32Exception(error).Message));
    }

    private sealed class Configuration
    {
        internal readonly Path[] Paths;
        internal readonly Mode[] Modes;

        internal Configuration(Path[] paths, Mode[] modes)
        {
            Paths = paths;
            Modes = modes;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator, Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Source
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Target
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling;
        public Rational RefreshRate;
        public uint ScanlineOrdering;
        public int Available; // Win32 BOOL
        public uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Path
    {
        public Source Source;
        public Target Target;
        public uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO has a union at offset 16. The opaque fields
    // preserve the full target mode when passing it back to Windows.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct Mode
    {
        [FieldOffset(16)] public ulong Alignment;
        [FieldOffset(16)] public uint Width;
        [FieldOffset(20)] public uint Height;
        [FieldOffset(28)] public int X;
        [FieldOffset(32)] public int Y;
        [FieldOffset(56)] public ulong TargetModeEnd;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public uint Type, Size;
        public Luid Adapter;
        public uint Id, Flags, Technology;
        public ushort EdidManufacturer, EdidProduct;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] Path[] paths,
        ref uint modeCount, [Out] Mode[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] Path[] paths,
        uint modeCount, [In] Mode[] modes, uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetName name);
}
