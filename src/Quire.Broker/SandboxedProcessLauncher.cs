using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Quire.Broker.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Quire.Broker;

/// <summary>
/// What to start. There is deliberately no list of handles to pass: the child inherits nothing (R1). Anything it needs is duplicated into
/// it afterwards and announced over its pipe.
/// </summary>
public sealed record SandboxLaunchRequest(string ExecutablePath, string Arguments)
{
    /// <summary>Receives the job's messages from the moment the process exists. Runs on the job's own thread; must not block or throw.</summary>
    public Action<EngineJobEvent>? OnJobEvent { get; init; }

    /// <summary>
    /// Leave the process suspended; the caller duplicates handles into it and then calls <see cref="SandboxedProcess"/>'s resume. The launcher
    /// itself always creates the process suspended, so it is in its job and its token before it executes a single instruction.
    /// </summary>
    internal bool StartSuspended { get; init; }
}

public sealed class SandboxLaunchException(string message, int win32Error, Exception? inner = null) : Exception(message, inner)
{
    public int Win32Error { get; } = win32Error;
}

/// <summary>
/// Starts a process in a zero-capability AppContainer with the creation attributes, job and mitigation policies of ADR-0004.
/// There is no fallback: if any part of the sandbox cannot be set up the launch fails, and the document is not opened.
/// <para>
/// <b>No handle is inherited.</b> The process is created suspended with <c>bInheritHandles = FALSE</c> and no handle list, so no other
/// <c>CreateProcess</c> in the broker can leak a handle of ours either: nothing of ours is ever inheritable. Handles reach the child
/// by <c>DuplicateHandle</c> (a document, the slot ring), and diagnostics through a named log pipe it opens by name.
/// </para>
/// </summary>
public sealed class SandboxedProcessLauncher
{
    private readonly AppContainerProfile _profile;
    private readonly SandboxPolicy _policy;

    public SandboxedProcessLauncher(AppContainerProfile profile, SandboxPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _policy = policy ?? SandboxPolicy.Default;
    }

    public SandboxPolicy Policy => _policy;

    public AppContainerProfile Profile => _profile;

