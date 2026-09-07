#!/usr/bin/env python3
"""Derive repeatable Scryfall Oracle-corpus counts for protection-oracle needles.

Downloads Scryfall's "Oracle Cards" bulk-data archive (one card per Oracle ID), caches it
under ``_protection-vocab-research/`` keyed by the bulk descriptor's ``updated_at`` stamp, and
counts how many Commander-legal cards contain each candidate needle substring. Emits a Markdown
report to ``--out`` naming, per needle: the total match count, the "exclusive" match count (cards
this needle matches that no other *supplied* needle also matches — the column that exposes a
needle earning its own place versus riding on another needle's coverage), and a deterministic
sample of matching card names.

Python 3, standard library only. This script derives and counts vocabulary; it is NOT the
classification authority — that is ``DeckFlow.Core/Analysis/DeckStatClassifier.IsProtectionCard``
and its C# unit tests. Python's ``str.lower()`` is not byte-identical to .NET's
``StringComparison.OrdinalIgnoreCase`` for exotic (non-ASCII) characters; for English Oracle text
the two are close enough to guide vocabulary selection, but the C# tests remain the sole authority
on actual classification behavior. Do not try to reimplement ordinal casing here.

Usage:
    python3 scripts/protection-vocab/derive-vocabulary.py \\
        --needle "gains hexproof" --needle "gains indestructible" \\
        --needle "gain protection from" --needle "gains protection from" \\
        --needle "phases out" \\
        --out docs/research/protection-vocabulary-corpus-2026-09.md
"""

from __future__ import annotations

import argparse
import gzip
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

BULK_DATA_URL = "https://api.scryfall.com/bulk-data"
USER_AGENT = "DeckFlow-protection-vocab/1.0 (+https://github.com/luntc1972/DeckFlow)"
DEFAULT_CACHE_DIR = Path("_protection-vocab-research")
EXCLUDED_LAYOUTS = {"token", "double_faced_token", "emblem", "art_series"}
# Why: keeps the report short and diffable; the exact count (not the sample) is the evidence a
# needle earns its place — the sample is illustrative only.
SAMPLE_CAP = 10


