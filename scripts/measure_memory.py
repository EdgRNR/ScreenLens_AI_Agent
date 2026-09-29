# -*- coding: utf-8 -*-
"""ScreenLens 进程内存采样工具。

测量口径（与阶段计划书第 2 节一致）：
- Private Working Set：psutil memory_full_info().uss（进程独占的驻留物理内存）
- Private Bytes (Commit)：psutil memory_info().vms（已提交的私有虚拟内存）

用法示例：
  python scripts/measure_memory.py --root-pid 1234 --duration 60 --label 后台空闲
  python scripts/measure_memory.py --patterns ScreenLensAgent ScreenLens.WinUI python pythonw
"""
from __future__ import annotations

import argparse
import math
import os
import time

import psutil


def matches(name: str, patterns: list[str]) -> bool:
    return any(name.lower().startswith(p.lower()) for p in patterns)


def selected_pids(root_pids: list[int], patterns: list[str]) -> set[int]:
    """进程树模式会动态纳入全部后代，防止遗漏 OCR worker。"""
    pids: set[int] = set()
    for root_pid in root_pids:
        try:
            root = psutil.Process(root_pid)
            pids.add(root.pid)
            pids.update(child.pid for child in root.children(recursive=True))
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    if pids:
        return pids
    return {p.info["pid"] for p in psutil.process_iter(["pid", "name"])
            if p.info["pid"] != os.getpid()
            and matches(p.info["name"] or "", patterns)}


def snapshot(root_pids: list[int], patterns: list[str]) -> list[dict]:
    rows = []
    pids = selected_pids(root_pids, patterns)
    for p in psutil.process_iter(["pid", "name", "memory_full_info", "memory_info"]):
        try:
            info = p.info
            if info["pid"] not in pids or info["pid"] == os.getpid():
                continue
            full = info["memory_full_info"]
            rows.append({
                "pid": info["pid"],
                "name": info["name"],
                # psutil 在 Windows 上 uss=私有工作集、vms=Private Bytes
                "uss": getattr(full, "uss", 0),
                "vms": info["memory_info"].vms,
            })
        except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
            continue
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root-pid", nargs="+", type=int, default=[],
                    help="应用根进程 PID；自动包含所有后代进程，推荐使用")
    ap.add_argument("--patterns", nargs="*", default=[],
                    help="备用进程名前缀过滤（不含 .exe；可能漏掉未匹配的子进程）")
    ap.add_argument("--duration", type=int, default=30, help="采样时长（秒）")
    ap.add_argument("--interval", type=float, default=1.0, help="采样间隔（秒）")
    ap.add_argument("--label", default="", help="本轮采样标签")
    args = ap.parse_args()
    if not args.root_pid and not args.patterns:
        ap.error("至少提供 --root-pid 或 --patterns")

    print(f"=== 内存采样开始 === 根 PID: {args.root_pid or '(none)'} "
          f"过滤: {args.patterns or '(process tree)'} 时长: {args.duration}s "
          f"间隔: {args.interval:.0f}s 标签: {args.label or '(none)'}")

    samples: list[tuple[float, float, int]] = []
    deadline = time.time() + args.duration
    while time.time() < deadline:
        rows = snapshot(args.root_pid, args.patterns)
        if rows:
            ws = sum(r["uss"] for r in rows) / 1048576
            pb = sum(r["vms"] for r in rows) / 1048576
            samples.append((ws, pb, len(rows)))
        else:
            samples.append((0.0, 0.0, 0))
        time.sleep(args.interval)

    n = len(samples)
    if not samples:
        print("未采样到任何数据")
        return 1

    ws_vals = [s[0] for s in samples]
    pb_vals = [s[1] for s in samples]
    ws_sorted = sorted(ws_vals)
    pb_sorted = sorted(pb_vals)
    p95_index = max(0, math.ceil(0.95 * n) - 1)
    print(f"--- 汇总（{n} 个采样点）---")
    print(f"  合计 Private Working Set: 均值 {sum(ws_vals)/n:7.1f} MiB / "
          f"峰值 {max(ws_vals):7.1f} MiB / P95 {ws_sorted[p95_index]:7.1f} MiB")
    print(f"  合计 Private Bytes(Commit): 均值 {sum(pb_vals)/n:7.1f} MiB / "
          f"峰值 {max(pb_vals):7.1f} MiB / P95 {pb_sorted[p95_index]:7.1f} MiB")
    print(f"  进程数峰值: {max(s[2] for s in samples)}")

    rows = snapshot(args.root_pid, args.patterns)
    if rows:
        print("--- 末次进程明细 ---")
        for r in sorted(rows, key=lambda x: -x["uss"]):
            print(f"  PID {r['pid']:<7} {r['name']:<22} "
                  f"PrivWS {r['uss']/1048576:8.1f} MiB  "
                  f"PrivBytes {r['vms']/1048576:8.1f} MiB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
