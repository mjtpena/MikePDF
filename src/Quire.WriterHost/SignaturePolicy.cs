namespace Quire.WriterHost;

/// <summary>
/// The runtime Microsoft-signed-only image policy (ADR-0004 decision 2), applied after warm-up. The result is logged so the broker's tests
/// (and a person reading the log) can see what the OS actually did, including a refusal.
/// <para>
/// <b>Finding (measured on DEV, Windows 11 26100, .NET 10 self-contained, WP4b).</b> The policy <b>can</b> be applied in a .NET process, and it is
/// effective against managed code as well as native code, but only once every assembly the host needs is already loaded:
/// </para>
/// <list type="bullet">
/// <item>Applied <b>before</b> the host's own assemblies were loaded (an experiment, since reverted): <c>SetProcessMitigationPolicy</c> succeeded
/// (read back 0x5), then loading <c>Quire.PdfWriter.dll</c> failed with <c>FileLoadException</c> 0x80070241 ("Windows cannot verify the digital
/// signature for this file"), and a native <c>LoadLibrary</c> of an unsigned DLL was DENIED. Our assemblies are unsigned, so CoreCLR's image
/// mapping of them is subject to the policy. The host died before Hello.</item>
/// <item>Applied <b>after</b> <see cref="WarmUp"/> (this code): applied, read back 0x5, and every request path works: strict parse and structure
/// validation of all 18 PdfWriter fixtures plus damaged inputs, the codec, memory-mapped file access, and process shutdown. CoreCLR, the
/// framework assemblies and the JIT are Microsoft-signed; nothing of ours is loaded lazily on those paths.</item>
/// </list>
/// <para>
/// So: apply it last, after loading every assembly and exercising every code path a request takes (<see cref="WarmUp"/>). A code path that was
/// not warmed and needs a not-yet-loaded unsigned assembly would fail with a <c>FileLoadException</c> after the policy; the host treats that as a
/// fatal error (fail closed, the broker restarts it). That is the cost, and the reason the warm-up exists. Signing our assemblies (§14) would remove it.
/// ADR-0004 should be amended: the runtime signature policy applies to WriterHost as well as the engine, with this ordering rule.
/// </para>
/// </summary>
internal static class SignaturePolicy
{
    public static void Apply()
    {
        var error = Native.ApplyMicrosoftSignedOnly();
        var readBack = Native.ReadSignaturePolicy();
        LogPipe.Write(error == 0
            ? $"writerhost: signature policy applied, read back 0x{readBack:X}"
            : $"writerhost: signature policy NOT applied, error {error}");
    }
}
