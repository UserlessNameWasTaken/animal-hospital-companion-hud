# Animal Hospital visual dataset

The original user-supplied archive is extracted under `raw/Animal_Hospital_ZIP`.
Do not rename or modify those files. Generated frames, thumbnails, manifests,
and recognition experiments belong under `derived/` or `analysis/`.

## Current coverage

- Rooms 1–8: entrance stills, exit still, and a 360-degree video
- Hall Center: entrance, exit, and 360-degree video
- Medical Bay: entrance and 360-degree video
- Office: entrance, exit, shutter-open video, and shutter-closed video
- Reception Area: entrance, exit, and 360-degree video
- Emergency Bay: entrance and exit stills

All 42 still images are 1920x1080. The source set contains 13 MP4 videos.

## Recognition strategy

1. Detect doorway/room-number signs during entry and exit.
2. Retain the last confident location instead of continuously classifying every
   frame.
3. Sample one downscaled frame at a low rate only while Roblox is foregrounded.
4. Use multiple-frame agreement before changing the displayed location.
5. Treat rooms 1–5 as visually similar; do not infer their number from generic
   beds, cabinets, or curtains alone.
6. Keep automatic patient and event changes confirmation-based until dedicated
   popup examples are available.

## Baseline results

The first offline benchmarks intentionally use lightweight techniques that
could plausibly run without affecting Roblox:

- Whole-scene compact feature classifier: 59.1% overall accuracy
- Whole-scene accuracy for similar Rooms 1–5: 36.0%
- Local SIFT landmark matching on room-entry stills: 52.0%
- Compact feature classification cost: approximately 0.73 ms per sampled frame

These results are not accurate enough for live automatic HUD changes. They
demonstrate that generic room interiors and doorway geometry are too similar.
The live overlay must not integrate these baselines.

The next experiment should combine:

1. Explicit recognition of the white digit on black doorway signs
2. Temporal voting across multiple sampled frames
3. Hospital-map transition constraints
4. An `Unknown` result whenever confidence is insufficient

Raw prediction details are under `analysis/location_predictions.csv`,
`analysis/location_benchmark.json`, and `analysis/entry_sign_benchmark.json`.

## Needed additions

- Shift completion/report summary sequence
- Room assignment popup examples for all digits 1–8
- Fire, ritual, death, and other room-event popup examples
- Tall coffee machine in ready and brewing states
- Small coffee machine from several natural interaction angles
