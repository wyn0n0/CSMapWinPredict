# CS Demo Map

English | [简体中文](README.zh-CN.md)

CS Demo Map imports Counter-Strike 2 .dem files, replays the match on an interactive radar, and shows the current round's T/CT win probability. The prediction target is the current round winner, not the map or series winner.

![Mirage replay dashboard](docs/assets/demo-replay-dashboard.png)

## Highlights

- CS2 CSTV/GOTV and POV demo parsing through DemoFile.Game.Cs.
- An 8 Hz player timeline covering position, velocity, view angle, health, weapon, economy, equipment, utility, round state, C4 state, score, loss streaks, and zone occupancy.
- 16 Hz projectile trajectories and lifecycle-accurate smoke and fire effects.
- A Vue 3 + SVG replay UI with seeking, playback speed, player names, trails, projectile, effect, and inventory layers.
- Brotli-compressed 30-second windows with two-second overlap. The browser keeps only a small active-window cache and prefetches the next window.
- Schema v4.2 Mirage round-win inference at roughly one-second intervals during live and post-plant play.
- A current-round probability card that interpolates only within the same round segment and phase. It clears stale values during freeze time, at round end, and across phase changes.

The repository also includes a synthetic Mirage timeline, so the UI can be explored without a demo file or backend.

## Architecture

~~~text
.dem
  -> DemoFile.Game.Cs
  -> DemoParserService
  -> WinFeatureSampleBuilder
  -> WinInferenceClient (managed Python process)
  -> WinTimelinePredictionService
  -> DemoImportService (Brotli window files)
  -> Vue replay UI and probability card
~~~

The API owns one long-lived Python inference subprocess. At startup it validates the model manifest, artifact hashes, dependency contract, and fixed inference fixtures. If the model cannot load, demo replay remains available and the UI reports that predictions are unavailable.

## Requirements

- Node.js 20+
- .NET SDK 10.0+
- Python 3 and pip for model inference
- A local schema v4.2 model bundle when real predictions are required

The default model directory is models/win-baseline-v4-holdout-68-5-20260906. Model bundles, datasets, and demo files are intentionally ignored by Git.

## Quick start

~~~bash
npm install
dotnet restore apps/api/CsDemoMap.Api.csproj
python -m pip install -r requirements-inference.txt
npm run dev
~~~

Open http://localhost:5173. The frontend proxies API calls to http://localhost:5088. Starting npm run dev launches the API, the web app, and the managed Python inference process.

To browse the synthetic replay without the API:

~~~bash
npm install
npm run dev:web
~~~

### Inference configuration

Before starting the API, these optional environment variables override the defaults:

~~~powershell
$env:WinInference__PythonExecutable = "python"
$env:WinInference__ModelDirectory = "models/win-baseline-v4-holdout-68-5-20260906"
npm run dev
~~~

Check runtime status with:

~~~http
GET /api/win-model/status
~~~

A ready response includes the schema, semantic version, selected model, calibration, artifact hash, and Python runtime. When the model is missing, incompatible, or disabled, the endpoint returns unavailable or disabled with an error; replay import still works.

## Import a demo

Choose a .dem file in the upper-right corner of the web UI:

~~~http
POST /api/demos/import
Content-Type: multipart/form-data
file=<demo file>

GET /api/demos/{id}/status
GET /api/demos/{id}/windows/{index}
~~~

The UI reports uploading, queueing, parsing, prediction, window creation, and first-window loading. Each completed import has:

- manifest.winPrediction: model state, schema and semantic versions, selected model, calibration, artifact hash, sample count, sampling interval, and an error when unavailable.
- windows[].winPredictions[]: tick, demo time, round identity, segment, round number, phase, T probability, and CT probability.

The UI displays predictions only in live and post-plant phases. It interpolates adjacent samples in the same round segment and phase, and clears the card for freeze, ended, cross-phase, or expired data.

### Offline import

The API can import a file already stored under data/mirage without another large HTTP upload:

~~~http
GET /api/demos/offline

POST /api/demos/offline/import
Content-Type: application/json

{"fileName":"furia-vs-pain-m1-mirage.dem"}
~~~

Only top-level .dem file names are accepted. To use another directory:

~~~powershell
$env:OfflineDemos__RootPath = "D:\other\demo-directory"
dotnet run --project apps/api/CsDemoMap.Api.csproj
~~~

## Data and model workflow

The v4.2 workflow uses causally safe, as-of-tick features for live and post-plant samples. Features do not include player identity, future events, final round state, or the target label.

Install the fixed training dependencies before training:

~~~bash
python -m pip install -r requirements-train.txt
~~~

~~~powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --export-win-data-v4 data/mirage datasets/mirage-v4-new-run
python tools/train_win_baseline_v4.py --input datasets/mirage-v4-20260906/samples.jsonl --manifest datasets/mirage-v4-20260906/manifest.json --comparison datasets/mirage-v4-20260906/comparison.json --validation-split models/win-baseline-v3-holdout-68-5/validation-split.json --v3-predictions models/win-baseline-v3-holdout-68-5/validation_predictions.jsonl --output-dir models/win-baseline-v4-new-run --threads 4 --folds 5 --seed 42
~~~

Use a new output directory for every export and training run. Historical v3 data and models are retained only for reproducibility and must not be mixed with v4 inputs or model bundles.

## Validation

~~~bash
npm run typecheck
npm test
npm run test:inference
npm run test:inference:dotnet
npm run test:audit
npm run test:train
npm run test:train:v4
npm run build
~~~

The latest end-to-end verification imported furia-vs-pain-m1-mirage.dem: 2,124.92 seconds, 71 windows, and 1,282 probability points. The page matched the API in live and post-plant phases, remained correct around a window boundary, cleared probability during freeze time, and displayed the unavailable state when inference was disabled.

## Documentation

- [Architecture and runtime boundaries](docs/architecture.md)
- [Schema v4.2 implementation](docs/semantic-v4-implementation.md)
- [Semantic validation](docs/semantic-v4-validation.md)
- [v4 training report](docs/training-report-v4.md)
- [68-match historical validation summary](docs/training-report-68-matches.md)
- [Round and clock upstream review](docs/round-clock-upstream-review.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)

## Limitations

- Jobs and manifests are held in process memory. Restarting the API invalidates active imports.
- The parser still builds a complete DemoTimeline before splitting it into windows, so peak parsing memory is not fully streaming.
- The current model and map geometry are Mirage-specific.
- Live game-state input, persistent job storage, TTL cleanup, multi-instance coordination, and more map configurations remain future work.

## Credits and license notes

- Demo parsing uses [demofile-net](https://github.com/saul/demofile-net), licensed under MIT.
- The Simple Radar assets in apps/web/public/radars/simpleradar originate from [cs-hud](https://github.com/drweissbrot/cs-hud). Their provenance and redistribution notes are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
