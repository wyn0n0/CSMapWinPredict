import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope } from 'vue'
import { useReplay } from './useReplay'
import { sampleTimeline } from '../data/sampleTimeline'
import type { DemoWindow } from '../domain/timeline'
import type { ImportedDemo, ImportStage } from '../services/api'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

function windowAt(index: number): DemoWindow {
  return {
    index, coreFromSeconds: index * 20, coreToSeconds: (index + 1) * 20,
    dataFromSeconds: index * 20, dataToSeconds: (index + 1) * 20,
    firstFrameIndex: index, totalFrameCount: 1,
    frames: [{ ...sampleTimeline.frames[0], timeSeconds: index * 20 }],
    utilityTracks: [], utilityEffects: [], playerUtilityStates: [], playerEquipmentStates: [], winPredictions: [],
  }
}

function imported(id = 'demo'): ImportedDemo {
  return {
    id, firstWindow: windowAt(0),
    manifest: {
      id, metadata: { ...sampleTimeline.metadata, durationSeconds: 120 }, events: [],
      frameCount: 6, utilityTrackCount: 0, utilityEffectCount: 0, playerUtilityStateCount: 0,
      playerEquipmentStateCount: 0, windowSeconds: 20, windowCount: 6,
      winPrediction: { status: 'unavailable', schemaVersion: 4, semanticVersion: '', selectedModel: null,
        calibration: null, artifactSha256: null, sampleCount: 0, sampleIntervalSeconds: 1, error: null },
    },
  }
}

const fileEvent = () => ({ target: { files: [{ name: 'demo.dem' }], value: 'demo.dem' } }) as unknown as Event
const flush = () => new Promise(resolve => setTimeout(resolve, 0))
const scopes: ReturnType<typeof effectScope>[] = []

function setup(api = {
  importDemo: vi.fn(async (_file: File, _onStage?: (stage: ImportStage) => void) => imported()),
  loadDemoWindow: vi.fn(async (_id: string, index: number) => windowAt(index)),
}) {
  const scope = effectScope()
  scopes.push(scope)
  return { replay: scope.run(() => useReplay(api))!, scope, api }
}

beforeEach(() => {
  vi.stubGlobal('requestAnimationFrame', vi.fn(() => 1))
  vi.stubGlobal('cancelAnimationFrame', vi.fn())
})
afterEach(() => {
  scopes.splice(0).forEach(scope => scope.stop())
  vi.unstubAllGlobals()
})

describe('replay lifetime', () => {
  it.each(['sample', 'dispose'])('ignores late import status and success after %s', async action => {
    const pending = deferred<ImportedDemo>()
    let progress!: (stage: ImportStage) => void
    const { replay, scope } = setup({
      importDemo: vi.fn((_file, onStage) => { progress = onStage!; return pending.promise }),
      loadDemoWindow: vi.fn(async (_id, index) => windowAt(index)),
    })
    const loading = replay.handleFile(fileEvent())
    if (action === 'sample') replay.loadSample()
    else scope.stop()
    progress('parsing')
    pending.resolve(imported())
    await loading
    expect(replay.manifest.value).toBeNull()
    expect(replay.importing.value).toBe(false)
    expect(replay.importStage.value).toBe('uploading')
    expect(replay.timeline.value.metadata).toEqual(sampleTimeline.metadata)
  })

  it('a failed old import does not stop or overwrite a newer import', async () => {
    const first = deferred<ImportedDemo>(), second = deferred<ImportedDemo>()
    const { replay } = setup({
      importDemo: vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise),
      loadDemoWindow: vi.fn(async (_id, index) => windowAt(index)),
    })
    const old = replay.handleFile(fileEvent()), current = replay.handleFile(fileEvent())
    first.reject(new Error('stale error'))
    await old
    expect(replay.importing.value).toBe(true)
    expect(replay.error.value).toBe('')
    second.resolve(imported('new'))
    await current
    expect(replay.manifest.value?.id).toBe('new')
  })

  it('deduplicates pending windows and ignores out-of-order seek responses', async () => {
    const one = deferred<DemoWindow>(), two = deferred<DemoWindow>()
    const { replay, api } = setup({
      importDemo: vi.fn(async () => imported()),
      loadDemoWindow: vi.fn((_id, index) => index === 1 ? one.promise : two.promise),
    })
    await replay.handleFile(fileEvent())
    replay.seek(21)
    replay.seek(22)
    replay.seek(41)
    expect(api.loadDemoWindow).toHaveBeenCalledTimes(2)
    two.resolve(windowAt(2))
    await flush()
    one.resolve(windowAt(1))
    await flush()
    expect(replay.activeWindow.value?.index).toBe(2)
    expect(replay.activatingWindow.value).toBe(false)
  })

  it('ignores a window response after switching to the sample', async () => {
    const pending = deferred<DemoWindow>()
    const { replay } = setup({
      importDemo: vi.fn(async () => imported()), loadDemoWindow: vi.fn(() => pending.promise),
    })
    await replay.handleFile(fileEvent())
    replay.seek(21)
    replay.loadSample()
    pending.resolve(windowAt(1))
    await flush()
    expect(replay.activeWindow.value).toBeNull()
    expect(replay.currentTime.value).toBe(0)
  })

  it('evicts old windows, reuses recent windows and cancels playback on disposal', async () => {
    const { replay, api, scope } = setup()
    await replay.handleFile(fileEvent())
    for (const seconds of [21, 41, 61, 41]) { replay.seek(seconds); await flush() }
    expect(api.loadDemoWindow).toHaveBeenCalledTimes(3)
    replay.seek(0)
    await flush()
    expect(api.loadDemoWindow).toHaveBeenLastCalledWith('demo', 0)
    replay.togglePlayback()
    expect(replay.playing.value).toBe(true)
    scope.stop()
    expect(replay.playing.value).toBe(false)
    expect(cancelAnimationFrame).toHaveBeenLastCalledWith(1)
  })
})
