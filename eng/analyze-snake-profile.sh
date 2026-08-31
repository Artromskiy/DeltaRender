#!/bin/sh
set -eu

if [ "$#" -lt 1 ] || [ "$#" -gt 3 ]; then
    printf '%s\n' "usage: $0 LOG [TSV_OUTPUT] [SUMMARY_OUTPUT]" >&2
    exit 2
fi

input=$1
tsv_output=${2:-/tmp/delta-snake-profile-last100.tsv}
summary_output=${3:-/tmp/delta-snake-profile-last100.txt}
sample_count=${SNAKE_PROFILE_COUNT:-100}

python3 - "$input" "$tsv_output" "$summary_output" "$sample_count" <<'PY'
import collections
import pathlib
import re
import statistics
import sys

input_path = pathlib.Path(sys.argv[1])
tsv_path = pathlib.Path(sys.argv[2])
summary_path = pathlib.Path(sys.argv[3])
sample_count = int(sys.argv[4])
unit_pattern = r"ps|ns|us|ms|\u00b5s|s|min|h|d"

profile_pattern = re.compile(
    r"Render profile: frame=(?P<frame>\d+), status=(?P<status>[^,]+), "
    rf"build=(?P<build>[0-9.]+)(?P<build_unit>{unit_pattern}), "
    rf"acquire=(?P<acquire>[0-9.]+)(?P<acquire_unit>{unit_pattern}), "
    rf"record=(?P<record>[0-9.]+)(?P<record_unit>{unit_pattern}), "
    rf"submit-present=(?P<submit>[0-9.]+)(?P<submit_unit>{unit_pattern}), "
    rf"fence-wait=(?P<fence>[0-9.]+)(?P<fence_unit>{unit_pattern}), "
    rf"layout-shaping=(?P<layout>[0-9.]+)(?P<layout_unit>{unit_pattern}), "
    r"passes=(?P<passes>\d+), resources=(?P<resources>\d+), "
    r"draws=(?P<draws>\d+), descriptor-binds=(?P<descriptor_binds>\d+), "
    r"vertex-binds=(?P<vertex_binds>\d+), index-binds=(?P<index_binds>\d+), "
    r"upload-bytes=(?P<upload_bytes>\d+), gpu-timestamps=(?P<gpu_timestamps>\w+)"
)
pass_pattern = re.compile(
    r"^\s+pass=(?P<name>.*?), kind=(?P<kind>.*?), "
    rf"cpu-record=(?P<cpu>[0-9.]+)(?P<cpu_unit>{unit_pattern}), "
    rf"gpu=(?P<gpu>unavailable|[0-9.]+(?:{unit_pattern}))$"
)
duration_pattern = re.compile(rf"(?P<value>[0-9.]+)(?P<unit>{unit_pattern})")
factor = {
    "ps": 0.001,
    "ns": 1.0,
    "us": 1_000.0,
    "ms": 1_000_000.0,
    "\u00b5s": 1_000.0,
    "s": 1_000_000_000.0,
    "min": 60_000_000_000.0,
    "h": 3_600_000_000_000.0,
    "d": 86_400_000_000_000.0,
}


def duration(value: str, unit: str) -> float:
    return float(value) * factor[unit]


def parse_duration(value: str) -> float:
    match = duration_pattern.fullmatch(value)
    if match is None:
        raise ValueError(f"invalid duration: {value}")
    return duration(match.group("value"), match.group("unit"))


