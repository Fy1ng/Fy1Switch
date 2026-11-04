#!/usr/bin/env python3
"""Exercise the unchanged OpenWrt dispatcher inside an isolated user/network namespace.

Linux only. Uses chroot, unshare and the host's POSIX utilities. The rootfs, fake
network interface and etherwake stub are temporary. No real network is reachable.
"""
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def main():
    repo = Path(__file__).resolve().parent.parent
    dispatcher = repo / "src/wol/openwrt/fy1switch-wol"
    if os.name != "posix" or not shutil.which("unshare") or not shutil.which("chroot"):
        raise SystemExit("Requires Linux with unshare and chroot; no dispatcher was executed.")
    with tempfile.TemporaryDirectory(prefix="fy1switch-wol-script-") as temporary:
        root = Path(temporary)
        for executable in ["/bin/sh", "/usr/bin/grep", "/usr/bin/stat", "/usr/bin/sha256sum"]:
            source = Path(executable)
            target = root / source.relative_to("/")
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            libraries = subprocess.check_output(["ldd", executable], text=True)
            for library in re.findall(r"(/[\w/+.\-]+)", libraries):
                source_library = Path(library)
                if not source_library.is_file():
                    continue
                destination = root / source_library.relative_to("/")
                destination.parent.mkdir(parents=True, exist_ok=True)
                if not destination.exists():
                    shutil.copy2(source_library, destination)
        for directory in ["usr/libexec", "etc/fy1switch-wol", "sys/class/net/br-test", "dev"]:
            (root / directory).mkdir(parents=True, exist_ok=True)
        script = root / "usr/libexec/fy1switch-wol"
        shutil.copyfile(dispatcher, script)
        script.chmod(0o755)
        # A regular file is sufficient for the dispatcher's output redirection.
        (root / "dev/null").touch()
        stub = root / "usr/bin/etherwake"
        stub.write_text('#!/bin/sh\n[ "$1" = -i ] && [ "$2" = br-test ] && [ "$3" = 02:00:00:00:00:01 ] || exit 9\nprintf "packet\\n" >> /calls\n')
        stub.chmod(0o755)
        device = "a1111111-1111-4111-8111-111111111111"
        digest = hashlib.sha256(f"{device}|02:00:00:00:00:01|br-test".encode()).hexdigest()
        row = f"{device} 02:00:00:00:00:01 br-test {digest}\n"
        allowlist = root / "etc/fy1switch-wol/devices"
        allowlist.write_text(row)
        allowlist.chmod(0o600)
        cases = 0

        def run(command, expected_exit, expected_output=""):
            nonlocal cases
            environment = {**os.environ, "SSH_ORIGINAL_COMMAND": command}
            result = subprocess.run(
                ["unshare", "-Urn", "--", "chroot", str(root), "/bin/sh", "/usr/libexec/fy1switch-wol"],
                env=environment, text=True, capture_output=True, timeout=5,
            )
            assert result.returncode == expected_exit, (command, result.returncode, result.stderr)
            assert result.stdout == expected_output, (command, result.stdout)
            cases += 1

        run(f"check {device} {digest}", 0, f"FY1SWITCH_WOL_V1 ready {device} {digest}\n")
        assert not (root / "calls").exists(), "check must never invoke etherwake"
        run(f"wake {device} {digest}", 0, f"FY1SWITCH_WOL_V1 packet_sent {device} {digest}\n")
        assert (root / "calls").read_text() == "packet\n"
        for invalid in ["", "id", "/bin/sh", "scp -t /tmp/file", "sftp", f"wake {device}",
                        f"wake {device} {digest} extra", f"wake {device} {digest};id",
                        f"wake {device} $(id)", f"wake {device}\n{digest}",
                        f"wake {device.upper()} {digest}", f"wake bad-uuid {digest}"]:
            run(invalid, 64)
        run(f"wake {device} {'0' * 64}", 66)
        run(f"wake 22222222-2222-4222-8222-222222222222 {digest}", 66)
        allowlist.write_text(row + row)
        run(f"wake {device} {digest}", 65)
        allowlist.write_text(row)
        allowlist.chmod(0o666)
        run(f"wake {device} {digest}", 65)
        allowlist.chmod(0o600)
        allowlist.parent.chmod(0o777)
        run(f"wake {device} {digest}", 65)
        allowlist.parent.chmod(0o700)
        saved_allowlist = allowlist.with_name("saved-devices")
        allowlist.rename(saved_allowlist)
        allowlist.symlink_to("saved-devices")
        run(f"wake {device} {digest}", 65)
        allowlist.unlink()
        saved_allowlist.rename(allowlist)
        (root / "sys/class/net/br-test").rmdir()
        run(f"wake {device} {digest}", 69)
        (root / "sys/class/net/br-test").mkdir()
        stub.write_text("#!/bin/sh\nexit 1\n")
        run(f"wake {device} {digest}", 70)
        assert (root / "calls").read_text() == "packet\n", "Rejected requests must not send another packet"
        print(f"PASS: {cases} OpenWrt dispatcher cases in isolated namespaces; only a stub etherwake ran.")


if __name__ == "__main__":
    main()
