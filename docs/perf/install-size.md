# Install size

Master prompt §11: **60 MB per architecture**, defined in [`budgets.json`](budgets.json) as `install.size`: the unpacked size of the single-architecture `.msix`, which is the sum of the lengths of its files, in MB of 1,048,576 bytes (`package.install_size` in `tools/MikePDF.Perf/Scenarios/PackageScenario.cs`). The size of the `.msix` file itself (what is downloaded) is `package.msix_file_size`. Optional AI models are excluded; there are none.

Written for work package `m3/install-size`. Measured with SDK 10.0.403 (runtime 10.0.14), Windows App SDK 2.5.1, Win2D 1.4.0 and C#/WinRT 2.2.0 (the `WinRT.Runtime` in the .NET SDK's Windows SDK projection pack 10.0.26100.57), on x64. The ARM64 figure is derived, not packaged: see "ARM64".

## Result

| | Before | After |
|---|---:|---:|
| x64 unpacked | **150.5 MB** (90.5 over) | **42.8 MB** (17.2 under) |
| x64 `.msix` file | 58.4 MB | 19.3 MB |
| x64 files in the package | 328 | 158 |
| ARM64 unpacked | not built (no ARM64 C++ tools on the development machine) | about 46 MB, derived |

Where it was, and where it is (`build/store/check-package-size.ps1`, which is also the CI gate):

