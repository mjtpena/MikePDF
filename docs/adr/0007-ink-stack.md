# ADR-0007: Ink stack
- Status: **Proposed.** The latency gate is unmeasured (S5 needs a Surface Pro, a pen and a 240 fps camera)
- Date: 2026-10-28
- Deciders: Opus (approver), Sonnet (S5 inventory)

## Context
§8.3 and §11 require pressure-sensitive ink with ≤ 25 ms pen-to-pixel latency, palm rejection, a highlighter, and conversion to `/Ink` annotations with appearance streams. [S5](../spikes/S5.md) inventoried what Windows App SDK 2.5.1 offers:
- WinUI 3 has **no `InkCanvas` or ink presenter**, and Win2D 1.4.0 has **no `DrawInk`**.
- `PointerPointProperties` exposes pressure, tilt, barrel button and eraser. `GetIntermediatePoints` and `PointerPredictor` exist.
- The DirectComposition delegated-ink COM objects can be created, but there's no confirmed way to attach them to the WinUI visual tree.

[S4](../spikes/S4.md) showed variable-width ink must live in the appearance stream, because `/BS /W` is a single number per annotation.

## Options considered
1. Native WinUI 3 ink. **Not available.**
2. **Custom pipeline:** `GetIntermediatePoints` → MikePDF smoothing → Win2D drawing, with `PointerPredictor` for latency. **Chosen as the working assumption.**
3. Option 2 plus DirectComposition delegated ink trails. **An experiment for the device, not a commitment.**

## Decision
- **`MikePDF.Ink`** owns capture, smoothing, the eraser and stroke batching, and has no UWP dependency. The same stroke model drives both the screen rendering and the `/Ink` output (`/InkList` plus a variable-width filled-outline appearance stream written by `MikePDF.PdfWriter`, ADR-0008).
- Live strokes go on a dedicated ink layer above the tiles (§7.1 rule 7). They're committed to an annotation on pen-up, and batched per §8.3.
- Palm rejection: in pen mode, ignore touch contacts while the pen is in range, and route finger input to scrolling.
- Option 3 is tried on real hardware during S5's measurement run. Adopt it only if it beats option 2 and attaches to WinUI cleanly.

## Consequences
- More MikePDF-owned code (smoothing, prediction tuning). In return, ink is consistent between the screen and the saved file.
- If option 2 misses 25 ms p95 on the Surface, this ADR must either adopt option 3 or revise the budget with evidence (§11 allows that through an ADR).

## Verification
- S5 measurement run on the Surface with the camera: p95 ≤ 25 ms, monotonic pressure, and no palm strokes in 5 minutes. **This is a blocker for M4 (ink latency gate), not for M0's architecture.**
