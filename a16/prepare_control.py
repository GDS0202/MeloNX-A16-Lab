"""CI-only: restore performance baselines while retaining safety fixes, profile and interval metrics.
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
    (root / manifest["targets"][name]).write_bytes(data)
app = root / "src/MeloNX/MeloNX/Common/HostRuntime.swift"
text = app.read_text()
setting = 'let policyEnabled = UserDefaults.standard.object(forKey: "a16WorkloadPolicyEnabled") as? Bool ?? true'
assert text.count(setting) == 1, "A16 policy bootstrap changed; update the control preparation"
text = text.replace('let textureCacheEnabled = UserDefaults.standard.object(forKey: "a16TextureDiskCacheEnabled") as? Bool ?? true', 'let textureCacheEnabled = false // Control baseline: converted texture cache disabled.')
app.write_text(text.replace(setting, 'let policyEnabled = false // Control baseline: host workload policy disabled.'))
path = root / "src/MeloNX/MeloNX/Info.plist"
with path.open("rb") as stream: info = plistlib.load(stream)
info["CFBundleDisplayName"] = "MeloNX A16 Control"
with path.open("wb") as stream: plistlib.dump(info, stream, sort_keys=False)
print("Control restored from", manifest["upstream_commit"])
