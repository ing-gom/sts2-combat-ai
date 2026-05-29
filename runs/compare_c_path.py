"""Compare BEFORE (b9d45a3) vs AFTER (c0204fa C-path overlay) sweeps."""
import re, sys, os

def parse(path):
    """Returns {(arm,bucket): (wins,total)} parsed from sweep CSV."""
    cells = {}
    arm = None
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"\[arm-start\] cost_bias=(-?\d+)", line)
            if m:
                arm = int(m.group(1))
                continue
            m = re.search(r"bucket=(\w+)\s+wins=(\d+)/(\d+)", line)
            if m and arm is not None:
                bucket = m.group(1)
                w, t = int(m.group(2)), int(m.group(3))
                key = (arm, bucket)
                a, b = cells.get(key, (0, 0))
                cells[key] = (a + w, b + t)
    return cells

import sys
b_path = sys.argv[1] if len(sys.argv) > 1 else "runs/c_path_before180.csv"
a_path = sys.argv[2] if len(sys.argv) > 2 else "runs/c_path_after180.csv"
before = parse(b_path)
after = parse(a_path)
print(f"BEFORE={b_path}")
print(f"AFTER ={a_path}")

keys = sorted(set(before) | set(after))
header = f"{'arm':>4} {'bucket':<10} {'before':>12} {'after':>12} {'delta':>8}"
print(header)
print("-" * len(header))
for k in keys:
    arm, bucket = k
    bw, bt = before.get(k, (0, 0))
    aw, at = after.get(k, (0, 0))
    bp = bw / bt if bt else 0
    ap = aw / at if at else 0
    delta = (ap - bp) * 100
    print(f"{arm:>4} {bucket:<10} {bw:>5}/{bt:>3} ({bp*100:>5.1f}%) {aw:>5}/{at:>3} ({ap*100:>5.1f}%) {delta:>+7.1f}pp")
