# Quire sandbox-in-package probe
Run at 2026-11-01T22:18:03.2705304Z, OS 10.0.26200.0, process architecture Arm64

## Package identity and token of this process (the broker)
- GetCurrentPackageFullName: `Quire.SandboxProbe_1.0.0.0_arm64__rtt22k44bspry`
- GetCurrentPackageFamilyName: `Quire.SandboxProbe_rtt22k44bspry`
- GetCurrentPackagePath: `C:\Program Files\WindowsApps\Quire.SandboxProbe_1.0.0.0_arm64__rtt22k44bspry`
- Under WindowsApps: **True**
- Integrity level (RID): **0x3000** (high); administrator role: True; user runneradmin

## ACL of the package directory and LocalState (all paths in acl.txt)
```
C:\Program Files\WindowsApps\Quire.SandboxProbe_1.0.0.0_arm64__rtt22k44bspry BUILTIN\Users:(OI)(CI)(Rc,S,RD,REA,X,RA)
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
Protected private directory:
```
C:\Users\runneradmin\AppData\Local\Packages\Quire.SandboxProbe_rtt22k44bspry\LocalState\private-probe NT AUTHORITY\SYSTEM:(OI)(CI)(F)
                                                                                                      runnervmmaf2v\runneradmin:(OI)(CI)(F)

Successfully processed 1 files; Failed processing 0 files

```
AppContainer profile `Quire.PackageProbe` created by this call: True; SID `S-1-15-2-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250-329789988-3524764629-4215146597-2989021428`

Control (unsandboxed) probe: finished=True, rows=23, deny-rows that were ALLOWED in the control=19
Control for the unsigned-DLL row: the DLL loaded and its code ran (marker seen) = **True**

### Mode a: direct from the package directory
- engine launched: **True** (engine pid 3844; Hello and AttachSlotRing succeeded)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **True** (200x300, 7140 non-white pixels; corners TL FF000000 TR FF0000FF BL FFFF0000 BR FF00FF00)
- main probe: finished=True, rows run=23; token capabilities: 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250]
- unsigned DLL in its own process: row=(none: the process ended first), process exited=True exit code 0x60C201E, DLL code ran (marker)=False, marker mechanism proven by the control=True, Code Integrity events naming the DLL: none read
  - control: fs.write.engine-dir was DENIED outside the sandbox, so its denial proves nothing

Copy-out target (mode k): `C:\Users\runneradmin\AppData\Local\Packages\Quire.SandboxProbe_rtt22k44bspry\LocalState\engine-copy-b9e97052` (twin for the rename row: `C:\Users\runneradmin\AppData\Local\Packages\Quire.SandboxProbe_rtt22k44bspry\LocalState\engine-copy-b9e97052-twin`)
### Mode k: copied to LocalState, protected DACL
- engine launched: **True** (engine pid 10108; Hello and AttachSlotRing succeeded)
- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **True** (200x300, 7140 non-white pixels; corners TL FF000000 TR FF0000FF BL FFFF0000 BR FF00FF00)
- main probe: finished=True, rows run=23; token capabilities: 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250]
- unsigned DLL in its own process: row=DENIED Win32 577, process exited=True exit code 0x0, DLL code ran (marker)=False, marker mechanism proven by the control=True, Code Integrity events naming the DLL: yes (see rogue-events-k.txt)
  - control: fs.write.engine-dir was DENIED outside the sandbox, so its denial proves nothing

## Summary
Broker integrity level 0x3000 (high), arch Arm64, OS 10.0.26200.0.

| mode | launched | tile rendered | rows run / all denied except the known package-data rows | other package denied | engine dir write denied | engine dir rename denied | protected private file read denied | protected private dir rename denied | unsigned DLL: denied, code did not run | package capability in the child's token |
|---|---|---|---|---|---|---|---|---|---|---|
| a (direct from the package directory) | True | True | 23 / False | True | True | True | True | True | True | 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] |
| k (copied to LocalState, protected DACL) | True | True | 23 / False | True | True | False | True | True | True | 1 [S-1-15-3-1858972943-3405380640-321896579-116791022-2625772238-946535615-3133892250] |

- mode a problems: control: fs.write.engine-dir was DENIED outside the sandbox, so its denial proves nothing
- mode k problems: control: fs.write.engine-dir was DENIED outside the sandbox, so its denial proves nothing

