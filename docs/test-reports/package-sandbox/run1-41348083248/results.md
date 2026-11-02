# Quire sandbox-in-package probe
Run at 2026-11-01T20:43:11.5647041Z, OS 10.0.26100.0, arch X64

## Package identity (this process)
- GetCurrentPackageFullName: `Quire.SandboxProbe_1.0.0.0_x64__rtt22k44bspry`
- GetCurrentPackageFamilyName: `Quire.SandboxProbe_rtt22k44bspry`
- GetCurrentPackagePath: `C:\Program Files\WindowsApps\Quire.SandboxProbe_1.0.0.0_x64__rtt22k44bspry`
- Under WindowsApps: **True**
- Administrator role: True, user runneradmin

## ACL of the package directory (all paths in acl.txt)
```
C:\Program Files\WindowsApps\Quire.SandboxProbe_1.0.0.0_x64__rtt22k44bspry BUILTIN\Users:(OI)(CI)(Rc,S,RD,REA,X,RA)
                                                                           S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250:(OI)(CI)(RX)
                                                                           BUILTIN\Users:(OI)(CI)(R)
                                                                           NT SERVICE\TrustedInstaller:(I)(OI)(CI)(F)
                                                                           S-1-15-3-1024-3635283841-2530182609-996808640-1887759898-3848208603-3313616867-983405619-2501854204:(I)(OI)(CI)(RX)
                                                                           NT AUTHORITY\SYSTEM:(I)(OI)(CI)(F)
                                                                           BUILTIN\Administrators:(I)(CI)(RX)
                                                                           NT AUTHORITY\LOCAL SERVICE:(I)(OI)(CI)(RX)
                                                                           NT AUTHORITY\NETWORK SERVICE:(I)(OI)(CI)(RX)
                                                                           NT AUTHORITY\RESTRICTED:(I)(OI)(CI)(RX)

Successfully processed 1 files; Failed processing 0 files

```
- Engine directory: `C:\Program Files\WindowsApps\Quire.SandboxProbe_1.0.0.0_x64__rtt22k44bspry\Engine`; other-package file given to the probe: `C:\Program Files\WindowsApps\Microsoft.SecHealthUI_1000.26100.33648.0_x64__8wekyb3d8bbwe\AppxManifest.xml`

AppContainer profile `Quire.PackageProbe` created by this call: True; SID `S-1-15-2-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250-329789988-3524764629-4215146597-2989021428`

Control (unsandboxed) probe: finished=True, rows=20, deny-rows that were ALLOWED in the control=18

### Mode a: direct from the package directory, zero capabilities
- engine launched: **True** (engine pid 5112; Hello and AttachSlotRing succeeded)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **True** (200x300, 7140 non-white pixels; corners TL FF000000 TR FF0000FF BL FFFF0000 BR FF00FF00)
- negative probe: ran=False, **all denied=False**; token capabilities: 
  - the probe did not finish (exit 0x60C201E): INFO	isAppContainer	1
INFO	integrityRid	0x1000
INFO	capabilityCount	1
INFO	capabilitySids	S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250
INFO	privilegeCount	2
INFO	mit.creation.DEP	0x3
INFO	mit.creation.ASLR	0xF
INFO	mit.creation.DynamicCode	0x1
INFO	mit.creation.StrictHandleCheck	0x3
INFO	mit.creation.SystemCallDisable	0x0
INFO	mit.creation.MitigationOptionsMask	0x33333303
INFO	mit.creation.ExtensionPointDisable	0x1
INFO	mit.creation.ControlFlowGuard	0x1
INFO	mit.creation.Signature	0x0
INFO	mit.creation.FontDisable	0x1
INFO	mit.creation.ImageLoad	0x7
INFO	m

### Mode b: direct from the package directory, only the packageContents capability
- engine launched: **False** -> EngineStartException: Starting the engine failed: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. | inner: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **False** 
- negative probe: ran=False, **all denied=False**; token capabilities: 
  - probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)

### Mode e: direct from the package directory, child breaks away from the package
- engine launched: **False** -> EngineStartException: Starting the engine failed: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. | inner: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **False** 
- negative probe: ran=False, **all denied=False**; token capabilities: 
  - probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)

Copy-out target: `C:\Users\runneradmin\AppData\Local\Packages\Quire.SandboxProbe_rtt22k44bspry\LocalState\engine-copy-25403b06`
### Mode c: copied to LocalState, AppContainer SID read+execute only
- engine launched: **True** (engine pid 7564; Hello and AttachSlotRing succeeded)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **True** (200x300, 7140 non-white pixels; corners TL FF000000 TR FF0000FF BL FFFF0000 BR FF00FF00)
- negative probe: ran=True, **all denied=False**; token capabilities: 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] **(differs from the request: token capabilities 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250], requested 0)**
  - fs.read.parent-package-data was ALLOWED (Win32 0): file opened
  - fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted

Copy-out target: `C:\Users\runneradmin\AppData\Local\Packages\Quire.SandboxProbe_rtt22k44bspry\LocalState\engine-copy-1d78995a`
### Mode d: copied to LocalState, child breaks away from the package
- engine launched: **False** -> EngineStartException: Starting the engine failed: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. | inner: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **False** 
- negative probe: ran=False, **all denied=False**; token capabilities: 
  - probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)

## Summary
| mode | engine launched | real tile rendered | all probes denied | token capabilities | notes |
|---|---|---|---|---|---|
| a (direct from the package directory, zero capabilities) | True | True | False |  | the probe did not finish (exit 0x60C201E): INFO	isAppContainer	1
INFO	integrityRid	0x1000
INFO	capabilityCount	1
INFO	capabilitySids	S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250
INFO	privilegeCount	2
INFO	mit.creation.DEP	0x3
INFO	mit.creation.ASLR	0xF
INFO	mit.creation.DynamicCode	0x1
INFO	mit.creation.StrictHandleCheck	0x3
INFO	mit.creation.SystemCallDisable	0x0
INFO	mit.creation.MitigationOptionsMask	0x33333303
INFO	mit.creation.ExtensionPointDisable	0x1
INFO	mit.creation.ControlFlowGuard	0x1
INFO	mit.creation.Signature	0x0
INFO	mit.creation.FontDisable	0x1
INFO	mit.creation.ImageLoad	0x7
INFO	m |
| b (direct from the package directory, only the packageContents capability) | False | False | False |  | probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612) |
| e (direct from the package directory, child breaks away from the package) | False | False | False |  | probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612) |
| c (copied to LocalState, AppContainer SID read+execute only) | True | True | False | 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] | fs.read.parent-package-data was ALLOWED (Win32 0): file opened; fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted |
| d (copied to LocalState, child breaks away from the package) | False | False | False |  | probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612) |

