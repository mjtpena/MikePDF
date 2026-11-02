# Certification notes

Paste these into Partner Center under **Submission options**. Update them if the app's behaviour changes.

## Why this app needs `runFullTrust`
> MikePDF is a PDF reader built on WinUI 3 with the Windows App SDK, which runs as a packaged full-trust desktop app. It uses runFullTrust for two reasons. First, the Windows App SDK desktop app model requires it. Second, for security, the app doesn't parse PDF files in its own process. It starts a helper executable bundled in the package, MikePDF.Engine.exe, in place from the package directory, inside an AppContainer. It requests no capabilities; Windows gives the child only the package's own identity capability. The app passes it the document through a duplicated read-only file handle. The helper can't access the network, user files, other packages or other processes. Creating that sandbox from the app requires a full-trust parent process. The app doesn't install services or drivers, change system settings, or download code. It makes no network connections. Source code: https://github.com/mjtpena/MikePDF

## Notes for certification testers
> No account, sign-in or network connection is needed. To test: launch the app, choose Open, and select any PDF. You can also drag a PDF onto the window or open one from File Explorer via Open with. Scroll with the mouse wheel, zoom with Ctrl+mouse wheel, and use Ctrl+G to go to a page.
>
> The app starts a helper process, MikePDF.Engine.exe, bundled in the package, for each open document. It's expected behaviour: the helper runs in an AppContainer sandbox and renders PDF pages. It's built from the public source and links PDFium, which is included as pdfium.dll.
>
> The app declares a .pdf file association but doesn't change the user's default app. Users choose it in Windows Settings.

## Age-rating questionnaire (IARC)
MikePDF is a utility that displays documents the user opens. Answer based on the app itself, not on possible document content:

| Question area | Answer |
|---|---|
| Category | Productivity / utility |
| Violence, sexual content, drugs, gambling, strong language | None in the app |
| Users can interact or share content with each other | No |
| Shares the user's location | No |
| Purchases, digital goods | No |
| Unrestricted internet access or browser | No. The app makes no network connections |
| Collects personal information | No |

If a question doesn't fit, answer for what the app does and don't guess. The rating is issued by IARC at submission.

## Windows App Certification Kit
Run it before each submission. CI uploads the report as the `wack-report` artifact, and fails on anything except the optional **Blocked executables** test. MikePDF is expected to fail that one: the broker has to start the engine with `CreateProcess`, and .NET's own files reference process-launch APIs too. Locally it needs an elevated prompt:

```powershell
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" reset
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" test -appxpackagepath <package> -reportoutputpath <report.xml>
```

## File Explorer thumbnails and preview pane
> MikePDF registers a thumbnail provider and a preview handler for .pdf files (desktop2:ThumbnailHandler and desktop2:DesktopPreviewHandler, hosted by com:SurrogateServer classes). The DLL (ShellExt\MikePDF.ShellExt.dll) runs in the system's own thumbnail and preview host processes and parses nothing: it copies the file's bytes into a memory section, asks MikePDF.ShellHost.exe (ShellHost\, started on demand, gone after 60 seconds idle) over a per-user named pipe, and draws the bitmap that comes back after checking it. ShellHost starts the same sandboxed AppContainer engine as the app, one fresh process per file. To see it: put a PDF in a folder, choose Large icons, and open the preview pane (Alt+P). Both can be turned off in Settings (Privacy and data) or by policy (docs/admin/policies.md).
