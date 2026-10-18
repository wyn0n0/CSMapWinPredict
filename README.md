# CS Demo Map

English | [简体中文](README.zh-CN.md)

CS Demo Map is a Counter-Strike 2 replay and win-probability analysis project. It turns `.dem` recordings into an interactive radar replay, showing match progress alongside round-win estimates based on player status, equipment and the bomb situation.

The project currently focuses on **Mirage** and supports match review, tactical observation and prediction-model research.

![Mirage replay dashboard](docs/assets/demo-replay-dashboard.png)

## Features

- **Demo import and map replay:** Read CS2 recordings and display player positions, facing direction, health, weapons and equipment on a radar map.
- **Interactive playback:** Play, pause, seek and adjust playback speed to locate and review key moments.
- **Movement and utility layers:** View player trails, projectiles, smoke and fire areas, with individual display layers that can be toggled as needed.
- **Round-win prediction:** With a compatible model configured, follow the estimated probability of T or CT winning the current round as the replay progresses.
- **Built-in example:** Explore the replay interface with simulated data, without a real Demo or model.

The replay uses a top-down radar view to highlight positioning and changes in the round. It does not reproduce the game’s first-person view.

## About win predictions

The model uses information available at the current point in the recording, with estimates usually updated about once per second. The prediction target is **the current round winner**, not the final result of the map or match.

Win probability provides context for reviewing a round, rather than a definite outcome. Predictions remain uncertain early in a round, with equal player counts, or when information is limited.

The probability card includes the recording time associated with its estimate. It clears during round preparation, after the round ends, or when a current prediction is unavailable, to avoid displaying outdated results.

If the model is missing or fails to load, Demo import and replay remain available. The interface indicates that predictions are unavailable.

## Quick start

The project is currently distributed as source code and runs locally after installing its dependencies.

The example requires only **Node.js 20.19+ (20.x) or 22.12+**. Download or clone the project, open a terminal in the project root (PowerShell on Windows), and run:

```bash
npm install
npm run dev:web
```

These commands install the frontend dependencies and start the example interface. The initial installation requires internet access.

Open [http://localhost:5173](http://localhost:5173), or the address shown in the terminal. Keep the service running while using the app.

Example mode uses built-in simulated data. To import a real Demo, start the full service as described below.

## Local setup and Demo import

The full replay service also requires **.NET SDK 10.0 or later**. To enable win predictions, additionally install **Python 3 and pip**, and provide a compatible model bundle.

Install dependencies from the project root:

```bash
npm install
dotnet restore apps/api/CsDemoMap.Api.csproj
```

For win predictions, install the Python dependencies:

```bash
python -m pip install -r requirements-inference.txt
```

**The production model, training datasets and real Demos are not included. The `codex/endgame-model` branch includes the standalone endgame model bundle.** Predictions require a model compatible with the current version. See “Configuration and development” below for model paths and settings.

If the example service is still running, stop it with `Ctrl+C`, then start the full service:

```bash
npm run dev
```

Open [http://localhost:5173](http://localhost:5173), then:

1. Select a CS2 `.dem` file in the upper-right corner of the page.
2. Wait for the import to complete. The page shows reading and processing progress; larger files may take longer.
3. Review the match using the playback controls, timeline and speed settings.
4. When the model is available, round-win estimates appear during play. Otherwise, replay remains available without predictions.

Stop the service with `Ctrl+C` in the terminal when finished.

## Standalone endgame prediction and GSI recording (experimental)

[`endgame_prediction/`](endgame_prediction/README.md) provides standalone Mirage round-win predictions when both teams have surviving players and no more than seven players remain in total. It leaves the replay interface, API and default model unchanged.

[`endgame_prediction/gsi/`](endgame_prediction/gsi/README.md) receives GSI data from a CS2 observer with full spectator information and records raw snapshots, receipt times and individual prediction results locally. The recorder defaults to `127.0.0.1:3002`. This branch includes the compatible endgame model bundle; source data and recordings remain local. After installing the Python dependencies, follow the [full-match recording guide](endgame_prediction/gsi/README.md#录制一场完整比赛) to install the configuration, start and stop recording.

Engineering checks pass; real-match field alignment and prediction quality remain unvalidated. A normal player view does not expose complete data for both teams.

## Development status

As of **2026-10-18**:

- **Implemented:** Mirage replay, playback controls, player and utility displays, and round-win prediction with a compatible model.
- **Ongoing improvements:** The current model has been evaluated on 13 additional matches. Early-round and equal-player-count situations remain key areas for improvement.
- **Experimental:** Standalone endgame prediction, GSI reception and recording are implemented. Spatial-feature model comparisons have completed development evaluation; adoption still requires new-match validation. Automatic situation commentary remains in development. These experiments are not integrated into the interface.

## Limitations

- The main interface analyzes recorded Demos. GSI input is handled by separate experimental scripts and is not integrated into the interface.
- Predictions and map settings currently focus on Mirage. Results on other maps have not been verified.
- Large Demos may require substantial processing time and memory.
- Import records are not yet saved between sessions. Demos must be reimported after restarting the service.

<details>
<summary>Configuration and development: model settings, validation and documentation</summary>

### Model configuration

Default model directory:

```text
models/win-baseline-v4-holdout-87-8-20260908
```

Use a complete model bundle that matches the program’s data version. Avoid mixing models and configuration from different versions.

To override the Python command or model directory, configure these values in Windows PowerShell before starting:

```powershell
$env:WinInference__PythonExecutable = "python"
$env:WinInference__ModelDirectory = "models/win-baseline-v4-holdout-87-8-20260908"
npm run dev
```

Once running, the [model status page](http://localhost:5088/api/win-model/status) returns status information. `ready` means the model is available; `unavailable` or `disabled` means predictions cannot currently be provided.

### Development checks

After changing code, run the relevant type checks, tests and build, or view the available tool commands:

```bash
npm run typecheck
npm test
npm run build
npm run test:groups -- --help
npm run cli -- --help
```

Install the relevant dependencies before running checks. Some checks also require a local model or Demo. Use a new output directory for model training and batch data processing to avoid overwriting previous results.

Further reading:

- [Project architecture](docs/architecture.md)
- [Data processing commands](apps/cli/DeveloperCommandHelp.cs)
- [Prediction evaluation tool](tools/win_experiment.py)
- [Player-position data checker](tools/audit_win_spatial.py)

</details>

## Credits

Match recording support uses [demofile-net](https://github.com/saul/demofile-net), licensed under MIT. Simple Radar map artwork comes from [cs-hud](https://github.com/drweissbrot/cs-hud). See [third-party notices](THIRD_PARTY_NOTICES.md) for sources and usage details.
