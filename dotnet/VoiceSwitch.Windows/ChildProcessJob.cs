using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceSwitch.Windows;

// Scripts run from a file under %LOCALAPPDATA%\voice-switch: passed by -EncodedCommand, the dictation script alone put
// 44,541 characters on the command line, past the Windows 32,767 limit, and Process.Start failed with Win32Exception.
public static class PowerShellScript
{
    public static ProcessStartInfo StartInfo(string fileName, string script)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "voice-switch");
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path) || File.ReadAllText(path) != script)
        {
            Directory.CreateDirectory(dir);
            // BOM: Windows PowerShell reads a BOM-less .ps1 in the ANSI code page.
            File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        return new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -File \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
}

// Children started here die with voice-switch.exe, even when it crashes or is killed: we never close the job handle,
// so the kernel closes it at our exit and JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE ends every process still in the job.
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private static readonly Lazy<nint> Job = new(Create);

    public static Process Start(ProcessStartInfo psi)
    {
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {psi.FileName}");
        // A failure only loses the cleanup guarantee, so the child keeps running rather than failing recognition.
        if (OperatingSystem.IsWindows() && (Job.Value == 0 || !AssignProcessToJobObject(Job.Value, process.Handle)))
        {
            Log.Info($"child process job: pid={process.Id} not assigned (error {Marshal.GetLastWin32Error()}); it can outlive voice-switch");
        }

        return process;
    }

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var job = CreateJobObject(0, null);
        var info = new ExtendedLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose };
        if (job != 0 && SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            return job;
        }

        Log.Info($"child process job: not created (error {Marshal.GetLastWin32Error()})");
        if (job != 0)
        {
            CloseHandle(job);
        }

        return 0;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION with JOBOBJECT_BASIC_LIMIT_INFORMATION and IO_COUNTERS inlined (144 bytes on x64).
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
