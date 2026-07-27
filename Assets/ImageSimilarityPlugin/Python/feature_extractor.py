"""Image feature extraction, duplicate grouping, and query-by-image search."""

from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
import json
import os
import sys
import time

os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")

import numpy as np
from PIL import Image
import tensorflow as tf
from tensorflow.keras.applications import MobileNetV2
from tensorflow.keras.applications.mobilenet_v2 import preprocess_input
from tensorflow.keras.preprocessing import image


tf.get_logger().setLevel("ERROR")

SUPPORTED_EXTS = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp"}
FEATURE_DIMENSION = 1280
MTIME_TOLERANCE_SECONDS = 1.0

_model = None


def get_model():
    """Lazy-load MobileNetV2. Keras downloads ImageNet weights on first use."""
    global _model
    if _model is None:
        _model = MobileNetV2(weights="imagenet", include_top=False, pooling="avg")
    return _model


def extract_features(img_path):
    """Return one 1280-dimensional feature vector, or None on failure."""
    try:
        model = get_model()
        with Image.open(img_path) as source:
            resized = source.convert("RGB").resize((224, 224))
        img_array = image.img_to_array(resized)
        img_array = preprocess_input(np.expand_dims(img_array, axis=0))
        return model.predict(img_array, verbose=0).flatten()
    except Exception:
        return None


def collect_image_paths(folder_path, recursive=False, excluded_directories=None):
    """Collect supported images while pruning configured directory subtrees."""
    if not os.path.isdir(folder_path):
        return []

    excluded_keys = _normalize_excluded_directories(excluded_directories)
    if _is_path_excluded(folder_path, excluded_keys):
        return []

    image_paths = []
    if recursive:
        for root, dirs, files in os.walk(folder_path):
            # 自顶向下剪枝，排除目录下的文件不会进入枚举，也不会产生无效缓存项。
            dirs[:] = sorted(
                directory for directory in dirs
                if not directory.startswith(".")
                and not _is_path_excluded(os.path.join(root, directory), excluded_keys)
            )
            for file_name in files:
                if os.path.splitext(file_name)[1].lower() in SUPPORTED_EXTS:
                    image_paths.append(os.path.join(root, file_name))
    else:
        for file_name in os.listdir(folder_path):
            full_path = os.path.join(folder_path, file_name)
            if os.path.isfile(full_path) and os.path.splitext(file_name)[1].lower() in SUPPORTED_EXTS:
                image_paths.append(full_path)

    return sorted(image_paths, key=_path_key)


def _normalize_excluded_directories(excluded_directories):
    if not excluded_directories:
        return []
    return sorted({
        os.path.normcase(os.path.realpath(path))
        for path in excluded_directories
        if path
    })


def _is_path_excluded(path, excluded_keys):
    path_key = os.path.normcase(os.path.realpath(path))
    for excluded_key in excluded_keys:
        try:
            if os.path.commonpath((path_key, excluded_key)) == excluded_key:
                return True
        except ValueError:
            # Windows 不同盘符没有公共路径，视为无关目录。
            continue
    return False


def _path_key(path):
    return os.path.normcase(os.path.abspath(path))


def _process_one(img_path):
    return img_path, extract_features(img_path)


def _extract_paths(image_paths, workers, progress_callback=None, progress_start=0, progress_span=100):
    """Extract a path list and preserve its deterministic input order."""
    if not image_paths:
        if progress_callback:
            progress_callback(progress_start + progress_span)
        return [], [], []

    # CLI 首次运行时先在主线程加载一次模型，避免多个 worker 同时创建模型和下载权重。
    get_model()
    workers = max(1, int(workers))
    feature_by_path = {}
    error_paths = []
    processed = 0

    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = [executor.submit(_process_one, path) for path in image_paths]
        for future in as_completed(futures):
            file_path, feature = future.result()
            processed += 1
            if feature is None:
                error_paths.append(file_path)
            else:
                feature_by_path[_path_key(file_path)] = feature

            if progress_callback:
                progress = progress_start + int(processed / len(image_paths) * progress_span)
                progress_callback(progress)

    successful_paths = [path for path in image_paths if _path_key(path) in feature_by_path]
    features = [feature_by_path[_path_key(path)] for path in successful_paths]
    return successful_paths, features, error_paths


