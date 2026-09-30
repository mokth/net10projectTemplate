# Process-owned BOM

Product Definition → Work Center → Process → optional material lines.
Polish, Wash and QC can have no material lines: they continue working on the output
without consuming the earlier process's materials again. A later process can also
consume additional materials, including an item consumed by another process.

The PROCESS tab links to each process's BOM. The BOM tab selects the consuming
process and shows only its materials, retaining Make/Phantom descendant trees.
Subassemblies retain their own product definitions and process ownership.

`OperationKey` is a stable identity within a product version, preserved through
process edits and version copying. A material's owner must exist in that version.
Component uniqueness is per process. Explosion traverses each material line once,
not once per route process. Existing work-order snapshots remain unchanged.

## Database rollout

Run `scripts/alter-prdefbom-process-ownership.sql` after the existing BOM and routing
schema scripts, before running the updated app. The script adds ownership columns
and replaces the product-wide component uniqueness index with a process-specific
index. It does not guess ownership or rewrite existing material quantities.

Existing material lines remain unassigned. For an active definition, create a new
draft version, add/select the appropriate process and assign each existing material
in the BOM tab. Routed definitions cannot activate with unassigned materials.
Standalone legacy BOMs without routing remain supported by the service during
migration. A routed definition can have no materials at all.

## Process resources

Each published process owns exactly one machine. That process-machine owns exactly
one labour/operator standard. Draft definitions may temporarily omit either while
being entered, but they cannot contain multiple machines or multiple labour rows,
and they cannot activate until both assignments are complete.
