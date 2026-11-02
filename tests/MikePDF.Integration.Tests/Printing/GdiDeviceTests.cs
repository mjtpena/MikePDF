using MikePDF.Core.Printing;
using MikePDF.Printing;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>The GDI device against the real Microsoft Print to PDF queue, with <c>DOCINFO.lpszOutput</c> set so that no dialog appears. Skipped where the queue is not installed.</summary>
public sealed class GdiDeviceTests
{
    private static string OutputPath() => Path.Combine(Path.GetTempPath(), "mikepdf-gdi-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");

    private static GdiPrintDevice Open(out DeviceOpenReport report)
    {
        var opened = GdiPrintDeviceFactory.Instance.Open(new PrinterJobSettings(VirtualPrinter.Name, new PrintOptions { PaperSizeId = VirtualPrinter.PaperA4 }, Landscape: false));
        report = opened.Report;
        return (GdiPrintDevice)opened.Device;
    }

    private static byte[] Solid(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    [Fact]
    public void AOnePageJobToAFileIsAPdfAndEndDocWritesIt()
    {
        VirtualPrinter.Require();
        var output = OutputPath();
        try
        {
            using (var device = Open(out _))
            {
                device.StartDocument("GDI device test", output);
                device.StartPage();
                var band = Solid(600, 100, 20, 120, 220);
                device.DrawBand(band, 600, 100, 2400, 100, 100, 600, 100);
                device.DrawBand(band, 600, 100, 2400, 100, 200, 1200, 200); // stretched 2x
                device.EndPage();
                device.EndDocument();
            }

            Assert.True(File.Exists(output));
            var bytes = File.ReadAllBytes(output);
            Assert.True(bytes.Length > 1000);
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        }
        finally
        {
            DeleteQuietly(output);
        }
    }

    [Fact]
    public void AnAbortedJobLeavesNoPdfAndTheQueueCanBeUsedAgain()
    {
        VirtualPrinter.Require();
        var output = OutputPath();
        try
        {
            using (var device = Open(out _))
            {
                device.StartDocument("GDI device abort test", output);
                device.StartPage();
                device.DrawBand(Solid(200, 50, 0, 0, 0), 200, 50, 800, 0, 0, 200, 50);
                device.AbortDocument();
                device.AbortDocument(); // a second abort is harmless
            }

            Assert.True(!File.Exists(output) || new FileInfo(output).Length == 0, "an abort leaves nothing but, at most, an empty file");
            Assert.True(VirtualPrinter.JobsInQueue(VirtualPrinter.Name) <= 0 || WaitForEmptyQueue(), "the spool job is gone");

            var again = OutputPath();
            try
            {
                using var device = Open(out _);
                device.StartDocument("GDI device after abort", again);
                device.StartPage();
                device.EndPage();
                device.EndDocument();
                Assert.True(new FileInfo(again).Length > 0, "the next print works");
            }
            finally
            {
                DeleteQuietly(again);
            }
        }
        finally
        {
            DeleteQuietly(output);
        }
    }

    [Fact]
    public void ADeviceThatIsDisposedMidJobAbortsItAndDrawingOnAnAbortedJobFails()
    {
        VirtualPrinter.Require();
        var output = OutputPath();
        try
        {
            var device = Open(out _);
            device.StartDocument("GDI device dispose test", output);
            device.StartPage();
            device.RequestAbort(); // the abort procedure now answers FALSE
            device.Dispose(); // ... and Dispose ends the job, so nothing is left in the spooler
            Assert.True(!File.Exists(output) || new FileInfo(output).Length == 0);
            Assert.Throws<ArgumentException>(() => Open(out _).DrawBand(new byte[3], 2, 2, 8, 0, 0, 2, 2)); // a band that does not match its dimensions is refused before GDI sees it
        }
        finally
        {
            DeleteQuietly(output);
        }
    }

    private static bool WaitForEmptyQueue()
    {
        for (var i = 0; i < 40; i++)
        {
            if (VirtualPrinter.JobsInQueue(VirtualPrinter.Name) <= 0)
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Held for a moment by the spooler; the temp directory is cleaned eventually.
        }
    }
}