def _folder_hash(folder_path, recursive=False, excluded_directories=None):
    """Generate a stable key for one folder and scan scope."""
    normalized = os.path.normcase(os.path.realpath(folder_path))
    scope = "recursive" if recursive else "top-level"
    exclusions = json.dumps(
        _normalize_excluded_directories(excluded_directories),
        ensure_ascii=False,
        separators=(",", ":"),
    )
    return hashlib.sha256(
        f"{normalized}|{scope}|{exclusions}".encode("utf-8")
    ).hexdigest()[:16]


def _cache_paths(cache_dir, folder_path, recursive, excluded_directories=None):
    cache_key = _folder_hash(folder_path, recursive, excluded_directories)
    return (
        os.path.join(cache_dir, f"{cache_key}.npy"),
        os.path.join(cache_dir, f"{cache_key}.json"),
    )


def save_features_cache(cache_dir, folder_path, image_paths, features, recursive=False,
                        excluded_directories=None):
    """Atomically save feature vectors and their path/mtime manifest."""
    os.makedirs(cache_dir, exist_ok=True)
    npy_path, json_path = _cache_paths(
        cache_dir, folder_path, recursive, excluded_directories)

    mtimestamps = {}
    for path in image_paths:
        try:
            mtimestamps[path] = os.path.getmtime(path)
        except OSError:
            mtimestamps[path] = 0.0

    manifest = {
        "images": image_paths,
        "count": len(image_paths),
        "folder": os.path.abspath(folder_path),
        "recursive": recursive,
        "excluded_directories": _normalize_excluded_directories(excluded_directories),
        "date": time.strftime("%Y-%m-%d %H:%M:%S"),
        "mtimestamps": mtimestamps,
    }

    npy_temp = npy_path + ".tmp"
    json_temp = json_path + ".tmp"
    try:
        with open(npy_temp, "wb") as npy_file:
            np.save(npy_file, np.asarray(features, dtype=np.float32))
        with open(json_temp, "w", encoding="utf-8") as json_file:
            json.dump(manifest, json_file, ensure_ascii=False, indent=2)
        os.replace(npy_temp, npy_path)
        os.replace(json_temp, json_path)
    finally:
        for temp_path in (npy_temp, json_temp):
            try:
                if os.path.exists(temp_path):
                    os.remove(temp_path)
            except OSError:
                pass


def load_features_cache(cache_dir, folder_path, recursive=False, excluded_directories=None):
    """Load a feature cache and report stale, missing, and fresh entries."""
    if cache_dir is None or not os.path.isdir(cache_dir):
        return None, None, None

    cache_dir = os.path.normpath(cache_dir)
    npy_path, json_path = _cache_paths(
        cache_dir, folder_path, recursive, excluded_directories)
    if not os.path.isfile(json_path) or not os.path.isfile(npy_path):
        return None, None, None

    try:
        with open(json_path, "r", encoding="utf-8") as manifest_file:
            manifest = json.load(manifest_file)
        cached_paths = manifest.get("images", [])
        cached_count = manifest.get("count", 0)
        mtimestamps = manifest.get("mtimestamps", {})
        features = np.load(npy_path, allow_pickle=False)
    except (OSError, ValueError, TypeError, json.JSONDecodeError):
        return None, None, None

    if cached_count != len(cached_paths) or features.shape != (cached_count, FEATURE_DIMENSION):
        return None, None, None

    stale_count = 0
    missing_count = 0
    fresh_count = 0
    for path in cached_paths:
        cached_mtime = mtimestamps.get(path)
        try:
            actual_mtime = os.path.getmtime(path)
        except OSError:
            missing_count += 1
            continue

        # 旧缓存没有 mtime 时只做一次全量更新，不能把未知状态永久当作新鲜数据。
        if cached_mtime is None or abs(actual_mtime - cached_mtime) > MTIME_TOLERANCE_SECONDS:
            stale_count += 1
        else:
            fresh_count += 1

    cache_info = {
        "stale_count": stale_count,
        "missing_count": missing_count,
        "fresh_count": fresh_count,
        "total_cached": cached_count,
        "_mtimestamps": mtimestamps,
    }
    return cached_paths, features, cache_info


