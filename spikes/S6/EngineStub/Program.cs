using System.Runtime.InteropServices;

// Stand-in for the C++ engine: prints identity and architecture proof, as the packaged app must be able to spawn it.
Native.IsWow64Process2(Native.GetCurrentProcess(), out var procMachine, out var nativeMachine);
Console.WriteLine($"ENGINE-HELLO pid={Environment.ProcessId} arch={RuntimeInformation.ProcessArchitecture} os={RuntimeInformation.OSArchitecture} wow64proc=0x{procMachine:X} native=0x{nativeMachine:X} exe={Environment.ProcessPath}");

internal static class Native
{
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool IsWow64Process2(nint h, out ushort proc, out ushort native);
}
