using System;
using System.Runtime.InteropServices;

namespace OpenCrazyArcade.ClientStubs;

/// <summary>
/// What CA.exe wants from its HackShield updater (HS_Updater_LaunchHSUpdate @0xAB0799): the process runs, signals the
/// named event it created for it, and exits with code 0. Nothing else is looked at (no window, no network, no files).
/// CA.exe also checks the PE image of this exe; that is CheckmateCaChildPe's job, after publish.
/// </summary>
internal static class Program
{
    private const string EventName = "Global\\EF81BA4B-4163-44f5-90E2-F05C1E49C12D";
    private const uint EventModifyState = 0x0002;

    [DllImport("kernel32", CharSet = CharSet.Ansi, EntryPoint = "OpenEventA")]
    private static extern IntPtr OpenEvent(uint access, int inherit, string name);

    [DllImport("kernel32")] private static extern int SetEvent(IntPtr handle);
    [DllImport("kernel32")] private static extern int CloseHandle(IntPtr handle);

    private static int Main()
    {
        IntPtr handle = OpenEvent(EventModifyState, 0, EventName);
        if (handle == IntPtr.Zero)
            handle = OpenEvent(EventModifyState, 0, EventName.Substring("Global\\".Length));   // session-local variant
        if (handle != IntPtr.Zero)
        {
            SetEvent(handle);
            CloseHandle(handle);
        }
        return 0;
    }
}
