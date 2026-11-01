# Certification notes

Paste these into Partner Center under **Submission options**. Update them if the app's behaviour changes.

## Why this app needs `runFullTrust`
> Quire is a PDF reader built on WinUI 3 with the Windows App SDK, which runs as a packaged full-trust desktop app. It uses runFullTrust for two reasons. First, the Windows App SDK desktop app model requires it. Second, for security, the app doesn't parse PDF files in its own process. It starts a helper executable bundled in the package, Quire.Engine.exe, inside an AppContainer with no capabilities, and passes it the document through a duplicated read-only file handle. The helper can't access the network, user files or other processes. Creating that sandbox from the app requires a full-trust parent process. The app doesn't install services or drivers, change system settings, or download code. It makes no network connections. Source code: https://github.com/mjtpena/QuirePDF

## Notes for certification testers
> No account, sign-in or network connection is needed. To test: launch the app, choose Open, and select any PDF. You can also drag a PDF onto the window or open one from File Explorer via Open with. Scroll with the mouse wheel, zoom with Ctrl+mouse wheel, and use Ctrl+G to go to a page.
>
> The app starts a helper process, Quire.Engine.exe, bundled in the package, for each open document. It's expected behaviour: the helper runs in an AppContainer sandbox and renders PDF pages. It's built from the public source and links PDFium, which is included as pdfium.dll.
>
> The app declares a .pdf file association but doesn't change the user's default app. Users choose it in Windows Settings.

## Age-rating questionnaire (IARC)
Quire is a utility that displays documents the user opens. Answer based on the app itself, not on possible document content:

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
Run it before each submission. CI uploads the report as the `wack-report` artifact. Locally it needs an elevated prompt:

```powershell
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" reset
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" test -appxpackagepath <package> -reportoutputpath <report.xml>
```
