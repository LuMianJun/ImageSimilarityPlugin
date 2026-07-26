"""
Feature extractor engine for image similarity detection.
Uses MobileNetV2 to extract 1280-dim feature vectors and cosine similarity for grouping.

Pure engine module — no GUI, no file I/O beyond reading images.
Designed to be called from CLI or external tools like Unity.
"""

import hashlib
import json
import os
import sys
import time
import numpy as np
from PIL import Image
import tensorflow as tf
from tensorflow.keras.applications import MobileNetV2
from tensorflow.keras.applications.mobilenet_v2 import preprocess_input
from tensorflow.keras.preprocessing import image
from sklearn.metrics.pairwise import cosine_similarity
from concurrent.futures import ThreadPoolExecutor, as_completed

# Suppress TensorFlow log noise
tf.get_logger().setLevel('ERROR')

# Supported image extensions
SUPPORTED_EXTS = {'.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tiff', '.tif', '.webp'}

# Lazy-loaded model
_model = None


def get_model():
    """Lazy-load MobileNetV2 (downloads weights on first call)."""
    global _model
    if _model is None:
        _model = MobileNetV2(weights='imagenet', include_top=False, pooling='avg')
    return _model


def extract_features(img_path):
    """
    Extract 1280-dim feature vector from a single image.
    Returns None if the image cannot be read or processed.
    """
    try:
        model = get_model()
        img = Image.open(img_path).convert('RGB').resize((224, 224))
        img_array = image.img_to_array(img)
        img_array = preprocess_input(np.expand_dims(img_array, axis=0))
        features = model.predict(img_array, verbose=0).flatten()
        return features
    except Exception:
        return None


def _process_one(args):
    """Worker function for ThreadPoolExecutor."""
    filepath, _ = args
    return filepath, extract_features(filepath)


