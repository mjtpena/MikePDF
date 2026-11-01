# Quire sandbox-in-package probe
Run at 2026-11-01T20:51:26.8545574Z, OS 10.0.26100.0, arch X64

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
- engine launched: **True** (engine pid 4324; Hello and AttachSlotRing succeeded)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **True** (200x300, 7140 non-white pixels; corners TL FF000000 TR FF0000FF BL FFFF0000 BR FF00FF00)
- negative probe: finished=False, original suite all denied=**False**, other-package denied=True, own-package-data denied=False; token capabilities: 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] **(differs from the request: token capabilities 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250], requested 0)**
  - the probe did NOT finish (exit 0x60C201E); last row printed: dll.load.unsigned-temp-copy
  - fs.read.parent-package-data was ALLOWED (Win32 0): file opened
  - fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted

### Mode b: direct from the package directory, only the packageContents capability
- engine launched: **False** -> EngineStartException: Starting the engine failed: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. | inner: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **False** 
- negative probe: finished=False, original suite all denied=**False**, other-package denied=False, own-package-data denied=False; token capabilities: 
  - probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)

Copy-out target (mode c): `C:\Users\runneradmin\AppData\Local\Packages\Quire.SandboxProbe_rtt22k44bspry\LocalState\engine-copy-0091d112`
### Mode c: copied to LocalState, AppContainer SID read+execute only
- engine launched: **True** (engine pid 6504; Hello and AttachSlotRing succeeded)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **True** (200x300, 7140 non-white pixels; corners TL FF000000 TR FF0000FF BL FFFF0000 BR FF00FF00)
- negative probe: finished=True, original suite all denied=**True**, other-package denied=True, own-package-data denied=False; token capabilities: 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] **(differs from the request: token capabilities 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250], requested 0)**
  - fs.read.parent-package-data was ALLOWED (Win32 0): file opened
  - fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted

Copy-out target (mode g): `C:\ProgramData\Quire-probe\engine-copy-1ecccaac`
### Mode g: copied to ProgramData\Quire-probe, AppContainer SID read+execute only
- engine launched: **True** (engine pid 5068; Hello and AttachSlotRing succeeded)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **True** (200x300, 7140 non-white pixels; corners TL FF000000 TR FF0000FF BL FFFF0000 BR FF00FF00)
- negative probe: finished=True, original suite all denied=**True**, other-package denied=True, own-package-data denied=False; token capabilities: 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] **(differs from the request: token capabilities 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250], requested 0)**
  - fs.read.parent-package-data was ALLOWED (Win32 0): file opened
  - fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted

Copy-out target (mode h): `C:\Users\runneradmin\AppData\Local\Quire-probe\engine-copy-cbfeab58`
- where MSIX redirection put it: `C:\Users\runneradmin\AppData\Local\Packages\Quire.SandboxProbe_rtt22k44bspry\LocalCache\Local\Quire-probe` exists=True
### Mode h: copied to LOCALAPPDATA\Quire-probe (MSIX may redirect this), AppContainer SID read+execute only
- engine launched: **False** -> EngineStartException: The engine exited with code 0xC0000135 before it connected. Log: 
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **False** 
- negative probe: finished=True, original suite all denied=**False**, other-package denied=True, own-package-data denied=False; token capabilities: 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] **(differs from the request: token capabilities 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250], requested 0)**
  - fs.read.parent-package-data was ALLOWED (Win32 0): file opened
  - fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted
  - dll.load.unsigned-in-engine-dir was FAILED (Win32 126): LoadLibrary
  - positive control ctl.read.engine-dir failed (Win32 3): open file

Copy-out target (mode i): `C:\ProgramData\Quire-probe\engine-copy-93c84f72`
### Mode i: copied to ProgramData\Quire-probe, free-standing (non-child) AppContainer SID
- engine launched: **False** -> EngineStartException: Starting the engine failed: CreateProcess in the AppContainer failed: The system cannot find the file specified. | inner: CreateProcess in the AppContainer failed: The system cannot find the file specified. (Win32 2)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **False** 
- negative probe: finished=False, original suite all denied=**False**, other-package denied=False, own-package-data denied=False; token capabilities: 
  - probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The system cannot find the file specified. (Win32 2)

### Mode j: direct from the package directory, free-standing (non-child) AppContainer SID
- engine launched: **False** -> EngineStartException: Starting the engine failed: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. | inner: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **False** 
- negative probe: finished=False, original suite all denied=**False**, other-package denied=False, own-package-data denied=False; token capabilities: 
  - probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612)

## Summary
| mode | engine launched | real tile rendered | original negative suite all denied | other-package read denied | own package data read+write denied | token capabilities | notes |
|---|---|---|---|---|---|---|---|
| a (direct from the package directory, zero capabilities) | True | True | False | True | False | 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] | the probe did NOT finish (exit 0x60C201E); last row printed: dll.load.unsigned-temp-copy; fs.read.parent-package-data was ALLOWED (Win32 0): file opened; fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted |
| b (direct from the package directory, only the packageContents capability) | False | False | False | False | False |  | probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612) |
| c (copied to LocalState, AppContainer SID read+execute only) | True | True | True | True | False | 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] | fs.read.parent-package-data was ALLOWED (Win32 0): file opened; fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted |
| g (copied to ProgramData\Quire-probe, AppContainer SID read+execute only) | True | True | True | True | False | 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] | fs.read.parent-package-data was ALLOWED (Win32 0): file opened; fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted |
| h (copied to LOCALAPPDATA\Quire-probe (MSIX may redirect this), AppContainer SID read+execute only) | False | False | False | True | False | 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] | fs.read.parent-package-data was ALLOWED (Win32 0): file opened; fs.write.parent-package-data was ALLOWED (Win32 0): file created and deleted; dll.load.unsigned-in-engine-dir was FAILED (Win32 126): LoadLibrary |
| i (copied to ProgramData\Quire-probe, free-standing (non-child) AppContainer SID) | False | False | False | False | False |  | probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The system cannot find the file specified. (Win32 2) |
| j (direct from the package directory, free-standing (non-child) AppContainer SID) | False | False | False | False | False |  | probe launch failed: SandboxLaunchException: CreateProcess in the AppContainer failed: The application cannot be started. Try reinstalling the application to fix the problem. (Win32 15612) |

