using System.Runtime.InteropServices;
using Windows.Win32;

namespace Quire.Broker;

/// <summary>
/// What a sandboxed process is started with (ADR-0004 decision 2 and 3). The AppContainer itself always has zero
/// capabilities and the child-process policy is always restricted; those are not options.
/// </summary>
public sealed record SandboxPolicy
{
    /// <summary>Per-process commit limit enforced by the job (default 2 GiB, §5.2 rule 5).</summary>
    public long ProcessMemoryLimitBytes { get; init; } = 2L << 30;

    /// <summary>
    /// Prohibit dynamic code at creation. True for the C++ engine (PDFium without V8 has no JIT); a .NET host needs it off
    /// until it is Native AOT (ADR-0004).
    /// </summary>
    public bool ProhibitDynamicCode { get; init; } = true;

    /// <summary>
    /// EXPERIMENT (sandbox-probe branch): named capabilities added to the container, such as <c>packageContents</c>. Empty by default, which is
    /// the zero-capability profile of ADR-0004. Remove if the packaged-install test does not need it.
    /// </summary>
    public IReadOnlyList<string> NamedCapabilities { get; init; } = [];

    /// <summary>
    /// EXPERIMENT (sandbox-probe branch): when the broker is a packaged desktop app, start the child outside the package
    /// (PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_OVERRIDE), so it does not inherit the package identity and its package capability.
    /// </summary>
    public bool BreakawayFromPackage { get; init; }

    public static SandboxPolicy Default { get; } = new();

    // PROCESS_CREATION_MITIGATION_POLICY_* flags, the first of the two DWORD64 values.
    internal ulong CreationMitigations1 =>
        PInvoke.PROCESS_CREATION_MITIGATION_POLICY_DEP_ENABLE
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_BOTTOM_UP_ASLR_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_HIGH_ENTROPY_ASLR_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_FORCE_RELOCATE_IMAGES_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_STRICT_HANDLE_CHECKS_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_EXTENSION_POINT_DISABLE_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_CONTROL_FLOW_GUARD_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_FONT_DISABLE_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_NO_REMOTE_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_NO_LOW_LABEL_ALWAYS_ON
        | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_PREFER_SYSTEM32_ALWAYS_ON
        | (ProhibitDynamicCode ? PInvoke.PROCESS_CREATION_MITIGATION_POLICY_PROHIBIT_DYNAMIC_CODE_ALWAYS_ON : 0UL);

    // PROCESS_CREATION_MITIGATION_POLICY2_*: CET user shadow stacks, compatibility mode (strict mode is an M5 evaluation).
    // CET is an x64 feature; the engine is not linked /CETCOMPAT on ARM64 either (cmake/QuireHardening.cmake).
    internal static ulong CreationMitigations2 =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? PInvoke.PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_ON : 0UL;
}
