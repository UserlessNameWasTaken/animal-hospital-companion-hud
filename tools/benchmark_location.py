"""Build and evaluate a low-cost visual location recognizer.

This is deliberately an offline experiment. It never captures the desktop and
does not modify the overlay. Videos are sampled at 1 FPS, resized to 160x90, and
represented by compact spatial color, HSV histogram, edge, and HOG features.
Alternating six-second video blocks are used for training and testing so a test
frame is not simply adjacent to every training frame.
"""

from __future__ import annotations

import csv
import json
import time
from collections import Counter, defaultdict
from pathlib import Path

import cv2
import numpy as np


PROJECT = Path(__file__).resolve().parents[1]
RAW = PROJECT / "dataset" / "raw" / "Animal_Hospital_ZIP"
DERIVED = PROJECT / "dataset" / "derived"
ANALYSIS = PROJECT / "dataset" / "analysis"
SAMPLE_FPS = 1.0
BLOCK_SECONDS = 6


def label_for(path: Path) -> str:
    return path.parent.name


def feature(image: np.ndarray) -> np.ndarray:
    small = cv2.resize(image, (160, 90), interpolation=cv2.INTER_AREA)
    hsv = cv2.cvtColor(small, cv2.COLOR_BGR2HSV)
    gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)

    # Coarse spatial color retains layout while remaining inexpensive.
    spatial = cv2.resize(hsv, (10, 6), interpolation=cv2.INTER_AREA).astype(np.float32)
    spatial[:, :, 0] /= 180.0
    spatial[:, :, 1:] /= 255.0

    histogram = cv2.calcHist([hsv], [0, 1], None, [12, 8], [0, 180, 0, 256])
    cv2.normalize(histogram, histogram)

    edges = cv2.Canny(gray, 60, 150)
    edge_grid = cv2.resize(edges, (10, 6), interpolation=cv2.INTER_AREA).astype(np.float32) / 255.0

    hog_image = cv2.resize(gray, (128, 64), interpolation=cv2.INTER_AREA)
    hog = cv2.HOGDescriptor(
        (128, 64), (16, 16), (8, 8), (8, 8), 9
    ).compute(hog_image).reshape(-1)

    return np.concatenate(
        [spatial.reshape(-1), histogram.reshape(-1), edge_grid.reshape(-1), hog]
    ).astype(np.float32)


def read_video_samples(path: Path):
    capture = cv2.VideoCapture(str(path))
    if not capture.isOpened():
        raise RuntimeError(f"Could not open {path}")

    native_fps = capture.get(cv2.CAP_PROP_FPS) or 30.0
    frame_count = int(capture.get(cv2.CAP_PROP_FRAME_COUNT))
    duration = frame_count / native_fps
    second = 0.0
    while second < duration:
        capture.set(cv2.CAP_PROP_POS_MSEC, second * 1000.0)
        ok, frame = capture.read()
        if not ok:
            break
        yield second, frame, duration
        second += 1.0 / SAMPLE_FPS
    capture.release()


