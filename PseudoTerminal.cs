using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

static class PseudoTerminal
{
    public static CommandOutput Run(string fileName, string[] arguments, string? cwd, int width, int height)
    {
        width = Math.Clamp(width, 1, 512);
        height = Math.Clamp(height, 1, 512);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsPseudoTerminal.Run(fileName, arguments, cwd, width, height);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            return UnixPseudoTerminal.Run(fileName, arguments, cwd, width, height);

        throw new PlatformNotSupportedException("PTY recording is not supported on this operating system.");
    }
}

static class WindowsPseudoTerminal
{
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint INFINITE = 0xFFFFFFFF;

    public static CommandOutput Run(string fileName, string[] arguments, string? cwd, int width, int height)
    {
        CreatePipePair(out var inputRead, out var inputWrite, inheritRead: true, inheritWrite: false);
        CreatePipePair(out var outputRead, out var outputWrite, inheritRead: false, inheritWrite: true);
        using var inputReadHandle = inputRead;
        using var inputWriteHandle = inputWrite;
        using var outputReadHandle = outputRead;
        using var outputWriteHandle = outputWrite;

        var size = new COORD((short)width, (short)height);
        var hpc = IntPtr.Zero;
        var hr = CreatePseudoConsole(size, inputReadHandle.DangerousGetHandle(), outputWriteHandle.DangerousGetHandle(), 0, out hpc);
        if (hr != 0)
            throw new Win32Exception(hr, "CreatePseudoConsole failed");

        var attrList = IntPtr.Zero;
        var processInfo = new PROCESS_INFORMATION();
        try
        {
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, out var attrListSize);
            attrList = Marshal.AllocHGlobal((IntPtr)attrListSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, out attrListSize))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed");

            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed");

            var startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startupInfo.StartupInfo.hStdInput = inputReadHandle.DangerousGetHandle();
            startupInfo.StartupInfo.hStdOutput = outputWriteHandle.DangerousGetHandle();
            startupInfo.StartupInfo.hStdError = outputWriteHandle.DangerousGetHandle();
            startupInfo.lpAttributeList = attrList;

            var commandLine = QuoteCommandLine(fileName, arguments);
            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero,
                    string.IsNullOrWhiteSpace(cwd) ? null : cwd,
                    ref startupInfo,
                    out processInfo))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess failed");
            inputReadHandle.Dispose();
            outputWriteHandle.Dispose();
            inputWriteHandle.Dispose();

            var outputTask = Task.Run(() => ReadAll(outputReadHandle));
            WaitForSingleObject(processInfo.hProcess, INFINITE);
            if (!GetExitCodeProcess(processInfo.hProcess, out var exitCode))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetExitCodeProcess failed");
            ClosePseudoConsole(hpc);
            hpc = IntPtr.Zero;
            var output = outputTask.GetAwaiter().GetResult();

            return new CommandOutput(output, "", unchecked((int)exitCode), true);
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero)
                CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero)
                CloseHandle(processInfo.hProcess);
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (hpc != IntPtr.Zero)
                ClosePseudoConsole(hpc);
        }
    }

    private static string ReadAll(SafeFileHandle handle)
    {
        using var stream = new FileStream(handle, FileAccess.Read, 4096, false);
        using var reader = new StreamReader(stream, Console.OutputEncoding, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static void CreatePipePair(out SafeFileHandle read, out SafeFileHandle write, bool inheritRead, bool inheritWrite)
    {
        var securityAttributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };
        if (!CreatePipe(out read, out write, ref securityAttributes, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed");
        if (!inheritRead && !SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation failed");
        if (!inheritWrite && !SetHandleInformation(write, HANDLE_FLAG_INHERIT, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation failed");
    }

    private static string QuoteCommandLine(string fileName, string[] arguments)
    {
        var parts = new string[arguments.Length + 1];
        parts[0] = fileName;
        Array.Copy(arguments, 0, parts, 1, arguments.Length);
        return string.Join(" ", parts.Select(QuoteArg));
    }

    private static string QuoteArg(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";
        if (!arg.Any(static c => char.IsWhiteSpace(c) || c is '"' or '\\'))
            return arg;

        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, out nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct COORD(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }
}

static class UnixPseudoTerminal
{
    public static CommandOutput Run(string fileName, string[] arguments, string? cwd, int width, int height)
    {
        var winsize = new Winsize { ws_col = (ushort)width, ws_row = (ushort)height };
        if (openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, ref winsize) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "openpty failed");

        var pid = fork();
        if (pid < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "fork failed");

        if (pid == 0)
        {
            close(master);
            setsid();
            ioctl(slave, TIOCSCTTY, 0);
            dup2(slave, 0);
            dup2(slave, 1);
            dup2(slave, 2);
            if (slave > 2)
                close(slave);
            if (!string.IsNullOrWhiteSpace(cwd))
                chdir(cwd);
            execvp(fileName, BuildArgv(fileName, arguments));
            _exit(127);
        }

        close(slave);
        var output = ReadAll(master);
        close(master);
        waitpid(pid, out var status, 0);
        var exitCode = WIFEXITED(status) ? WEXITSTATUS(status) : 1;
        return new CommandOutput(output, "", exitCode, true);
    }

    private static string[] BuildArgv(string fileName, string[] arguments)
    {
        var argv = new string[arguments.Length + 2];
        argv[0] = fileName;
        Array.Copy(arguments, 0, argv, 1, arguments.Length);
        argv[^1] = null!;
        return argv;
    }

    private static string ReadAll(int fd)
    {
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: false);
        using var stream = new FileStream(handle, FileAccess.Read, 4096, false);
        using var reader = new StreamReader(stream, Console.OutputEncoding, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static bool WIFEXITED(int status) => (status & 0x7f) == 0;
    private static int WEXITSTATUS(int status) => (status >> 8) & 0xff;

    private const ulong TIOCSCTTY = 0x540E;

    [DllImport("libc", SetLastError = true)]
    private static extern int openpty(out int amaster, out int aslave, IntPtr name, IntPtr termp, ref Winsize winp);

    [DllImport("libc", SetLastError = true)]
    private static extern int fork();

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int chdir(string path);

    [DllImport("libc", SetLastError = true)]
    private static extern int execvp(string file, string[] argv);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc")]
    private static extern void _exit(int status);

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }
}
