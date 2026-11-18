# eBPF TCP attribution controlled validation

> **Status: rejected as gate evidence (2026-11-18).** Byte totals were exact, but the expected process split into two rows because the prototype keyed identity by per-thread start time. See `ebpf-controlled-20261118T044634Z.md` for the accepted rerun after the group-leader fix, and `docs/attribution.md` for interpretation.

- Date (UTC): 2026-11-18T04:28:46+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Transfer: 16777216 bytes (16.000 MiB) over loopback `lo`
- Loader wall time: 4.13 s

## Results

| Metric | Bytes |
| --- | ---: |
| Payload sent (userspace) | 16777216 |
| Payload received (userspace) | 16777216 |
| Attributed TX (eBPF, `tcp_sendmsg`) | 16777234 |
| Attributed RX (eBPF, `tcp_recvmsg`) | 16777234 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both directions on lo) | 16793734 |
| `lo` RX delta (IP/link, both directions on lo) | 16793734 |

| Ratio | Value |
| --- | ---: |
| Attributed TX / payload sent | 100.0% |
| Attributed RX / payload received | 100.0% |

## Per-process rows (TGID + event-time start_ns + UID)

| TGID | UID | TX | RX | comm | exe | start_ns | cgroup | identity verified |
| ---: | ---: | ---: | ---: | --- | --- | ---: | --- | --- |
| 44074 | 0 | 0 | 16777216 | Thread-3 (serve | - | 15984678144554 | 24677 | no |
| 44074 | 0 | 16777216 | 0 | python3 | /usr/bin/python3.14 | 15983046056461 | 24677 | yes |
| 27831 | 1000 | 0 | 18 | HTTP Client | - | 10285071494173 | 22585 | no |
| 6309 | 1000 | 18 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 942694898176 | 18576 | yes |

## Loader overhead (first datapoint)

- CPU time: 0.01 s
- Average CPU: 0.242% of one core during the run
- Peak RSS: 3592 KiB

## Checks

- Attributed TX within ±5% of payload: True
- Attributed RX within ±5% of payload: True
- No unattributed bytes in a fully controlled single-process transfer: True

## Interpretation and caveats

- eBPF counts TCP payload bytes at `tcp_sendmsg`/`tcp_recvmsg` return; `lo` counters count
  IP/link bytes including headers. Ratios near 100% are expected but exact equality is not.
- Loopback traffic appears once in each interface direction; this run does not exercise
  physical-interface, UDP/QUIC, namespace, VPN, or container paths.
- The load generator runs in a single process, so its TX and RX rows are both expected.
- This is a controlled sanity check, not a claim of complete attribution coverage.
