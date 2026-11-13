# Flow database design (initial)

> Status: **Phase 0 baseline**. Schema below reflects the plan’s proposed model; concrete migrations, indexes, and rollup jobs must be finalized alongside validated measurement semantics and query patterns.

## 1. Storage choice

- **Engine:** SQLite (local, embedded).
- **Timestamps:** stored in **UTC**; display in OS local time zone.
- **Counters:** 64-bit integers for byte totals.
- **Integrity:** transactional writes, idempotent aggregation/rollups where possible, explicit migrations.
- **Semantics:** distinguish **unknown/unattributed** traffic from **zero** usage.

## 2. Proposed entities / tables

### Applications
- Stable application key (PK)
- Display name
- Desktop file ID (nullable)
- Executable path (nullable)
- Icon metadata/resolvable reference (nullable)
- First seen / last seen (UTC)

### Processes
- Stable process identity (PK; composite inputs: PID + process start/generation id + other disambiguators as needed)
- PID
- Process start time
- Parent process identity (nullable)
- Executable path / command metadata (as appropriate; privacy-aware)
- UID
- Associated application key (nullable, only when confidently identified)
- First seen / last seen (UTC)

### NetworkUsageBuckets (attributed/unattributed usage)
- Application key (nullable for unattributed)
- Process identity (nullable where not available/applicable)
- Interface identifier
- Bucket start (UTC) and duration
- Uploaded bytes, downloaded bytes (64-bit)
- Measurement type (documents observation point/semantics version)
- Attribution confidence/quality metadata

### NetworkInterfaces
- Stable interface identifier where possible (PK/logical key)
- Interface name
- Interface type
- First seen / last seen (UTC)
- Relevant metadata (addresses/state snapshots as needed, carefully scoped)

### InterfaceUsageBuckets
- Interface identifier
- Bucket start (UTC) and duration
- Uploaded bytes, downloaded bytes (64-bit)
- Measurement type

### MonitoringSessions
- Session start/end
- Provider and measurement version
- Health state
- Collection gaps / diagnostic metadata

### UserSettings
- Monitoring preferences
- Retention configuration
- Display preferences
- Notification preferences

## 3. Aggregation tiers and retention (proposed defaults)

| Granularity | Retention (default) |
|---|---|
| Raw events | Transient; process and aggregate promptly |
| 1-minute buckets | 48 hours |
| 5-minute buckets | 7 days |
| Hourly buckets | 90 days |
| Daily buckets | 2 years |
| Monthly buckets | Long-term |

- Retention must be configurable where practical.
- Cleanup/rollup runs asynchronously and must not block collection or make UI unresponsive.
- **Totals must remain consistent** when fine-grained buckets roll into coarser buckets (validate with tests).

## 4. Query patterns to optimize (expected)

- Current/rolling windows: last hour, 6h, 12h, 24h, 7d, 30d, 90d, etc.
- Calendar windows: today/yesterday, this/last week/month/year (system local TZ boundaries, stored/queried via UTC `[start, end)` intervals).
- Top-N applications/processes by total/upload/download for selected interface/range.
- Time-series for charts at range-appropriate resolution (select best available aggregation tier; avoid scanning huge raw sets).
- Interface breakdowns and comparison periods (including zero previous-period handling).

Indexes should be added based on measured query plans once schema stabilizes (likely on bucket start + interface/app/process dimensions).

## 5. Migrations and lifecycle

- Versioned migrations applied by daemon/startup path as appropriate; failures must surface as explicit health/diagnostic states.
- Upgrades preserve history/settings; uninstall data-retention behavior must be intentional and user-visible.
- Exports (CSV/required, JSON optional) must include range, time zone, interface filter, measurement method, attribution caveats, and generation time.

## 6. Open items

- Final key strategy for stable process identity and interface identity across renames/reboots.
- Exact attribution confidence schema and how it rolls up across buckets.
- Whether to separate raw ingestion staging tables from durable buckets.
- Practical DB size limits/compaction and vacuum strategy under sustained load.