def collect():
    DERIVED.mkdir(parents=True, exist_ok=True)
    ANALYSIS.mkdir(parents=True, exist_ok=True)
    records = []

    # Stills always belong to training: videos provide the held-out evaluation.
    for path in sorted(RAW.rglob("*.png")):
        image = cv2.imread(str(path))
        if image is None:
            continue
        records.append(
            {
                "label": label_for(path),
                "source": str(path.relative_to(PROJECT)),
                "second": None,
                "split": "train",
                "feature": feature(image),
            }
        )

    video_inventory = []
    for path in sorted(RAW.rglob("*.mp4")):
        label = label_for(path)
        samples = 0
        duration = 0.0
        for second, frame, duration in read_video_samples(path):
            # Alternating time blocks reduce adjacent-frame train/test leakage.
            block = int(second // BLOCK_SECONDS)
            split = "train" if block % 2 == 0 else "test"
            records.append(
                {
                    "label": label,
                    "source": str(path.relative_to(PROJECT)),
                    "second": round(second, 3),
                    "split": split,
                    "feature": feature(frame),
                }
            )
            samples += 1
        video_inventory.append(
            {
                "label": label,
                "file": str(path.relative_to(PROJECT)),
                "duration_seconds": round(duration, 2),
                "sampled_frames": samples,
            }
        )

    return records, video_inventory


def evaluate(records):
    labels = sorted({record["label"] for record in records})
    label_to_id = {label: index for index, label in enumerate(labels)}

    train = [record for record in records if record["split"] == "train"]
    test = [record for record in records if record["split"] == "test"]
    x_train = np.stack([record["feature"] for record in train])
    x_test = np.stack([record["feature"] for record in test])
    y_train = np.array([label_to_id[record["label"]] for record in train], dtype=np.int32)

    mean = x_train.mean(axis=0)
    std = x_train.std(axis=0)
    std[std < 1e-5] = 1.0
    x_train = (x_train - mean) / std
    x_test = (x_test - mean) / std

    np.savez_compressed(
        ANALYSIS / "location_model.npz",
        x_train=x_train.astype(np.float32),
        y_train=y_train,
        mean=mean.astype(np.float32),
        std=std.astype(np.float32),
        labels=np.array(labels),
    )

    # Brute-force kNN is appropriate for this small benchmark and gives a clear
    # estimate of feature cost without another dependency.
    started = time.perf_counter()
    predictions = []
    for vector in x_test:
        distances = np.mean((x_train - vector) ** 2, axis=1)
        nearest = np.argpartition(distances, min(4, len(distances) - 1))[:5]
        votes = Counter(y_train[nearest])
        predicted_id = votes.most_common(1)[0][0]
        predictions.append(labels[predicted_id])
    elapsed = time.perf_counter() - started

    rows = []
    confusion = defaultdict(Counter)
    for record, predicted in zip(test, predictions):
        correct = record["label"] == predicted
        confusion[record["label"]][predicted] += 1
        rows.append(
            {
                "actual": record["label"],
                "predicted": predicted,
                "correct": correct,
                "source": record["source"],
                "second": record["second"],
            }
        )

    per_area = {}
    for actual in sorted(confusion):
        counts = confusion[actual]
        total = sum(counts.values())
        per_area[actual] = {
            "accuracy": counts[actual] / total if total else 0.0,
            "correct": counts[actual],
            "total": total,
            "most_common_predictions": counts.most_common(3),
        }

    correct = sum(row["correct"] for row in rows)
    rooms_1_to_5 = [row for row in rows if row["actual"] in {f"Room {n}" for n in range(1, 6)}]
    room_correct = sum(row["correct"] for row in rooms_1_to_5)
    metrics = {
        "labels": labels,
        "training_samples": len(train),
        "test_samples": len(test),
        "overall_accuracy": correct / len(rows) if rows else 0.0,
        "rooms_1_to_5_accuracy": room_correct / len(rooms_1_to_5) if rooms_1_to_5 else 0.0,
        "classification_total_ms": elapsed * 1000.0,
        "classification_mean_ms": elapsed * 1000.0 / len(test) if test else 0.0,
        "per_area": per_area,
    }
    return metrics, rows


def write_results(metrics, rows, inventory):
    (ANALYSIS / "location_benchmark.json").write_text(
        json.dumps({"metrics": metrics, "videos": inventory}, indent=2),
        encoding="utf-8",
    )
    with (ANALYSIS / "location_predictions.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle, fieldnames=["actual", "predicted", "correct", "source", "second"]
        )
        writer.writeheader()
        writer.writerows(rows)

    lines = [
        "# Location recognition benchmark",
        "",
        f"- Training samples: {metrics['training_samples']}",
        f"- Held-out samples: {metrics['test_samples']}",
        f"- Overall accuracy: {metrics['overall_accuracy']:.1%}",
        f"- Rooms 1–5 accuracy: {metrics['rooms_1_to_5_accuracy']:.1%}",
        f"- Mean classification time: {metrics['classification_mean_ms']:.2f} ms",
        "",
        "| Area | Accuracy | Correct / Total |",
        "|---|---:|---:|",
    ]
    for label, result in metrics["per_area"].items():
        lines.append(
            f"| {label} | {result['accuracy']:.1%} | {result['correct']} / {result['total']} |"
        )
    (ANALYSIS / "location_benchmark.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main():
    records, inventory = collect()
    metrics, rows = evaluate(records)
    write_results(metrics, rows, inventory)
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()
