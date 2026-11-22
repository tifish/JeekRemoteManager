"""Loopback SSH fixture for the ssh_mfa_pool_check Debug MCP probe."""

import hashlib
import hmac
import socket
import struct
import threading
import time

import paramiko


host_key = paramiko.RSAKey.generate(2048)
used_steps = set()
used_steps_gate = threading.Lock()


def accept_code(value):
    step = int(time.time()) // 5
    with used_steps_gate:
        for candidate in (step, step - 1, step + 1):
            digest = hmac.new(b"12345678901234567890", struct.pack(">Q", candidate), hashlib.sha1).digest()
            expected = (struct.unpack_from(">I", digest, digest[-1] & 15)[0] & 0x7fffffff) % 1000000
            if value == f"{expected:06d}".encode() and candidate not in used_steps:
                used_steps.add(candidate)
                return True
    return False


class FixtureServer(paramiko.ServerInterface):
    def __init__(self):
        self._lock = threading.Lock()
        self.channels = 0
        self.verified = False

    def check_auth_password(self, username, password):
        if username == "mfa-pool" and password == "jrm-test-password":
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_request(self, kind, chanid):
        with self._lock:
            if kind != "session" or self.channels >= 2:
                return paramiko.OPEN_FAILED_RESOURCE_SHORTAGE
            self.channels += 1
            return paramiko.OPEN_SUCCEEDED

    def check_channel_pty_request(self, channel, term, width, height, pixelwidth, pixelheight, modes):
        return True

    def check_channel_shell_request(self, channel):
        threading.Thread(target=self.serve_shell, args=(channel,), daemon=True).start()
        return True

    def serve_shell(self, channel):
        try:
            with self._lock:
                verified = self.verified
            if not verified:
                channel.settimeout(12)
                channel.sendall(b"2nd Password:")
                value = bytearray()
                while True:
                    char = channel.recv(1)
                    if not char:
                        return
                    if char in (b"\r", b"\n"):
                        if accept_code(value):
                            break
                        value.clear()
                        channel.sendall(b"\r\n2nd Password:")
                    else:
                        value.extend(char)
                with self._lock:
                    self.verified = True
                channel.sendall(b"\r\nFixture MFA accepted\r\nfixture@target:~$ ")
            else:
                channel.sendall(b"Fixture reused\r\nfixture@target:~$ ")
            channel.settimeout(None)
            while channel.recv(256):
                pass
        except (OSError, EOFError):
            pass
        finally:
            channel.close()


def serve_connection(client):
    transport = paramiko.Transport(client)
    transport.add_server_key(host_key)
    try:
        transport.start_server(server=FixtureServer())
        channels = []
        while transport.is_active():
            channel = transport.accept(0.25)
            if channel:
                channels.append(channel)
    finally:
        transport.close()


with socket.socket() as server:
    server.bind(("127.0.0.1", 0))
    server.listen()
    print(server.getsockname()[1], flush=True)
    while True:
        client, _ = server.accept()
        threading.Thread(target=serve_connection, args=(client,), daemon=True).start()
