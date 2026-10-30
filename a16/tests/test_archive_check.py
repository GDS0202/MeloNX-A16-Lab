import contextlib
import importlib.util
import io
import plistlib
import struct
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("verify_app", Path(__file__).resolve().parents[1] / "verify_app.py")
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class ArchiveChecks(unittest.TestCase):
    def fixture(self, root, encoding="utf-8", platform=2):
        app = root / "MeloNX.app"
        (app / "Frameworks").mkdir(parents=True)
        donor = app / "PlugIns/Donor.appex"
        donor.mkdir(parents=True)
        (app / "Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": "dev.a16lab.MeloNX", "CFBundleDisplayName": "MeloNX A16 Lab", "CFBundleExecutable": "MeloNX", "CFBundleSupportedPlatforms": ["iPhoneOS"]}))
        (donor / "Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": "dev.a16lab.MeloNX.RamDonor"}))
        image = struct.pack("<IiiIIIII", 0xFEEDFACF, 0x0100000C, 0, 6, 1, 24, 0, 0) + struct.pack("<IIIIII", 0x32, 24, platform, 0, 0, 0)
        (app / "MeloNX").write_bytes(image)
        core = root / "core.dylib"
        core.write_bytes(image + "A16Pacing CPU-present".encode(encoding))
        (app / "Frameworks/Ryujinx.Library.dylib").write_bytes(core.read_bytes())
        return app, core

    def test_utf8_marker(self):
        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(io.StringIO()):
            check.verify(*self.fixture(Path(tmp)))

    def test_native_aot_utf16_marker(self):
        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(io.StringIO()):
            check.verify(*self.fixture(Path(tmp), encoding="utf-16le"))

    def test_reject_stock_core(self):
        with tempfile.TemporaryDirectory() as tmp:
            app, core = self.fixture(Path(tmp))
            (app / "Frameworks/Ryujinx.Library.dylib").write_bytes(core.read_bytes()[:56])
            with self.assertRaisesRegex(AssertionError, "Embedded core"): check.verify(app, core)

    def test_reject_lost_donor(self):
        with tempfile.TemporaryDirectory() as tmp:
            app, core = self.fixture(Path(tmp))
            (app / "PlugIns/Donor.appex/Info.plist").unlink()
            with self.assertRaisesRegex(AssertionError, "donor extension"): check.verify(app, core)

    def test_reject_simulator(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(AssertionError, "simulator"): check.verify(*self.fixture(Path(tmp), platform=7))

    def test_reject_truncated_macho(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "bad"
            path.write_bytes(b"bad binary")
            with self.assertRaisesRegex(AssertionError, "Truncated"): check.verify_ios_arm64(path)


if __name__ == "__main__": unittest.main()
