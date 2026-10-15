"""Run a judge tool from tools/bin and fail loudly when it breaks."""
import subprocess

def run_tool(tool, lines, name, allowed=None):
    p = subprocess.run([tool], input="\n".join(lines) + "\n", text=True, capture_output=True)
    if p.returncode != 0:
        raise SystemExit(f"{tool} exited {p.returncode}: {p.stderr.strip()}")
    out = p.stdout.rstrip("\n").split("\n") if p.stdout.strip() else []
    if not out or out[0] != f"#tool {name}":
        raise SystemExit(f"{tool} is not the {name} tool (first line {out[0] if out else ''!r})")
    out = out[1:]
    if len(out) != len(lines):
        raise SystemExit(f"{tool} printed {len(out)} lines for {len(lines)} inputs")
    if allowed is not None:
        bad = [o for o in out if not allowed(o)]
        if bad:
            raise SystemExit(f"{tool} printed unexpected output {bad[0]!r}; is this the right tool?")
    return out
