"""CI-only: restore upstream scheduling while retaining identical profile and metrics.
This modifies source files; only use a disposable checkout after regression checks.
"""
import hashlib
import json
import plistlib
from pathlib import Path

root = Path(__file__).resolve().parent.parent
manifest = json.loads((root / "a16/control/provenance.json").read_text())
for name, expected in manifest["files"].items():
    data = (root / "a16/control" / name).read_bytes()
    assert hashlib.sha256(data).hexdigest() == expected, "Control digest mismatch"
    (root / "src/Ryujinx.Graphics.Vulkan" / name).write_bytes(data)
path = root / "src/MeloNX/MeloNX/Info.plist"
with path.open("rb") as stream: info = plistlib.load(stream)
info["CFBundleDisplayName"] = "MeloNX A16 Control"
with path.open("wb") as stream: plistlib.dump(info, stream, sort_keys=False)
print("Control restored from", manifest["upstream_commit"])
