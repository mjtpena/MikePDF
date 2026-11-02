using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX.Direct3D11;

namespace S9;

/// <summary>DXGI adapter enumeration (raw COM vtables) and creation of a Win2D CanvasDevice on a chosen adapter, or on WARP. No registry changes, no windows.</summary>
internal static unsafe class Gpu
{
    public sealed record Adapter(int Index, string Name, uint Vendor, ulong DedicatedMB, uint Flags);

    static readonly Guid IID_Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid IID_DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(Guid* riid, void** ppFactory);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(void* adapter, int driverType, nint software, uint flags, int* levels, uint nLevels, uint sdk, void** device, int* featureLevel, void** ctx);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(void* dxgiDevice, void** inspectable);

    static void* Vt(void* o, int i) => (*(void***)o)[i];
    static uint Release(void* o) => o == null ? 0 : ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(o, 2))(o);

    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>(); void* f = null; var iid = IID_Factory1;
        if (CreateDXGIFactory1(&iid, &f) < 0) return list;
        for (uint i = 0; ; i++)
        {
            void* a = null;
            if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vt(f, 12))(f, i, &a) < 0) break;   // IDXGIFactory1::EnumAdapters1
            var desc = stackalloc byte[320];
            ((delegate* unmanaged[Stdcall]<void*, byte*, int>)Vt(a, 10))(a, desc);                             // IDXGIAdapter1::GetDesc1
            var name = new string((char*)desc).TrimEnd('\0');
            var vendor = *(uint*)(desc + 256); var ded = *(ulong*)(desc + 272); var flags = *(uint*)(desc + 304);
            list.Add(new Adapter((int)i, name, vendor, ded >> 20, flags)); Release(a);
        }
        Release(f); return list;
    }

    /// <summary>Win2D device on adapter index (>= 0), or the software rasteriser (WARP) when index is -1.</summary>
    public static CanvasDevice Create(int adapterIndex)
    {
        if (adapterIndex == -2) return CanvasDevice.GetSharedDevice();
        if (adapterIndex == -1) return new CanvasDevice(true);
        void* f = null; var iid = IID_Factory1; CreateDXGIFactory1(&iid, &f);
        void* a = null; ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vt(f, 12))(f, (uint)adapterIndex, &a);
        void* dev = null; int fl = 0;
        var hr = D3D11CreateDevice(a, 0 /*UNKNOWN: adapter given*/, 0, 0x20 /*BGRA_SUPPORT*/, null, 0, 7, &dev, &fl, null);
        Release(a); Release(f);
        if (hr < 0) throw new InvalidOperationException($"D3D11CreateDevice 0x{hr:X8}");
        void* dxgi = null; var idd = IID_DxgiDevice;
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Vt(dev, 0))(dev, &idd, &dxgi);
        void* insp = null; hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, &insp);
        if (hr < 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 0x{hr:X8}");
        var d3d = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi((nint)insp);
        Release(dxgi); Release(dev);
        return CanvasDevice.CreateFromDirect3D11Device(d3d);
    }

    /// <summary>Block until all GPU work submitted on this device has executed: read one pixel back from a render target the work drew to.</summary>
    public static void Flush(CanvasRenderTarget rt) => rt.GetPixelBytes(0, 0, 1, 1);
}
