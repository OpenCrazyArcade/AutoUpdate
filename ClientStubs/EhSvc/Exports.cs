using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenCrazyArcade.ClientStubs;

/// <summary>
/// HShield.lib, linked into CA.exe, talks to HackShield through exactly one function of EhSvc.dll:
/// <c>int __stdcall dispatch(int cmd, void** args, int* out)</c>, found by ordinal 10 (HS_VerifyEhSvcDll @0xAABB52,
/// sub_AAB978). The game wants <c>(byte)result == 1</c> and <c>*out == 0</c> for every command, and cmd 4 (initialize)
/// hands over an event callback that has to be called with 65537 about once a second, or the game's watchdog decides
/// HackShield died and ends the process after ~50 s. See RE/hackshield_architecture.md.
/// </summary>
public static unsafe class Exports
{
    private const int HeartbeatEvent = 65537;       // 0x10001: "all is well"
    private const int InitializeCommand = 4;

    private static delegate* unmanaged[Stdcall]<int, int, int, void> s_callback;
    private static Thread s_heartbeat;

    // The ordinal comes from ehsvc.def; the EntryPoint is the name that table needs to bind.
    [UnmanagedCallersOnly(EntryPoint = "10", CallConvs = [typeof(CallConvStdcall)])]
    public static int Dispatch(int cmd, void** args, int* output)
    {
        if (cmd == InitializeCommand && args != null && s_heartbeat == null)
        {
            s_callback = (delegate* unmanaged[Stdcall]<int, int, int, void>)args[0];
            if (s_callback != null)
            {
                s_heartbeat = new Thread(Heartbeat) { IsBackground = true, Name = "EhSvc heartbeat" };
                s_heartbeat.Start();
            }
        }

        if (output != null) *output = 0;
        return 1;
    }

    private static void Heartbeat()
    {
        for (;;)
        {
            s_callback(HeartbeatEvent, 0, 0);
            Thread.Sleep(1000);
        }
    }
}
