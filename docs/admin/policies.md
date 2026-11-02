# MikePDF enterprise policies

- Status: first version, written with the settings and policy provider (M3). The full admin guide, ADMX and ADML templates, and Intune examples are M5 and P2 work.
- Spec: [settings-policies](../features/settings-policies.md) section 3.4. The names below are a contract: a name is never renamed, because the templates will quote it.
- A test (`SET-25`) fails if this table and the policy list in `MikePDF.Core` (`PolicyCatalog`) differ.

## Where policies are read

MikePDF reads `HKEY_LOCAL_MACHINE\Software\Policies\MikePDF` first and `HKEY_CURRENT_USER\Software\Policies\MikePDF` second. Reading needs no elevation. MikePDF never writes either key.

- **Machine values win.** A standard user cannot change them.
- **User values can only make behaviour stricter.** A user-level value is used only if it is at least as strict as the machine value, or as the default when there is no machine value. A policy marked "machine only" ignores user-level values. Both cases show in About as "Ignored".
- **Value names are not case sensitive.** String values are trimmed. A value of the wrong type, out of range, or too long is *invalid*.
- **Invalid values.** A policy marked "fail closed" gives its restrictive result for an invalid value, and also when the registry cannot be read. The others ignore an invalid value, as if it were not set.
- **Changes apply while MikePDF runs**, within about 5 seconds, with no restart.
- A locked setting is disabled in Settings with the text "Managed by your organisation", and its command in the palette says why it is unavailable.
- Open **About** (or Settings, Enterprise policies) to see exactly what MikePDF received and what it resolved to. "Copy details" puts that report on the clipboard.

## The policies

| Value name | Type | Values | Not set means | Invalid means | User level | Effect in this build |
|---|---|---|---|---|---|---|
| `JavaScriptEnabled` | DWORD | 0, 1 | 0 | 0 (fail closed) | stricter only | Reserved for a JavaScript-capable engine. This build has none, so 1 changes nothing; About says "not available in this build". |
| `ExternalLinks` | DWORD | 0 block, 1 show the address, 2 ask first | 1 | 0 (fail closed) | stricter only | What happens when a link leads outside the document. MikePDF never opens an address; 2 shows the address like 1. Locks the setting "Links that lead outside the document". |
| `AllowedLinkSchemes` | MULTI_SZ | up to 16 schemes, `[a-z][a-z0-9+.-]{0,31}`; `file` is ignored | https, http, mailto | the default list | machine only | Read and shown. Nothing opens an address yet. |
| `AiMode` | DWORD | 0 off, 1 local only, 2 local and cloud | not configured | 0 (fail closed) | stricter only | Reserved until the AI features ship. Shown in About. |
| `AiAllowedEndpoints` | MULTI_SZ | up to 16 absolute `https` URLs, 1,024 characters each | none | none | machine only | Reserved until the AI features ship. |
| `TelemetryEnabled` | DWORD | 0, 1 | 0 | 0 (fail closed) | stricter only | This build has no telemetry code, whatever the value. About says "Telemetry: off (this build has none)". |
| `SaveMode` | DWORD | 0 save in place, 1 always save as a copy | 0 | 0 | stricter only (1 is stricter) | Takes effect when saving ships. Locks the setting `save.mode`. |
| `EngineMemoryLimitMB` | DWORD | 256 to 8192 | 2048 | 2048 | stricter only (lower is stricter) | The memory limit for each viewer process. Read when a viewer starts. |
| `MaxLiveTabs` | DWORD | 1 to 32 | 6 | 6 | stricter only (lower is stricter) | Takes effect when tabs ship. Locks the setting `engines.maxLiveTabs`. |
| `PrintingEnabled` | DWORD | 0, 1 | 1 | 1 | stricter only | 0 turns printing off everywhere, when printing ships. |
| `ShellThumbnailsEnabled` | DWORD | 0, 1 | 1 | 0 (fail closed) | stricter only | 0 turns off the Explorer thumbnails and locks the setting `shell.thumbnails`. The thumbnail handler reads this value itself before it does anything, so on a machine where it is 0 the handler never starts MikePDF's shell helper. While it is off, Explorer shows the generic PDF icon (see "What turning the shell handlers off shows" below). 1 changes nothing: the person's own setting stands. |
| `ShellPreviewEnabled` | DWORD | 0, 1 | 1 | 0 (fail closed) | stricter only | 0 turns off the Explorer preview pane and locks the setting `shell.preview`; the pane says "Preview is turned off by your organisation". Read by the preview handler itself, like `ShellThumbnailsEnabled`. |
| `ShellMaxEngines` | DWORD | 1 to 6 | 3 | 3 | stricter only (lower is stricter) | The most viewer processes the Explorer thumbnail and preview helper may run at once, the spare one it starts ahead included. Locks the setting `shell.maxEngines`. |
| `DefaultAppPromptEnabled` | DWORD | 0, 1 | 1 | 0 (fail closed) | machine only | 0 hides the "make MikePDF the default" prompt, when it ships. |
| `SettingsImportExportEnabled` | DWORD | 0, 1 | 1 | 0 (fail closed) | stricter only | 0 turns off importing and exporting settings and shortcuts, in Settings and in the command palette. |
| `AnnotationAuthorName` | SZ | 1 to 128 characters | not configured | not configured | machine only | Takes effect when comments ship. Fixes the author written into new comments and locks the setting `markup.authorName`. |
| `AllowWeakerSandbox` | DWORD | 0 | 0 | 0 | machine only | Not implemented, on purpose: there is no weaker sandbox. Read only so that About can say it is ignored. |
| `UpdateChannel` | DWORD | 0 | not applicable | not applicable | machine only | No effect. Updates come through the Microsoft Store. |

## What turning the shell handlers off shows

MikePDF's thumbnail and preview handlers are registered by the package, so they apply to every PDF in File Explorer for as long as the package is installed, whichever app is the default for `.pdf` (measured in spike S6b, docs/spikes/S6b-shell-transport.md). The policies and the settings do not unregister them: they make the handlers decline. Whether Explorer then falls through to the next handler registered for `.pdf` or shows the generic icon is tested by the integration workflow (docs/features/shell-extensions.md section 3.5, OQ-SHELL-14). Until that result is recorded here, Settings says that Explorer shows the generic icon.

## Example: scenario 9 (JavaScript off, AI local only, telemetry off)

```
reg add HKLM\Software\Policies\MikePDF /v JavaScriptEnabled /t REG_DWORD /d 0 /f
reg add HKLM\Software\Policies\MikePDF /v AiMode /t REG_DWORD /d 1 /f
reg add HKLM\Software\Policies\MikePDF /v TelemetryEnabled /t REG_DWORD /d 0 /f
```

These commands need an elevated prompt and are an example for an administrator; MikePDF's own tests never write to HKLM.