def find_duplicates(folder_path, threshold=0.95, workers=4, recursive=False,
                    progress_callback=None, cache_dir=None):
    """
    Scan a folder for images and group similar ones.

    Args:
        folder_path:  Path to the folder containing images.
        threshold:    Cosine similarity threshold (0-1). Higher = stricter.
        workers:      Number of parallel threads for feature extraction.
        recursive:    If True, walk subdirectories recursively.
        progress_callback:  Optional callable(int) called with 0-100 progress.
        cache_dir:    Optional directory for feature cache (.npy + manifest).

    Returns:
        (groups, total_images, elapsed_seconds, error_paths, cache_info)
          groups:       list of lists, each sublist is [path1, path2, ...] of similar images
          total_images: total number of images successfully processed
          elapsed_seconds: wall-clock time spent
          error_paths:  list of image paths that failed to process
          cache_info:   dict with incremental update stats (None if no cache was used)
    """
    t_start = time.time()

    # --- Collect image paths ---
    image_paths = []
    if recursive:
        for root, dirs, files in os.walk(folder_path):
            # Skip hidden directories
            dirs[:] = [d for d in dirs if not d.startswith('.')]
            for f in files:
                ext = os.path.splitext(f)[1].lower()
                if ext in SUPPORTED_EXTS:
                    image_paths.append(os.path.join(root, f))
    else:
        try:
            for f in os.listdir(folder_path):
                full = os.path.join(folder_path, f)
                if os.path.isfile(full):
                    ext = os.path.splitext(f)[1].lower()
                    if ext in SUPPORTED_EXTS:
                        image_paths.append(full)
        except FileNotFoundError:
            return [], 0, 0.0, [], None

    total_found = len(image_paths)
    if total_found == 0:
        return [], 0, time.time() - t_start, [], None

    # --- Load cache and identify what needs re-extraction ---
    cached_paths, cached_features, stale_info = load_features_cache(cache_dir, folder_path)
    features = []
    file_paths = []
    error_paths = []
    cache_info = None

    if cached_paths is not None and cached_features is not None:
        # ── Cache hit: incremental update ──
        cached_abs = {os.path.abspath(p): i for i, p in enumerate(cached_paths)}
        mtimestamps = stale_info.get("_mtimestamps", {})

        fresh_paths = []
        fresh_features = []
        stale_paths = []
        new_paths = []

        for tp in image_paths:
            tp_abs = os.path.abspath(tp)
            idx = cached_abs.get(tp_abs)
            if idx is not None:
                cached_mtime = mtimestamps.get(cached_paths[idx], 0.0)
                try:
                    actual_mtime = os.path.getmtime(tp)
                except OSError:
                    error_paths.append(tp)
                    continue
                if cached_mtime and abs(actual_mtime - cached_mtime) <= 1.0:
                    fresh_paths.append(tp)
                    fresh_features.append(cached_features[idx])
                else:
                    stale_paths.append(tp)
            else:
                new_paths.append(tp)

        re_extract_list = stale_paths + new_paths

        if re_extract_list:
            sys.stderr.write(
                f"[cache] Scan incremental: re-extracting {len(stale_paths)} stale + "
                f"{len(new_paths)} new = {len(re_extract_list)} images.\n"
            )
            processed = 0
            with ThreadPoolExecutor(max_workers=workers) as executor:
                futures = [executor.submit(_process_one, (p, None)) for p in re_extract_list]
                for future in as_completed(futures):
                    filepath, feat = future.result()
                    processed += 1
                    if feat is not None:
                        features.append(feat)
                        file_paths.append(filepath)
                    else:
                        error_paths.append(filepath)

                    if progress_callback:
                        progress_callback(int(processed / len(re_extract_list) * 60))

        # Merge fresh + re-extracted
        file_paths = fresh_paths + file_paths
        features = fresh_features + features

        # Save updated cache
        if cache_dir is not None and len(file_paths) > 0:
            try:
                save_features_cache(cache_dir, folder_path, file_paths, features)
            except Exception:
                pass

        cache_info = {
            "cache_hit": True,
            "fresh_used": len(fresh_paths),
            "re_extracted": len(stale_paths),
            "new_added": len(new_paths),
            "missing_removed": stale_info.get("missing_count", 0),
            "total_cached": len(file_paths),
        }
        sys.stderr.write(
            f"[cache] Scan updated: fresh={len(fresh_paths)} re_extracted={len(stale_paths)} "
            f"new_added={len(new_paths)} missing_removed={cache_info['missing_removed']}\n"
        )
    else:
        # ── No cache — extract all features from scratch ──
        sys.stderr.write("[cache] Scan miss — extracting features from scratch.\n")
        processed = 0

        with ThreadPoolExecutor(max_workers=workers) as executor:
            futures = [executor.submit(_process_one, (p, None)) for p in image_paths]
            for future in as_completed(futures):
                filepath, feat = future.result()
                processed += 1
                if feat is not None:
                    features.append(feat)
                    file_paths.append(filepath)
                else:
                    error_paths.append(filepath)

                if progress_callback:
                    progress_callback(int(processed / total_found * 60))

        # Save cache for future queries
        if cache_dir is not None and len(file_paths) > 0:
            try:
                save_features_cache(cache_dir, folder_path, file_paths, features)
            except Exception:
                pass

    n_success = len(file_paths)
    if n_success < 2:
        return [], n_success, time.time() - t_start, error_paths, cache_info
        return [], n_success, time.time() - t_start, error_paths

    # --- Save feature cache if requested ---
    if cache_dir is not None:
        try:
            save_features_cache(cache_dir, folder_path, file_paths, features)
        except Exception:
            pass  # cache save failure should not abort the scan

    # --- Compute similarity matrix ---
    if progress_callback:
        progress_callback(65)

    similarity = cosine_similarity(features)

    if progress_callback:
        progress_callback(80)

    # --- Group duplicates ---
    visited = set()
    groups = []

    for i in range(n_success):
        if i in visited:
            continue
        group = [file_paths[i]]
        for j in range(i + 1, n_success):
            if j not in visited and similarity[i][j] > threshold:
                group.append(file_paths[j])
                visited.add(j)
        if len(group) > 1:
            groups.append(group)

    if progress_callback:
        progress_callback(100)

    elapsed = time.time() - t_start
    return groups, n_success, elapsed, error_paths, cache_info


