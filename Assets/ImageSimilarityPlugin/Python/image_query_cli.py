"""
Headless CLI for query-by-image similarity search.
Called by Unity Editor (or standalone) via subprocess.

Usage:
  python image_query_cli.py --query "D:/path/to/query.png" --folder "D:/path/to/images"
                            --threshold 0.80 --output "result.json"

Stdout protocol (lines Unity parses):
  PROGRESS:<int>           # 0-100, real-time progress

After completion, writes JSON to --output path:
  {
    "total_images": 500,
    "query_image": "D:/path/to/query.png",
    "threshold": 0.80,
    "results": [
      {"image_path": "D:/path/to/similar.png", "similarity": 0.987, "rank": 1},
      ...
    ],
    "elapsed_seconds": 3.2
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

from feature_extractor import query_similar


def progress_printer(pct):
    """Print progress line that Unity parses."""
    sys.stdout.write(f"PROGRESS:{pct}\n")
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser(
        description="Query-by-image: find visually similar images using MobileNetV2 + cosine similarity."
    )
    parser.add_argument(
        "--query", required=True,
        help="Path to the query image."
    )
    parser.add_argument(
        "--folder", required=True,
        help="Path to the folder containing target images to search."
    )
    parser.add_argument(
        "--threshold", type=float, default=0.80,
        help="Cosine similarity threshold (0-1). Higher = stricter. Default: 0.80"
    )
    parser.add_argument(
        "--top-k", type=int, default=50,
        help="Maximum number of results to return. Default: 50"
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
        "--cache", type=str, default=None,
        help="Directory for feature cache files (.npy + manifest). If omitted, no caching."
    )

    args = parser.parse_args()

    # Validate
    if not os.path.isfile(args.query):
        print(f"ERROR: Query image not found: {args.query}", file=sys.stderr)
        sys.exit(1)

    if not os.path.isdir(args.folder):
        print(f"ERROR: Folder not found: {args.folder}", file=sys.stderr)
        sys.exit(1)

    if not (0 < args.threshold <= 1):
        print("ERROR: Threshold must be between 0 and 1", file=sys.stderr)
        sys.exit(1)

    # Run query
    results_list, total_images, elapsed, error_paths, _ = query_similar(
        query_image_path=args.query,
        folder_path=args.folder,
        threshold=args.threshold,
        top_k=args.top_k,
        workers=args.workers,
        recursive=args.recursive,
        progress_callback=progress_printer,
        cache_dir=args.cache,
    )

    # Build result JSON — field names match C# QueryResultData / SimilarImage
    result = {
        "total_images": total_images,
        "query_image": os.path.abspath(args.query),
        "threshold": args.threshold,
        "results": [
            {"image_path": item["path"], "similarity": item["similarity"], "rank": item["rank"]}
            for item in results_list
        ],
        "elapsed_seconds": round(elapsed, 2),
    }

    # Ensure output directory exists
    out_dir = os.path.dirname(args.output)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)

    with open(args.output, 'w', encoding='utf-8') as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    # Print final summary line (informational)
    sys.stdout.write(
        f"DONE: {total_images} images scanned, {len(results_list)} similar found, {elapsed:.2f}s\n"
    )
    sys.stdout.flush()


if __name__ == "__main__":
    main()
