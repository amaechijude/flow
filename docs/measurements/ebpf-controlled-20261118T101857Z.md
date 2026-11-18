# eBPF TCP+UDP attribution controlled validation

> **Status: accepted two-interface evidence (2026-11-18, veth across two network namespaces).**
> All checks passed. TCP and UDP each matched 16,777,216 TX/RX exactly. The
> root server's RX rows resolved to `flowvA` in the root netns; the client
> helpers' TX rows resolved to `22@4026532907` (flowvB's ifindex in `flowvns`).
> Expected TGIDs 59914, 59958, 59959 each had exactly one verified process
> generation. Expected-process unresolved bytes were 0. Background UDP rows
> from unrelated processes show unresolved interfaces; they are outside the
> expected-process gate and are reported rather than hidden.
>
> Scope: two virtual interfaces across two network namespaces, not physical
> NICs, VPN/tunnel paths, or virtual-interface double-counting. The BPF key is
> unchanged, so identical keys in distinct namespaces would still merge; this
> run distinguishes them via the emitted netns id at observation time.

- Date (UTC): 2026-11-18T10:19:04+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), temporary veth pair across two network namespaces (192.0.2.1/24 <-> 192.0.2.2/24)
- Transfers: TCP then UDP, root server plus client helpers in flowvns
- Expected process TGID(s): 59914, 59958, 59959
- Loader wall time: 7.28 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 16777216 | 16777216 | 16777216 | 16777216 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 16777216 | 16777216 | 16777216 | 16777216 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 33554432 |
| Total attributed RX, expected process | 33554432 |
| Other attributed TX (background traffic) | 4758 |
| Other attributed RX (background traffic) | 1550 |
| Total attributed TX | 33559190 |
| Total attributed RX | 33555982 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `flowvA` TX delta | 11941 |
| `flowvA` RX delta | 34025332 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 59958 | 0 | tcp | 22@4026532907 | 4026532907 | 16777216 | 0 | python3 | /usr/bin/python3.14 | 5943959775946 | 19055 | yes | yes |
| 59914 | 0 | tcp | flowvA | 4026531833 | 0 | 16777216 | python3 | /usr/bin/python3.14 | 5941880818160 | 19055 | yes | yes |
| 59914 | 0 | udp | flowvA | 4026531833 | 0 | 16777216 | python3 | /usr/bin/python3.14 | 5941880818160 | 19055 | yes | yes |
| 59959 | 0 | udp | 22@4026532907 | 4026532907 | 16777216 | 0 | python3 | /usr/bin/python3.14 | 5945638487409 | 19055 | yes | yes |
| 5569 | 1000 | udp | - | 4026531833 | 3208 | 0 | python3 | /usr/bin/python3.14 | 81419538534 | 15672 | yes | no |
| 1369 | 103 | udp | - | 4026531833 | 1550 | 0 | avahi-daemon | /usr/sbin/avahi-daemon | 6300592348 | 6245 | yes | no |
| 1369 | 103 | udp | flowvA | 4026531833 | 0 | 1550 | avahi-daemon | /usr/sbin/avahi-daemon | 6300592348 | 6245 | yes | no |

## Interface attribution

- Expected interface: RX via `flowvA` in the root netns; TX via flowvB ifindex 22 in netns 4026532907
- Expected-process resolved interface(s): 22@4026532907, flowvA
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): 22@4026532907, flowvA (TX: 22@4026532907; RX: flowvA)
- UDP interface name(s): 22@4026532907, flowvA (TX: 22@4026532907; RX: flowvA)

## Loader overhead (first datapoint)

- CPU time: 0.007 s
- Average CPU: 0.096% of one core during the run
- Peak RSS: 3900 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: True
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  `flowvA` counters count IP/link bytes including headers. Ratios near 100% are expected
  but exact equality is not.
- The expected rows include the root server and the client helpers in flowvns; each TGID
  has exactly one verified process generation.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- A temporary veth pair spans two network namespaces, forcing packets over both interfaces. ifindex is namespace-scoped, so the helper namespace's interface is reported as ifindex@netns rather than a resolved name. This does not cover physical NICs, VPN/tunnel paths, or virtual-interface double-counting.
- This is a controlled sanity check, not a claim of complete attribution coverage.