# ==============================================================================
#  特征缓存读写
# ==============================================================================

def _folder_hash(folder_path):
    """Generate a stable 8-char hex hash for a folder path."""
    return hashlib.md5(os.path.abspath(folder_path).encode('utf-8')).hexdigest()[:8]


def save_features_cache(cache_dir, folder_path, image_paths, features):
    """
    Save extracted features to disk cache.

    Writes two files:
      {hash}.npy  — (N, 1280) float32 feature array
      {hash}.json — manifest with image paths, mtime, and metadata

    Args:
        cache_dir:   Directory to store cache files.
        folder_path: The scanned folder (used for hash key).
        image_paths: List of absolute image paths (order matches features).
        features:    List of numpy feature vectors (each 1280-dim).
    """
    os.makedirs(cache_dir, exist_ok=True)
    h = _folder_hash(folder_path)
    npy_path = os.path.join(cache_dir, f"{h}.npy")
    json_path = os.path.join(cache_dir, f"{h}.json")

    arr = np.array(features, dtype=np.float32)
    np.save(npy_path, arr)

    # Record mtime for each file so we can detect changes later
    mtimestamps = {}
    for p in image_paths:
        try:
            mtimestamps[p] = os.path.getmtime(p)
        except OSError:
            mtimestamps[p] = 0.0

    manifest = {
        "images": image_paths,
        "count": len(image_paths),
        "folder": os.path.abspath(folder_path),
        "date": time.strftime("%Y-%m-%d %H:%M:%S"),
        "mtimestamps": mtimestamps,  # {path: mtime_float}
    }
    with open(json_path, 'w', encoding='utf-8') as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)


def load_features_cache(cache_dir, folder_path):
    """
    Load cached features for a folder.

    Checks mtime of each cached file to detect changes.
    Does NOT auto-invalidate — stale entries are reported so the caller
    can decide whether to warn the user or rebuild.

    Args:
        cache_dir:   Directory containing cache files.
        folder_path: The folder to load cache for.

    Returns:
        (image_paths, features_array, cache_info) on success.
        (None, None, None) if cache doesn't exist or is broken.

        cache_info dict keys:
            stale_count   — files whose mtime changed since caching
            missing_count — files in cache that no longer exist on disk
            fresh_count   — files that are unchanged
            total_cached  — total files in the cache
    """
    if cache_dir is None or not os.path.isdir(cache_dir):
        return None, None, None

    # Normalize cache_dir to fix mixed slashes from Unity's Application.temporaryCachePath
    cache_dir = os.path.normpath(cache_dir)

    h = _folder_hash(folder_path)
    npy_path = os.path.join(cache_dir, f"{h}.npy")
    json_path = os.path.join(cache_dir, f"{h}.json")

    if not os.path.isfile(json_path) or not os.path.isfile(npy_path):
        return None, None, None

    try:
        with open(json_path, 'r', encoding='utf-8') as f:
            manifest = json.load(f)

        cached_paths = manifest.get("images", [])
        cached_count = manifest.get("count", 0)
        mtimestamps = manifest.get("mtimestamps", {})
    except Exception:
        return None, None, None

    if cached_count != len(cached_paths):
        return None, None, None

    try:
        features = np.load(npy_path)
    except Exception:
        return None, None, None

    if features.shape != (cached_count, 1280):
        return None, None, None

    # --- Check mtime for each cached file ---
    stale_count = 0
    missing_count = 0
    fresh_count = 0
    now = time.time()

    for p in cached_paths:
        cached_mtime = mtimestamps.get(p)
        if cached_mtime is None:
            # Legacy cache without mtime entry — treat as fresh but note
            fresh_count += 1
            continue
        try:
            actual_mtime = os.path.getmtime(p)
            # Allow 1-second tolerance for filesystem timestamp granularity
            if abs(actual_mtime - cached_mtime) > 1.0:
                stale_count += 1
            else:
                fresh_count += 1
        except OSError:
            missing_count += 1

    cache_info = {
        "stale_count": stale_count,
        "missing_count": missing_count,
        "fresh_count": fresh_count,
        "total_cached": cached_count,
        "_mtimestamps": mtimestamps,  # internal: per-file mtime for incremental update
    }

    return cached_paths, features, cache_info


