"""
Feature extractor engine for image similarity detection.
Uses MobileNetV2 to extract 2048-dim feature vectors and cosine similarity for grouping.

Pure engine module — no GUI, no file I/O beyond reading images.
Designed to be called from CLI or external tools like Unity.
"""

import os
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
    Extract 2048-dim feature vector from a single image.
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
                    progress_callback=None):
    """
    Scan a folder for images and group similar ones.

    Args:
        folder_path:  Path to the folder containing images.
        threshold:    Cosine similarity threshold (0-1). Higher = stricter.
        workers:      Number of parallel threads for feature extraction.
        recursive:    If True, walk subdirectories recursively.
        progress_callback:  Optional callable(int) called with 0-100 progress.

    Returns:
        (groups, total_images, elapsed_seconds, error_paths)
          groups:       list of lists, each sublist is [path1, path2, ...] of similar images
          total_images: total number of images successfully processed
          elapsed_seconds: wall-clock time spent
          error_paths:  list of image paths that failed to process
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
            return [], 0, 0.0, []

    total_found = len(image_paths)
    if total_found == 0:
        return [], 0, time.time() - t_start, []

    # --- Extract features in parallel ---
    features = []
    file_paths = []
    error_paths = []
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
                # First 60% of progress is feature extraction
                progress_callback(int(processed / total_found * 60))

    n_success = len(file_paths)
    if n_success < 2:
        return [], n_success, time.time() - t_start, error_paths

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
    return groups, n_success, elapsed, error_paths
