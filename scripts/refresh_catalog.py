"""
Single entry point for refreshing this repo after the parent repo
(sts2-card-advisor-dev) regenerates cards_catalog.json — typically as part
of its version-bump skill after a game patch.

Pipeline:
  1. Copy <source>/cards_catalog.json -> scripts/cards_catalog.json
  2. Run extract_card_triggers.py -> Sts2CombatAICode/Core/Data/card_triggers.json
  3. Run measure_ai_card_coverage.py -> docs/ai_card_coverage.md

The parent repo's version-bump skill should invoke this once per catalog
regen and then commit + push the resulting changes here. Example:

    python scripts/refresh_catalog.py \\
        --from ../sts2-card-advisor-dev/scripts/cards_catalog.json

Exit codes:
  0  success
  2  source file missing / unreadable
  3  catalog has no game_version key (malformed)
"""

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_SOURCE = REPO_ROOT.parent / "sts2-card-advisor-dev" / "scripts" / "cards_catalog.json"
TARGET_CATALOG = REPO_ROOT / "scripts" / "cards_catalog.json"
EXTRACT_SCRIPT = REPO_ROOT / "scripts" / "extract_card_triggers.py"
AUDIT_SCRIPT = REPO_ROOT / "scripts" / "measure_ai_card_coverage.py"
AUDIT_OUT = REPO_ROOT / "docs" / "ai_card_coverage.md"


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--from", dest="source", type=Path, default=DEFAULT_SOURCE,
                   help=f"Path to the new cards_catalog.json (default: {DEFAULT_SOURCE})")
    p.add_argument("--skip-audit", action="store_true",
                   help="Refresh catalog + triggers but skip the coverage audit")
    return p.parse_args()


def read_version(path: Path) -> str:
    data = json.loads(path.read_text(encoding="utf-8"))
    version = data.get("game_version")
    if not version:
        print(f"ERROR: {path} has no 'game_version' key — refusing to sync.",
              file=sys.stderr)
        sys.exit(3)
    return version


def run(cmd: list[str]) -> None:
    print(f"$ {' '.join(cmd)}")
    subprocess.run(cmd, check=True, cwd=REPO_ROOT)


def main() -> int:
    args = parse_args()
    source: Path = args.source.resolve()

    if not source.is_file():
        print(f"ERROR: source catalog not found: {source}", file=sys.stderr)
        return 2

    new_version = read_version(source)
    old_version = read_version(TARGET_CATALOG) if TARGET_CATALOG.exists() else None

    print(f"Source : {source}  (game_version={new_version})")
    print(f"Target : {TARGET_CATALOG}  (game_version={old_version})")

    if old_version == new_version:
        print(f"NOTE: target already at {new_version} — copying anyway in case cards changed.")

    shutil.copyfile(source, TARGET_CATALOG)
    print(f"Copied catalog -> {TARGET_CATALOG.relative_to(REPO_ROOT)}")

    run([sys.executable, str(EXTRACT_SCRIPT)])

    if args.skip_audit:
        print("Skipped audit (--skip-audit).")
        return 0

    run([sys.executable, str(AUDIT_SCRIPT), "--out", str(AUDIT_OUT)])
    print(f"\nDone. Review and commit:")
    print(f"  - scripts/cards_catalog.json")
    print(f"  - Sts2CombatAICode/Core/Data/card_triggers.json")
    print(f"  - docs/ai_card_coverage.md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
