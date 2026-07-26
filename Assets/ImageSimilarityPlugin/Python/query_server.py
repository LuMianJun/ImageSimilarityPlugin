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
    load_features_cache, SUPPORTED_EXTS,
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
    results_list, total_images, elapsed, error_paths = query_similar(
        query_image_path=cmd["query"],
        folder_path=cmd["folder"],
        threshold=cmd.get("threshold", 0.80),
        top_k=cmd.get("top_k", 50),
        workers=cmd.get("workers", 4),
        recursive=cmd.get("recursive", True),
        progress_callback=progress_callback,
        cache_dir=cmd.get("cache_dir"),
    )
    write_response({
        "type": "result",
        "total_images": total_images,
        "query_image": os.path.abspath(cmd["query"]),
        "threshold": cmd.get("threshold", 0.80),
        "results": [
            {"image_path": r["path"], "similarity": r["similarity"], "rank": r["rank"]}
            for r in results_list
        ],
        "elapsed_seconds": round(elapsed, 2),
    })


def handle_scan(cmd):
    groups, total_images, elapsed, error_paths = find_duplicates(
        folder_path=cmd["folder"],
        threshold=cmd.get("threshold", 0.95),
        workers=cmd.get("workers", 4),
        recursive=cmd.get("recursive", False),
        progress_callback=progress_callback,
        cache_dir=cmd.get("cache_dir"),
    )
    write_response({
        "type": "result",
        "total_images": total_images,
        "total_groups": len(groups),
        "groups": [{"id": i + 1, "images": g} for i, g in enumerate(groups)],
        "elapsed_seconds": round(elapsed, 2),
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
            else:
                write_response({"type": "error", "message": f"Unknown action: {action}"})
        except Exception as e:
            sys.stderr.write(f"[server] Error: {e}\n{traceback.format_exc()}\n")
            sys.stderr.flush()
            write_response({"type": "error", "message": str(e)})


if __name__ == "__main__":
    main()
