using System.Runtime.InteropServices;

namespace Quire.S3.Engine;

internal static unsafe class TokenInfo
{
    public static void Describe(Action<string, string> L)
    {
        nint tok;
        if (N.OpenProcessToken(N.GetCurrentProcess(), N.TOKEN_QUERY, &tok) == 0) { L("tokenError", N.LastError.ToString()); return; }
        byte* buf = stackalloc byte[2048];
        uint ret;
        uint v = 0;
        L("isAppContainer", N.GetTokenInformation(tok, N.TokenIsAppContainer, &v, 4, &ret) != 0 ? v.ToString() : "err" + N.LastError);
        if (N.GetTokenInformation(tok, N.TokenIntegrityLevel, buf, 2048, &ret) != 0)
        {
            byte* sid = *(byte**)buf;                      // TOKEN_MANDATORY_LABEL.Label.Sid
            uint rid = *(uint*)(sid + 8 + 4 * (sid[1] - 1));
            L("integrityRid", "0x" + rid.ToString("X"));
        }
        if (N.GetTokenInformation(tok, N.TokenAppContainerSid, buf, 2048, &ret) != 0)
        {
            byte* sid = *(byte**)buf;
            L("appContainerSid", sid == null ? "(none)" : N.SidToString(sid));
        }
        if (N.GetTokenInformation(tok, N.TokenCapabilities, buf, 2048, &ret) != 0) L("capabilityCount", (*(uint*)buf).ToString());
        if (N.GetTokenInformation(tok, 3 /*TokenPrivileges*/, buf, 2048, &ret) != 0) L("privilegeCount", (*(uint*)buf).ToString());
        if (N.GetTokenInformation(tok, 2 /*TokenGroups*/, buf, 2048, &ret) != 0) L("groupCount", (*(uint*)buf).ToString());
        if (N.GetTokenInformation(tok, 11 /*TokenRestrictedSids*/, buf, 2048, &ret) != 0) L("restrictedSidCount", (*(uint*)buf).ToString());
        N.CloseHandle(tok);
    }
}

internal static unsafe class MitigationInfo
{
    // PROCESS_MITIGATION_POLICY enum values (winnt.h)
    public static readonly string[] Names =
    [
        "DEP", "ASLR", "DynamicCode", "StrictHandleCheck", "SystemCallDisable(Win32k)", "MitigationOptionsMask", "ExtensionPointDisable",
        "ControlFlowGuard", "Signature", "FontDisable", "ImageLoad", "SystemCallFilter", "PayloadRestriction", "ChildProcess",
        "SideChannelIsolation", "UserShadowStack", "RedirectionTrust", "UserPointerAuth", "SEHOP"
    ];

    public static void Describe(Action<string, string> L)
    {
        for (int p = 0; p < Names.Length; p++)
        {
            uint* buf = stackalloc uint[8];
            for (int i = 0; i < 8; i++) buf[i] = 0;
            if (N.GetProcessMitigationPolicy(N.GetCurrentProcess(), p, buf, p == 0 ? 8u : p == 5 ? 24u : 4u) != 0) L("mit." + Names[p], "0x" + buf[0].ToString("X"));
            else L("mit." + Names[p], "err" + N.LastError);
        }
    }
}

internal static unsafe class RuntimeMitigations
{
    // bit -> (policy id, flags value, name)
    static readonly (uint bit, int policy, uint flags, string name)[] Map =
    [
        (1,   8,  0x1,       "Signature.MicrosoftSignedOnly"),
        (2,   13, 0x1,       "ChildProcess.NoChildProcessCreation"),
        (4,   6,  0x1,       "ExtensionPointDisable"),
        (8,   9,  0x1,       "FontDisable.DisableNonSystemFonts"),
        (16,  10, 0x1 | 0x2 | 0x4, "ImageLoad.NoRemote|NoLowLabel|PreferSystem32"),
        (32,  2,  0x1,       "DynamicCode.ProhibitDynamicCode"),
        (64,  3,  0x1 | 0x2, "StrictHandleCheck.Raise|Permanent"),
        (128, 4,  0x1,       "SystemCallDisable.DisallowWin32k"),
    ];

    public static void Apply(PayloadReader r, PayloadWriter o)
    {
        uint mask = r.U32();
        var results = new List<(string, int)>();
        foreach (var m in Map)
        {
            if ((mask & m.bit) == 0) continue;
            uint f = m.flags;
            int ok = N.SetProcessMitigationPolicy(m.policy, &f, 4);
            results.Add((m.name, ok != 0 ? 0 : N.LastError));
        }
        o.I32(0).I32(results.Count);
        foreach (var (n, e) in results) o.Str(n).I32(e);
    }
}

internal static unsafe class GdiProbe
{
    public static void Run(PayloadWriter o)
    {
        var lines = new List<string>();
        nint dc = N.CreateCompatibleDC(0);
        lines.Add($"CreateCompatibleDC => {(dc != 0 ? "ok" : "FAIL")} err={(dc != 0 ? 0 : N.LastError)}");
        if (dc != 0)
        {
            nint f = N.CreateFontW(-16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Arial");
            lines.Add($"CreateFont(Arial) => {(f != 0 ? "ok" : "FAIL")} err={(f != 0 ? 0 : N.LastError)}");
            if (f != 0)
            {
                nint old = N.SelectObject(dc, f);
                char* name = stackalloc char[64];
                int n = N.GetTextFaceW(dc, 64, name);
                lines.Add($"GetTextFace => \"{(n > 0 ? new string(name) : "")}\" err={(n > 0 ? 0 : N.LastError)}");
                uint sz = N.GetFontData(dc, 0, 0, null, 0);
                lines.Add($"GetFontData(whole file) => {(sz == 0xFFFFFFFF ? "GDI_ERROR" : sz + " bytes")} err={(sz == 0xFFFFFFFF ? N.LastError : 0)}");
                N.SelectObject(dc, old);
                N.DeleteObject(f);
            }
            N.DeleteDC(dc);
        }
        // direct file read of the system font (what a self-made FPDF_SYSFONTINFO would do)
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        nint h = N.CreateFileW(path, N.GENERIC_READ, N.FILE_SHARE_ALL, null, N.OPEN_EXISTING, N.FILE_ATTRIBUTE_NORMAL, 0);
        lines.Add($"CreateFile({path}) => {(h != -1 ? "ok" : "FAIL")} err={(h != -1 ? 0 : N.LastError)}");
        if (h != -1) N.CloseHandle(h);
        lines.Add($"fontProvider: MapFont calls={FontProvider.MapCalls} hits={FontProvider.MapHits}");
        o.I32(0).I32(lines.Count);
        foreach (var l in lines) o.Str(l);
    }
}
