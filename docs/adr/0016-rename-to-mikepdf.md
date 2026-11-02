# ADR-0016: Rename the product to MikePDF
- Status: Accepted
- Date: 2026-11-02
- Deciders: User (name), Opus (approver and scope)

## Context
The user renamed the project from Quire to MikePDF before any release. Nothing has shipped, so no installed base, saved data or file format depends on the old name. The old name also had conflicts: a Chrome extension called Quire PDF and the Quire project-management product.

## Options considered
1. Change only the display name and Store listing. Smallest change, but the old name would stay in executables, the package and every path, where users and contributors see it.
2. Rename everything current, and leave historical records as they were. Larger change, once, with no compatibility cost now.

## Decision
Option 2.
- `Quire` becomes `MikePDF`, `quire` becomes `mikepdf` and `QUIRE` becomes `MIKEPDF`, in namespaces, projects, executables, the package, environment variables, C++ namespaces and documents. There is one spelling, so .NET identifiers keep the capitals of the product name.
- **Not renamed:** `docs/MASTER-PROMPT.md` (verbatim), and `spikes/`, `docs/spikes/` and `docs/test-reports/`. These record what was run and measured under the old name, and their paths and logs need to keep matching. Nothing in the build uses them.
- The GitHub repository was renamed from `mjtpe/QuirePDF` to `mjtpe/MikePDF` on 2026-11-02, at the user's request. GitHub redirects the old URL, and links now use the new one.

## Consequences
- Old names in the historical folders and in earlier commits mean this project.
- Developers with a Quire build registered have to remove it; `Quire.Dev` and `MikePDF.Dev` are different packages.
- The Store identity values still come from Partner Center, after the name is reserved.

## Verification
- No `Quire` is left outside the folders listed above.
- The tests, UI journeys, package self-test and sandbox probe all pass under the new name, on CI as well as locally.