def _save_cache_safely(cache_dir, folder_path, file_paths, features, recursive,
                       excluded_directories):
    if cache_dir is None or not file_paths:
        return
    try:
        save_features_cache(
            cache_dir, folder_path, file_paths, features, recursive,
            excluded_directories)
    except Exception as error:
        sys.stderr.write(f"[cache] Failed to save feature cache: {error}\n")
        sys.stderr.flush()


def _load_or_extract_features(
        folder_path,
        image_paths,
        workers,
        recursive,
        cache_dir,
        excluded_directories,
        progress_callback,
        progress_start,
        progress_span):
    """Resolve a complete feature set by reusing fresh cache entries."""
    cached_paths, cached_features, stale_info = load_features_cache(
        cache_dir, folder_path, recursive, excluded_directories)

    if cached_paths is None or cached_features is None:
        sys.stderr.write("[cache] Miss - extracting features from scratch.\n")
        file_paths, features, error_paths = _extract_paths(
            image_paths, workers, progress_callback, progress_start, progress_span)
        _save_cache_safely(
            cache_dir, folder_path, file_paths, features, recursive,
            excluded_directories)
        return file_paths, features, error_paths, None

    cached_index = {_path_key(path): index for index, path in enumerate(cached_paths)}
    mtimestamps = stale_info.get("_mtimestamps", {})
    feature_by_path = {}
    stale_paths = []
    new_paths = []
    error_paths = []

    for path in image_paths:
        index = cached_index.get(_path_key(path))
        if index is None:
            new_paths.append(path)
            continue

        cached_mtime = mtimestamps.get(cached_paths[index])
        try:
            actual_mtime = os.path.getmtime(path)
        except OSError:
            error_paths.append(path)
            continue

        if cached_mtime is not None and abs(actual_mtime - cached_mtime) <= MTIME_TOLERANCE_SECONDS:
            feature_by_path[_path_key(path)] = cached_features[index]
        else:
            stale_paths.append(path)

    re_extract_paths = stale_paths + new_paths
    if re_extract_paths:
        sys.stderr.write(
            f"[cache] Incremental update: {len(stale_paths)} stale, "
            f"{len(new_paths)} new.\n")
    extracted_paths, extracted_features, extraction_errors = _extract_paths(
        re_extract_paths, workers, progress_callback, progress_start, progress_span)
    error_paths.extend(extraction_errors)
    for path, feature in zip(extracted_paths, extracted_features):
        feature_by_path[_path_key(path)] = feature

    # 按本次扫描顺序重新组装，避免线程完成顺序让分组和排名在多次运行间漂移。
    file_paths = [path for path in image_paths if _path_key(path) in feature_by_path]
    features = [feature_by_path[_path_key(path)] for path in file_paths]
    _save_cache_safely(
        cache_dir, folder_path, file_paths, features, recursive,
        excluded_directories)

    cache_info = {
        "cache_hit": True,
        "fresh_used": len(file_paths) - len(extracted_paths),
        "re_extracted": len(stale_paths),
        "new_added": len(new_paths),
        "missing_removed": stale_info.get("missing_count", 0),
        "total_cached": len(file_paths),
    }
    return file_paths, features, error_paths, cache_info


def _cosine_similarity_matrix(features):
    matrix = np.asarray(features, dtype=np.float32)
    norms = np.linalg.norm(matrix, axis=1, keepdims=True)
    normalized = np.divide(matrix, norms, out=np.zeros_like(matrix), where=norms != 0)
    return normalized @ normalized.T