| Component | Before (MB) | After (MB) |
|---|---:|---:|
| .NET libraries (`System.*`) | 47.3 | 4.9 |
| .NET runtime (CLR, JIT, host, CoreLib; before: also debugger, dump, GC and symbol helpers) | 30.3 | 14.8 |
| C#/WinRT runtime and the Windows SDK projection | 26.5 | 1.4 |
| Windows App SDK and WinUI (projections, bootstrap, WebView2 loader) | 24.3 | 3.8 |
| Engine and PDFium (`Engine\`) | 7.5 | 7.5 |
| MikePDF managed code and MessagePack | 5.4 | 3.6 |
| Win2D | 4.3 | 1.9 |
| ShellHost (Native AOT) | 3.9 | 3.9 |
| Resources, handlers DLL, licences, package metadata | 1.0 | 0.9 |
| **Total** | **150.5** | **42.8** |

The writer host that `m4/save-app` adds (Native AOT, about 2.9 MB) is not in these numbers; with it the x64 package is about 45.7 MB.

## Why it was 150 MB

The app was published self-contained and **not trimmed**. A self-contained publish copies the whole shared framework (about 170 assemblies) and none of it was removed:

- `System.Private.Xml` (7.4 MB), `System.Data.Common` (2.6 MB), `System.Private.DataContractSerialization`, `Microsoft.VisualBasic.Core`, `System.Net.Http` and the rest were in the package **because they are in the framework**, not because MikePDF uses them. No MikePDF assembly references any of them (the assembly references of every MikePDF assembly were read from the metadata). The only reference to `System.Linq.Expressions` is from `WinRT.Runtime` and the Windows SDK projection; after trimming it is still there, small.
- `Microsoft.Windows.SDK.NET.dll` (25.1 MB) is the projection of every Windows API; MikePDF calls a handful. `Microsoft.WinUI.dll` (15.7 MB, ReadyToRun) is the projection of every WinUI type; the app names a fraction of them.
- The runtime's debugger and dump helpers (`mscordaccore*.dll`, `mscordbi.dll`, `createdump.exe`, 3.8 MB), the standalone GCs (`clrgc.dll`, `clrgcexp.dll`, 1.3 MB), `msquic.dll` (HTTP/3, 0.5 MB) and `Microsoft.DiaSymReader.Native` (2.3 MB) are copied by the self-contained publish and used by nothing the app does: 7.9 MB.

## What was done

1. **`PublishTrimmed` with `TrimMode=full`** in `MikePDF.App.csproj` (set for a self-contained publish, which is what `build/package.ps1` does; the trimmer refuses a framework-dependent build, which is what the development build is). This is almost all of the saving: `Microsoft.Windows.SDK.NET.dll` 25.1 → 0.3 MB, `Microsoft.WinUI.dll` 15.7 → 2.1, CoreLib 15.3 → 7.3, `System.*` 47 → 5.
2. **Left out of the publish** (the `LeaveOutUnusedWindowsAppSdkParts` target in `MikePDF.App.csproj`, which already dropped ONNX Runtime and DirectML): the 7.9 MB of native debugger, dump, GC, HTTP/3 and symbol-reader files above, which trimming does not touch.
3. **ReadyToRun stays on.** It costs about 11 MB (the app's publish folder is 38.1 MB with it and 27.2 MB without, before the leave-outs) and the package fits with it, so there is nothing to trade it for. Trimming runs before ReadyToRun compilation, so only code that is kept is precompiled. Partial ReadyToRun of the startup path was not needed.
4. **Trim and AOT analysers on every build**: `IsAotCompatible` in Core, Broker, Protocol and Printing; `EnableTrimAnalyzer` and `CsWinRTAotWarningLevel=1` in the app. `Microsoft.NET.ILLink.Tasks` is named at `10.0.14` (`VersionOverride`) in those five projects: the SDK otherwise adds it by itself at the version of the installed SDK patch, which would change five lock files at every SDK update and fail locked restore in CI (the same reason `MikePDF.ShellHost` has lock files off). It is a build tool, MIT, and nothing from it is shipped. **Bump it together with the SDK pin** (ADR-0001); the SDK's own advice to delete the reference is silenced for that reason (`SuppressILLinkExplicitPackageReferenceWarning` in `Directory.Build.props`).
5. **The size gate and the probe** in CI (below).

### Trim warnings

| Warning | Where | Fix |
|---|---|---|
| IL2026 | `MikePDF.Core/Editing/EditCommand.cs`: `Create(string, object)` serialises an anonymous object by reflection | The method now says so (`RequiresUnreferencedCode`, `RequiresDynamicCode`) and is for tests, fuzz targets and tools. Shipped code uses the new `EditCommand.FromJson(string, string)`, which does no reflection. In a trimmed build the old call throws, because `PublishTrimmed` turns reflection-based JSON off. **`m4/save-app` calls `Create` from `MainWindow.Save.cs` (`FakeEdit`)** and will need `FromJson` when it merges. |
| CsWinRT1028 | `PageSurface`, `PageSurfaceAutomationPeer`, `PageTextRangeProvider` (they implement UI Automation's WinRT interfaces `ITextProvider`, `ITextRangeProvider`, ...) | Made `partial`, so that the C#/WinRT source generator writes the interface table the runtime would otherwise find by reflection, which the trimmer breaks. Found by publishing the app with Native AOT once (the analyser is off in a trimmed build unless `CsWinRTAotWarningLevel` is set; it now is). `ThumbnailItem` (`INotifyPropertyChanged`) and `ThumbnailPages` (`IList`, a list view's `ItemsSource`) are `partial` too: level 1 does not report them but they cross the XAML ABI the same way. The built `MikePDF.dll` carries the generator's `WinRTExposedType` registration for all five. |
| IL2104 for `WinRT.Runtime` and `Microsoft.Windows.SDK.NET` | 35 × IL2081 in the generic ABI fallbacks of C#/WinRT 2.2.0 (`ABI.System.Collections.Generic.*`, `ABI.Windows.Foundation.*`), found with `TrimmerSingleWarn=false` | Not ours and not fixable here: both assemblies come from the .NET SDK's Windows SDK projection pack, and the newer pack (10.0.26100.87) is C#/WinRT 3.0, which ADR-0001 excludes. They stay **visible** as warnings; `WarningsNotAsErrors` names IL2104 only, and `build/package.ps1` and `build/trim-probe.ps1` fail if IL2104 names any other assembly. Every warning in our own assemblies is still an error. |

There are no other warnings: the trimmer's analysis of MikePDF.App, Core, Broker, Protocol and Printing is clean. Level 2 of `CsWinRTAotWarningLevel` (types that implement built-in .NET interfaces) lists 70 more, nearly all library classes that implement `IDisposable` and never cross the ABI; it was read, not enabled.

## What is safe, and what could not be checked

C#/WinRT 2.2.0 is written for this: its runtime-class lookup (`TypeNameSupport.FindTypeByName`) is documented in its source as tolerating types the trimmer removed ("any types which are trimmed are not used by user code and there is fallback logic to handle that"), the XAML compiler's `XamlTypeInfo.g.cs` names every type in the markup with `typeof`, which roots it, and the WinUI, Windows SDK, WebView2 and Win2D 1.4.0 projections all carry the `WinRTImplementationTypeRcwFactory` attribute that the trimmer-safe creation path needs (checked in the metadata of each). The app has no `{Binding}`, no `[ComImport]`, no reflection-based XML, data-contract or binary serialisation, and its JSON is source-generated or `JsonDocument`/`Utf8JsonWriter`.

`build/trim-probe.ps1` publishes `tools/MikePDF.TrimProbe` the way the app is published (self-contained, `TrimMode=full`, ReadyToRun) and runs it; it is a step of the package CI job on both architectures. It first checks that it really is trimmed (reflection JSON is off), then runs: the settings store (set, flush, reload), the document store, the session file, edit commands (`FromJson`), the WinRT calls the app makes that need no window (`StorageFile.GetFileFromPathAsync`, `GetBasicPropertiesAsync`, `StorageFolder.GetItemsAsync` returning `IStorageItem`s that must come back as `StorageFile`s, `DataPackage` text, `UISettings` colour), and a **real sandboxed engine session** (AppContainer profile, pipes, MessagePack protocol, open a document, render a tile and check it is not blank). All pass on x64. ARM64 runs in CI only.

**Not verifiable without the UI**, for the CI UI journeys (PR #24) to cover on the trimmed package:

- the window comes up, XAML loads, and `x:Name` fields and event handlers connect (`IComponentConnector.Connect` receives runtime classes by name);
- `{x:Bind}` and `x:Uid` resources in every view, the command palette, the settings view, the print sheet, the custom `PageSurface` control and its `Generic.xaml` style;
- `ItemsSource` with a `List<PaletteRow>` and the recent-files list: a generic list handed to native XAML needs C#/WinRT's generic ABI helpers (the 35 IL2081 above are exactly those fallbacks), which only the UI exercises;
- Win2D drawing (`CanvasVirtualControl`) and the swap chain;
- UI Automation through `PageSurfaceAutomationPeer` (Narrator, NVDA, Accessibility Insights): the text provider is now `partial` and registered, but nothing here asks Windows for it;
- COM activation: the Explorer thumbnail and preview handlers (`MikePDF.ShellExt.dll`, `MikePDF.ShellHost.exe`; ShellHost is Native AOT and was already trimmed), the file pickers and drag and drop themselves (the probe covers the call shapes, not the dialogs), the Jump List, `AppInstance` redirection, the Windows App SDK bootstrapper and the framework package lookup;
- anything that reads a `Windows.*` or `Microsoft.UI.*` runtime class by name that the projection trims and no code names. The fallback is to the static type, so it shows up as an `InvalidCastException` in a journey.

If a journey finds a trimmed-away type, the fix is a `TrimmerRootAssembly` or `TrimmerRootDescriptor` item for that one assembly or type in `MikePDF.App.csproj`, not turning trimming off.

## Startup

The §11 budgets (600 ms cold start on R1, 800 ms to first pixel) are measured on the packaged app by `tools/MikePDF.Perf/measure-app.ps1` on reference hardware, which needs the installed package and a window. That could not be run for this change, and `MikePDF.exe` refuses to start unpackaged, so there is no before and after of the app itself. What was measured, headlessly, on this development machine (24 cores, shared with other jobs, not R1) is the same non-UI code and engine session as a trimmed and an untrimmed self-contained ReadyToRun publish of the probe: 21 runs each, alternating, wall time of the whole process (`./build/trim-probe.ps1 -Compare -Runs 21`):

| | Trimmed (median, min–max) | Untrimmed (median, min–max) |
|---|---:|---:|
| Process start and the JSON checks | 205 ms (185–253) | 207 ms (190–292) |
| Plus WinRT calls and a sandboxed engine session with one tile | 1036 ms (841–1320) | 979 ms (820–1335) |

No difference beyond the noise (an earlier run of 15 had the second row the other way round). Trimming keeps ReadyToRun and only removes code that is never reached, so none was expected on the managed side; fewer and smaller WinUI assemblies to map is not measured. The reviewer's `measure-app.ps1` run on R1 and R3 is the real check.

## Native AOT for the whole app

Not switched to: trimming alone meets the budget with 17 MB to spare. What was found by trying (a copy of the tree, nothing installed): **`dotnet publish -p:PublishAot=true` of MikePDF.App succeeds on x64 in about a minute** with the pinned SDK, with **no IL warnings** and the three CsWinRT1028 above (now fixed). The result is `MikePDF.exe` 14.1 MB and 17.4 MB for the app with its DLLs (against 30.6 MB for the trimmed ReadyToRun publish), so an AOT package would be about 29.5 MB. Spike S6 measured AOT starting about 120 ms faster than ReadyToRun on a trivial WinUI app. It needs `link.exe` and `vswhere.exe` (the VS C++ tools).

What this does not show: nothing was run, so every item in the list above applies with more force (no reflection fallback at all; `ItemsSource` lists, UI Automation and the custom `PageSurface` control need the generated tables or fail); **ARM64 AOT cannot be built on the development machine** (no ARM64 C++ tools; the ARM64 CI image was not checked); and the single-project MSIX publish needs a manual manifest step (S6). It is a separate work package if startup, not size, asks for it, with the UI journeys as its test.

## The gate in CI

- `store-release.yml`, job `package` (both architectures; runs on every change under `src/`, `tools/MikePDF.TrimProbe/`, the packaging scripts, `Directory.*.props`, `global.json` and `docs/perf/budgets.json`): after `build/package.ps1`, `build/store/check-package-size.ps1` opens the `.msix` as a zip, sums the entries, reads the limit from `budgets.json` (`install.size`, 60), writes the breakdown by component and the 15 largest files to the job summary, and **fails above the limit** with an error annotation naming the overshoot and the five largest files. The next step runs the trim probe (also when the size step failed).
- `perf.yml`: the head is also packaged (not in the nightly A/A run) and its `.msix` is passed to the harness with `--head-package`, so `package.install_size` and `package.msix_file_size` are in `perf-results` and the trend instead of skipped. The base side has no package, so the comparison says "not measured on the base" and never gates on it. The gate itself is the job above.

By hand: `./build/package.ps1 -Arch x64` then `./build/store/check-package-size.ps1 -Msix build/out/package/MikePDF_1.0.0.0_x64.msix`; `-NoFail` reports without failing.

## ARM64

Not built here: the development machine has MSVC 14.44 with x64 and x86 libraries only, so neither the ARM64 engine, the ARM64 ShellExt DLL nor the ARM64 Native AOT ShellHost can be linked. What was measured is the managed half: `dotnet publish -r win-arm64` of the app with the same settings succeeds (the same two IL2104 warnings) and is 34.4 MB without the engine, against 30.8 MB for x64 (ARM64 ReadyToRun images and runtime files are larger). Adding the parts that cannot be built here at the sizes in S1 and the x64 package (PDFium arm64 6.6 MB, engine executable 0.3, ShellHost about 3.9, ShellExt 0.3, licences and metadata 0.5) gives **about 46 MB**, 14 MB under budget. The CI job measures the real figure on `windows-11-vs2026-arm` on its first run; if it differs by more than a few MB from this estimate, correct this section.
