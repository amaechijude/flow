# eBPF TCP+UDP attribution controlled validation

> **Status: accepted 60-second soak evidence (2026-11-18).** All checks passed.
> Parent TGID 84922 accounted for exactly 73,400,320 TX/RX bytes per protocol
> (70 iterations × 1 MiB), one verified generation, all rows on `lo`, 0
> unresolved/unattributed bytes. 14 short-lived child transfers were reported as
> background rows and produced distinct process-generation rows; some show
> `netns=0` and `exe=-` because they exited before `/proc` enrichment.
>
> This is the first sustained datapoint: loader CPU rose from 0.038 s to 0.152 s
> cumulatively (~0.2% of one core) while peak RSS stayed at 3,916 KiB. BPF map
> entries grew from 6 to 34 with no expiry — growth is recorded as an
> observation, not a claim of bounded steady-state memory. A longer run and
> higher churn were not measured.

- Date (UTC): 2026-11-18T11:56:10+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), loopback soak, 60s, 1.000 MiB per protocol per iteration
- Transfers: TCP then UDP, repeated loopback transfers for 60s with short-lived child churn
- Expected process TGID(s): 84922
- Loader wall time: 65.47 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 73400320 | 73400320 | 73400320 | 73400320 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 73400320 | 73400320 | 73400320 | 73400320 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 146800640 |
| Total attributed RX, expected process | 146800640 |
| Other attributed TX (background traffic) | 29360779 |
| Other attributed RX (background traffic) | 29361671 |
| Total attributed TX | 176161419 |
| Total attributed RX | 176162311 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 176914882 |
| `lo` RX delta (IP/link, both protocols) | 176914882 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 84922 | 0 | udp | lo | 4026531833 | 73400320 | 73400320 | python3 | /usr/bin/python3.14 | 11709788305845 | 18750 | yes | yes |
| 84922 | 0 | tcp | lo | 4026531833 | 73400320 | 73400320 | python3 | /usr/bin/python3.14 | 11709788305845 | 18750 | yes | yes |
| 84936 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11715642414001 | 18750 | yes | no |
| 84936 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11715642414001 | 18750 | yes | no |
| 84951 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 11719959119269 | 18750 | yes | no |
| 84951 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 11719959119269 | 18750 | yes | no |
| 84973 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11724278386268 | 18750 | yes | no |
| 84973 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11724278386268 | 18750 | yes | no |
| 84987 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11728597134011 | 18750 | yes | no |
| 84987 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11728597134011 | 18750 | yes | no |
| 85001 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 11732913368301 | 18750 | yes | no |
| 85001 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 11732913368301 | 18750 | yes | no |
| 85035 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11737227916781 | 18750 | yes | no |
| 85035 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11737227916781 | 18750 | yes | no |
| 85052 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11741544123565 | 18750 | yes | no |
| 85052 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11741544123565 | 18750 | yes | no |
| 85065 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11745861155186 | 18750 | yes | no |
| 85065 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11745861155186 | 18750 | yes | no |
| 85078 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11750176838192 | 18750 | yes | no |
| 85078 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11750176838192 | 18750 | yes | no |
| 85100 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11754494734822 | 18750 | yes | no |
| 85100 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11754494734822 | 18750 | yes | no |
| 85115 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11758814190199 | 18750 | yes | no |
| 85115 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11758814190199 | 18750 | yes | no |
| 85128 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 11763127125244 | 18750 | yes | no |
| 85128 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 11763127125244 | 18750 | yes | no |
| 85162 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11767438624301 | 18750 | yes | no |
| 85162 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11767438624301 | 18750 | yes | no |
| 85177 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11771752485220 | 18750 | yes | no |
| 85177 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 11771752485220 | 18750 | yes | no |
| 6030 | 1000 | tcp | wlp0s20f3 | 4026531833 | 460 | 1282 | opencode | /home/amaechi/.opencode/bin/opencode | 114950780258 | 18270 | yes | no |
| 66102 | 1000 | tcp | wlp0s20f3 | 4026531833 | 101 | 171 | msedge | /opt/microsoft/msedge/msedge | 7287130883236 | 22107 | yes | no |
| 6019 | 1000 | tcp | lo | 4026531833 | 0 | 90 | opencode | /home/amaechi/.opencode/bin/opencode | 114587790130 | 18270 | yes | no |
| 6030 | 1000 | tcp | lo | 4026531833 | 90 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 114950780258 | 18270 | yes | no |

## Interface attribution

- Expected interface: all expected-process rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Soak / map growth observation

- Duration requested: 60 s; wall time: 65.47 s
- Parent iterations: 70
- Short-lived child transfers: 14
- BPF map entries: first 6, last 34, max 34
- CPU samples (s): 0.038378516, 0.043023519, 0.048880794, 0.057185982, 0.067102651, 0.077203318, 0.085904101, 0.097401693, 0.108899419, 0.124845645, 0.140567567, 0.151503326
- Peak RSS samples (KiB): 3916, 3916, 3916, 3916, 3916, 3916, 3916, 3916, 3916, 3916, 3916, 3916
- The BPF map has no expiry; growth here is an observation, not a claim of bounded steady-state memory.

## Loader overhead (first datapoint)

- CPU time: 0.124 s
- Average CPU: 0.189% of one core during the run
- Peak RSS: 3916 KiB

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
  `lo` counters count IP/link bytes including headers. Ratios near 100% are expected but exact equality
  is not.
- Exactness checks cover only the parent TGID (84922); 14 short-lived child transfer(s) are reported as background rows and are not asserted.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- Loopback traffic appears once in each interface direction. Child churn rows may have unverified identity after exit; map growth is reported as observation only, since the BPF map has no expiry.
- This is a controlled sanity check, not a claim of complete attribution coverage.
