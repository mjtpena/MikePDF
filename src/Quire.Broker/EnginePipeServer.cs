using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Quire.Broker.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Pipes;

namespace Quire.Broker;

public sealed class EnginePipeException(string message) : Exception(message);

/// <summary>
/// The broker's end of the engine pipe (ADR-0005 rule 1): a random 128-bit name, a single first instance, no remote
/// clients, a DACL for the current user plus the engine's AppContainer SID only, overlapped I/O, and a check that the
/// process that connected is the one that was launched.
/// </summary>
public sealed class EnginePipeServer : IDisposable
{
    private readonly NamedPipeServerStream _stream;

    private EnginePipeServer(string name, NamedPipeServerStream stream)
    {
        Name = name;
        _stream = stream;
    }

    /// <summary>32 lower-case hex digits; the engine opens <c>\\.\pipe\</c> followed by this.</summary>
    public string Name { get; }

    public static unsafe EnginePipeServer Create(SecurityIdentifier appContainerSid)
    {
        ArgumentNullException.ThrowIfNull(appContainerSid);
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var user = WindowsIdentity.GetCurrent().User ?? throw new EnginePipeException("The current user has no SID.");
        var sddl = $"D:(A;;FRFW;;;{user.Value})(A;;FRFW;;;{appContainerSid.Value})";

        // Created natively: NamedPipeServerStream cannot take the AppContainer SID in its DACL together with these flags.
        PSECURITY_DESCRIPTOR descriptor;
        Win32.ThrowIfFalse(PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out descriptor, out _), "ConvertStringSecurityDescriptorToSecurityDescriptor");
        HANDLE pipe;
        try
        {
            var attributes = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = descriptor.Value, bInheritHandle = false };
            fixed (char* path = @"\\.\pipe\" + name + "\0")
            {
                pipe = PInvoke.CreateNamedPipe(
                    new PCWSTR(path),
                    FILE_FLAGS_AND_ATTRIBUTES.PIPE_ACCESS_DUPLEX | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
                    NAMED_PIPE_MODE.PIPE_TYPE_BYTE | NAMED_PIPE_MODE.PIPE_READMODE_BYTE | NAMED_PIPE_MODE.PIPE_WAIT | NAMED_PIPE_MODE.PIPE_REJECT_REMOTE_CLIENTS,
                    1,
                    64 * 1024,
                    64 * 1024,
                    0,
                    &attributes);
            }
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)descriptor.Value);
        }

        if (pipe == Win32.InvalidHandle)
        {
            throw Win32.LastError("CreateNamedPipe");
        }

        var stream = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, new SafePipeHandle(pipe, ownsHandle: true));
        return new EnginePipeServer(name, stream);
    }

    /// <summary>
    /// Waits for the engine to connect and returns the connected stream. The client must be <paramref name="expectedClientProcessId"/>:
    /// anyone else who won the race to a pipe they should not know the name of is refused.
    /// </summary>
    public async Task<NamedPipeServerStream> AcceptAsync(uint expectedClientProcessId, CancellationToken cancellationToken)
    {
        await _stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        var clientPid = ClientProcessId((HANDLE)_stream.SafePipeHandle.DangerousGetHandle());
        if (clientPid != expectedClientProcessId)
        {
            throw new EnginePipeException($"The pipe client is process {clientPid}, not the engine that was launched ({expectedClientProcessId}).");
        }

        return _stream;
    }

    /// <summary>The pipe handle, for tests that check it is not inheritable.</summary>
    internal nint NativeHandle => _stream.SafePipeHandle.DangerousGetHandle();

    public void Dispose() => _stream.Dispose();

    private static unsafe uint ClientProcessId(HANDLE pipe)
    {
        uint pid;
        Win32.ThrowIfFalse(PInvoke.GetNamedPipeClientProcessId(pipe, &pid), "GetNamedPipeClientProcessId");
        return pid;
    }
}
