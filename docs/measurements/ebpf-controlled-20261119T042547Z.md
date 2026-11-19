# eBPF TCP+UDP attribution controlled validation

> **Status: accepted diagnostic evidence; identity mismatch localized but not fixed.**
> Byte totals were exact and all expected rows were on `lo`. The diagnostic shows
> loader raw `/proc` ticks = harness `/proc` starttime = **4,444,599**, so the
> userspace `/proc` read is consistent. However BPF `start_ns` converts to
> **4,314,025** ticks — a **130,574-tick (~1,305 s) gap**. This is far beyond the
> ±2-tick tolerance and explains `identity_verified: false`. The cached
> `startticks=0` is only a consequence: the loader caches enrichment only after
> verification succeeds. Next isolation: rerun with `--prod-workers 1` to see if
> the gap is specific to multithreaded event attribution or systemic.

- Date (UTC): 2026-11-19T04:26:21+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), identity diagnostic, 8 workers × 30s
- Transfers: 8 concurrent workers, 30s, 1.000 MiB per protocol per transfer
- Expected process TGID(s): 174081
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
| Other attributed TX (background traffic) | 48391 |
| Other attributed RX (background traffic) | 188863 |
| Total attributed TX | 604028167 |
| Total attributed RX | 604168639 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 607395794 |
| `lo` RX delta (IP/link, both protocols) | 607395794 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 174081 | 0 | udp | lo | 4026531833 | 301989888 | 301989888 | Thread-3 (prod_ | - | 43140251955951 | 23051 | no | yes |
| 174081 | 0 | tcp | lo | 4026531833 | 301989888 | 301989888 | Thread-11 (serv | - | 43140251955951 | 23051 | no | yes |
| 46770 | 1000 | tcp | wlp0s20f3 | 4026531833 | 36659 | 158173 | Chrome_ChildIOT | - | 14343641288622 | 25707 | no | no |
| 46946 | 1000 | tcp | wlp0s20f3 | 4026531833 | 2018 | 16166 | code | - | 14345129201072 | 25707 | no | no |
| 494 | 989 | udp | lo | 4026531833 | 4984 | 2434 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 22691 | 1000 | tcp | wlp0s20f3 | 4026531833 | 2021 | 4887 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 494 | 989 | udp | wlp0s20f3 | 4026531833 | 660 | 2620 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 46770 | 1000 | udp | lo | 4026531833 | 923 | 1994 | Chrome_ChildIOT | - | 14343641288622 | 25707 | no | no |
| 7292 | 1000 | tcp | wlp0s20f3 | 4026531833 | 460 | 1321 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 46731 | 1000 | udp | lo | 4026531833 | 118 | 498 | ThreadPoolForeg | - | 14343437733001 | 25707 | no | no |
| 1612 | 988 | udp | wlp0s20f3 | 4026531833 | 188 | 188 | chronyd | /usr/sbin/chronyd | 7331500526 | 6369 | yes | no |
| 46946 | 1000 | udp | lo | 4026531833 | 96 | 260 | libuv-worker | - | 14345129201072 | 25707 | no | no |
| 107711 | 1000 | tcp | wlp0s20f3 | 4026531833 | 130 | 196 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |
| 22691 | 1000 | udp | lo | 4026531833 | 80 | 72 | Bun Pool 0 | - | 7554278408042 | 22527 | no | no |
| 7292 | 1000 | tcp | lo | 4026531833 | 54 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 22691 | 1000 | tcp | lo | 4026531833 | 0 | 54 | HTTP Client | - | 7554278408042 | 22527 | no | no |

## Interface attribution

- Expected interface: all expected rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Identity diagnostic

- clk_tck from loader M line: 100
- /proc/174081/stat starttime: 4444599
- /proc/174081/exe: /usr/bin/python3.14
- TGID 174081 proto 2 comm=Thread-3 (prod_ exe=-: start_ns=43140251955951 cached_ticks=0 raw_ticks=4444599 computed=4314025 computed−raw=-130574 verified=False
- TGID 174081 proto 1 comm=Thread-11 (serv exe=-: start_ns=43140251955951 cached_ticks=0 raw_ticks=4444599 computed=4314025 computed−raw=-130574 verified=False
- If computed≈stored but verified is false, investigate /proc exe enrichment; if they differ, investigate event-time start-time/leader handling.

## Loader overhead (first datapoint)

- CPU time: 0.055 s
- Average CPU: 0.16% of one core during the run
- Peak RSS: 3864 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: n/a (short-lived emitting threads)
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
