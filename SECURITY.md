# Security policy

Quire opens untrusted documents, so we take reports seriously.

## Reporting a vulnerability
**Please don't open a public issue.** Report it privately through GitHub: the repository's **Security** tab, then **Report a vulnerability**.

Include what you can: affected version or commit, a minimal test file, and what an attacker gains. Please don't send files containing real personal data.

We aim to acknowledge reports within 5 working days. Crashes inside PDFium itself are also reported upstream under PDFium's security process, and Quire then pins a patched build.

## Scope
In scope: escapes from the engine or writer sandbox, attacks on the broker through IPC, signature-validation bypasses, redaction leaks, malicious links and attachments, and update tampering. The threat model is in [docs/threat-model](docs/threat-model/README.md).

A crash that stays inside a sandboxed engine process is still a bug worth reporting, but it isn't a sandbox escape.

## Supported versions
Quire is pre-1.0. Only the latest commit on `main` and the latest Store release get fixes.
