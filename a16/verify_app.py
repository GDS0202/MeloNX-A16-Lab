"""Verify archive structure, iOS ARM64 platform and the new core's diagnostic marker.
This is not a signing, launch or gameplay test.
"""
import hashlib
import plistlib
import struct
import sys
from pathlib import Path


def verify_ios_arm64(path):
    data = path.read_bytes()
    assert len(data) >= 32, "Truncated Mach-O"
    magic, cpu, _, _, commands, _, _, _ = struct.unpack_from("<IiiIIIII", data)
    assert magic == 0xFEEDFACF and cpu == 0x0100000C, "Expected thin ARM64 Mach-O"
    offset, ios = 32, False
    for _ in range(commands):
        assert offset + 8 <= len(data), "Truncated command"
        command, length = struct.unpack_from("<II", data, offset)
        assert length >= 8 and offset + length <= len(data), "Invalid command length"
        if command == 0x32:
            assert length >= 24, "Truncated build version"
            assert struct.unpack_from("<I", data, offset + 8)[0] == 2, "Expected iOS device, not simulator"
            ios = True
        offset += length
    assert ios, "Missing iOS build platform"


def verify(app, core):
    with (app / "Info.plist").open("rb") as stream:
        info = plistlib.load(stream)
    assert info["CFBundleIdentifier"] == "dev.a16lab.MeloNX", "Unexpected app ID"
    assert info["CFBundleDisplayName"] in ("MeloNX A16 Lab", "MeloNX A16 Control"), "Unexpected name"
    assert info.get("CFBundleSupportedPlatforms") == ["iPhoneOS"], "Wrong platform"
    executable = app / info["CFBundleExecutable"]
    embedded = app / "Frameworks/Ryujinx.Library.dylib"
    for path in (executable, embedded, core):
        assert path.is_file(), "Missing executable/core"
        verify_ios_arm64(path)
    markers = ["A16Pacing CPU-present".encode(e) for e in ("utf-8", "utf-16le")]
    assert any(m in core.read_bytes() for m in markers), "New core lacks A16 diagnostics"
    assert any(m in embedded.read_bytes() for m in markers), "Embedded core is not the A16 build"
    donors = []
    for path in (app / "PlugIns").glob("*.appex/Info.plist"):
        with path.open("rb") as stream:
            donors.append(plistlib.load(stream).get("CFBundleIdentifier"))
    assert "dev.a16lab.MeloNX.RamDonor" in donors, "Missing RAM donor extension"
    print("Archive structure, platform and diagnostic marker verified.")
    print("Native core SHA256", hashlib.sha256(core.read_bytes()).hexdigest())
    print("Embedded core SHA256", hashlib.sha256(embedded.read_bytes()).hexdigest())
    print("Signing, installation, JIT and performance are not validated by this check.")


if __name__ == "__main__":
    verify(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
