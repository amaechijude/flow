# Agent instructions for Flow

- **Start here:** read `HANDOFF.md`, `docs/progress.md`, and `flow-project-plan.md` before making changes. Summarize current state and confirm the next task.
- **Work incrementally:** make small, verifiable changes; run relevant builds/tests (`dotnet build Flow.slnx`, `dotnet test Flow.slnx`, prototype checks as applicable) and report results.
- **Respect project constraints:** per-process attribution is the core requirement—do not substitute interface totals; keep measurement semantics, unattributed traffic, and limitations explicit and honest. Do not run the desktop GUI as root.
- **Track progress:** update `docs/progress.md` (and decisions in `docs/decisions/` when appropriate) after meaningful changes. Keep behavior aligned with the plan unless evidence requires a documented revision.
