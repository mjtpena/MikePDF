"""Shared helpers for the S1 scripts: locate the pinned PDFium variants and read PE exports."""
from pathlib import Path

import pefile

REPO = Path(__file__).resolve().parents[2]
PDFIUM_ROOT = REPO / "spikes" / "_shared" / "pdfium"
OUT = Path(__file__).resolve().parent / "out"

# variant label -> directory under spikes/_shared/pdfium
VARIANTS = {
    "x64": "pdfium-win-x64",
    "arm64": "pdfium-win-arm64",
    "v8-x64": "pdfium-v8-win-x64",
}


# Own builds registered at run time (symdiff.py --extra label=path); empty in the prebuilt-only run.
EXTRA: dict[str, Path] = {}


def dll_path(label: str) -> Path:
    if label in EXTRA:
        return EXTRA[label]
    return PDFIUM_ROOT / VARIANTS[label] / "bin" / "pdfium.dll"


def include_dir(label: str = "x64") -> Path:
    return PDFIUM_ROOT / VARIANTS[label] / "include"


def exported_names(path: Path) -> set[str]:
    """Names of all named exports of a PE file (ordinal-only exports are not included)."""
    pe = pefile.PE(str(path), fast_load=True)
    pe.parse_data_directories(
        directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]]
    )
    names = {e.name.decode("ascii") for e in pe.DIRECTORY_ENTRY_EXPORT.symbols if e.name}
    pe.close()
    return names
