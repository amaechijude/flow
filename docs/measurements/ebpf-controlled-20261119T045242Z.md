# eBPF TCP+UDP attribution controlled validation

> **Status: accepted identity fix validation, 8 workers (2026-11-19).** With eight
> concurrent workers, BPF `start_ns` converts exactly to `/proc` starttime
> (4,606,117 ticks), `computed−raw=0`, `verified=True`, `exe=/usr/bin/python3.14`,
> and BPF comm is `python3` for both protocol rows. Byte totals were exact, all
> rows on `lo`, 0 unattributed. This confirms the boottime fix (ADR 0009) works
> under multithreaded load.

- Date (UTC): 2026-11-19T04:53:16+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), identity diagnostic, 8 workers × 30s
- Transfers: 8 concurrent workers, 30s, 1.000 MiB per protocol per transfer
- Expected process TGID(s): 177477
- Loader wall time: 34.35 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 301989888 | 301989888 | 301989888 | 301989888 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 301989888 | 301989888 | 301989888 | 301989888 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 603979776 |
| Total attributed RX, expected process | 603979776 |
| Other attributed TX (background traffic) | 9113 |
| Other attributed RX (background traffic) | 23407 |
| Total attributed TX | 603988889 |
| Total attributed RX | 604003183 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 607503754 |
| `lo` RX delta (IP/link, both protocols) | 607503754 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 177477 | 0 | udp | lo | 4026531833 | 301989888 | 301989888 | python3 | /usr/bin/python3.14 | 46061171819091 | 23051 | yes | yes |
| 177477 | 0 | tcp | lo | 4026531833 | 301989888 | 301989888 | python3 | /usr/bin/python3.14 | 46061171819091 | 23051 | yes | yes |
| 46770 | 1000 | tcp | wlp0s20f3 | 4026531833 | 6573 | 19881 | code | /snap/code/268/usr/share/code/code | 14573971416518 | 25707 | yes | no |
| 494 | 989 | udp | lo | 4026531833 | 1713 | 684 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133884 | 2746 | yes | no |
| 494 | 989 | udp | wlp0s20f3 | 4026531833 | 261 | 1412 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133884 | 2746 | yes | no |
| 46770 | 1000 | udp | lo | 4026531833 | 342 | 1206 | code | /snap/code/268/usr/share/code/code | 14573971416518 | 25707 | yes | no |
| 1612 | 988 | udp | wlp0s20f3 | 4026531833 | 188 | 188 | chronyd | /usr/sbin/chronyd | 7331500665 | 6369 | yes | no |
| 22691 | 1000 | tcp | lo | 4026531833 | 0 | 36 | opencode | /home/amaechi/.opencode/bin/opencode | 7784608536226 | 22527 | yes | no |
| 7292 | 1000 | tcp | lo | 4026531833 | 36 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857158 | 18956 | yes | no |

## Interface attribution

- Expected interface: all expected rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Identity diagnostic

- clk_tck from loader M line: 100
- /proc/177477/stat starttime: 4606117
- /proc/177477/exe: /usr/bin/python3.14
- TGID 177477 proto 2 comm=python3 exe=/usr/bin/python3.14: start_ns=46061171819091 cached_ticks=4606117 raw_ticks=4606117 computed=4606117 computed−raw=0 verified=True
- TGID 177477 proto 1 comm=python3 exe=/usr/bin/python3.14: start_ns=46061171819091 cached_ticks=4606117 raw_ticks=4606117 computed=4606117 computed−raw=0 verified=True
- If computed≈stored but verified is false, investigate /proc exe enrichment; if they differ, investigate event-time start-time/leader handling.

## Loader overhead (first datapoint)

- CPU time: 0.046 s
- Average CPU: 0.134% of one core during the run
- Peak RSS: 3812 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: True (see Identity diagnostic)
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  `lo` counters include IP/link bytes above socket payload. Ratios near 100% are expected but exact equality
  is not.
- Expected rows cover the parent workers and any short-lived children spawned during the soak; per-process byte totals are aggregated.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- Loopback-only production-load proxy: high concurrency and longer duration, not a real Internet workload, physical NIC, or VPN.
- This is a controlled sanity check, not a claim of complete attribution coverage.
