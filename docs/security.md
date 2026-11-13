# Flow security and privacy (initial)

> Status: **Phase 0 baseline**. Captures required posture from `flow-project-plan.md`; implementation details (capabilities, policies, sandboxing) must be finalized with the chosen monitoring provider.

## 1. Trust boundaries and privilege model

- **Flow.Desktop:** runs as the logged-in user, **no root privileges**.
- **Flow.Daemon:** runs as a system service with the **least privilege necessary** for the selected monitoring provider (e.g. eBPF load/attach and required observability). Do not run the entire application stack as unrestricted root.
- **Flow.Native (if used):** minimal C/C++ ABI shim only; no business logic, strict input validation at boundaries.

Investigate and apply **systemd sandboxing** directives and **Linux capabilities** appropriate to the provider. Use **Polkit** only for operations that genuinely require authorization.

## 2. IPC (D-Bus) security

- Define a versioned, explicit contract; reject unknown/invalid requests.
- **Validate callers and inputs** for all methods; do not trust the GUI.
- Restrict privileged operations to specific authorized actions; an unprivileged GUI must not be able to trigger arbitrary privileged behavior.
- Do not expose arbitrary shell execution or file-system escape hatches through the daemon.
- Return explicit errors/health states; support safe reconnect behavior.

## 3. Data handling and privacy

- **Local-only by default:** no account, cloud service, telemetry, or external transfer of usage data required.
- **Not collected/stored:** packet payloads, browsing history, DNS query history/content inspection features.
- Persist only what is needed for attribution/history (process/app/interface identities, timestamps, byte/rate aggregates, health/diagnostics metadata) with appropriate filesystem permissions on the SQLite database.
- Logs must avoid leaking unnecessary sensitive data (e.g. full command lines with secrets/tokens where avoidable); document diagnostic data handling.

## 4. Filesystem and runtime protections

- Protect database and runtime state directories with least-privilege ownership/permissions.
- Ensure upgrades preserve user data intentionally; uninstallation behavior for retained history must be explicit (never silent deletion).
- Native eBPF objects/artifacts (if any) should be installed from trusted package contents with controlled permissions.

## 5. eBPF-specific considerations

- On this system, `kernel.unprivileged_bpf_disabled=2`; provider operations are expected to require elevated privileges/capabilities.
- Document exact capabilities/program types/attach points required once the hook is chosen.
- Prefer verifier-safe, minimal BPF programs; validate map sizes and failure modes to avoid resource exhaustion.
- Treat provider failures as degraded/unavailable health states with clear diagnostics, not silent fallback claims.

## 6. Supply chain and dependency hygiene

- Verify maintained libraries for Linux/Wayland/.NET 10 compatibility before adoption (Avalonia, D-Bus, SQLite, any eBPF bindings).
- Pin versions via central package management when solution is scaffolded; track security updates.
- Keep native dependencies minimal and auditable.

## 7. Security validation (planned)

- Review D-Bus policy and Polkit rules for least privilege.
- Test authorization boundaries (allowed vs denied operations).
- Verify database/log permissions and absence of sensitive leakage.
- Include security-relevant cases in integration/system tests once components exist.
