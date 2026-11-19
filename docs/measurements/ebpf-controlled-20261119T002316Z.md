# eBPF TCP+UDP attribution controlled validation

> **Status: accepted production-load proxy evidence (2026-11-19), with an identity caveat.**
> Eight concurrent workers ran 2,850 TCP+UDP transfers over 300 s. Attributed
> bytes matched exactly per protocol/direction (2,988,441,600), expected rows
> were all on `lo`, 0 unattributed, `check_single_generation: True`. Loader CPU
> averaged **0.144%** of one core and peak RSS stayed **3,944 KiB**; BPF map
> entries stayed at 4→14. This accepts short-term concurrent overhead/stability.
>
> Caveat: `check_identity: False` — the expected process row retained
> kernel-captured start time/cgroup/comm but `/proc` enrichment did not verify
> it in this multithreaded sustained load. This does not affect byte/interface
> checks here, but identity verification under such load remains an open issue.
> This is a loopback concurrency/duration proxy, not a production SLA or real
> Internet workload. The per-transfer payload line below is corrected to 1 MiB.

- Date (UTC): 2026-11-19T00:28:20+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per transfer: 1048576 bytes (1.000 MiB) per protocol, production-load soak, 8 workers × 300s
- Transfers: 8 concurrent workers, 300s, 1.000 MiB per protocol per transfer
- Expected process TGID(s): 84782
- Loader wall time: 304.54 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 2988441600 | 2988441600 | 2988441600 | 2988441600 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 2988441600 | 2988441600 | 2988441600 | 2988441600 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 5976883200 |
| Total attributed RX, expected process | 5976883200 |
| Other attributed TX (background traffic) | 32505 |
| Other attributed RX (background traffic) | 135107 |
| Total attributed TX | 5976915705 |
| Total attributed RX | 5977018307 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 6008999431 |
| `lo` RX delta (IP/link, both protocols) | 6008999431 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 84782 | 0 | udp | lo | 4026531833 | 2988441600 | 2988441600 | Thread-3 (prod_ | - | 29664814661748 | 23051 | no | yes |
| 84782 | 0 | tcp | lo | 4026531833 | 2988441600 | 2988441600 | Thread-3 (prod_ | - | 29664814661748 | 23051 | no | yes |
| 46770 | 1000 | tcp | wlp0s20f3 | 4026531833 | 18742 | 114666 | Chrome_ChildIOT | - | 14343641288622 | 25707 | no | no |
| 7292 | 1000 | tcp | wlp0s20f3 | 4026531833 | 6834 | 10335 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 494 | 989 | udp | lo | 4026531833 | 3076 | 1680 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 22691 | 1000 | tcp | wlp0s20f3 | 4026531833 | 2021 | 2295 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 494 | 989 | udp | wlp0s20f3 | 4026531833 | 632 | 2496 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 46770 | 1000 | udp | lo | 4026531833 | 490 | 1505 | Chrome_ChildIOT | - | 14343641288622 | 25707 | no | no |
| 1403 | 103 | udp | wlp0s20f3 | 4026531833 | 0 | 1077 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 7292 | 1000 | udp | lo | 4026531833 | 174 | 247 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 46731 | 1000 | udp | lo | 4026531833 | 96 | 278 | ThreadPoolForeg | - | 14343437733001 | 25707 | no | no |
| 22691 | 1000 | tcp | lo | 4026531833 | 0 | 360 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 7292 | 1000 | tcp | lo | 4026531833 | 360 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 22691 | 1000 | udp | lo | 4026531833 | 80 | 168 | Bun Pool 3 | - | 7554278408042 | 22527 | no | no |

## Interface attribution

- Expected interface: all expected rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Production-load soak observation

- Duration requested: 300 s; wall time: 304.54 s
- Workers: 8; transfers: 2850; children: 0
- Attributed expected TX/RX: 5976883200 / 5976883200
- BPF map entries: first 4, last 14, max 14
- CPU samples (s): 0.043884849, 0.04733919, 0.052589376, 0.059763596, 0.066881269, 0.073997061, 0.080782319, 0.08656616, 0.093684883, 0.099691259, 0.106209212, 0.111887762, 0.117887542, 0.124304917, 0.130820486, 0.137563412, 0.144925882, 0.150771141, 0.1570326, 0.162980899, 0.168527941, 0.175656802, 0.182916634, 0.189695295, 0.196667133, 0.203506387, 0.209107839, 0.215069571, 0.221974342, 0.228864621, 0.235899497, 0.243322641, 0.251995571, 0.260553024, 0.268209151, 0.276303425, 0.283319621, 0.290481781, 0.298858424, 0.30685051, 0.314099172, 0.322523029, 0.329961472, 0.338189196, 0.345621499, 0.354188829, 0.36198767, 0.370314068, 0.379058998, 0.386876632, 0.396027845, 0.40402927, 0.41200661, 0.421976356, 0.42987374, 0.438754453, 0.446777587, 0.455716949, 0.46372892
- Peak RSS samples (KiB): 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944, 3944
- This is a loopback concurrency/duration proxy, not a real Internet workload or production SLA evidence.

## Loader overhead (first datapoint)

- CPU time: 0.438 s
- Average CPU: 0.144% of one core during the run
- Peak RSS: 3944 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: False
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