def fetch_bulk_descriptor() -> dict[str, Any]:
    """GET the Scryfall bulk-data index and return the "oracle_cards" entry's descriptor."""
    request = urllib.request.Request(
        BULK_DATA_URL,
        headers={"User-Agent": USER_AGENT, "Accept": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:  # noqa: S310 (fixed HTTPS host)
        payload = json.load(response)

    for entry in payload.get("data", []):
        if entry.get("type") == "oracle_cards":
            if "jsonl_download_uri" not in entry:
                print(
                    "FATAL: oracle_cards bulk-data descriptor has no 'jsonl_download_uri'. "
                    f"Descriptor keys: {sorted(entry.keys())}",
                    file=sys.stderr,
                )
                sys.exit(1)
            return entry

    print("FATAL: no 'oracle_cards' entry found in Scryfall bulk-data response.", file=sys.stderr)
    sys.exit(1)


def sanitize_stamp(updated_at: str) -> str:
    """Turn an ISO-8601 timestamp into a filesystem-safe token (colons/dots/plus stripped)."""
    return re.sub(r"[^0-9A-Za-z]+", "-", updated_at).strip("-")


def ensure_archive_cached(descriptor: dict[str, Any], cache_dir: Path) -> Path:
    """Download the archive into cache_dir unless an identically-stamped copy already exists."""
    cache_dir.mkdir(parents=True, exist_ok=True)
    stamp = sanitize_stamp(descriptor["updated_at"])
    archive_path = cache_dir / f"oracle-cards-{stamp}.jsonl.gz"

    if archive_path.exists():
        print(f"Using cached archive: {archive_path}", file=sys.stderr)
        return archive_path

    download_uri = descriptor["jsonl_download_uri"]
    print(f"Downloading {download_uri} -> {archive_path}", file=sys.stderr)
    request = urllib.request.Request(download_uri, headers={"User-Agent": USER_AGENT})
    tmp_path = archive_path.with_suffix(archive_path.suffix + ".part")
    try:
        with urllib.request.urlopen(request, timeout=120) as response, open(tmp_path, "wb") as fh:  # noqa: S310
            while True:
                chunk = response.read(1024 * 1024)
                if not chunk:
                    break
                fh.write(chunk)
        tmp_path.rename(archive_path)
    except (urllib.error.URLError, OSError) as exc:
        tmp_path.unlink(missing_ok=True)
        print(f"FATAL: archive download failed: {exc}", file=sys.stderr)
        sys.exit(1)

    return archive_path


def compose_oracle_text(card: dict[str, Any]) -> str:
    """Mirror DeckFlow.Core.Manabase.ScryfallCardFactMapper.JoinOracleText exactly.

    Multi-face cards join each face's non-blank oracle_text with "\\n"; if that joins to nothing
    (no faces have oracle text), fall back to the card-level oracle_text; single-face cards use
    the card-level oracle_text directly.
    """
    faces = card.get("card_faces")
    if faces:
        parts = [f.get("oracle_text") for f in faces if f.get("oracle_text")]
        joined = "\n".join(parts)
        if joined:
            return joined
        return card.get("oracle_text") or ""

    return card.get("oracle_text") or ""


def is_counted_population(card: dict[str, Any]) -> bool:
    """Commander-legal cards, excluding token/double_faced_token/emblem/art_series layouts."""
    legalities = card.get("legalities") or {}
    if legalities.get("commander") != "legal":
        return False
    if card.get("layout") in EXCLUDED_LAYOUTS:
        return False
    return True


def load_needles(args: argparse.Namespace) -> list[str]:
    needles: list[str] = list(args.needle or [])
    if args.needles_file:
        for line in Path(args.needles_file).read_text(encoding="utf-8").splitlines():
            stripped = line.strip()
            if stripped:
                needles.append(stripped)

    if not needles:
        print("FATAL: no needles supplied — use --needle (repeatable) or --needles-file.", file=sys.stderr)
        sys.exit(1)

    return needles


def count_needles(archive_path: Path, needles: list[str]) -> dict[str, Any]:
    """Single streaming pass over the archive; never loads the whole file into memory."""
    lowered_needles = [n.lower() for n in needles]
    total_counts = [0] * len(needles)
    exclusive_counts = [0] * len(needles)
    name_lists: list[list[str]] = [[] for _ in needles]
    included = 0
    excluded = 0

    with gzip.open(archive_path, "rt", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip().rstrip(",")
            if not line or line in ("[", "]"):
                continue
            try:
                card = json.loads(line)
            except json.JSONDecodeError:
                continue

            if not is_counted_population(card):
                excluded += 1
                continue
            included += 1

            oracle_text = compose_oracle_text(card).lower()
            matched = [i for i, needle in enumerate(lowered_needles) if needle in oracle_text]
            for i in matched:
                total_counts[i] += 1
                name_lists[i].append(card.get("name", ""))
            if len(matched) == 1:
                exclusive_counts[matched[0]] += 1

    # Deterministic sample: the alphabetically-first N matching names, independent of archive
    # traversal order, so two runs over the same archive are byte-identical.
    samples = [sorted(names)[:SAMPLE_CAP] for names in name_lists]

    return {
        "included": included,
        "excluded": excluded,
        "total_counts": total_counts,
        "exclusive_counts": exclusive_counts,
        "samples": samples,
    }


def render_markdown(
    descriptor: dict[str, Any],
    needles: list[str],
    results: dict[str, Any],
    command_line: str,
) -> str:
    lines = [
        "# Protection-Vocabulary Corpus Derivation — 2026-09",
        "",
        "Repeatable Scryfall Oracle-corpus counts for the protection-oracle needles in",
        "`DeckFlow.Core/Analysis/DeckStatClassifier.ProtectionOracleNeedles`. Generated by",
        "`scripts/protection-vocab/derive-vocabulary.py`.",
        "",
        "## Archive",
        "",
        f"- Bulk descriptor `updated_at`: `{descriptor['updated_at']}`",
        f"- Compressed size: `{descriptor.get('compressed_size', 'unknown')}` bytes",
        f"- Source: `{descriptor['jsonl_download_uri']}`",
        "",
        "## Command",
        "",
        "```",
        command_line,
        "```",
        "",
        "## Population",
        "",
        f"- Included (Commander-legal, non-excluded layout): {results['included']}",
        f"- Excluded (not Commander-legal, or token/double_faced_token/emblem/art_series layout): {results['excluded']}",
        "",
        "## Per-Needle Counts",
        "",
        "| Needle | Total matches | Exclusive matches | Sample (first 10 alphabetically) |",
        "|--------|---------------:|-------------------:|-----------------------------------|",
    ]

    for i, needle in enumerate(needles):
        sample = ", ".join(results["samples"][i]) or "(none)"
        lines.append(
            f"| `{needle}` | {results['total_counts'][i]} | {results['exclusive_counts'][i]} | {sample} |"
        )

    lines += [
        "",
        "## How to Reproduce",
        "",
        "```",
        f"python3 scripts/protection-vocab/derive-vocabulary.py {' '.join(f'--needle \"{n}\"' for n in needles)} --out docs/research/protection-vocabulary-corpus-2026-09.md",
        "```",
        "",
        "The archive is cached under `_protection-vocab-research/`, named from the descriptor's",
        "`updated_at` stamp; re-running with the same cached archive present makes zero network",
        "requests and reproduces byte-identical counts (the per-needle sample is the",
        "alphabetically-first 10 matching names, independent of archive traversal order).",
        "",
    ]

    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Derive repeatable Scryfall Oracle-corpus counts for protection-oracle needles.",
    )
    parser.add_argument(
        "--needle",
        action="append",
        help="A candidate needle substring to count (repeatable). Matched case-insensitively.",
    )
    parser.add_argument(
        "--needles-file",
        help="Path to a file of newline-separated needle substrings, combined with --needle.",
    )
    parser.add_argument(
        "--out",
        required=True,
        help="Path to write the Markdown report to.",
    )
    parser.add_argument(
        "--cache-dir",
        default=str(DEFAULT_CACHE_DIR),
        help=f"Directory to cache the downloaded archive in (default: {DEFAULT_CACHE_DIR}).",
    )
    args = parser.parse_args()

    needles = load_needles(args)
    descriptor = fetch_bulk_descriptor()
    archive_path = ensure_archive_cached(descriptor, Path(args.cache_dir))
    results = count_needles(archive_path, needles)

    command_line = "python3 scripts/protection-vocab/derive-vocabulary.py " + " ".join(
        f'--needle "{n}"' for n in needles
    ) + f" --out {args.out}"
    report = render_markdown(descriptor, needles, results, command_line)

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(report, encoding="utf-8")
    print(f"Wrote {out_path}", file=sys.stderr)


if __name__ == "__main__":
    main()
