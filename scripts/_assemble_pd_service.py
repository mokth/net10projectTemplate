from pathlib import Path

base = Path(r"c:/wincom/net10projects/ErpWeb.Core/Planning")
part1 = (base / "_service_head_part1.cs").read_text(encoding="utf-8")
part2 = (base / "_service_head_part2.cs").read_text(encoding="utf-8")
part3 = (base / "_service_head_part3.cs").read_text(encoding="utf-8")
helpers = (base / "_helpers_tail_patched.cs").read_text(encoding="utf-8")

assert "PLACEHOLDER_REST" in part1
assert "PLACEHOLDER_HELPERS" in part3

# part1 ends with PLACEHOLDER_REST - replace with part2+part3 (without PLACEHOLDER_HELPERS) + helpers
combined = part1.replace("PLACEHOLDER_REST\n", part2 + "\n" + part3.replace("PLACEHOLDER_HELPERS\n", helpers))

out = base / "PrProductDefService.cs"
out.write_text(combined, encoding="utf-8")
print("wrote", out, "lines", combined.count("\n") + 1)

# cleanup temp files
for name in [
    "_service_head_part1.cs",
    "_service_head_part2.cs",
    "_service_head_part3.cs",
    "_helpers_tail.cs",
    "_helpers_tail_patched.cs",
]:
    (base / name).unlink(missing_ok=True)
print("cleaned temp")
