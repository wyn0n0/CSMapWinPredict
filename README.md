# CS Demo Map

English | [简体中文](README.zh-CN.md)

**Turn a CS2 match recording into a map replay you can explore, with estimates of each side’s chance of winning the current round.**

Use it to review Mirage matches: follow player movement, see where utility lands, and watch how the balance changes as players, equipment and the bomb situation change.

![Mirage replay dashboard](docs/assets/demo-replay-dashboard.png)

## What can you do?

- **Watch a match on a map:** Open a CS2 `.dem` recording to see player positions, facing direction, health, weapons and equipment.
- **Control the replay:** Play, pause, jump to another moment and change the speed to review key events.
- **Follow movement and utility:** Show player trails, thrown grenades, smoke and fire areas. Turn display layers on or off as needed.
- **See round-win estimates:** With the matching prediction files installed, view the estimated chance that the attacking side (T) or defending side (CT) will win the current round.
- **Try an example first:** A simulated replay is included, so you can explore the interface without a match recording or prediction files.

The project currently focuses on **Mirage**. It shows the match from above on a map, rather than as a first-person video from the game.

## What does the win estimate mean?

The estimate uses information known at the current point in the recording and usually updates about once per second. It describes **this round**, not the result of the whole map or match.

For example, 70% for T means the program considers T more likely to win from the current situation. It does not mean T will definitely win. Estimates can be less reliable early in a round, when both sides have equal numbers of players, or when information is limited.

The card shows the recording time associated with the estimate. It clears before a round starts, after it ends, or when no suitable estimate is available, so an old result is not presented as current.

**You can still import and replay recordings without the prediction files; win estimates will simply be unavailable.**

## Try the example

For now, you need to run the project on your own computer. Initial setup requires a few software tools; there is no ready-to-use installer yet.

To try only the example, install **Node.js 20.19+ (20.x) or 22.12+**, download and extract the project, then open a terminal in the project folder. On Windows, you can use PowerShell. Run these commands one at a time:

```bash
npm install
npm run dev:web
```

The first command downloads the components needed by the interface and requires an internet connection on first use. The second starts the interface.

Keep the terminal open and visit [http://localhost:5173](http://localhost:5173) in your browser. If the terminal shows a different address, use that address instead.

This mode lets you explore the included simulated replay. To import your own recording, continue with the setup below.

## Replay your own match

In addition to Node.js, install **.NET SDK 10.0 or later**, which reads match recordings. For win estimates, you also need **Python 3, pip** and the matching prediction files.

Run these commands one at a time in the project folder:

```bash
npm install
dotnet restore apps/api/CsDemoMap.Api.csproj
```

If you want win estimates, also install the Python components used for prediction:

```bash
python -m pip install -r requirements-inference.txt
```

The prediction files are also called a “model bundle”: files saved after the program has learned from match data. **The project download does not include a model bundle or real match recordings.** Installing the software alone will therefore not enable win estimates. See “Further setup” below for where to place a model bundle.

If the example is still running, press `Ctrl+C` in its terminal to stop it. Then run:

```bash
npm run dev
```

Open [http://localhost:5173](http://localhost:5173), then:

1. Choose a CS2 `.dem` recording in the upper-right corner of the page.
2. Wait while the program reads and prepares it. The page shows progress; larger files may take longer.
3. Once it loads, use the playback button, timeline and speed controls to explore the match.
4. If a working model bundle is configured, round-win estimates will appear during play. Otherwise, the page will say predictions are unavailable.

Keep the terminal open while using the app. Press `Ctrl+C` in the terminal when you want to stop it.

## What is available today?

As of **2026-10-14**:

- **Available:** Mirage map replay, playback controls, player and utility displays, and round-win estimates when a model bundle is installed.
- **Still improving:** The prediction model has been checked on 13 additional matches, but early rounds and situations with equal player counts remain weaknesses. Treat its output as an estimate, not a reliable answer to who will win.
- **Not yet available in the interface:** Better predictions based on player positioning and automatically written situation commentary. These are still being researched and tested.

## Before you use it

- The app reads recorded matches, not a game currently being played live.
- Predictions and map settings currently focus on Mirage. Results on other maps have not been verified.
- Large recordings can take longer to open and use more memory.
- After restarting the program, you need to import your recordings again.

<details>
<summary>Further setup: model location, checks and developer resources</summary>

### Place the model bundle

The default model folder is:

```text
models/win-baseline-v4-holdout-87-8-20260908
```

Use a complete bundle that matches the current program (schema v4 / `mirage-semantics-v4.2`). Unrelated prediction files will not work as a replacement.

To choose the Python command or model location, set these values in Windows PowerShell before starting:

```powershell
$env:WinInference__PythonExecutable = "python"
$env:WinInference__ModelDirectory = "models/win-baseline-v4-holdout-87-8-20260908"
npm run dev
```

Once running, the [model status page](http://localhost:5088/api/win-model/status) returns status information. `ready` means the model is available; `unavailable` or `disabled` means predictions cannot currently be provided.

### Check changes to the program

These commands are for developers. You do not need to run them just to watch a replay:

```bash
npm run typecheck
npm test
npm run build
npm run test:groups -- --help
npm run cli -- --help
```

Checks require the relevant software and components. Some model and recording checks also need additional local files. When training a new model or preparing data in bulk, use a new output folder to avoid overwriting previous results.

Further reading:

- [How the program is organized](docs/architecture.md)
- [Data processing commands](apps/cli/DeveloperCommandHelp.cs)
- [Prediction evaluation tool](tools/win_experiment.py)
- [Player-position data checker](tools/audit_win_spatial.py)

</details>

## Credits

Match recording support uses [demofile-net](https://github.com/saul/demofile-net), licensed under MIT. Simple Radar map artwork comes from [cs-hud](https://github.com/drweissbrot/cs-hud). See [third-party notices](THIRD_PARTY_NOTICES.md) for sources and usage details.
