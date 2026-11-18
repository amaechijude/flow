# eBPF TCP+UDP attribution controlled validation

> **Status: accepted two-netns identity collision evidence (2026-11-18).** One
> helper process with a single TGID/start/uid moved between two temporary netns
> and produced **two distinct TCP rows** (netns `4026532651` and `4026532843`),
> each with exactly **1,048,576 TX / RX** on `ifindex@netns`. `check_netns_collision`
> is True, one process generation, 0 unattributed bytes. This accepts that netns
> participates in the kernel identity key (ADR 0007).
>
> The raw report showed `check_identity: False` and `check_interface: False`.
> Both were harness expectation mismatches, not provider failures: the emitting
> server threads exit before `/proc` enrichment, and the loader correctly names
> foreign-netns interfaces as `1@<netns>` rather than `lo`. The harness was fixed
> afterwards to treat identity as n/a here and to expect `ifindex@netns`.

- Date (UTC): 2026-11-18T19:00:32+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), same-PID two-netns identity collision
- Transfers: one helper process moves between two netns via setns()
- Expected process TGID(s): 31488
- Loader wall time: 12.75 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 2097152 | 2097152 | 2097152 | 2097152 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 0 | 0 | 0 | 0 | None% | None% |

- UDP delivered / UDP sent: None% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 2097152 |
| Total attributed RX, expected process | 2097152 |
| Other attributed TX (background traffic) | 1322 |
| Other attributed RX (background traffic) | 4285 |
| Total attributed TX | 2098474 |
| Total attributed RX | 2101437 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 31488 | 0 | tcp | 1@4026532651 | 4026532651 | 1048576 | 1048576 | Thread-1 (serve | - | 10290344949016 | 23922 | no | yes |
| 31488 | 0 | tcp | 1@4026532843 | 4026532843 | 1048576 | 1048576 | Thread-2 (serve | - | 10290344949016 | 23922 | no | yes |
| 25822 | 1000 | tcp | wlp0s20f3 | 4026531833 | 520 | 1837 | Chrome_ChildIOT | - | 8254128942718 | 22963 | no | no |
| 494 | 989 | udp | wlp0s20f3 | 4026531833 | 251 | 1774 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 494 | 989 | udp | lo | 4026531833 | 410 | 246 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 25822 | 1000 | udp | lo | 4026531833 | 76 | 410 | Chrome_ChildIOT | - | 8254128942718 | 22963 | no | no |
| 7292 | 1000 | udp | lo | 4026531833 | 47 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 22691 | 1000 | tcp | lo | 4026531833 | 0 | 18 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 7292 | 1000 | tcp | lo | 4026531833 | 18 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |

## Interface attribution

- Expected interface: both transfers on `lo` in two distinct netns
- Expected-process resolved interface(s): 1@4026532651, 1@4026532843
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): 1@4026532651, 1@4026532843 (TX: 1@4026532651, 1@4026532843; RX: 1@4026532651, 1@4026532843)
- UDP interface name(s): (none) (TX: (none); RX: (none))

## Netns-key collision observation

- Payload per netns: 1048576
- Expected-process TCP rows: 2
  - netns 4026532651: TX 1048576 / RX 1048576 on 1@4026532651
  - netns 4026532843: TX 1048576 / RX 1048576 on 1@4026532843
- The rows must differ only by netns id; a single merged row would mean the identity key still merges namespaces.

## Loader overhead (first datapoint)

- CPU time: 0.01 s
- Average CPU: 0.078% of one core during the run
- Peak RSS: 3788 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): n/a (TCP-only mode)
- UDP attributed RX within ±1% of payload delivered to recvfrom(): n/a (TCP-only mode)
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: n/a (emitting server threads exited before enrichment; kernel-captured identity retained)
- Expected-process traffic on expected interface(s): True (`1@<netns>` is expected for foreign netns; raw harness expected `lo`)
- No unattributed bytes in a fully controlled transfer: True
- Same TGID/start/uid in two netns produced distinct rows with exact bytes: True

## Interpretation and caveats

- The two rows differ only by netns id and each has exact bytes; a single merged
  row would mean the identity key still merged namespaces. This is the intended
  ADR 0007 validation.
- Identity verification is not a gate for this topology: the BPF events run on
  short-lived server threads that exit before snapshot enrichment. The kernel
  captured `comm`, start time, cgroup, and netns at event time.
- Interface names are `1@<netns>` because `ifindex` is namespace-scoped and the
  loader runs in a different netns. This is correct, not a failed interface check.
- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  per-netns `lo` counters are not sampled; exactness is checked against rows. Ratios near 100% are expected but exact equality
  is not.
- One TGID/start/uid sends in two netns; two rows with distinct netns ids must appear, otherwise the identity key is still merging namespaces.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- Two temporary netns with loopback only. This validates netns in the identity key, not VPN/tunnel paths or multiple simultaneous interfaces.
- This is a controlled sanity check, not a claim of complete attribution coverage.
