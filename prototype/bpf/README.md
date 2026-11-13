# TCP byte attribution prototype (BPF)

This prototype counts bytes returned by kernel TCP send/recv functions:

- `fexit/tcp_sendmsg` → upload/TX bytes (`ret > 0`)
- `kretprobe/tcp_recvmsg` → download/RX bytes (`ret > 0`)

Counters are aggregated per `(tgid, uid)` in BPF maps and printed periodically by a userspace loader.

## Measurement semantics (important)
- **What is counted:** bytes returned by `tcp_sendmsg` / `tcp_recvmsg` at hook time (TCP-path bytes observed by these functions), cumulative while attached.
- **Not counted:** UDP, QUIC-over-UDP, ICMP, non-TCP kernel paths, traffic before attach/after detach.
- **Attribution scope:** keyed by current task TGID/UID at hook execution. Userspace loader enriches/caches `/proc` identity (comm/exe/startticks) when first seen/alive to retain context if a process exits before later snapshots.
- **Unattributed (prototype):** when `tgid==0` in hook context, bytes/events are counted in an explicit unattributed map/counter instead of being dropped silently.
- **This is a prototype, not final Flow attribution.** Edge cases (context/ownership nuances, namespaces, tunnels/VPN stacks, PID reuse handling in storage, UDP coverage) require validation.

## Build artifacts
- `flow_tcp_bytes.bpf.c` — BPF program source
- `flow_tcp_bytes.bpf.o` — compiled BPF object
- `flow_tcp_bytes.skel.h` — generated libbpf skeleton (`bpftool gen skeleton`)
- `flow_tcp_bytes_loader.c` / `flow_tcp_bytes_loader` — userspace loader/monitor

## Rebuild
```bash
clang -O2 -g -target bpf -D__TARGET_ARCH_x86 -I prototype/bpf -c prototype/bpf/flow_tcp_bytes.bpf.c -o prototype/bpf/flow_tcp_bytes.bpf.o
bpftool gen skeleton prototype/bpf/flow_tcp_bytes.bpf.o > prototype/bpf/flow_tcp_bytes.skel.h
cc -O2 -Wall -I prototype/bpf -o prototype/bpf/flow_tcp_bytes_loader prototype/bpf/flow_tcp_bytes_loader.c -lbpf -lelf -lz
```

## Run (requires sufficient BPF privileges)
On systems with `unprivileged_bpf_disabled=2`, loading/attaching typically requires elevated privileges (root or appropriate capabilities depending on policy/kernel config):

```bash
sudo ./prototype/bpf/flow_tcp_bytes_loader 2 15
```

Args: `interval_seconds rounds` (defaults: `2 10`).

Loader output includes cumulative and per-snapshot delta bytes per `(TGID,UID)` plus best-effort process enrichment from `/proc/<tgid>/{comm,exe,stat}` (startticks). This aids correlation but is **not** yet a complete stable identity implementation (PID reuse/start-time binding and lifecycle persistence still required).

## Controlled validation idea
In another terminal, generate TCP traffic while attached, e.g.:
```bash
curl -fsSLo /dev/null https://example.com/
curl -fsSLo /dev/null http://speedtest.tele2.net/10MB.zip  # or any suitable test endpoint
```
Then compare reported TGIDs/byte deltas with the generating processes and with aggregate interface deltas (`ip -s link`, `/proc/net/dev`). Record gaps (UDP/QUIC, offload/path differences, tunnels/VPN, short-lived processes, unattributed cases) in docs.
