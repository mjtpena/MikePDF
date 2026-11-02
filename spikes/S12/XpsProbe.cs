using System.Runtime.InteropServices;

namespace S12;

/// <summary>Candidate (b), XPS flavour: can this process create an IPrintDocumentPackageTarget for the printer, and what package formats does it offer? No page is printed; the job is cancelled.</summary>
internal static unsafe class XpsProbe
{
    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint r, uint flags);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(Guid* clsid, nint outer, uint ctx, Guid* iid, void** obj);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(void* p);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attr, bool create, nint templ, out void* stream);

    static void* Vt(void* o, int i) => (*(void***)o)[i];
    static uint Release(void* o) => o == null ? 0 : ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(o, 2))(o);

    public static string Run(string outFile)
    {
        var sb = new System.Text.StringBuilder();
        CoInitializeEx(0, 2 /*COINIT_APARTMENTTHREADED*/);
        var clsid = new Guid("348EF17D-6C81-4982-92B4-EE188A43867A");     // PrintDocumentPackageTargetFactory
        var iid = new Guid("D2959BF7-B31B-4A3D-9600-712EB1335BA4");       // IPrintDocumentPackageTargetFactory
        void* factory = null; var hr = CoCreateInstance(&clsid, 0, 1 /*CLSCTX_INPROC_SERVER*/, &iid, &factory);
        sb.AppendLine($"CoCreateInstance(PrintDocumentPackageTargetFactory) = 0x{hr:X8}"); if (hr < 0) return sb.ToString();
        hr = SHCreateStreamOnFileEx(outFile, 0x1001 /*STGM_CREATE|STGM_WRITE*/, 0x80, true, 0, out var stream);
        sb.AppendLine($"SHCreateStreamOnFileEx = 0x{hr:X8}");
        fixed (char* printer = "Microsoft Print to PDF") fixed (char* job = "S12 xps probe")
        {
            void* target = null;
            hr = ((delegate* unmanaged[Stdcall]<void*, char*, char*, void*, void*, void**, int>)Vt(factory, 3))(factory, printer, job, stream, null, &target);
            sb.AppendLine($"CreateDocumentPackageTargetForPrintJob(Microsoft Print to PDF, job output = a file stream in the worktree) = 0x{hr:X8}");
            if (hr >= 0)
            {
                uint count = 0; Guid* types = null;
                hr = ((delegate* unmanaged[Stdcall]<void*, uint*, Guid**, int>)Vt(target, 3))(target, &count, &types);
                sb.AppendLine($"GetPackageTargetTypes = 0x{hr:X8}, {count} type(s):");
                for (uint i = 0; i < count && hr >= 0; i++) sb.AppendLine("  " + types[i]);
                if (types != null) CoTaskMemFree(types);
                var cancel = ((delegate* unmanaged[Stdcall]<void*, int>)Vt(target, 5))(target); sb.AppendLine($"Cancel() = 0x{cancel:X8} (no page was submitted)");
                Release(target);
            }
        }
        Release(stream); Release(factory);
        return sb.ToString();
    }
}
