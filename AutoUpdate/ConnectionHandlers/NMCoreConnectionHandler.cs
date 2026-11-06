using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace OpenCrazyArcade.AutoUpdate.ConnectionHandlers;

internal class NMCoreConnectionHandler(ILogger<NMCoreConnectionHandler> logger, Managers.NMCoreManager nmCoreManager, Nmco.NmcoPipeRouter router) : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        logger.LogInformation("Client connected to the NMCO named pipe.");
        await nmCoreManager.EnsureConnectToOCAServer();

        try
        {
            using var reader = new StreamReader(connection.Transport.Input.AsStream(), Encoding.UTF8);
            using var writer = new StreamWriter(connection.Transport.Output.AsStream(), Encoding.UTF8) { AutoFlush = true };

            while (!connection.ConnectionClosed.IsCancellationRequested)
            {
                string line = await reader.ReadLineAsync();
                if (line == null) break;

                string reply = await router.HandleAsync(line);
                if (reply != null)
                {
                    logger.LogDebug("NMCO IPC TX: {Reply}", reply);
                    await writer.WriteLineAsync(reply);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("NMCO Pipe connection terminated: {Message}", ex.Message);
        }
        finally
        {
            logger.LogInformation("Client disconnected from OpenCrazyArcade_NMCO NamedPipe.");
        }
    }
}