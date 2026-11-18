#!/usr/bin/env python3
"""Combined TCP+UDP echo listener for Termux on an Android phone hotspot.

Run this on the phone while using `validate-ebpf-attribution.py --mode nic`:

    pkg update -y
    pkg install -y python
    termux-wake-lock
    python3 termux_echo_listener.py

TCP listens on 0.0.0.0:9000, reads until EOF, echoes the payload back, prints
`tcp <bytes>`, then exits. UDP listens on 0.0.0.0:9001, echoes datagrams, and
prints `udp <bytes>` after 3 idle seconds. Keep Termux awake until both counts
are printed. This is a test helper only; bind to a trusted local hotspot.
"""

import socket
import threading

TCP_PORT = 9000
UDP_PORT = 9001
UDP_IDLE_SECONDS = 3


def tcp_echo() -> None:
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", TCP_PORT))
    server.listen(1)
    print(f"tcp listening on {TCP_PORT}", flush=True)
    conn, addr = server.accept()
    print("tcp peer", addr, flush=True)
    received = bytearray()
    try:
        while True:
            data = conn.recv(1 << 20)
            if not data:
                break
            received.extend(data)
        conn.sendall(received)
        print("tcp", len(received), flush=True)
    finally:
        conn.close()
        server.close()


def udp_echo() -> None:
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.bind(("0.0.0.0", UDP_PORT))
    server.settimeout(UDP_IDLE_SECONDS)
    print(f"udp listening on {UDP_PORT}", flush=True)
    total = 0
    try:
        while True:
            try:
                data, addr = server.recvfrom(1 << 16)
            except socket.timeout:
                break
            if not data:
                continue
            total += len(data)
            server.sendto(data, addr)
        print("udp", total, flush=True)
    finally:
        server.close()


def main() -> None:
    threading.Thread(target=udp_echo, daemon=True).start()
    tcp_echo()


if __name__ == "__main__":
    main()
