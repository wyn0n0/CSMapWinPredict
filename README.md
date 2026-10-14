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
- A current-round probability card that holds the latest observed sample and displays its timestamp, without interpolating toward future samples. It rejects samples before the current round boundary and clears stale or cross-phase values.

The repository also includes a synthetic Mirage timeline, so the UI can be explored without a demo file or backend.

## Current status (2026-10-14)

- **Replay and prediction:** Mirage replay and current-round probability are available in the UI. The configured default remains `win-baseline-v4-holdout-87-8-20260908`, using logistic regression with sigmoid calibration. No model was retrained or replaced for this update.
- **Model evaluation:** The existing 2026-10-14 evaluation covered 13 matches, 271 rounds and 24,489 samples, with round-weighted log loss **0.4836**, Brier score **0.1626** and accuracy **74.3%**. These figures come from an existing local report, not a rerun for this documentation update, and do not represent opening-round accuracy. A stable improvement over the player-count/equipment baseline has not been established. These matches are now observed and cannot serve as unseen final tests for new candidates.
- **Spatial experiments:** A separate `win-spatial-v1` exporter and audit cover C4, carrier support, site proximity and formation distances. Engineering validation covered 3 matches and 7,309 samples. These features are not connected to production inference; predictive gains remain unverified. Straight-line distance does not model navigation or actual support.
- **Situation commentary:** Scene/Facts, core-input, protocol and budget tooling remain offline research. The sequence budget is 5120 tokens: up to 4608 input and 512 output. Refactored sources require new provenance and budget evidence; generation/training admission and product HTTP/UI integration remain pending.

This update publishes source, tests, required configuration and current usage documentation. Historical execution notes, old stage reports, archived drafts and local training artifacts are excluded. Historical files already on GitHub are not removed by this update.

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

The repository is organized by responsibility:

- `apps/api/Program.cs` contains only the HTTP host and dependency wiring.
- `apps/api/Features/` groups backend code into Replay, Semantics, Maps, WinPrediction, and Situation modules.
- `apps/cli/` contains data export, diagnostics, and acceptance workflows.
- `tests/CsDemoMap.Api.Tests/` contains the .NET verification suites and real-Demo prefix checks.
- `apps/web/src/composables/useReplay.ts` manages imports, replay and window caching.
- `tools/` contains Python training, auditing, inference and commentary research tools.
- `tools/run-tests.mjs` schedules test groups with one .NET build per invocation.

## Requirements

- Node.js 20.19+ (20.x) or 22.12+
- .NET SDK 10.0+
- Python 3 and pip for model inference
- A local schema v4 / `mirage-semantics-v4.2` model bundle when real predictions are required

The default model directory is models/win-baseline-v4-holdout-87-8-20260908. Model bundles, datasets, and demo files are intentionally ignored by Git.

## Quick start

~~~bash
npm install
dotnet restore apps/api/CsDemoMap.Api.csproj
dotnet restore apps/cli/CsDemoMap.Cli.csproj
dotnet restore tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj
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
$env:WinInference__ModelDirectory = "models/win-baseline-v4-holdout-87-8-20260908"
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

The UI displays predictions only in live and post-plant phases. It holds the most recent sample at or before playback time, preserves the sample timestamp, and rejects samples older than 1.5 seconds or before the current round boundary. Freeze, ended and cross-phase states clear the card.

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

The following commands are templates: replace `<...>` before running. Export, audit and match-level split must describe the same schema v4 dataset. Train only after preparing the matching manifest, audit and split.

~~~text
npm run cli -- --export-win-data-v4 <demo-directory> <new-dataset-directory>
python tools/audit_win_v4.py --v3 <matching-v3.jsonl> --v4-dir <new-dataset-directory> --output <new-audit.json>
python tools/train_win_baseline_v4.py --input <samples.jsonl> --manifest <manifest.json> --comparison <audit.json> --validation-split <match-split.json> --output-dir <new-model-directory> --threads 4 --folds 5 --seed 42
~~~

Use a new, nonexistent output directory for every export and training run. v3 is retained for reproduction or the audit comparison above; never use it as v4 training input or mix model bundles. Models, data, splits and audit artifacts are not included in a repository download.

### Experiment tools

~~~text
python tools/win_experiment.py --help
python tools/train_win_baseline_v4.py --help
npm run cli -- --export-win-spatial <demo-or-directory> <new-output> <support-radius> <site-radius>
python tools/audit_win_spatial.py <spatial-output-directory>
~~~

`win_experiment.py` initializes registration templates and freezes provenance. `--evaluate-predictions` evaluates saved predictions; final tests require matching experiment and candidate provenance. Spatial radii use world units and must be selected on train/dev only. The spatial companion cannot be passed directly to the existing v4 trainer or inference service. Run `npm run cli -- --help` for all CLI commands.

## Validation

`npm test` runs web, API pipeline and semantic checks by default. Calls containing .NET tests restore/build into `.artifacts/dotnet/`; the first run needs accessible NuGet sources or a complete local cache. Select groups for the changed area; commentary and Python suites are not all included in the default test command.

~~~bash
npm run typecheck
npm test
npm run test:groups -- --help
npm run test:groups -- api semantics situation:stage6
npm run test:commands
npm run cli -- --help
npm run test:inference
npm run test:inference:dotnet
npm run test:audit
npm run test:train
npm run test:train:v4
python -B -m unittest discover -s tools -p "test_audit_win_spatial.py"
python -B -m unittest discover -s tools -p "test_situation_*.py"
npm run build
~~~

Run Python checks in an environment with the corresponding dependencies; situation tooling uses a separate environment and local configuration. Real-demo, model-loading, GPU generation and training checks require additional local artifacts. Passing source tests alone does not establish end-to-end acceptance.

## Documentation and source entry points

- [Architecture and runtime boundaries](docs/architecture.md)
- [Win features and exporters](apps/api/Features/WinPrediction/)
- [CLI command help](apps/cli/DeveloperCommandHelp.cs)
- [Experiment registration and freezing](tools/win_experiment.py)
- [Spatial companion audit](tools/audit_win_spatial.py)
- [Situation schemas](schemas/situation/)
- [Third-party notices](THIRD_PARTY_NOTICES.md)

## Limitations

- Jobs and manifests are held in process memory. Restarting the API invalidates active imports.
- The parser still builds a complete DemoTimeline before splitting it into windows, so peak parsing memory is not fully streaming.
- The current model and map geometry are Mirage-specific.
- Live game-state input, persistent job storage, TTL cleanup, multi-instance coordination, and more map configurations remain future work.

## Credits and license notes

- Demo parsing uses [demofile-net](https://github.com/saul/demofile-net), licensed under MIT.
- The Simple Radar assets in apps/web/public/radars/simpleradar originate from [cs-hud](https://github.com/drweissbrot/cs-hud). Their provenance and redistribution notes are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
