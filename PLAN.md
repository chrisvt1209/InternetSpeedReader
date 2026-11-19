# Network Throughput Monitor — Plan & Design

Status: **plan only, nothing built yet.**

## 1. Goal

Passively measure how many **bytes per second** flow through the network adapter of this PC (download/RX and upload/TX) while the user actively uses the internet (downloads, game updates, backups, ...). Store the measurements in a database and show them in a web application as an overview table (plus charts).

- This is **not** a speed test. The agent never generates traffic.
- Subscription: 1000 Mbps (= 125 MB/s theoretical). Real-world observed: ~890–950 Mbps (~111–119 MB/s).

Two applications:

1. **Agent** — runs on the PC, samples throughput, writes to the database.
2. **Web app** — reads the database and shows sessions, charts and a live view.

## 2. Language decision: C# (.NET)

| | C# | Rust |
|---|---|---|
| Counter sampling | `NetworkInterface` built in | `sysinfo` / `windows` crate, also easy |
| Per-process (ETW) | `TraceEvent` library, mature | `ferrisetw`, far less established |
| Windows service | Worker Service, first-class | Possible, more manual |
| Web table + charts | Razor Pages / Blazor, quick | Axum / Leptos, slower to build |
| Shared DB model | One EF Core `Core` library for both apps | Separate schema management |
| Resource use | ~30–60 MB RAM idle | ~2–5 MB (irrelevant at 1 Hz) |
| Productivity | Strongest skill of the user | Learning curve |

**Decision: C# on .NET 11 (SDK 11.0.100, EF Core 11) for both apps.** Rust is a legitimate option for the agent later (small, long-running, resource-sensitive); the database schema is the contract between the apps, so a Rust agent rewrite stays possible (optional phase 7).

## 3. How to measure

| Method | Gives | Needs | Use |
|---|---|---|---|
| NIC byte counters (`GetIPStatistics()` / `GetIfEntry2`) | Total RX/TX bytes per adapter; sample at 1 s, delta / elapsed | No admin, ~0 CPU | **MVP** |
| ETW `Microsoft-Windows-Kernel-Network` | Bytes per process / connection | Admin or Performance Log Users | Phase 2, optional |
| Packet capture (Npcap/SharpPcap) | Every packet | Driver, admin, heavy | Not planned |

Accuracy notes:
- Compute rate from **real elapsed time** (`Stopwatch`), not an assumed 1.000 s.
- NIC counters include Ethernet/IP/TCP headers: ~3–5% above goodput.
- Show **bytes/s and Mbps** plus **% of link capacity** (configurable reference, default 1000 Mbps).
- Select the correct adapter; exclude loopback, Hyper-V/WSL virtual switches and VPN adapters to avoid double counting.
- A counter that goes backwards (adapter reset) → skip that sample.
- Keep a faster in-memory pass (~250 ms) so short peaks are not averaged away; store only the 1 s values.

## 4. Architecture

```
┌─────────────────────────────┐          ┌───────────┐          ┌───────────────────────┐
│ ThroughputAgent (Worker)    │  batch   │ Database  │  query   │ ThroughputWeb         │
│  ├ AdapterSelector          │ ───────► │ (SQLite → │ ◄─────── │  ├ Sessions table     │
│  ├ CounterSampler (1 Hz)    │  writes  │ Postgres) │          │  ├ Throughput chart   │
│  ├ SessionDetector          │          └───────────┘          │  ├ Live view (polling)│
│  ├ RollupService            │                                 │  └ Per-process (ph.2) │
│  └ (ETW process sampler)    │                                 └───────────────────────┘
└─────────────────────────────┘
```

Solution layout:

```
InternetSpeedReader/
├─ src/
│  ├─ ThroughputMonitor.Core/    # entities, DbContext, migrations, DTOs (shared)
│  ├─ ThroughputMonitor.Agent/   # Worker Service
│  └─ ThroughputMonitor.Web/     # ASP.NET Core Razor Pages (read-only)
├─ tests/
│  ├─ ThroughputMonitor.Core.Tests/
│  └─ ThroughputMonitor.Agent.Tests/
├─ PLAN.md
└─ ThroughputMonitor.sln
```

## 5. Agent design

1. **CounterSampler** — `PeriodicTimer` at 1 s; reads RX/TX counters of the selected adapter(s); `bytesPerSec = Δbytes / Δseconds`.
2. **SessionDetector** — captures "when I actively use internet":
   - Start: throughput > threshold (default 5 Mbps) for 3 s.
   - End: throughput < threshold for 10 s.
   - Records start/end, duration, peak + average RX/TX, total bytes.
   - **Manual labels:** CLI/tray command (`speedlog mark "Steam download"`) names the current session.
3. **Write strategy** — buffer in memory, insert in batches every 5–10 s. Raw 1 s samples are stored **only during sessions**, plus a coarse idle baseline (1 row/minute). Avoids 86,400 rows/day.
4. **RollupService + retention** — per-minute and per-hour min/avg/max/p95. Delete raw samples after 14–30 days; keep sessions and rollups indefinitely.
5. **ETW process sampler** — per-process RX/TX per second via `Microsoft-Windows-Kernel-Network` (`TraceEvent` library). Needs elevation, so the agent runs as an elevated Windows Service / scheduled task. To limit volume, only processes above a small threshold (default 10 KB/s) get a row per second; at session end, per-process totals are written to `SessionProcessTotals`. If the agent is started unelevated it logs a warning and continues with total-adapter sampling only.
6. **Cross-cutting** — `appsettings.json` (adapter, threshold, intervals, connection string, reference link speed), Serilog rolling file, `UseWindowsService()`, graceful shutdown that closes the open session.

