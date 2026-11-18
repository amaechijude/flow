# ADR 0007: Network-namespace identity in attribution keys

- Status: Accepted (implemented; privileged two-netns validation pending)
- Date: 2026-11-18

## Context

The eBPF attribution key is currently
`{start_ns, tgid, uid, ifindex, proto}`. It has no network-namespace dimension.
`ProcessIdentity.StableKey` is `pid:startUnixMs:uid`, also with no netns.

The loader emits a netns id as an optional TSV field, but it is derived from
`/proc/<pid>/ns/net` at snapshot time. That fails for processes that exited
before enrichment and can be misleading after PID reuse. It is also not part of
the kernel map key, so two processes in different netns with identical values
already share a counter row and a persisted identity.

This matters for containers, sandboxed apps, and VPN netns. Accepted evidence
covers loopback, veth across two netns, physical NICs, and soaks, but none of
those runs exercised two distinct netns with identical process keys.

## Decision

1. Capture the network namespace id **at event time in the kernel** from the
   thread-group leader's `nsproxy->net_ns->ns.inum` (CO-RE read).
2. Add `netns_id` to the BPF `traffic_key`:
   `{start_ns, tgid, uid, ifindex, proto, netns_id}`. Use `0` only as an
   unknown fallback.
3. Make the loader treat `netns_id` as authoritative for the row and stop
   relying on `/proc`-derived netns for identity. It remains a tab-separated
   field after `ifname` for backward compatibility.
4. Include netns in `ProcessIdentity.StableKey`:
   `pid:startUnixMs:uid:netns`. Missing netns is rendered as `-`.
5. Populate the existing `processes.network_namespace_id` column from the
   event-time value.
6. Do **not** automatically rewrite historical `usage_buckets` or `processes`
   rows. Old rows keep the 3-part stable key; new rows use the 4-part key.
   This is a development-stage identity migration and must be documented.
7. Keep the existing interface representation (`ifindex@netns` when the row's
   netns differs from the loader's) as an observation detail, not as the
   identity key.

## Consequences

- Two generations with the same PID/start/UID in different netns produce
  distinct kernel rows, stable keys, and persisted process metadata.
- Short-lived processes retain their netns id even if they exit before
  userspace enrichment, because it is captured in the kernel.
- Existing persisted rows are not migrated; historical and new rows can have
  different key shapes. Queries that group by stable key will treat them as
  different process generations.
- The BPF key grows by 8 bytes; map capacity remains 10,240 entries.
- Legacy 11/13/15/16-field TSV lines remain parseable; the netns field is
  optional in the parser for older loaders.
- This does **not** by itself validate multi-namespace attribution. A
  controlled two-netns collision test is still required before claiming that
  cross-namespace merging is fixed in production behavior. The harness mode
  `--mode netns-key` implements that test: one process moves between two
  temporary netns via `setns()`, and two distinct rows with exact bytes must
  appear. It is not yet privileged-run.
