using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Setup;

/// <summary>Reads what the baseline file must say about the machine: CPU, RAM, GPUs, OS, power plan. Nothing that identifies a person or a host name is recorded.</summary>
public static class MachineProbe
{
    private const string DisplayAdapterClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static MachineInfo Read()
    {
        var hosted = string.Equals(System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        return new MachineInfo
        {
            Cpu = ReadCpu(),
            LogicalCores = System.Environment.ProcessorCount,
            RamGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024), 1),
            Gpus = ReadGpus(),
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            DotNet = RuntimeInformation.FrameworkDescription,
            PowerPlan = ReadPowerPlan(),
            CiHosted = hosted,
            CiImage = hosted ? System.Environment.GetEnvironmentVariable("ImageVersion") : null,
        };
    }

    private static string ReadCpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                return string.Join(' ', name.Split([' '], StringSplitOptions.RemoveEmptyEntries));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall through to the generic answer.
        }

        return "unknown (" + RuntimeInformation.ProcessArchitecture + ")";
    }

    private static List<string> ReadGpus()
    {
        var gpus = new List<string>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClass);
            if (classKey is not null)
            {
                foreach (var name in classKey.GetSubKeyNames())
                {
                    if (name.Length != 4 || !name.All(char.IsAsciiDigit))
                    {
                        continue; // "Configuration" and "Properties" are not adapters
                    }

                    using var adapter = classKey.OpenSubKey(name);
                    if (adapter?.GetValue("DriverDesc") is string description && !string.IsNullOrWhiteSpace(description) && !gpus.Contains(description, StringComparer.Ordinal))
                    {
                        var driver = adapter.GetValue("DriverVersion") as string;
                        gpus.Add(string.IsNullOrWhiteSpace(driver) ? description : $"{description} (driver {driver})");
                    }
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Reported as none found.
        }

        return gpus;
    }

    private static string? ReadPowerPlan()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powercfg.exe", "/getactivescheme")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var open = output.IndexOf('(', StringComparison.Ordinal);
            var close = output.LastIndexOf(')');
            return open >= 0 && close > open ? output[(open + 1)..close] : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