## 6. Data model

Raw values are stored as **bytes per second**; convert to Mbps in the UI. All times UTC.

```
Adapters       Id, Name, Description, LinkSpeedBps, IsMonitored
Samples        Id, SessionId?, AdapterId, TimestampUtc, IntervalMs, RxBytesPerSec, TxBytesPerSec,
               LinkSpeedBps,                 -- negotiated link speed at that moment (e.g. 1 Gbps vs 100 Mbps)
               CpuPercent, DiskBytesPerSec   -- system load at that moment (bottleneck check)
Sessions       Id, AdapterId, StartUtc, EndUtc?, Label?, Category?, Note?, PeakRxBps, PeakTxBps,
               AvgRxBps, AvgTxBps, TotalRxBytes, TotalTxBytes, Source ("auto"|"manual"),
               MinLinkSpeedBps, AvgCpuPercent, PeakCpuPercent, AvgDiskBytesPerSec, PeakDiskBytesPerSec
Rollups        Id, AdapterId, BucketStartUtc, BucketSeconds (60|3600),
               RxMin, RxAvg, RxMax, RxP95, TxMin, TxAvg, TxMax, TxP95, RxBytes, TxBytes
ProcessSamples Id, SessionId?, TimestampUtc, ProcessName, ExePath?, Pid, RxBytesPerSec, TxBytesPerSec
SessionProcessTotals  Id, SessionId, ProcessName, ExePath?, RxBytes, TxBytes, PeakRxBps, PeakTxBps
               -- per-session "which program used how much" summary, kept permanently
SessionRemoteTotals   Id, SessionId, ProcessName, RemoteIp, RemotePort?, RemoteHostName?, RxBytes, TxBytes
               -- which remote server each program talked to; ETW events carry the remote address,
               -- reverse DNS (RemoteHostName) is resolved lazily and may stay null
```

Notes on the extra columns:
- **Category / Note** (sessions): free text plus a short category ("game update", "backup", ...). Set via the `mark` command (`speedlog mark "Steam download" --category "game update" --note "..."`) and editable in the web app later.
- **Remote server / IP:** from the same ETW events as per-process traffic, no extra capture needed. Totals per session only (not per second) to keep volume low.
- **CPU / disk:** read from Windows performance counters (`Processor Information\% Processor Time`, `PhysicalDisk(_Total)\Disk Bytes/sec`) in the same 1 s tick, so a slow download can be correlated with a busy CPU or disk.
- **Link speed:** `NetworkInterface.Speed` at each tick; shows if the link negotiated down (e.g. 100 Mbps cable fault).

Indexes: `Samples(TimestampUtc)`, `Samples(SessionId)`, `Sessions(StartUtc)`, `Rollups(AdapterId, BucketSeconds, BucketStartUtc)`.

Volume: a 2 h download ≈ 7,200 rows; well under 1M rows/year → fine for SQLite (WAL mode).

## 7. Database

- **Start with SQLite** (WAL mode): zero setup, agent writes while the web app reads.
- **Switch to PostgreSQL** if the web app runs on another machine or multiple agents are wanted (EF Core provider swap + new migration).
- Alternative for remote web hosting: REST ingest endpoint on the web app (needs auth + offline queue). Skipped unless needed.

## 8. Web app design

- **Overview table (main requirement):** one row per session — start, duration, label, avg/peak RX/TX (MB/s and Mbps), % of 1000 Mbps, total GB. Sorting, paging, date filter, label search, "peak > 900 Mbps" quick filter.
- **Session detail:** RX/TX line chart with a reference line at link capacity; per-process breakdown in phase 2.
- **Live view:** polls `/api/latest` every 1–2 s, shows the last 60 s.
- **Summary cards:** peak ever, average during active sessions, GB today / this month.
- Read-only DB access; bound to localhost/LAN only; no write endpoints.

## 9. Testing

- Unit tests: rate calculation (including counter reset and uneven intervals), session detector state machine, rollup/p95 math.
- Counter source behind an interface (`ICounterSource`) so tests feed fake byte counts.
- One integration test with SQLite in-memory.
- Manual validation: compare the agent's numbers to Task Manager / Resource Monitor during a real large download.

## 10. Phases

1. **Foundation** — solution, `Core` entities, SQLite migration.
2. **Sampler spike** — throwaway console app printing RX/TX MB/s; verify against a real download and settle adapter selection. *(Start here.)*
3. **Agent MVP** — adapter sampling, batch writes, session detection, config.
4. **Per-process sampling** — ETW spike (verify per-process numbers against Resource Monitor), then `ProcessSamples` + `SessionProcessTotals`.
5. **Service hardening** — elevated Windows Service install, logging, retention + rollups, `mark` command.
6. **Web MVP** — sessions table (with top processes per session), filters, session chart.
7. **Live view + summary cards.**
8. **Optional** — PostgreSQL switch, alerting, Rust agent rewrite.

## 11. Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Per-process breakdown? | **Yes, both.** Total adapter traffic *and* per-process (which program uses the bandwidth). ETW module is now part of the core scope; the agent therefore runs **elevated** (admin / Performance Log Users). |
| 2 | Adapters | **Wired Ethernet only** for now. |
| 3 | Web app location / database | **Same PC, SQLite** (WAL mode). PostgreSQL stays a later option. |
| 4 | Granularity | **1 s stored**, ~250 ms in memory to catch the true peak per second. |
| 5 | Retention | **~30 days** of raw 1 s data, permanent session summaries. |

### Extra data to store (decided)

1. Note / category per session
2. Remote server / IP per program
3. CPU and disk usage at the same moment
4. Active network link speed at the time

Schema for these is in section 6. Further columns can be added later through an EF Core migration.
