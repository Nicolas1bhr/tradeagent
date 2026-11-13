#!/usr/bin/env python3
"""A REAL VNC LOGIN (U-window-door), for CI's linux-host job: RFB 3.8, VNC authentication, against Xvnc.

    vnc-auth.py <address> <port>        the password on stdin (its first line), never an argument

Exit 0 and "accepted" when the server answers the login with OK and then sends its ServerInit (the session is real); exit 1
and "refused: <the server's reason>" when it answers the login with failure; exit 2 on anything else (no VNC there, no VNC
authentication offered, a cut connection). It prints nothing of the password.

Standard library only, so the job downloads nothing for it: VNC authentication is single DES (ECB) over the server's
16-byte challenge, keyed with the password's first eight bytes (zero-padded) each with its bits reversed, and DES is not in
Python's standard library, so it is here, written from FIPS 46-3 and checked against that standard's published example on
every run before it is used.
"""
import socket
import struct
import sys

IP = [58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4, 62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
      57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3, 61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7]
FP = [40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31, 38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
      36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27, 34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9, 49, 17, 57, 25]
E = [32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
     16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1]
P = [16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10, 2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25]
PC1 = [57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
       63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4]
PC2 = [14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4, 26, 8, 16, 7, 27, 20, 13, 2,
       41, 52, 31, 37, 47, 55, 30, 40, 51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32]
SHIFTS = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1]
S = [
    [14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7, 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
     4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0, 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13],
    [15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10, 3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
     0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15, 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9],
    [10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8, 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
     13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7, 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12],
    [7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15, 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
     10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4, 3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14],
    [2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9, 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
     4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14, 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3],
    [12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11, 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
     9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6, 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13],
    [4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1, 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
     1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2, 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12],
    [13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7, 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
     7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8, 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11],
]


def permute(value, table, width):
    out = 0
    for pos in table:
        out = (out << 1) | ((value >> (width - pos)) & 1)
    return out


def subkeys(key):
    k = permute(int.from_bytes(key, "big"), PC1, 64)
    c, d = k >> 28, k & 0xFFFFFFF
    keys = []
    for s in SHIFTS:
        c = ((c << s) | (c >> (28 - s))) & 0xFFFFFFF
        d = ((d << s) | (d >> (28 - s))) & 0xFFFFFFF
        keys.append(permute((c << 28) | d, PC2, 56))
    return keys


def des_encrypt_block(key, block):
    keys = subkeys(key)
    v = permute(int.from_bytes(block, "big"), IP, 64)
    left, right = v >> 32, v & 0xFFFFFFFF
    for k in keys:
        x = permute(right, E, 32) ^ k
        f = 0
        for i in range(8):
            six = (x >> (42 - 6 * i)) & 0x3F
            row = ((six & 0x20) >> 4) | (six & 1)
            col = (six >> 1) & 0xF
            f = (f << 4) | S[i][row * 16 + col]
        left, right = right, left ^ permute(f, P, 32)
    return permute((right << 32) | left, FP, 64).to_bytes(8, "big")


def reverse_bits(b):
    return int(f"{b:08b}"[::-1], 2)


def recv_exactly(sock, n):
    data = b""
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise ConnectionError(f"the server closed the connection after {len(data)} of {n} bytes")
        data += chunk
    return data


def main():
    # FIPS 46-3's published example (key 133457799BBCDFF1, plaintext 0123456789ABCDEF): a DES that is wrong never logs in.
    assert des_encrypt_block(bytes.fromhex("133457799BBCDFF1"), bytes.fromhex("0123456789ABCDEF")) == bytes.fromhex("85E813540F0AB405"), \
        "this script's DES does not reproduce FIPS 46-3's example"
    if len(sys.argv) != 3:
        print("usage: vnc-auth.py <address> <port>   (the password on stdin)", file=sys.stderr)
        return 2
    password = sys.stdin.readline().rstrip("\r\n").encode("ascii")
    key = bytes(reverse_bits(b) for b in (password + b"\0" * 8)[:8])
    with socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=15) as sock:
        version = recv_exactly(sock, 12)
        if not version.startswith(b"RFB 003."):
            print(f"no VNC server there: {version!r}", file=sys.stderr)
            return 2
        sock.sendall(b"RFB 003.008\n")
        count = recv_exactly(sock, 1)[0]
        if count == 0:
            reason = recv_exactly(sock, struct.unpack(">I", recv_exactly(sock, 4))[0]).decode("utf-8", "replace")
            print(f"refused before any login: {reason}")
            return 1
        types = list(recv_exactly(sock, count))
        if 2 not in types:
            print(f"the server offers no VNC authentication: {types}", file=sys.stderr)
            return 2
        sock.sendall(bytes([2]))
        challenge = recv_exactly(sock, 16)
        sock.sendall(des_encrypt_block(key, challenge[:8]) + des_encrypt_block(key, challenge[8:]))
        result = struct.unpack(">I", recv_exactly(sock, 4))[0]
        if result != 0:
            try:
                reason = recv_exactly(sock, struct.unpack(">I", recv_exactly(sock, 4))[0]).decode("utf-8", "replace")
            except (ConnectionError, OSError):
                reason = "(no reason given)"
            print(f"refused: {reason}")
            return 1
        sock.sendall(bytes([1]))   # ClientInit, shared: the owner's own session, if one is open, stays
        width, height = struct.unpack(">HH", recv_exactly(sock, 4))
        recv_exactly(sock, 16)
        name = recv_exactly(sock, struct.unpack(">I", recv_exactly(sock, 4))[0]).decode("utf-8", "replace")
        print(f"accepted: desktop '{name}', {width}x{height}")
        return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ConnectionError, OSError) as e:
        print(f"no VNC login: {e}", file=sys.stderr)
        sys.exit(2)
