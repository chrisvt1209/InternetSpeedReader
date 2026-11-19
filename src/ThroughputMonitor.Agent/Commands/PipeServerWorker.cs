using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Agent.Commands;

/// <summary>
/// Local command channel for the speedlog CLI: a named pipe, one JSON line in, one JSON line out.
/// Any logged-in local user may connect (labels are harmless); network clients are denied.
/// </summary>
public sealed class PipeServerWorker(
    MarkCoordinator coordinator,
    IOptions<AgentOptions> options,
    ILogger<PipeServerWorker> log) : BackgroundService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string pipeName = string.IsNullOrWhiteSpace(options.Value.PipeName) ? AgentIpc.DefaultPipeName : options.Value.PipeName;
        log.LogInformation("Command pipe ready: \\\\.\\pipe\\{Pipe}", pipeName);

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                await server.WaitForConnectionAsync(ct);

                var connected = server;
                server = null;   // ownership moves to the handler
                _ = Task.Run(() => HandleAsync(connected, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Command pipe failed; retrying in 5 s (is a second agent running?)");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CommandTimeout + TimeSpan.FromSeconds(2));

            var utf8 = new UTF8Encoding(false);
            using var reader = new StreamReader(pipe, utf8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, utf8, 1024, leaveOpen: true) { AutoFlush = true };

            AgentResponse response;
            try
            {
                string? line = await AgentIpc.ReadLineLimitedAsync(reader, AgentIpc.MaxMessageChars, timeout.Token);
                var request = line is null ? null : JsonSerializer.Deserialize<AgentRequest>(line, AgentIpc.Json);
                response = request is null || string.IsNullOrWhiteSpace(request.Cmd)
                    ? new AgentResponse(false, "Empty or unreadable request.")
                    : await coordinator.SubmitAsync(request, CommandTimeout, timeout.Token);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                response = new AgentResponse(false, "Invalid request.");
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentIpc.Json).AsMemory(), timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // client went away or the agent is stopping
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Command pipe client failed");
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        // Logged-in users may talk to the agent; nobody on the network may.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } me)
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
