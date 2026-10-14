"""Merges the Cobertura files of every test project into one per-assembly line-coverage table (Markdown).

A line counts as covered when any test project ran it. Generated code (migrations, *.g.cs, Program.cs, the
design-time factory) is reported separately because it would otherwise skew the numbers.

Usage: coverage-summary.py <directory with *.cobertura.xml> [repository root]
"""

import glob
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

GENERATED = re.compile(r"(/Migrations/|/obj/|\.g\.cs$|/Program\.cs$|DesignTime|DbContextFactory\.cs$)", re.IGNORECASE)


def collect(directory):
    """Returns {(assembly, source file): {line number: max hits}}."""
    lines = defaultdict(dict)
    for path in glob.glob(os.path.join(directory, "**", "*.cobertura.xml"), recursive=True):
        root = ET.parse(path).getroot()
        sources = [s.text for s in root.iter("source") if s.text]
        for package in root.iter("package"):
            for cls in package.iter("class"):
                filename = cls.get("filename") or ""
                full = filename
                if not os.path.isabs(filename):
                    full = next((os.path.join(s, filename) for s in sources if os.path.exists(os.path.join(s, filename))), filename)
                key = (package.get("name"), full.replace("\\", "/"))
                for line in cls.iter("line"):
                    number, hits = int(line.get("number")), int(line.get("hits"))
                    lines[key][number] = max(lines[key].get(number, 0), hits)
    return lines


def totals(rows):
    covered = sum(1 for hits in rows for h in hits.values() if h > 0)
    total = sum(len(hits) for hits in rows)
    return covered, total


def percent(covered, total):
    return f"{100 * covered / total:.1f}%" if total else "n/a"


def main():
    directory = sys.argv[1]
    lines = collect(directory)
    if not lines:
        print("No Cobertura files found; nothing to report.")
        return

    by_assembly = defaultdict(list)
    for (assembly, filename), hits in lines.items():
        by_assembly[assembly].append((filename, hits))

    print("## Code coverage (line)\n")
    print("| Assembly | Lines | Coverage | Lines without generated code | Coverage without generated code |")
    print("|---|---:|---:|---:|---:|")
    all_rows, all_hand_written = [], []
    for assembly in sorted(by_assembly):
        rows = [hits for _, hits in by_assembly[assembly]]
        hand_written = [hits for filename, hits in by_assembly[assembly] if not GENERATED.search(filename)]
        all_rows += rows
        all_hand_written += hand_written
        c, t = totals(rows)
        hc, ht = totals(hand_written)
        print(f"| {assembly} | {t} | {percent(c, t)} | {ht} | {percent(hc, ht)} |")
    c, t = totals(all_rows)
    hc, ht = totals(all_hand_written)
    print(f"| **All** | **{t}** | **{percent(c, t)}** | **{ht}** | **{percent(hc, ht)}** |")
    print("\nA line is covered when any test project ran it. This is a report only: no threshold is enforced.")


if __name__ == "__main__":
    main()
