"""Recompute process-group metrics and premultiplied screenshot differences.

Usage: python build/Summarize-NativeDockAB.py <Run-NativeDockAB output directory>
Requires Pillow and numpy. Screenshots are diagnostic readbacks after sampling.
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image


def mean(values):
    values = [v for v in values if v is not None]
    return sum(values) / len(values) if values else None


def summarize(root):
    receipt = json.loads((root / "ab.json").read_text(encoding="utf-8-sig"))
    runs = []
    for item in receipt["runs"]:
        path = root / Path(item["directory"]).name
        memory = json.loads((path / "memory/process-group.json").read_text(encoding="utf-8-sig"))
        gpu = json.loads((path / "memory/gpu-memory.json").read_text(encoding="utf-8-sig"))
        product = json.loads((path / "product.json").read_text(encoding="utf-8-sig"))
        runs.append({
            "run": path.name, "backend": item["backend"], "samples": len(memory["samples"]),
            "firstSampleSeconds": memory["samples"][0]["elapsedSeconds"],
            "lastSampleSeconds": memory["samples"][-1]["elapsedSeconds"],
            **{metric: mean([s[metric] for s in memory["samples"]])
               for metric in ["privateCommitBytes", "workingBytes", "privateWorkingBytes", "cpuPercent", "processCount"]},
            **{metric: mean([s[metric] for s in gpu["samples"]])
               for metric in ["dedicatedBytes", "sharedBytes", "committedBytes"]},
            "native": product["dock"]["nativeStatic"], "source": product["dock"]["source"],
            "launcherVisible": product.get("launcherVisible"),
            "launcherSleeping": product["performance"]["LauncherSleeping"],
            "dockBounds": product["dockBounds"],
            "lastMembers": memory["samples"][-1]["members"],
        })
    averages = {}
    metrics = ["privateCommitBytes", "workingBytes", "privateWorkingBytes", "cpuPercent", "dedicatedBytes", "sharedBytes", "processCount"]
    for backend in {r["backend"] for r in runs}:
        averages[backend] = {metric: mean([r[metric] for r in runs if r["backend"] == backend]) for metric in metrics}
    differences = []
    legacy = next((r for r in runs if r["backend"] == "legacy"), None)
    if legacy:
        original = np.asarray(Image.open(root / legacy["run"] / "dock.png").convert("RGBA")).astype(float)
        def premult(image):
            result = image.copy()
            result[:, :, :3] *= result[:, :, 3:] / 255
            return result
        for run in runs:
            if run["backend"] != "native-static":
                continue
            candidate = np.asarray(Image.open(root / run["run"] / "dock.png").convert("RGBA")).astype(float)
            if candidate.shape != original.shape:
                differences.append({"run": run["run"], "sameDimensions": False})
                continue
            delta = np.abs(premult(original) - premult(candidate))
            differences.append({"run": run["run"], "sameDimensions": True,
                "premultipliedMeanChannelDifference255": float(delta.mean()),
                "premultipliedP95ChannelDifference255": float(np.percentile(delta, 95)),
                "premultipliedMaxChannelDifference255": float(delta.max()),
                "alphaMaxDifference255": float(np.abs(original[:,:,3] - candidate[:,:,3]).max())})
    result = {"runAverages": runs, "equalWeightBackendMeans": averages, "quality": differences,
              "displayedFpsMeasured": False, "samplingDoesNotProveLongRunStability": True}
    if "legacy" in averages and "native-static" in averages:
        old, new = averages["legacy"], averages["native-static"]
        result["nativePrivateCommitReductionPercent"] = 100 * (1 - new["privateCommitBytes"] / old["privateCommitBytes"])
        result["privateCommit25PercentGoalAchieved"] = result["nativePrivateCommitReductionPercent"] >= 25
    for run in runs:
        # Keep the aggregate deliverable free of process/path identities.
        for member in run["lastMembers"]:
            member.pop("processId", None)
            member.pop("parentId", None)
    (root / "summary.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k:v for k,v in result.items() if k != "runAverages"}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    summarize(Path(sys.argv[1]).resolve())