# ==============================================================================
#  以图搜图 (Query-by-Image)
# ==============================================================================

def query_similar(query_image_path, folder_path, threshold=0.80, top_k=50,
                   workers=4, recursive=False, progress_callback=None,
                   cache_dir=None):
    """
    Given a query image, find all visually similar images in a target folder.

    Args:
        query_image_path: Absolute path to the query image.
        folder_path:      Path to the folder containing target images.
        threshold:        Minimum cosine similarity (0-1) to include.
        top_k:            Maximum number of results to return.
        workers:          Number of parallel threads for feature extraction.
        recursive:        If True, walk subdirectories recursively.
        progress_callback: Optional callable(int) called with 0-100 progress.
        cache_dir:        Optional directory for feature cache (.npy + manifest).

    Returns:
        (results, total_images, elapsed_seconds, error_paths)
          results:       list of {path, similarity, rank} dicts, sorted desc.
          total_images:  number of target images successfully processed.
          elapsed_seconds: wall-clock time spent.
          error_paths:   list of image paths that failed to process.
    """
    t_start = time.time()

    # --- Extract query feature ---
    query_vec = extract_features(query_image_path)
    if query_vec is None:
        return [], 0, time.time() - t_start, [query_image_path], None

    if progress_callback:
        progress_callback(5)

    # --- Collect target image paths (exclude the query image itself) ---
    query_abs = os.path.abspath(query_image_path)
    target_paths = []
    if recursive:
        for root, dirs, files in os.walk(folder_path):
            dirs[:] = [d for d in dirs if not d.startswith('.')]
            for f in files:
                ext = os.path.splitext(f)[1].lower()
                if ext in SUPPORTED_EXTS:
                    full = os.path.join(root, f)
                    if os.path.abspath(full) != query_abs:
                        target_paths.append(full)
    else:
        try:
            for f in os.listdir(folder_path):
                full = os.path.join(folder_path, f)
                if os.path.isfile(full):
                    ext = os.path.splitext(f)[1].lower()
                    if ext in SUPPORTED_EXTS:
                        if os.path.abspath(full) != query_abs:
                            target_paths.append(full)
        except FileNotFoundError:
            return [], 0, time.time() - t_start, [], None

    total_found = len(target_paths)
    if total_found == 0:
        return [], 0, time.time() - t_start, [], None

    if progress_callback:
        progress_callback(10)

    # --- Load or extract target features ---
    cached_paths, cached_features, cache_info = load_features_cache(cache_dir, folder_path)
    error_paths = []

    if cached_paths is not None and cached_features is not None:
        # ── Cache hit: identify fresh / stale / missing / new ──
        cached_abs = {os.path.abspath(p): i for i, p in enumerate(cached_paths)}
        mtimestamps = cache_info.get("_mtimestamps", {})

        fresh_paths = []
        fresh_features = []
        stale_paths = []
        new_paths = []

        for tp in target_paths:
            tp_abs = os.path.abspath(tp)
            idx = cached_abs.get(tp_abs)
            if idx is not None:
                # Check mtime
                cached_mtime = mtimestamps.get(cached_paths[idx], 0.0)
                try:
                    actual_mtime = os.path.getmtime(tp)
                except OSError:
                    # File disappeared since target scan — skip
                    error_paths.append(tp)
                    continue
                if cached_mtime and abs(actual_mtime - cached_mtime) <= 1.0:
                    fresh_paths.append(tp)
                    fresh_features.append(cached_features[idx])
                else:
                    stale_paths.append(tp)
            else:
                new_paths.append(tp)

        re_extract_list = stale_paths + new_paths
        re_extracted_features = []
        re_extracted_paths = []

        if re_extract_list:
            sys.stderr.write(
                f"[cache] Incremental update: re-extracting {len(stale_paths)} stale + "
                f"{len(new_paths)} new = {len(re_extract_list)} images.\n"
            )
            processed = 0
            with ThreadPoolExecutor(max_workers=workers) as executor:
                futures = [executor.submit(_process_one, (p, None)) for p in re_extract_list]
                for future in as_completed(futures):
                    filepath, feat = future.result()
                    processed += 1
                    if feat is not None:
                        re_extracted_features.append(feat)
                        re_extracted_paths.append(filepath)
                    else:
                        error_paths.append(filepath)

                    if progress_callback:
                        # Extraction occupies 15%–65% of the bar
                        progress_callback(15 + int(processed / len(re_extract_list) * 50))
        else:
            if progress_callback:
                progress_callback(65)

        # Merge: fresh (unchanged) + re-extracted (stale + new)
        file_paths = fresh_paths + re_extracted_paths
        features = fresh_features + re_extracted_features

        # Save updated cache
        if cache_dir is not None and len(file_paths) > 0:
            try:
                save_features_cache(cache_dir, folder_path, file_paths, features)
            except Exception:
                pass

        n_success = len(file_paths)
        cache_info = {
            "cache_hit": True,
            "fresh_used": len(fresh_paths),
            "re_extracted": len(stale_paths),
            "new_added": len(new_paths),
            "missing_removed": cache_info.get("missing_count", 0),
            "total_cached": n_success,
        }
        sys.stderr.write(
            f"[cache] Updated: fresh={len(fresh_paths)} re_extracted={len(stale_paths)} "
            f"new_added={len(new_paths)} missing_removed={cache_info['missing_removed']}\n"
        )
    else:
        # ── No cache — extract all features from scratch ──
        cache_info = None
        sys.stderr.write("[cache] Miss — extracting features from scratch.\n")
        features = []
        file_paths = []
        processed = 0

        with ThreadPoolExecutor(max_workers=workers) as executor:
            futures = [executor.submit(_process_one, (p, None)) for p in target_paths]
            for future in as_completed(futures):
                filepath, feat = future.result()
                processed += 1
                if feat is not None:
                    features.append(feat)
                    file_paths.append(filepath)
                else:
                    error_paths.append(filepath)

                if progress_callback:
                    # 10%–60% range for extraction
                    progress_callback(10 + int(processed / total_found * 50))

        # Save cache for future queries
        if cache_dir is not None and len(file_paths) > 0:
            try:
                save_features_cache(cache_dir, folder_path, file_paths, features)
            except Exception:
                pass

        n_success = len(file_paths)

    if n_success == 0:
        return [], 0, time.time() - t_start, error_paths, cache_info

    if progress_callback:
        progress_callback(70)

    # --- Compute 1xN cosine similarity ---
    target_matrix = np.array(features, dtype=np.float32)
    similarities = cosine_similarity([query_vec], target_matrix)[0]

    if progress_callback:
        progress_callback(85)

    # --- Filter, sort, truncate ---
    results = []
    for i in range(n_success):
        score = float(similarities[i])
        if score > threshold:
            results.append((file_paths[i], score))

    results.sort(key=lambda x: x[1], reverse=True)
    results = results[:top_k]

    if progress_callback:
        progress_callback(100)

    elapsed = time.time() - t_start
    return [
        {"path": p, "similarity": s, "rank": idx + 1}
        for idx, (p, s) in enumerate(results)
    ], n_success, elapsed, error_paths, cache_info
