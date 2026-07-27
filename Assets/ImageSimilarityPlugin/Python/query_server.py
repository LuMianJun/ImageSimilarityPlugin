"""
Persistent query server for image similarity.
Launched once by Unity, reads JSON commands from stdin, writes JSON results to stdout.

Startup: loads MobileNetV2, then writes {"type":"ready"} to stdout.
Commands: one JSON object per line on stdin (see below).
Responses: one JSON object per line on stdout (type: progress / result / error / ready).
"""

import json
import os
import sys
import traceback

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from feature_extractor import (
    get_model, query_similar, find_duplicates,
    collect_image_paths, load_features_cache,
)


def write_response(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def progress_callback(pct):
    write_response({"type": "progress", "value": pct})


# ===================================================================
#  Action handlers
# ===================================================================

def handle_query(cmd):
    results_list, total_images, elapsed, error_paths, cache_info = query_similar(
        query_image_path=cmd["query"],
        folder_path=cmd["folder"],
        threshold=cmd.get("threshold", 0.80),
        top_k=cmd.get("top_k", 50),
        workers=cmd.get("workers", 4),
        recursive=cmd.get("recursive", True),
        progress_callback=progress_callback,
        cache_dir=cmd.get("cache_dir"),
        excluded_directories=cmd.get("exclude_dirs", []),
    )
    result = {
        "type": "result",
        "total_images": total_images,
        "query_image": os.path.abspath(cmd["query"]),
        "threshold": cmd.get("threshold", 0.80),
        "results": [
            {"image_path": r["path"], "similarity": r["similarity"], "rank": r["rank"]}
            for r in results_list
        ],
        "failed_images": error_paths,
        "elapsed_seconds": round(elapsed, 2),
    }
    if cache_info is not None:
        # Strip internal keys before sending to C#
        result["cache_info"] = {k: v for k, v in cache_info.items() if not k.startswith("_")}
    write_response(result)


def handle_scan(cmd):
    groups, total_images, elapsed, error_paths, cache_info = find_duplicates(
        folder_path=cmd["folder"],
        threshold=cmd.get("threshold", 0.95),
        workers=cmd.get("workers", 4),
        recursive=cmd.get("recursive", False),
        progress_callback=progress_callback,
        cache_dir=cmd.get("cache_dir"),
        excluded_directories=cmd.get("exclude_dirs", []),
    )
    result = {
        "type": "result",
        "total_images": total_images,
        "total_groups": len(groups),
        "groups": [{"id": i + 1, "images": g} for i, g in enumerate(groups)],
        "failed_images": error_paths,
        "elapsed_seconds": round(elapsed, 2),
    }
    if cache_info is not None:
        result["cache_info"] = {k: v for k, v in cache_info.items() if not k.startswith("_")}
    write_response(result)


def handle_check_cache(cmd):
    """Lightweight check — only reads manifest + compares mtime, no TF inference."""
    folder = cmd["folder"]
    cache_dir = cmd.get("cache_dir")
    recursive = cmd.get("recursive", True)
    excluded_directories = cmd.get("exclude_dirs", [])

    current_paths = collect_image_paths(folder, recursive, excluded_directories)
    total_current = len(current_paths)

    # Load cache (single call; returns paths, features, cache_info)
    cached_paths, _, cache_info = load_features_cache(
        cache_dir, folder, recursive, excluded_directories)

    if cache_info is None or cached_paths is None:
        write_response({"type": "result", "cache_info": None,
                        "total_current": total_current})
        return

    # Count new images (in folder but not in cache)
    cached_abs = {os.path.abspath(p) for p in cached_paths}
    new_count = sum(1 for p in current_paths if os.path.abspath(p) not in cached_abs)

    cache_info["new_since_cache"] = new_count
    cache_info["total_current"] = total_current

    write_response({
        "type": "result",
        "cache_info": {k: v for k, v in cache_info.items() if not k.startswith("_")},
        "total_current": total_current,
    })


# ===================================================================
#  Main
# ===================================================================

def main():
    sys.stderr.write("[server] Loading model...\n")
    sys.stderr.flush()
    get_model()

    # Handshake: C# waits for this before sending commands
    write_response({"type": "ready"})
    sys.stderr.write("[server] Ready.\n")
    sys.stderr.flush()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            cmd = json.loads(line)
        except json.JSONDecodeError:
            sys.stderr.write(f"[server] Bad JSON: {line[:200]}\n")
            sys.stderr.flush()
            continue

        action = cmd.get("action", "")
        sys.stderr.write(f"[server] action={action}\n")
        sys.stderr.flush()

        if action == "exit":
            break

        try:
            if action == "query":
                handle_query(cmd)
            elif action == "scan":
                handle_scan(cmd)
            elif action == "check_cache":
                handle_check_cache(cmd)
            else:
                write_response({"type": "error", "message": f"Unknown action: {action}"})
        except Exception as e:
            sys.stderr.write(f"[server] Error: {e}\n{traceback.format_exc()}\n")
            sys.stderr.flush()
            write_response({"type": "error", "message": str(e)})


if __name__ == "__main__":
    main()