frames = []
current = None
with input_path.open(encoding="utf-8") as source:
    for line in source:
        profile = profile_pattern.search(line)
        if profile is not None:
            values = profile.groupdict()
            current = {
                "frame": int(values["frame"]),
                "status": values["status"].strip(),
                "build": duration(values["build"], values["build_unit"]),
                "acquire": duration(values["acquire"], values["acquire_unit"]),
                "record": duration(values["record"], values["record_unit"]),
                "submit": duration(values["submit"], values["submit_unit"]),
                "fence": duration(values["fence"], values["fence_unit"]),
                "layout": duration(values["layout"], values["layout_unit"]),
                "passes_count": int(values["passes"]),
                "resources": int(values["resources"]),
                "draws": int(values["draws"]),
                "descriptor_binds": int(values["descriptor_binds"]),
                "vertex_binds": int(values["vertex_binds"]),
                "index_binds": int(values["index_binds"]),
                "upload_bytes": int(values["upload_bytes"]),
                "gpu_timestamps": values["gpu_timestamps"] == "True",
                "passes": [],
            }
            frames.append(current)
            continue

        current_pass = pass_pattern.match(line)
        if current is None or current_pass is None:
            continue

        values = current_pass.groupdict()
        current["passes"].append(
            {
                "name": values["name"],
                "kind": values["kind"],
                "cpu": duration(values["cpu"], values["cpu_unit"]),
                "gpu": None if values["gpu"] == "unavailable" else parse_duration(values["gpu"]),
            }
        )

if sample_count <= 0:
    raise SystemExit("SNAKE_PROFILE_COUNT must be positive")
if not frames:
    raise SystemExit(f"no Render profile records found in {input_path}")

selected = frames[-sample_count:]
tsv_path.parent.mkdir(parents=True, exist_ok=True)
summary_path.parent.mkdir(parents=True, exist_ok=True)

with tsv_path.open("w", encoding="utf-8") as output:
    for frame in selected:
        output.write(
            "F\t{frame}\t{build:.3f}\t{acquire:.3f}\t{record:.3f}\t"
            "{submit:.3f}\t{fence:.3f}\t{layout:.3f}\t{passes_count}\t"
            "{resources}\t{draws}\t{descriptor_binds}\t{vertex_binds}\t"
            "{index_binds}\t{upload_bytes}\n".format(**frame)
        )
        for render_pass in frame["passes"]:
            gpu = "" if render_pass["gpu"] is None else f"{render_pass['gpu']:.3f}"
            output.write(
                f"P\t{frame['frame']}\t{render_pass['name']}\t{render_pass['kind']}\t"
                f"{render_pass['cpu']:.3f}\t{gpu}\n"
            )


def stats(values):
    return (
        statistics.median(values),
        min(values),
        max(values),
        statistics.fmean(values),
    )


def format_stats(values, suffix="ns"):
    median, minimum, maximum, mean = stats(values)
    return f"median={median:.3f} {suffix} min={minimum:.3f} {suffix} max={maximum:.3f} {suffix} mean={mean:.3f} {suffix}"


groups = collections.defaultdict(lambda: {"cpu": [], "gpu": []})
for frame in selected:
    for render_pass in frame["passes"]:
        group = groups[(render_pass["name"], render_pass["kind"])]
        group["cpu"].append(render_pass["cpu"])
        if render_pass["gpu"] is not None:
            group["gpu"].append(render_pass["gpu"])

with summary_path.open("w", encoding="utf-8") as output:
    output.write("Snake profile last 100 completed frames\n")
    output.write(f"frame-range={selected[0]['frame']}..{selected[-1]['frame']}, samples={len(selected)}\n")
    for key in ("build", "acquire", "record", "submit", "fence", "layout"):
        output.write(f"{key} {format_stats([frame[key] for frame in selected])}\n")
    for key, label in (("passes_count", "passes"), ("resources", "resources"), ("draws", "draws"), ("descriptor_binds", "descriptor-binds"), ("vertex_binds", "vertex-binds"), ("index_binds", "index-binds"), ("upload_bytes", "upload-bytes")):
        values = [frame[key] for frame in selected]
        output.write(f"{label} median={statistics.median(values):.3f} min={min(values)} max={max(values)}\n")
    output.write("passes:\n")
    for (name, kind), values in sorted(groups.items()):
        output.write(f"  {name} [{kind}] cpu {format_stats(values['cpu'])}")
        if values["gpu"]:
            output.write(f" gpu {format_stats(values['gpu'])}")
        else:
            output.write(" gpu=unavailable")
        output.write("\n")
PY