def _cosine_similarity_to_query(query_feature, target_features):
    query = np.asarray(query_feature, dtype=np.float32)
    targets = np.asarray(target_features, dtype=np.float32)
    query_norm = np.linalg.norm(query)
    target_norms = np.linalg.norm(targets, axis=1)
    denominators = target_norms * query_norm
    return np.divide(
        targets @ query,
        denominators,
        out=np.zeros(len(targets), dtype=np.float32),
        where=denominators != 0,
    )


def find_duplicates(folder_path, threshold=0.95, workers=4, recursive=False,
                    progress_callback=None, cache_dir=None,
                    excluded_directories=None):
    """Scan a folder and return anchor-based groups above the threshold."""
    started_at = time.time()
    image_paths = collect_image_paths(folder_path, recursive, excluded_directories)
    if not image_paths:
        return [], 0, time.time() - started_at, [], None

    file_paths, features, error_paths, cache_info = _load_or_extract_features(
        folder_path, image_paths, workers, recursive, cache_dir,
        excluded_directories,
        progress_callback, 0, 60)
    image_count = len(file_paths)
    if image_count < 2:
        return [], image_count, time.time() - started_at, error_paths, cache_info

    if progress_callback:
        progress_callback(65)
    similarity = _cosine_similarity_matrix(features)
    if progress_callback:
        progress_callback(80)

    visited = set()
    groups = []
    for anchor in range(image_count):
        if anchor in visited:
            continue
        visited.add(anchor)
        group = [file_paths[anchor]]
        for candidate in range(anchor + 1, image_count):
            if candidate not in visited and similarity[anchor][candidate] >= threshold:
                group.append(file_paths[candidate])
                visited.add(candidate)
        if len(group) > 1:
            groups.append(group)

    if progress_callback:
        progress_callback(100)
    return groups, image_count, time.time() - started_at, error_paths, cache_info


def query_similar(query_image_path, folder_path, threshold=0.80, top_k=50,
                  workers=4, recursive=False, progress_callback=None,
                  cache_dir=None, excluded_directories=None):
    """Find target images whose cosine similarity meets the threshold."""
    started_at = time.time()
    query_feature = extract_features(query_image_path)
    if query_feature is None:
        return [], 0, time.time() - started_at, [query_image_path], None

    if progress_callback:
        progress_callback(5)

    all_image_paths = collect_image_paths(
        folder_path, recursive, excluded_directories)
    query_key = _path_key(query_image_path)
    if not any(_path_key(path) != query_key for path in all_image_paths):
        return [], 0, time.time() - started_at, [], None

    if progress_callback:
        progress_callback(10)

    # 缓存始终覆盖完整扫描范围；查询图只在计算结果时排除，避免轮换查询导致缓存反复缺项。
    file_paths, features, error_paths, cache_info = _load_or_extract_features(
        folder_path, all_image_paths, workers, recursive, cache_dir,
        excluded_directories,
        progress_callback, 10, 50)

    targets = [
        (path, feature)
        for path, feature in zip(file_paths, features)
        if _path_key(path) != query_key
    ]
    if not targets:
        return [], 0, time.time() - started_at, error_paths, cache_info

    target_paths = [path for path, _ in targets]
    target_features = [feature for _, feature in targets]
    if progress_callback:
        progress_callback(70)
    similarities = _cosine_similarity_to_query(query_feature, target_features)
    if progress_callback:
        progress_callback(85)

    matches = [
        (path, float(score))
        for path, score in zip(target_paths, similarities)
        if score >= threshold
    ]
    matches.sort(key=lambda item: (-item[1], _path_key(item[0])))
    matches = matches[:max(0, int(top_k))]

    if progress_callback:
        progress_callback(100)
    results = [
        {"path": path, "similarity": score, "rank": rank}
        for rank, (path, score) in enumerate(matches, start=1)
    ]
    return results, len(target_paths), time.time() - started_at, error_paths, cache_info
