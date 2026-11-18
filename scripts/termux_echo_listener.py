#!/usr/bin/env python3
"""Combined TCP+UDP echo listener for Termux on an Android phone hotspot.

Run this on the phone while using `validate-ebpf-attribution.py --mode nic`:

    pkg update -y
    pkg install -y python
    termux-wake-lock
    python3 termux_echo_listener.py

TCP listens on 0.0.0.0:9000, reads until EOF, echoes the payload back, prints
`tcp <bytes>`, then signals the UDP thread to finish. UDP listens on 0.0.0.0:9001,
echoes datagrams, and prints `udp <bytes>` after TCP completes plus a short drain
window. Keep Termux awake until both counts are printed. This is a test helper
only; bind to a trusted local hotspot.
"""

import socket
import threading
import time

TCP_PORT = 9000
UDP_PORT = 9001
UDP_DRAIN_SECONDS = 5
tcp_done = threading.Event()


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
        tcp_done.set()


def udp_echo() -> None:
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 << 20)
    server.bind(("0.0.0.0", UDP_PORT))
    server.settimeout(0.5)
    print(f"udp listening on {UDP_PORT}", flush=True)
    total = 0
    last_packet = time.monotonic()
    try:
        while True:
            try:
                data, addr = server.recvfrom(1 << 16)
            except socket.timeout:
                # Stay alive while TCP is still running; only drain after TCP
                # completes, so the harness's UDP phase is not missed.
                if tcp_done.is_set() and time.monotonic() - last_packet > UDP_DRAIN_SECONDS:
                    break
                continue
            if not data:
                continue
            last_packet = time.monotonic()
            total += len(data)
            server.sendto(data, addr)
        print("udp", total, flush=True)
    finally:
        server.close()


def main() -> None:
    # Non-daemon so the process stays alive for the UDP drain after TCP exits.
    threading.Thread(target=udp_echo, daemon=False).start()
    tcp_echo()


if __name__ == "__main__":
    main()
