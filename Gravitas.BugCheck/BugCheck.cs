using System.Diagnostics;
using System.Runtime.InteropServices;
using Mono.Unix.Native;

namespace Gravitas.BugCheck;

public enum BugCheckAction
{
    Throw,
    HangProc,
    HangThread,
    Crash
}

public readonly record struct BugCheckOption(
    bool TryBreak = true,
    BugCheckAction Action = BugCheckAction.Throw
);

public sealed class BugCheckException : Exception
{
}

file static class SuspendNtOs
{
    [Flags]
    private enum ThreadAccess
    {
        SuspendResume = 0x0002
    }

    [DllImport("ntdll.dll")]
    private static extern nint NtSuspendProcess(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint OpenThread(ThreadAccess access, bool inherit, uint id);

    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(nint handle);

    public static bool SuspendProc()
    {
        var process = Process.GetCurrentProcess();
        // normally the application should be able to self-suspend
        return NtSuspendProcess(process.Handle) == 0;
    }

    public static bool SuspendThread()
    {
        var hThread = OpenThread(ThreadAccess.SuspendResume, false, (uint)Environment.CurrentManagedThreadId);
        if (hThread == nint.Zero) return false;
        return SuspendThread(hThread) != uint.MaxValue;
    }
}

// TODO: I have absolutely no idea if this works properly or not. Needs validation 
file static class SuspendPosix
{   
    public static bool SuspendProc()
    {
        var pid = Syscall.getpid();
        return Syscall.kill(pid, Signum.SIGSTOP) == 0;
    }

    public static bool SuspendThread()
    {
        return Syscall.pause() == -1;
    }
}

public static class Exceptional
{
    private static BugCheckOption _option;

    public static void Install(BugCheckOption option = new()) => _option = option;

    public static void BugCheck()
    {
        if (_option.TryBreak && Debugger.Launch()) Debugger.Break();
        switch (_option.Action)
        {
            case BugCheckAction.Throw:
                throw new BugCheckException();
            case BugCheckAction.HangProc:
                if (OperatingSystem.IsWindows())
                {
                    if (SuspendNtOs.SuspendProc()) return;
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    if (SuspendPosix.SuspendProc()) return;
                }

                Console.Error.WriteLine("BugCheck degrade to HangThread");
                goto case BugCheckAction.HangThread;
            case BugCheckAction.HangThread:
                if (OperatingSystem.IsWindows())
                {
                    if (SuspendNtOs.SuspendThread()) return;
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    if (SuspendPosix.SuspendThread()) return;
                }

                // nothing is known to work, just sleep the current thread
                Thread.Sleep(TimeSpan.MaxValue);
                break;
            case BugCheckAction.Crash:
            default:
                Process.GetCurrentProcess().Kill(true);
                break;
        }
    }
}