# M0 gate waiver: M1 foundations only

- Date: 2026-10-28
- Granted by: the user (product owner), in chat
- Recorded by: Opus
- Exact instruction: "Waive the M0 gate for M1 foundations only (repo skeleton, CI, IPC schema, engine host, PdfWriter core). Keep performance and fidelity gates open until the hardware and manual passes land."

## What the waiver allows
M1 work can start before the M0 gate passes, **limited to**:
1. Repo skeleton (§5.5), build props, analysers, central package management
2. CI workflow
3. IPC schema and code generation (ADR-0005)
4. Engine host (C++20, ADR-0004 sandbox profile, ADR-0005 IPC), plus the broker-side launcher and watchdog it needs
5. `Quire.PdfWriter` core and `Quire.WriterHost` (ADR-0008)

## What stays closed
- **Every performance gate** (§11; S2, S3, S6 numbers on R1/R2/R3) and **every fidelity gate** (S4/S8 Acrobat and Edge manual passes; §12.4).
- M0 itself stays **not passed**. This waiver doesn't change the [M0 report](M0-report.md). M1 can't be declared passed while M0 is open.
- No UI work (M2) and no features beyond the five items above.
- ADR-0001 (24H2 minimum) and ADR-0003 (licence) stay Proposed. M1 code follows ADR-0003's interim policy.

## Risks accepted with this waiver
- If R1/R2 numbers or the manual passes overturn an ADR (for example, Acrobat rejecting Quire-written xref streams), some M1 code gets reworked. That's mitigated by ADRs 0004, 0005 and 0008 resting on correctness evidence that is already in, not on performance numbers.
