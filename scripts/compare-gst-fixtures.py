"""Compare native SDK fixture exports with Nila's independent native expectations.

Usage: python scripts/compare-gst-fixtures.py EXPORT_DIRECTORY NILA_REPOSITORY
EXPORT_DIRECTORY contains native-2026-04.json and native-2026-05.json.
Only comparison copies are normalized; source files are never changed.
"""
import hashlib
import json
import sys
from pathlib import Path


def compare(export_root, nila_root):
    for current, baseline in [("04", "02"), ("05", "03")]:
        source = export_root / f"native-2026-{current}.json"
        raw = source.read_bytes()
        actual = json.loads(raw)
        expected = json.loads((nila_root / f"test/fixtures/gstr1/tally/2026-{baseline}/native-gstr1.json").read_bytes())
        if actual["gstin"] != expected["gstin"] or actual["fp"] != f"{current}2026":
            raise ValueError(f"Wrong company/period: {source}")

        def normalize(obj):
            if isinstance(obj, dict):
                return {k: normalize(v) for k, v in obj.items() if k not in ("fp", "doc_issue")}
            if isinstance(obj, list):
                return sorted((normalize(v) for v in obj), key=lambda v: json.dumps(v, sort_keys=True))
            if isinstance(obj, str):
                return obj.removeprefix("SDK-").replace(f"15-{current}-2026", f"15-{baseline}-2026")
            return obj

        def documents(obj):
            return sorted((b["doc_num"], d["totnum"], d["cancel"], d["net_issue"])
                          for b in obj["doc_issue"]["doc_det"] for d in b["docs"])

        if normalize(actual) != normalize(expected) or documents(actual) != documents(expected):
            raise ValueError(f"Native return mismatch: {source}")
        # Different creation order intentionally changes the nonnumeric note
        # range endpoints. Still require each endpoint to be a known fixture.
        cases = json.loads((Path(__file__).parent / "fixtures/gst-sales.json").read_bytes())
        numbers = {case["Number"] for case in cases if case["Date"].startswith(f"2026-{current}")}
        for block in actual["doc_issue"]["doc_det"]:
            for row in block["docs"]:
                if row["from"] not in numbers or row["to"] not in numbers:
                    raise ValueError(f"Unexpected document range: {row}")
        print(f"2026-{current}: matched; sha256={hashlib.sha256(raw).hexdigest()}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    compare(Path(sys.argv[1]), Path(sys.argv[2]))
