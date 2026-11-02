using System.ComponentModel;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MikePDF.ShellHost;

/// <summary>What a request through the pipe is for. The handler's two classes connect to two pipes, so ShellHost knows which switch and which rules apply without a field in the schema.</summary>
internal enum ShellFeature
{
    Thumbnails,
    Preview,
}

/// <summary>
/// The names of the pipes and how they are made (ADR-0018 decision 4): per user, a DACL for the user and SYSTEM only, a Low mandatory label so that a handler host
/// running at Low integrity can still use a ShellHost that is already running, remote clients rejected, and <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> so that a process that
/// got there first makes ShellHost fail instead of being served by an impostor.
/// </summary>
internal static class ShellPipe
{
    /// <summary>The thumbnail pipe's name for a user. The handler computes the same name (src/MikePDF.ShellExt/shell_client.cpp, <c>pipe_name_for_sid</c>).</summary>
    public static string NameFor(string userSid, ShellFeature feature) => "MikePDF.Shell." + userSid + (feature == ShellFeature.Preview ? ".Preview" : string.Empty);

    /// <summary>The security descriptor of every pipe instance.</summary>
    public static string Sddl(string userSid, bool lowIntegrityMayConnect = true) =>
        "D:P(A;;GA;;;" + userSid + ")(A;;GA;;;SY)" + (lowIntegrityMayConnect ? "S:(ML;;NW;;;LW)" : string.Empty);

    /// <summary>Creates one instance of the pipe. Overlapped, byte mode, 255 instances at most; <paramref name="first"/> adds <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>.</summary>
    public static unsafe NamedPipeServerStream Create(string name, string sddl, bool first)
    {
        if (!Native.ConvertStringSecurityDescriptor(sddl, 1, out var descriptor, 0))
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "ConvertStringSecurityDescriptorToSecurityDescriptor failed");
        }

        try
        {
            var attributes = new Native.SecurityAttributes { Length = (uint)sizeof(Native.SecurityAttributes), SecurityDescriptor = descriptor };
            const uint duplex = 0x00000003;
            const uint overlapped = 0x40000000;
            const uint firstInstance = 0x00080000;
            const uint rejectRemoteClients = 0x00000008;
            var handle = Native.CreateNamedPipe(@"\\.\pipe\" + name, duplex | overlapped | (first ? firstInstance : 0), rejectRemoteClients, 255, 1 << 20, 1 << 20, 0, &attributes);
            if (handle is -1 or 0)
            {
                throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "CreateNamedPipe failed");
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, new SafePipeHandle(handle, ownsHandle: true));
        }
        finally
        {
            Native.LocalFree(descriptor);
        }
    }

    public static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
}
