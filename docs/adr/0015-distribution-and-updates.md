# ADR-0015: Distribution channels and update mechanism
- Status: Accepted
- Date: 2026-11-02
- Deciders: User (open source, Store publishing), Opus (approver)

## Context
§15.3 planned an App Installer file for v1.0, with the Store and winget as P2. The user has now asked to make MikePDF open source and to get it ready for the Microsoft Store. Checked against official Microsoft documentation on 2026-11-02 (research notes in [docs/store/README.md](../store/README.md)):
- The Store **re-signs MSIX packages**, so a Store release needs no code-signing certificate.
- Partner Center accounts are free when started from `storedeveloper.microsoft.com`.
- A package's first submission must be made by hand. Later updates can be automated with the Microsoft Store Developer CLI.
- The fourth version part must be 0, and the first part can't be 0.
- Choosing a public audience for a listing is **one-way**. A private audience works only before the app goes public.
- Policy 10.5.1 requires a privacy policy for Win32 and Desktop Bridge products, so we treat the URL as mandatory.

## Options considered
1. **App Installer over HTTPS** (the §15.3 plan). Needs a trusted code-signing certificate. Risk R-18 showed that's hard to obtain as an individual outside the US or Canada.
2. **Microsoft Store first.** No certificate, automatic updates, reputation handled by Microsoft, staged rollout built in. **Chosen.**
3. Both at once. Deferred; there's no need yet.

## Decision
1. **The Microsoft Store is the first distribution channel.** App Installer and winget are revisited when enterprise offline deployment work starts. Enterprise Intune deployment can use the Store too.
2. **First release goes to a private audience.** It stays private until a release decision is recorded, because going public can't be undone.
3. **Identity lives in `build/store/identity.json`,** with placeholders until Partner Center issues the real values. Package and bundle scripts read it, so no code changes when the values arrive.
4. **Versioning:** `MAJOR.MINOR.BUILD.0`. The first Store package is `1.0.0.0`, because the first part can't be 0. The Store listing, not the version number, says it's a preview.
5. **CI builds the upload.** `.github/workflows/store-release.yml` builds both architectures and produces the `.msixupload`. Automated publishing is present, but stays off until the user sets the repository variable and secrets after the first manual submission.
6. **No shell thumbnail or preview handlers in the Store preview.** Without a brokered engine they'd parse untrusted files at medium integrity, which ADR-0004 forbids. They return with §8.11. *Update 2026-11-03: they have returned, as [ADR-0018](0018-shell-handler-transport.md) decided (the DLL parses nothing; ShellHost and the sandboxed engine do). The package now registers them (OQ-SHELL-9: a Store resubmission re-runs certification).*

## Consequences
- R-18 is resolved for the Store route. A certificate is needed only if direct downloads are added later.
- `build/package.ps1` is the single route to a package for local tests and CI.
- Sideload testing uses an unsigned test package, installed in Developer Mode. It never goes to users.
- This reverses the §2.3 and §15.3 ordering (Store as P2). The master prompt isn't edited; this ADR takes precedence.

## Verification
- `store-release.yml` produces a `.msixupload` containing x64 and ARM64 packages.
- The Windows App Certification Kit passes on the package.
- The first manual Partner Center submission passes certification.
