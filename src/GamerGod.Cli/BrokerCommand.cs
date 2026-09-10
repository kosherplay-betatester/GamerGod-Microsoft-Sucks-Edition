using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using GamerGod.Core.Engine;

namespace GamerGod.Cli;

/// <summary>
/// The elevated half of the desktop application, alive for as long as the window is.
///
/// <para>
/// One consent prompt instead of one per click. The window has no rights of its own and the
/// journal it must write lives where only administrators may, so every arm and every disarm used
/// to raise a Windows permission prompt — including one before the window was usable, for anyone
/// who had asked GamerGod to arm on launch. A prompt that appears that often stops being read.
/// </para>
///
/// <para>
/// <b>This is a privilege boundary, so the rules are narrow on purpose:</b>
/// </para>
/// <list type="bullet">
///   <item>The pipe grants access to <em>one</em> account — the one that launched this — and to
///   nobody else. Not Administrators, not Everyone, not the interactive group.</item>
///   <item>Only the verbs in <see cref="BrokerProtocol.IsAllowedVerb"/> are acted on. There is no
///   path, no executable name and no pass-through argument list, so the most a caller can ask for
///   is the thing the switch already does — journalled, and reversible by design.</item>
///   <item>Lever arguments are re-parsed through <see cref="LeverArguments"/> rather than handed
///   onward as text, so anything that is not a known flag is dropped rather than obeyed.</item>
///   <item>It serves a single connection. When that closes — the window shut, or crashed — this
///   exits rather than waiting for another client to attach to an elevated pipe.</item>
///   <item>It exits on its own after <see cref="IdleTimeout"/> with nothing to do, so a window
///   that stops talking without closing cleanly cannot leave an elevated process behind.</item>
/// </list>
///
/// <para>
/// Not started by hand. <c>GamerGod.exe</c> launches it with a pipe name it generated for that
/// one run, and stops it on exit.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BrokerCommand
{
    /// <summary>
    /// How long to wait for a request before giving up and exiting.
    ///
    /// <para>
    /// Long enough that a session left open all evening keeps working, short enough that a
    /// helper orphaned by a crash does not sit elevated overnight. It is refreshed by every
    /// request, so it measures silence rather than uptime.
    /// </para>
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);

    public static async Task<int> RunAsync(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine("  gamergod broker --pipe <name>");
            return 2;
        }

        using var identity = WindowsIdentity.GetCurrent();

        if (identity.User is not { } user)
        {
            Console.Error.WriteLine("  Could not determine which account started this.");
            return 2;
        }

        // The account that launched this, and nothing else. Under UAC the elevated token and the
        // window's ordinary token share a user SID, which is what lets the window connect while
        // another signed-in user cannot.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            user, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        await using var server = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);

        using var idle = new CancellationTokenSource(IdleTimeout);

        try
        {
            await server.WaitForConnectionAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Nothing ever connected. The window died between starting this and reaching it.
            return 0;
        }

        using var reader = new StreamReader(server, leaveOpen: true);
        await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

        while (true)
        {
            idle.CancelAfter(IdleTimeout);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (IOException)
            {
                // The window went away mid-sentence. Same answer as a clean close.
                return 0;
            }

            if (line is null)
            {
                // Pipe closed: the application exited. This elevated process goes with it.
                return 0;
            }

            var (verb, arguments) = BrokerProtocol.Parse(line);

            if (!BrokerProtocol.IsAllowedVerb(verb))
            {
                // Named, not echoed. The refusal says which verb was refused, never the rest of
                // the line — a log that repeats whatever it was sent is a log that can be
                // written into.
                await writer.WriteLineAsync(BrokerProtocol.Error + "unknown verb").ConfigureAwait(false);
                continue;
            }

            if (verb == BrokerProtocol.Quit)
            {
                await writer.WriteLineAsync(BrokerProtocol.Reply(0)).ConfigureAwait(false);
                return 0;
            }

            int exitCode;
            try
            {
                exitCode = await ExecuteAsync(verb, arguments).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A verb that throws must not take the helper down: the window would then need
                // another consent prompt to get one back, which is the whole thing being fixed.
                await writer.WriteLineAsync(BrokerProtocol.Error + ex.Message.Replace('\n', ' '))
                    .ConfigureAwait(false);
                continue;
            }

            await writer.WriteLineAsync(BrokerProtocol.Reply(exitCode)).ConfigureAwait(false);
        }
    }

    private static async Task<int> ExecuteAsync(string verb, string[] arguments) => verb switch
    {
        // Re-parsed rather than forwarded. LeverArguments knows the whole vocabulary, so a flag
        // it does not recognise never reaches the engine.
        BrokerProtocol.On => await SessionCommands.OnAsync(
            dryRun: false,
            options: LeverArguments.Parse(arguments),
            owner: null,
            ownerExecutable: OwnerArguments.FindExecutable(arguments)),

        BrokerProtocol.Off => await SessionCommands.OffAsync(),
        BrokerProtocol.Status => await SessionCommands.StatusAsync(),

        // Alive, elevated, and changed nothing.
        _ => 0,
    };
}
