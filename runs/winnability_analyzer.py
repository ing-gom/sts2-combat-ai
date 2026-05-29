"""Cell-level winnability analyzer.

Single win-rate numbers are ambiguous because they mix three populations:
  - unwinnable cells (random 0/4 AND planner 0/4) - deck/encounter too hard
  - too-easy cells   (random 4/4)                 - any policy wins
  - meaningful cells (random 0-3 with lift over)  - where the policy matters

Run as:
    python winnability_analyzer.py random.csv planner.csv [hybrid.csv]

Output: per-bucket breakdown of cell classes + meaningful-cell-only lift.
"""
import re
import sys
from collections import defaultdict


def parse_cells(path):
    """Returns {(arm, bucket, cell): (wins, total)} from a sweep CSV."""
    cells = {}
    arm = None
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"\[arm-start\] cost_bias=(-?\d+)", line)
            if m:
                arm = int(m.group(1))
                continue
            m = re.search(r"\[cell\]\s+(\S+)\s+bucket=(\w+)\s+wins=(\d+)/(\d+)", line)
            if m and arm is not None:
                cell = m.group(1)
                bucket = m.group(2)
                w, t = int(m.group(3)), int(m.group(4))
                cells[(arm, bucket, cell)] = (w, t)
    return cells


def classify(rand_w, plan_w, total):
    """Cell class given random and planner wins out of `total` trials."""
    if rand_w == total:
        return "easy"
    if rand_w == 0 and plan_w == 0:
        return "unwinnable"
    if plan_w > rand_w:
        return "meaningful_lift"
    if plan_w == rand_w:
        return "no_lift"
    return "regression"


def aggregate(label, cells, classes_by_key):
    """Win-rate stats by class for a single policy."""
    by_class = defaultdict(lambda: [0, 0])
    by_bucket_class = defaultdict(lambda: [0, 0])
    for key, (w, t) in cells.items():
        cls = classes_by_key.get(key, "unknown")
        by_class[cls][0] += w
        by_class[cls][1] += t
        by_bucket_class[(key[1], cls)][0] += w
        by_bucket_class[(key[1], cls)][1] += t
    return by_class, by_bucket_class


def main():
    if len(sys.argv) < 3:
        print("usage: winnability_analyzer.py random.csv planner.csv [hybrid.csv]")
        return 1
    rand_path = sys.argv[1]
    plan_path = sys.argv[2]
    hyb_path = sys.argv[3] if len(sys.argv) > 3 else None

    rand = parse_cells(rand_path)
    plan = parse_cells(plan_path)
    hyb = parse_cells(hyb_path) if hyb_path else {}

    keys = sorted(set(rand) | set(plan))
    classes = {}
    for key in keys:
        rw, rt = rand.get(key, (0, 0))
        pw, pt = plan.get(key, (0, 0))
        if rt == 0 or pt == 0:
            continue
        classes[key] = classify(rw, pw, rt)

    print(f"random  = {rand_path}")
    print(f"planner = {plan_path}")
    if hyb_path:
        print(f"hybrid  = {hyb_path}")
    print()

    # Cell-class counts per bucket
    print("=" * 70)
    print("Cell class distribution (per bucket, per arm)")
    print("=" * 70)
    by_arm_bucket_class = defaultdict(lambda: defaultdict(int))
    for key, cls in classes.items():
        arm, bucket, _ = key
        by_arm_bucket_class[(arm, bucket)][cls] += 1
    header = f"{'arm':>4} {'bucket':<10} {'easy':>5} {'unwin':>6} {'meanful':>8} {'noLift':>7} {'regr':>5} {'total':>6}"
    print(header)
    print("-" * len(header))
    for key in sorted(by_arm_bucket_class.keys()):
        arm, bucket = key
        c = by_arm_bucket_class[key]
        total = sum(c.values())
        print(f"{arm:>4} {bucket:<10} {c.get('easy', 0):>5} {c.get('unwinnable', 0):>6} {c.get('meaningful_lift', 0):>8} {c.get('no_lift', 0):>7} {c.get('regression', 0):>5} {total:>6}")

    # Win-rate aggregates by class
    print()
    print("=" * 70)
    print("Win-rate by class (random / planner / hybrid)")
    print("=" * 70)
    _, rand_bb = aggregate("random", rand, classes)
    _, plan_bb = aggregate("planner", plan, classes)
    hyb_bb = None
    if hyb:
        _, hyb_bb = aggregate("hybrid", hyb, classes)

    bbkeys = sorted(set(list(rand_bb.keys()) + list(plan_bb.keys())))
    if hyb_bb:
        bbkeys = sorted(set(bbkeys + list(hyb_bb.keys())))
    header2 = f"{'bucket':<10} {'class':<16} {'random':>14} {'planner':>14}" + (f" {'hybrid':>14}" if hyb_bb else "") + f" {'lift':>8}"
    print(header2)
    print("-" * len(header2))
    for bk in bbkeys:
        bucket, cls = bk
        rw, rt = rand_bb.get(bk, (0, 0))
        pw, pt = plan_bb.get(bk, (0, 0))
        if rt == 0 or pt == 0:
            continue
        rp = 100.0 * rw / rt
        pp = 100.0 * pw / pt
        lift = pp - rp
        rand_s = f"{rw:>4}/{rt:>4} ({rp:>5.1f}%)"
        plan_s = f"{pw:>4}/{pt:>4} ({pp:>5.1f}%)"
        hyb_s = ""
        if hyb_bb:
            hw, ht = hyb_bb.get(bk, (0, 0))
            if ht > 0:
                hp = 100.0 * hw / ht
                hyb_s = f" {hw:>4}/{ht:>4} ({hp:>5.1f}%)"
            else:
                hyb_s = f" {'-':>14}"
        print(f"{bucket:<10} {cls:<16} {rand_s} {plan_s}{hyb_s} {lift:>+6.1f}pp")

    # Headline: meaningful cells only
    print()
    print("=" * 70)
    print("Headline - meaningful cells only (random 0-3/4, planner > random)")
    print("=" * 70)
    by_bucket = defaultdict(lambda: [0, 0, 0, 0])  # rand_w, plan_w, hyb_w, total
    for key, cls in classes.items():
        if cls != "meaningful_lift":
            continue
        _, bucket, _ = key
        rw, rt = rand.get(key, (0, 0))
        pw, _ = plan.get(key, (0, 0))
        hw, _ = hyb.get(key, (0, 0)) if hyb else (0, 0)
        by_bucket[bucket][0] += rw
        by_bucket[bucket][1] += pw
        by_bucket[bucket][2] += hw
        by_bucket[bucket][3] += rt

    h_header = f"{'bucket':<10} {'random':>14} {'planner':>14}" + (f" {'hybrid':>14}" if hyb else "") + f" {'planner_lift':>12}"
    print(h_header)
    print("-" * len(h_header))
    for bucket in sorted(by_bucket.keys()):
        rw, pw, hw, t = by_bucket[bucket]
        if t == 0:
            continue
        rp = 100.0 * rw / t
        pp = 100.0 * pw / t
        lift = pp - rp
        rand_s = f"{rw:>4}/{t:>4} ({rp:>5.1f}%)"
        plan_s = f"{pw:>4}/{t:>4} ({pp:>5.1f}%)"
        hyb_s = ""
        if hyb:
            hp = 100.0 * hw / t
            hyb_s = f" {hw:>4}/{t:>4} ({hp:>5.1f}%)"
        print(f"{bucket:<10} {rand_s} {plan_s}{hyb_s} {lift:>+10.1f}pp")

    return 0


if __name__ == "__main__":
    sys.exit(main())
