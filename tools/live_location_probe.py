"""Low-frequency, candidate-only desktop scene observer.

Captures a single downscaled frame every two seconds only while a Roblox window
is foregrounded. Predictions are diagnostic JSON lines; they must never mutate
overlay state without confirmation.
"""

from __future__ import annotations

import ctypes
import json
import time
from pathlib import Path

import cv2
import numpy as np
from PIL import ImageGrab

from benchmark_location import feature


PROJECT = Path(__file__).resolve().parents[1]
MODEL = PROJECT / "dataset" / "analysis" / "location_model.npz"
INTERVAL_SECONDS = 2.0


def foreground_is_roblox() -> bool:
    user32 = ctypes.windll.user32
    window = user32.GetForegroundWindow()
    if not window:
        return False
    length = user32.GetWindowTextLengthW(window)
    title = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(window, title, length + 1)
    return "roblox" in title.value.lower()


def main():
    model = np.load(MODEL, allow_pickle=False)
    x_train = model["x_train"]
    y_train = model["y_train"]
    mean = model["mean"]
    std = model["std"]
    labels = model["labels"]

    print(json.dumps({"status": "ready"}), flush=True)
    while True:
        started = time.perf_counter()
        if foreground_is_roblox():
            screenshot = np.asarray(ImageGrab.grab(all_screens=False))
            frame = cv2.cvtColor(screenshot, cv2.COLOR_RGB2BGR)
            vector = (feature(frame) - mean) / std
            distances = np.mean((x_train - vector) ** 2, axis=1)
            nearest = np.argpartition(distances, min(4, len(distances) - 1))[:5]
            votes = np.bincount(y_train[nearest], minlength=len(labels))
            winner = int(np.argmax(votes))
            vote_confidence = float(votes[winner] / len(nearest))
            # Confidence is intentionally conservative because the held-out
            # benchmark showed visually similar rooms.
            confidence = min(0.85, vote_confidence * 0.85)
            print(
                json.dumps(
                    {
                        "status": "prediction",
                        "location": str(labels[winner]),
                        "confidence": round(confidence, 3),
                        "processing_ms": round((time.perf_counter() - started) * 1000, 1),
                    }
                ),
                flush=True,
            )
        remaining = INTERVAL_SECONDS - (time.perf_counter() - started)
        if remaining > 0:
            time.sleep(remaining)


if __name__ == "__main__":
    main()
