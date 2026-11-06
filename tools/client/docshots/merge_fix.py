"""A retake's rows into the main run: their raw frames replace the main run's for those rows, the manifest takes their frame list. Then --remask redoes the pictures.

    python -I merge_fix.py <retake out> <main out> <res> [row,row...]      default: every row the retake shot (setup rows have no frames and are left alone)
"""
import json
import shutil
import sys
from pathlib import Path

fix, main, res = Path(sys.argv[1]) / sys.argv[3], Path(sys.argv[2]) / sys.argv[3], sys.argv[3]
only = set(sys.argv[4].split(",")) if len(sys.argv) > 4 else None
fm = json.loads((fix / "manifest.json").read_text(encoding="utf-8"))
mm = json.loads((main / "manifest.json").read_text(encoding="utf-8"))
for rid, got in fm["rows"].items():
    if not got.get("frames") or (only and rid not in only):
        continue
    for old in mm["rows"].get(rid, {}).get("frames", []):
        for k in ("file", "crop"):
            if old.get(k):
                (main / old[k]).unlink(missing_ok=True)
        (main / "_raw" / old["file"]).unlink(missing_ok=True)
    for f in got["frames"]:
        shutil.copy2(fix / "_raw" / f["file"], main / "_raw" / f["file"])
    mm["rows"][rid] = {"frames": got["frames"]}
    print(rid, [f["file"] for f in got["frames"]])
(main / "manifest.json").write_text(json.dumps(mm, indent=1), encoding="utf-8")
