using System;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// How a second launcher talks to the first one: plain HTTP over a named pipe. Kestrel in the running launcher
/// listens on <see cref="PipeName"/> next to the NMCO pipe and serves <c>Controllers/InstanceController</c>;
/// this is the client half. Only the current Windows user can open the pipe.
/// </summary>
internal static class InstanceControl
{
    public static string PipeName => "OpenCrazyArcade.AutoUpdate.control." + Environment.UserName;

    public const string ActivatePath = "/instance/activate";
    public const string LinkPath = "/instance/link";

    /// <summary>
    /// POST <paramref name="body"/> to the running launcher. The first launcher takes a moment between
    /// owning the mutex and listening, so a refused connection is retried until <paramref name="timeoutMs"/>.
    /// </summary>
    public static bool Post(string path, object body, int timeoutMs = 8000)
    {
        // The shell started us because of a click: let the instance that receives the request take the foreground.
        try { AllowSetForegroundWindow(-1); } catch { }

        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.ConnectAsync(1000, ct); return pipe; }
                catch { await pipe.DisposeAsync(); throw; }
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(3) };

        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                using var response = http.PostAsJsonAsync(path, body).GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch (Exception) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(200);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
