using System;
using System.Threading;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// One launcher per Windows session. The first instance owns a named mutex; a later one (a second double-click, or
/// the browser opening an <c>opencrazyarcade://</c> link) finds the mutex taken, hands its request to the running
/// instance through <see cref="InstanceControl"/> and exits. The mutex is only the "is somebody there" test - the
/// request itself travels over the control endpoint Kestrel serves (see <c>Controllers/InstanceController</c>).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const int MaxLine = 4096;

    private static string MutexName => @"Local\OpenCrazyArcade.AutoUpdate." + Environment.UserName;

    private readonly Mutex _mutex;

    public bool IsOwner { get; }

    private SingleInstance(Mutex mutex, bool owner)
    {
        _mutex = mutex;
        IsOwner = owner;
    }

    /// <summary>
    /// Try to become the one instance. <paramref name="waitMs"/> &gt; 0 is for a launcher that was just
    /// started by an update: the old one is still shutting down and releases the mutex when it exits.
    /// Must be called from the thread that stays alive for the whole run (the mutex belongs to it).
    /// </summary>
    public static SingleInstance Acquire(int waitMs)
    {
        var mutex = new Mutex(true, MutexName, out bool created);
        if (created) return new SingleInstance(mutex, true);

        if (waitMs > 0)
        {
            try
            {
                if (mutex.WaitOne(waitMs)) return new SingleInstance(mutex, true);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing it - exactly what an exiting launcher does.
                return new SingleInstance(mutex, true);
            }
        }
        return new SingleInstance(mutex, false);
    }

    public void Dispose()
    {
        try { if (IsOwner) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}

/// <summary>
/// Requests that reach the launcher before its window exists (a link that starts the launcher) wait here
/// until the window attaches; later ones are delivered at once. Line format:
/// "ACTIVATE" (bring the window to the front) or "URI\t&lt;opencrazyarcade:// link&gt;".
/// </summary>
public sealed class RemoteCommands
{
    private readonly object _lock = new();
    private readonly System.Collections.Generic.Queue<string> _pending = new();
    private Action<string> _handler;

    public void Submit(string line)
    {
        Action<string> h;
        lock (_lock)
        {
            h = _handler;
            if (h == null) { _pending.Enqueue(line); return; }
        }
        h(line);
    }

    public void Attach(Action<string> handler)
    {
        string[] queued;
        lock (_lock)
        {
            _handler = handler;
            queued = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var l in queued) handler(l);
    }
}
