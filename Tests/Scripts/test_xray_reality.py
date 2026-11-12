"""Exercise the bundled shell scripts in a sandbox, validating JSON with real Xray.

Windows example (Git Bash and an extracted official Xray release):
  python Tests/Scripts/test_xray_reality.py --xray C:/temp/xray.exe
No packages, services, firewall rules or files outside the temporary root are changed.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


parser = argparse.ArgumentParser()
parser.add_argument("--xray", required=True, type=Path)
parser.add_argument("--shell", default=shutil.which("bash") if os.name != "nt" else "C:/Program Files/Git/bin/bash.exe")
args = parser.parse_args()
scripts = Path(__file__).resolve().parents[2] / "bin/Data/Scripts"
xray = args.xray.resolve()


class RealityScripts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="jrm-xray-script-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for directory in ("run/systemd/system", "usr/local/bin", "usr/local/etc/xray", "etc/systemd/system", "root"):
            (self.root / directory).mkdir(parents=True, exist_ok=True)
        self.config = self.root / "usr/local/etc/xray/config.json"
        self.log = self.root / "operations.log"
        self.capture = self.root / "checked.json"
        wrapper = self.root / "usr/local/bin/xray"
        wrapper.write_text('''#!/bin/sh
if [ "$1" = run ]; then
    cp "$6" "$HARNESS_ROOT/checked.json"
    [ "${FAIL_VALIDATION:-0}" = 0 ] || exit 1
fi
exec "$REAL_XRAY" "$@"
''', encoding="utf-8", newline="\n")
        wrapper.chmod(0o755)
        key_output = subprocess.check_output([str(xray), "x25519"], text=True)
        self.public = next(line.split(":", 1)[1].strip() for line in key_output.splitlines()
                           if line.startswith(("Password:", "Password (PublicKey):", "PublicKey:", "Public key:")))
        self.link = ("vless://00000000-0000-4000-8000-000000000001@example.com:443"
                     "?security=reality&sni=example.com&pbk=" + self.public
                     + "&sid=0123456789abcdef&flow=xtls-rprx-vision&type=tcp")

    def run_script(self, suite, script="install.sh", **values):
        text = (scripts / ("xray reality " + suite) / script).read_text(encoding="utf-8")
        # Redirect every fixed system path before execution. Kernel random sources stay real.
        for absolute in ("/usr/local/etc/xray", "/usr/local/bin/xray", "/etc/systemd/system",
                         "/run/systemd/system", "/etc/sysctl.d", "/root"):
            text = text.replace(absolute, self.root.as_posix() + absolute)
        header = r'''
uname() { printf 'Linux\n'; }
id() { if [ "$1" = -u ]; then printf '0\n'; else printf 'root\n'; fi; }
chown() { printf 'chown %s\n' "$*" >> "$HARNESS_ROOT/operations.log"; }
sysctl() { return 1; }
modprobe() { return 1; }
ufw() { return 1; }
firewall-cmd() { return 1; }
systemctl() {
    printf 'systemctl %s\n' "$*" >> "$HARNESS_ROOT/operations.log"
    case "$1" in
        show) printf 'root\n' ;;
        restart) [ "${FAIL_RESTART:-0}" = 0 ] ;;
        is-active) [ "${2:-}" = --quiet ] && [ "${3:-}" = xray ] ;;
    esac
}
curl() {
    printf 'curl %s\n' "$*" >> "$HARNESS_ROOT/operations.log"
    [ "${FAIL_DOWNLOAD:-0}" = 0 ] || return 1
    case "$*" in
        *install-release.sh*)
            while [ "$#" -gt 0 ]; do
                if [ "$1" = -o ]; then printf 'exit 0\n' > "$2"; return; fi
                shift
            done
            return 1 ;;
        *) printf '203.0.113.10\n' ;;
    esac
}
'''
        target = self.root / "script.sh"
        target.write_text(header + text, encoding="utf-8", newline="\n")
        env = dict(os.environ, HARNESS_ROOT=self.root.as_posix(), REAL_XRAY=xray.as_posix(), TMPDIR=self.root.as_posix(),
                   PORT="443", SNI="example.com", LISTEN_PORT="1080", UPDATE_XRAY="false")
        env.update(values)
        result = subprocess.run([args.shell, str(target)], env=env, text=True, capture_output=True, timeout=30)
        self.output = result.stdout + result.stderr
        return result.returncode

    def operations(self):
        return self.log.read_text() if self.log.exists() else ""

    def test_shell_syntax(self):
        for file in scripts.glob("xray reality */*.sh"):
            result = subprocess.run([args.shell, "-n", str(file)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_server_and_saved_link(self):
        self.assertEqual(self.run_script("server"), 0, self.output)
        config = json.loads(self.config.read_text())
        inbound = config["inbounds"][0]
        self.assertEqual(inbound["port"], 443)
        self.assertEqual(inbound["streamSettings"]["realitySettings"]["target"], "example.com:443")
        metadata = (self.root / "usr/local/etc/xray/jeekremote-reality-link.conf").read_text()
        self.assertNotIn("PRIVATE", metadata)
        uri = next(line for line in self.output.splitlines() if line.startswith("vless://"))
        self.assertEqual(self.run_script("server", "show-link.sh", FAIL_DOWNLOAD="1"), 0, self.output)
        self.assertIn(uri, self.output)
        # The client's actual parser consumes the server's generated share link.
        self.assertEqual(self.run_script("client", SERVER_LINK_1=uri), 0, self.output)

    def test_client_sparse_ipv6_mapping_and_backup(self):
        self.config.write_text('{"old":true}')
        ipv6_link = self.link.replace("example.com:443?", "[2001:db8::1]:8443?")
        self.assertEqual(self.run_script("client", SERVER_LINK_2=self.link, SERVER_LINK_9=ipv6_link,
                                         ALLOW_EXTERNAL="true"), 0, self.output)
        config = json.loads(self.config.read_text())
        self.assertEqual([i["port"] for i in config["inbounds"]], [1080, 1081])
        self.assertEqual([i["listen"] for i in config["inbounds"]], ["0.0.0.0"] * 2)
        self.assertEqual(config["outbounds"][1]["settings"]["vnext"][0]["address"], "2001:db8::1")
        self.assertEqual(config["routing"]["rules"][-1]["outboundTag"], "proxy-2")
        self.assertEqual(next(self.config.parent.glob("config.json.backup-*")).read_text(), '{"old":true}')
        self.assertNotIn("curl", self.operations())

    def test_nine_links(self):
        links = {f"SERVER_LINK_{i}": self.link for i in range(1, 10)}
        self.assertEqual(self.run_script("client", **links), 0, self.output)
        self.assertEqual(len(json.loads(self.config.read_text())["inbounds"]), 9)

    def test_invalid_links_and_port_overflow_do_not_change_config(self):
        self.config.write_text("original")
        cases = [dict(), dict(SERVER_LINK_1=self.link, SERVER_LINK_2=self.link, LISTEN_PORT="65535"),
                 dict(SERVER_LINK_1=self.link.replace("sid=0123456789abcdef", "sid=abc")),
                 dict(SERVER_LINK_1=self.link.replace("security=reality", "security=tls")),
                 dict(SERVER_LINK_1=self.link.replace("type=tcp", "type=ws")),
                 dict(SERVER_LINK_1=self.link.replace("example.com:443", 'evil".com:443'))]
        for values in cases:
            self.assertNotEqual(self.run_script("client", **values), 0, self.output)
            self.assertEqual(self.config.read_text(), "original")
        self.assertNotIn("systemctl restart", self.operations())
        self.assertNotIn("curl", self.operations())

    def test_validation_failure_preserves_config_and_service(self):
        for suite in ("server", "client"):
            self.config.write_text("original")
            self.assertNotEqual(self.run_script(suite, SERVER_LINK_1=self.link, FAIL_VALIDATION="1"), 0)
            self.assertEqual(self.config.read_text(), "original")
        self.assertNotIn("systemctl restart", self.operations())

    def test_download_failure_falls_back_to_installed_binary(self):
        self.assertEqual(self.run_script("client", SERVER_LINK_1=self.link,
                                         UPDATE_XRAY="true", FAIL_DOWNLOAD="1"), 0, self.output)
        self.assertIn("Continuing with the existing binary", self.output)

    def test_no_binary_and_update_disabled_fails(self):
        (self.root / "usr/local/bin/xray").unlink()
        self.assertNotEqual(self.run_script("client", SERVER_LINK_1=self.link), 0)
        self.assertNotIn("systemctl restart", self.operations())

    def test_restart_failure_is_reported(self):
        self.assertNotEqual(self.run_script("client", SERVER_LINK_1=self.link, FAIL_RESTART="1"), 0)
        self.assertIn("not active after restart", self.output)

    def test_uninstall_keeps_backup_and_removes_metadata(self):
        self.assertEqual(self.run_script("server"), 0, self.output)
        original = self.config.read_text()
        self.assertEqual(self.run_script("server", "uninstall.sh"), 0, self.output)
        self.assertFalse(self.config.exists())
        self.assertFalse((self.root / "usr/local/etc/xray/jeekremote-reality-link.conf").exists())
        self.assertEqual(next((self.root / "root").glob("xray-config-backup-*.json")).read_text(), original)


if __name__ == "__main__":
    unittest.main(argv=[__file__], verbosity=2)
