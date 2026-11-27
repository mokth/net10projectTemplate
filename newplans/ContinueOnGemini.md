Continue implementing the existing costing plan.

Repository/worktree:
C:\wincom\net10projects\.replay-productionv2-20261104

Target branch: productionv2

Read these first:
1. newplans\costing_plan.md
2. docs\replay-benchmark-baseline.md
3. The original plan:
   C:\wincom\net10projects\plans\productionv2_open_period_backdated_stock_cost_replay_AI_AGENT_PLAN_v5.md
4. Applicable repository instructions and productionv2 costing skills.

Before changing anything:
- Check the current branch, HEAD, and git status. The branch may have
  changed since the last review.
- Preserve all existing uncommitted changes. The costing plan and benchmark
  report may contain uncommitted review updates.
- Compare the plan with the current code and tests. Do not reimplement
  work that is already completed.

Priority:
Focus on the important remaining backdated posting and rollback cases first.
Choose an actual unsupported or incomplete case from the plan, trace its
public posting and rollback paths, implement it, and verify it before
moving to the next case. Include QA effects where applicable.

Safety requirements:
- Keep unsupported cases safely blocked until their implementation and
  tests are complete.
- Preserve stock quantity, costing, FIFO/WAC/Standard rules, WIP, purchase
  settlement, Sales COGS, QA holds, audit history, and rollback/repost.
- Use the existing coordinator and costing services.
- Do not run migrations or modify the operational ERPWeb database.
- Use a disposable database for SQL Server tests. If SQL Server is not
  available, state which checks could not be run; continue independent work.
- Do not enable replay or Subcontract production flags.
- Do not add unrelated features or expand the plan unnecessarily.

Important review findings:
- Several SQL test failures are historical and have no matching passing
  results in the inspected artifacts. Do not assume they still fail; verify
  them when a disposable SQL Server database is available.
- The current benchmark measures a later lock re-acquisition, so its
  reported lock-wait headroom is not certified. Keep existing limits until
  proper measurements support changing them.
- Future stock/cost transactions need transaction-specific regression
  tests; the existing focused gate does not add them automatically.

For each completed case:
- Run the relevant tests, including posting, rollback, repost, costing
  reconciliation, and applicable failure and concurrency checks.
- Update costing_plan.md with what was completed and what remains.
- Report the exact tests passed, failed, or not run.
- Commit only the changes belonging to this work on productionv2.
  Do not include unrelated existing changes.

Continue through the important remaining work instead of stopping after
one small task, unless a genuine blocker requires my input. Do not estimate
a completion percentage without clear evidence.