    public SandboxedProcess Launch(SandboxLaunchRequest request)
    {
        var process = LaunchSuspended(request);
        if (!request.StartSuspended)
        {
            try
            {
                process.Resume();
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        return process;
    }

    private unsafe SandboxedProcess LaunchSuspended(SandboxLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exe = Path.GetFullPath(request.ExecutablePath);
        if (!File.Exists(exe))
        {
            throw new SandboxLaunchException("Executable not found: " + exe, 2);
        }

        var job = EngineJob.Create(_policy.ProcessMemoryLimitBytes, request.OnJobEvent);
        EnginePipeServer? logServer = null;
        try
        {
            logServer = EnginePipeServer.Create(_profile.Sid);
            var commandLine = "\"" + exe + "\"" + (request.Arguments.Length > 0 ? " " + request.Arguments : string.Empty);
            var environment = BuildEnvironment(logServer.Name);
            var jobHandle = (nint)job.Handle;
            var mitigations = stackalloc ulong[2];
            mitigations[0] = _policy.CreationMitigations1;
            mitigations[1] = SandboxPolicy.CreationMitigations2;
            var childPolicy = PInvoke.PROCESS_CREATION_CHILD_PROCESS_RESTRICTED;

            PROCESS_INFORMATION processInfo;
            fixed (byte* sidPointer = _profile.SidBytes)
            fixed (char* commandLinePointer = commandLine + "\0")
            fixed (char* environmentPointer = environment)
            fixed (char* workingDirectory = Path.GetDirectoryName(exe) + "\0")
            {
                var capabilities = new SECURITY_CAPABILITIES { AppContainerSid = new PSID(sidPointer), Capabilities = null, CapabilityCount = 0 };

                // Four attributes: AppContainer, mitigations, child-process policy, job. No handle list: nothing is inherited.
                nuint size = 0;
                PInvoke.InitializeProcThreadAttributeList(default, 4, 0, &size);
                var attributeMemory = NativeMemory.AllocZeroed(size);
                var attributes = new LPPROC_THREAD_ATTRIBUTE_LIST(attributeMemory);
                try
                {
                    Win32.ThrowIfFalse(PInvoke.InitializeProcThreadAttributeList(attributes, 4, 0, &size), "InitializeProcThreadAttributeList");
                    try
                    {
                        Update(attributes, PInvoke.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, &capabilities, (nuint)sizeof(SECURITY_CAPABILITIES), "SECURITY_CAPABILITIES");
                        Update(attributes, PInvoke.PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY, mitigations, 2 * sizeof(ulong), "MITIGATION_POLICY");
                        Update(attributes, PInvoke.PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY, &childPolicy, sizeof(uint), "CHILD_PROCESS_POLICY");
                        Update(attributes, PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST, &jobHandle, (nuint)sizeof(nint), "JOB_LIST");

                        var startup = default(STARTUPINFOEXW);
                        startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
                        startup.lpAttributeList = attributes;

                        // DETACHED_PROCESS: a console-subsystem process started without a console gets a conhost child, which the child-process
                        // policy kills (S3). The engine and the probe are GUI-subsystem, but detach regardless. CREATE_SUSPENDED: the process is in
                        // its job (JOB_LIST, atomically) and cannot run until the caller has finished setting it up.
                        var flags = PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT
                            | PROCESS_CREATION_FLAGS.DETACHED_PROCESS
                            | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT
                            | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;
                        if (!PInvoke.CreateProcess(default, new PWSTR(commandLinePointer), null, null, false, flags, environmentPointer, new PCWSTR(workingDirectory), &startup.StartupInfo, &processInfo))
                        {
                            var error = Marshal.GetLastPInvokeError();
                            throw new SandboxLaunchException("CreateProcess in the AppContainer failed: " + new Win32Exception(error).Message, error);
                        }
                    }
                    finally
                    {
                        PInvoke.DeleteProcThreadAttributeList(attributes);
                    }
                }
                finally
                {
                    NativeMemory.Free(attributeMemory);
                }
            }

            return new SandboxedProcess(new OwnedHandle(processInfo.hProcess), new OwnedHandle(processInfo.hThread), processInfo.dwProcessId, job, logServer);
        }
        catch
        {
            logServer?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private static unsafe void Update(LPPROC_THREAD_ATTRIBUTE_LIST list, uint attribute, void* value, nuint size, string name)
    {
        if (!PInvoke.UpdateProcThreadAttribute(list, 0, attribute, value, size, null, null))
        {
            throw Win32.LastError("UpdateProcThreadAttribute(" + name + ")");
        }
    }

    /// <summary>
    /// A minimal environment: the child gets no tokens, proxies or user paths from the broker.
    /// <para>
    /// <b>LOCALAPPDATA, USERPROFILE and APPDATA are required</b>: the OS derives the AppContainer's own TEMP and profile folders from the
    /// creator's environment, and <c>CreateProcess</c> fails with ERROR_ENVVAR_NOT_FOUND (203) without them. The child sees the real paths in these
    /// three variables but cannot open them (the negative-access probe proves that).
    /// </para>
    /// QUIRE_LOG_PIPE names the diagnostics pipe the child opens, since it has no inherited stderr.
    /// </summary>
    private static string BuildEnvironment(string logPipeName)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var builder = new StringBuilder();
        foreach (var name in (string[])["LOCALAPPDATA", "USERPROFILE", "APPDATA"])
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                builder.Append(name).Append('=').Append(value).Append('\0');
            }
        }

        builder.Append("PATH=").Append(Path.Combine(windows, "System32")).Append('\0');
        builder.Append("QUIRE_LOG_PIPE=").Append(logPipeName).Append('\0');
        builder.Append("SystemDrive=").Append(Path.GetPathRoot(windows)?.TrimEnd('\\')).Append('\0');
        builder.Append("SystemRoot=").Append(windows).Append('\0');
        builder.Append("windir=").Append(windows).Append('\0');
        builder.Append('\0');
        return builder.ToString();
    }
}
