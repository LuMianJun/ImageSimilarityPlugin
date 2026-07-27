"""
Headless CLI for image similarity detection.
Called by Unity Editor (or standalone) via subprocess.

Usage:
  python duplicate_detector_cli.py --folder "D:/path/to/images" --threshold 0.95 --output "result.json"

Stdout protocol (lines Unity parses):
  PROGRESS:<int>           # 0-100, real-time progress

After completion, writes JSON to --output path:
  {
    "total_images": 123,
    "total_groups": 5,
    "groups": [{"id": 1, "images": ["path/a.png", "path/b.png"]}, ...],
    "elapsed_seconds": 12.5
  }
"""

import argparse
import json
import os
import sys

# Ensure the script's own directory is on sys.path so feature_extractor can be imported
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from feature_extractor import find_duplicates


def progress_printer(pct):
    """Print progress line that Unity parses."""
    sys.stdout.write(f"PROGRESS:{pct}\n")
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser(
        description="Detect similar/duplicate images using MobileNetV2 + cosine similarity."
    )
    parser.add_argument(
        "--folder", required=True,
        help="Path to the folder containing images to scan."
    )
    parser.add_argument(
        "--threshold", type=float, default=0.95,
        help="Cosine similarity threshold (0-1). Higher = stricter. Default: 0.95"
    )
    parser.add_argument(
        "--output", required=True,
        help="Path to write the JSON results file."
    )
    parser.add_argument(
        "--recursive", action="store_true",
        help="Scan subdirectories recursively."
    )
    parser.add_argument(
        "--workers", type=int, default=4,
        help="Number of parallel worker threads. Default: 4"
    )
    parser.add_argument(
        "--cache-features", type=str, default=None,
        help="Directory for feature cache files (.npy + manifest). If omitted, no caching."
    )
    parser.add_argument(
        "--exclude", action="append", default=[],
        help="Directory subtree to exclude. May be supplied multiple times."
    )

    args = parser.parse_args()

    # Validate
    if not os.path.isdir(args.folder):
        print(f"ERROR: Folder not found: {args.folder}", file=sys.stderr)
        sys.exit(1)

    if not (0 <= args.threshold <= 1):
        print("ERROR: Threshold must be between 0 and 1", file=sys.stderr)
        sys.exit(1)

    if args.workers < 1:
        print("ERROR: Workers must be at least 1", file=sys.stderr)
        sys.exit(1)

    # Run detection
    groups, total_images, elapsed, error_paths, _ = find_duplicates(
        folder_path=args.folder,
        threshold=args.threshold,
        workers=args.workers,
        recursive=args.recursive,
        progress_callback=progress_printer,
        cache_dir=args.cache_features,
        excluded_directories=args.exclude,
    )

    # Build result
    result = {
        "total_images": total_images,
        "total_groups": len(groups),
        "groups": [
            {"id": idx + 1, "images": group}
            for idx, group in enumerate(groups)
        ],
        "failed_images": error_paths,
        "elapsed_seconds": round(elapsed, 2),
    }

    # Ensure output directory exists
    out_dir = os.path.dirname(args.output)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)

    with open(args.output, 'w', encoding='utf-8') as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    # Print a final summary line (informational, Unity can parse if needed)
    sys.stdout.write(
        f"DONE: {total_images} images, {len(groups)} groups, "
        f"{len(error_paths)} failed, {elapsed:.2f}s\n"
    )
    sys.stdout.flush()


if __name__ == "__main__":
    main()
