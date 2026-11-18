# eBPF TCP attribution controlled validation

- Date (UTC): 2026-11-18T04:46:38+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Transfer: 16777216 bytes (16.000 MiB) over loopback `lo`
- Expected process TGID: 47748
- Loader wall time: 4.14 s

## Results

| Metric | Bytes |
| --- | ---: |
| Payload sent (userspace) | 16777216 |
| Payload received (userspace) | 16777216 |
| Attributed TX, expected process (eBPF, `tcp_sendmsg`) | 16777216 |
| Attributed RX, expected process (eBPF, `tcp_recvmsg`) | 16777216 |
| Other attributed TX (background traffic) | 2777 |
| Other attributed RX (background traffic) | 10108 |
| Total attributed TX | 16779993 |
| Total attributed RX | 16787324 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both directions on lo) | 16796684 |
| `lo` RX delta (IP/link, both directions on lo) | 16796684 |

| Ratio | Value |
| --- | ---: |
| Expected-process TX / payload sent | 100.0% |
| Expected-process RX / payload received | 100.0% |

## Per-process rows (TGID + event-time start_ns + UID)

| TGID | UID | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 47748 | 0 | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 17055033283099 | 24677 | yes | yes |
| 44356 | 1000 | 2777 | 10108 | code | /snap/code/268/usr/share/code/code | 16117762328264 | 25243 | yes | no |

## Loader overhead (first datapoint)

- CPU time: 0.004 s
- Average CPU: 0.097% of one core during the run
- Peak RSS: 3672 KiB

## Checks

- Expected-process attributed TX within ±1% of payload: True
- Expected-process attributed RX within ±1% of payload: True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: True
- No unattributed bytes in a fully controlled single-process transfer: True

## Interpretation and caveats

- eBPF counts TCP payload bytes at `tcp_sendmsg`/`tcp_recvmsg` return; `lo` counters count
  IP/link bytes including headers. Ratios near 100% are expected but exact equality is not.
- The expected-process rows are the load generator itself (its server thread shares the TGID);
  background traffic from other processes during the run is reported separately.
- Loopback traffic appears once in each interface direction; this run does not exercise
  physical-interface, UDP/QUIC, namespace, VPN, or container paths.
- This is a controlled sanity check, not a claim of complete attribution coverage.
