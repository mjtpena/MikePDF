using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

// S6.6: FlaUI.UIA3 5.0.0 smoke test against HelloQuire.
// usage: UiTest <exe-or-alias> [--mouse] [--wait-process]
//   finds Button AutomationId=ClickButton, clicks it (UIA Invoke pattern, or a real mouse click with --mouse),
//   asserts TextBlock AutomationId=StatusText changes from "Not clicked" to "Clicked 1"; then clicks LaunchEngineButton and
//   asserts EngineText begins with ENGINE-HELLO. Prints the UIA tree of the first two levels as evidence.
var exe = args[0];
bool mouse = args.Contains("--mouse");
var sw = Stopwatch.StartNew();
int pid;
var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
if (args.Contains("--wait-process"))
{
    // Packaged launch through an execution alias: the alias process is a stub, find the real HelloQuire process.
    foreach (var old in Process.GetProcessesByName("HelloQuire")) old.Kill();
    Process.Start(psi);
    Process? real = null;
    for (int i = 0; i < 100 && real == null; i++) { Thread.Sleep(100); real = Process.GetProcessesByName("HelloQuire").FirstOrDefault(); }
    pid = real?.Id ?? throw new Exception("HelloQuire process never appeared");
}
else pid = Process.Start(psi)!.Id;

using var automation = new UIA3Automation();
var app = Application.Attach(pid);
Window? win = null;
for (int i = 0; i < 100 && win == null; i++) { try { win = app.GetMainWindow(automation, TimeSpan.FromMilliseconds(200)); } catch { } }
if (win == null) { Console.WriteLine("FAIL: no main window"); return 2; }
Console.WriteLine($"window found after {sw.ElapsedMilliseconds} ms: title='{win.Title}' framework={win.FrameworkType}");

var btn = win.FindFirstDescendant(cf => cf.ByAutomationId("ClickButton"))?.AsButton();
var status = win.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"));
if (btn == null || status == null) { Console.WriteLine("FAIL: ClickButton or StatusText not found"); Dump(win, 0, 3); app.Close(); return 3; }
string before = status.Name;
if (mouse) { win.SetForeground(); btn.Click(); } else btn.Invoke();
string after = before;
for (int i = 0; i < 50 && after == before; i++) { Thread.Sleep(50); after = status.Name; }
Console.WriteLine($"StatusText: '{before}' -> '{after}' ({(mouse ? "mouse click" : "UIA Invoke")})");
bool ok = after == "Clicked 1";

var eng = win.FindFirstDescendant(cf => cf.ByAutomationId("LaunchEngineButton"))?.AsButton();
var engText = win.FindFirstDescendant(cf => cf.ByAutomationId("EngineText"));
if (eng != null && engText != null)
{
    eng.Invoke();
    string t = "";
    for (int i = 0; i < 100 && !t.StartsWith("ENGINE"); i++) { Thread.Sleep(50); t = engText.Name; }
    Console.WriteLine("EngineText: " + t);
    ok &= t.StartsWith("ENGINE-HELLO");
}
var canvas = win.FindFirstDescendant(cf => cf.ByAutomationId("PageCanvas"));
Console.WriteLine("PageCanvas (Win2D CanvasVirtualControl) in UIA tree: " + (canvas != null ? $"yes, ControlType={canvas.ControlType}" : "no (the CanvasVirtualControl has no automation peer / is not exposed in the UIA tree)"));
Console.WriteLine("--- UIA tree (depth 3)");
Dump(win, 0, 3);
try { app.Close(); } catch { }
Thread.Sleep(300);
foreach (var p in Process.GetProcessesByName("HelloQuire")) { try { p.Kill(); } catch { } }
Console.WriteLine(ok ? "PASS" : "FAIL");
return ok ? 0 : 1;

static void Dump(AutomationElement e, int depth, int max)
{
    if (depth > max) return;
    string aid = ""; try { aid = e.AutomationId; } catch { }
    string cn = "?"; try { cn = e.ClassName; } catch { cn = "(ClassName not supported)"; }
    Console.WriteLine($"{new string(' ', depth * 2)}{e.ControlType} id='{aid}' name='{e.Name}' class='{cn}'");
    foreach (var c in e.FindAllChildren()) Dump(c, depth + 1, max);
}
