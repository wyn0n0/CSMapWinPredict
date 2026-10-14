import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createSSRApp, effectScope } from 'vue'
import { renderToString } from 'vue/server-renderer'
import App from './App.vue'
import * as replayModule from './composables/useReplay'
import { sampleTimeline } from './data/sampleTimeline'
import type { WinPredictionPoint } from './domain/timeline'

vi.mock('./components/RadarMap.vue', () => ({ default: { render: () => null } }))

const createReplay = replayModule.useReplay
let scope: ReturnType<typeof effectScope>
let replay: ReturnType<typeof createReplay>
let points: WinPredictionPoint[]
const render = () => renderToString(createSSRApp(App))

beforeEach(() => {
  vi.stubGlobal('cancelAnimationFrame', vi.fn())
  scope = effectScope()
  replay = scope.run(() => createReplay())!
  vi.spyOn(replayModule, 'useReplay').mockReturnValue(replay)
  points = [10, 11].map((timeSeconds, index) => ({
    tick: timeSeconds * 64, timeSeconds, roundId: 's0-a1', segmentId: 0,
    roundNumber: 1, phase: 'live', tWin: index ? .9 : .4, ctWin: index ? .1 : .6,
  }))
  replay.timeline.value = {
    ...sampleTimeline,
    // Legacy replay numbers can differ from semantic model numbers.
    frames: sampleTimeline.frames.map(frame => ({ ...frame, round: { ...frame.round, number: 99 } })),
    events: [{ tick: 576, timeSeconds: 9, type: 'round-live', title: '冻结时间结束' }],
  }
  replay.manifest.value = {
    id: 'demo', metadata: replay.timeline.value.metadata, events: replay.timeline.value.events,
    frameCount: 361, utilityTrackCount: 0, utilityEffectCount: 0,
    playerUtilityStateCount: 0, playerEquipmentStateCount: 0, windowSeconds: 30, windowCount: 3,
    winPrediction: {
      status: 'ready', schemaVersion: 4, semanticVersion: 'mirage-semantics-v4.2',
      selectedModel: 'test-model', calibration: null, artifactSha256: null,
      sampleCount: points.length, sampleIntervalSeconds: 1, error: null,
    },
  }
  replay.activeWindow.value = {
    index: 0, coreFromSeconds: 0, coreToSeconds: 30, dataFromSeconds: 0, dataToSeconds: 32,
    firstFrameIndex: 0, totalFrameCount: 361, frames: replay.timeline.value.frames,
    utilityTracks: [], utilityEffects: [], playerUtilityStates: [], playerEquipmentStates: [],
    winPredictions: points,
  }
  replay.currentTime.value = 10.5
})

afterEach(() => {
  scope.stop()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('win prediction display', () => {
  it('shows the reached sample time and restores the earlier value when seeking back', async () => {
    let html = await render()
    expect(html).toContain('T 方 40%，CT 方 60%')
    expect(html).toContain('采样于 00:10 · 10.000 秒')
    expect(html).toContain('按采样更新')
    replay.currentTime.value = 11
    html = await render()
    expect(html).toContain('T 方 90%，CT 方 10%')
    expect(html).toContain('采样于 00:11 · 11.000 秒')
    replay.currentTime.value = 10.5
    html = await render()
    expect(html).toContain('T 方 40%，CT 方 60%')
    expect(html).not.toContain('T 方 90%，CT 方 10%')
  })

  it.each(['round-start', 'round-live', 'round-end'])('does not reuse a prediction across %s', async type => {
    replay.timeline.value.events.push({ tick: 672, timeSeconds: 10.5, type, title: type })
    expect(await render()).not.toContain('class="probability-body"')
    replay.currentTime.value = 10.25
    expect(await render()).toContain('T 方 40%，CT 方 60%')
  })

  it('waits for the target window even when the old window contains overlapping predictions', async () => {
    replay.activeWindow.value!.winPredictions = points.map(point => ({
      ...point, tick: point.tick + 1280, timeSeconds: point.timeSeconds + 20,
    }))
    replay.currentTime.value = 30.5
    replay.activatingWindow.value = true
    expect(await render()).not.toContain('class="probability-body"')
    // A failed load clears the loading flag but must not expose the previous window's values.
    replay.activatingWindow.value = false
    expect(await render()).toContain('等待回放窗口')
    expect(await render()).not.toContain('class="probability-body"')
    replay.activeWindow.value = { ...replay.activeWindow.value!, index: 1, coreFromSeconds: 30, coreToSeconds: 60 }
    expect(await render()).toContain('T 方 40%，CT 方 60%')
  })

  it('hides predictions during import and keeps the sample source label after returning to the sample', async () => {
    replay.importing.value = true
    expect(await render()).not.toContain('class="probability-body"')
    replay.loadSample()
    const html = await render()
    expect(html).toContain('示例曲线 · 非模型输出')
    expect(html).toContain('采样于 00:00 · 0.000 秒')
  })
})
