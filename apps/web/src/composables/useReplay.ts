import { computed, onScopeDispose, ref } from 'vue'
import { sampleTimeline } from '../data/sampleTimeline'
import { findFrameIndex, windowIndexAt, type DemoManifest, type DemoTimeline, type DemoWindow } from '../domain/timeline'
import { importDemo, loadDemoWindow, loadWinReview, type ImportStage } from '../services/api'
import type { WinReview } from '../domain/winReview'

/** Owns playback, import lifetime and the bounded asynchronous window cache. */
export function useReplay(api = { importDemo, loadDemoWindow, loadWinReview }) {
  const timeline = ref<DemoTimeline>(sampleTimeline)
  const manifest = ref<DemoManifest | null>(null)
  const activeWindow = ref<DemoWindow | null>(null)
  const demoId = ref('')
  const currentTime = ref(0)
  const playing = ref(false)
  const speed = ref(1)
  const importing = ref(false)
  const importStage = ref<ImportStage>('uploading')
  const importFileName = ref('')
  const activatingWindow = ref(false)
  const error = ref('')
  const winReview = ref<WinReview | null>(null)
  const reviewLoading = ref(false)
  const reviewError = ref('')
  const windowCache = new Map<number, DemoWindow>()
  const windowRequests = new Map<number, Promise<DemoWindow>>()
  let animationFrame = 0
  let previousTimestamp = 0
  let windowGeneration = 0
  let activationSequence = 0
  let pendingWindowIndex: number | null = null

  const frameIndex = computed(() => findFrameIndex(timeline.value.frames, currentTime.value))
  const currentFrame = computed(() => timeline.value.frames[frameIndex.value])
  const duration = computed(() => timeline.value.metadata.durationSeconds)

  function togglePlayback() {
    if (playing.value) {
      stopPlayback()
      return
    }
    if (currentTime.value >= duration.value) {
      currentTime.value = 0
      void activateWindowAt(0)
    }
    playing.value = true
    previousTimestamp = performance.now()
    animationFrame = requestAnimationFrame(advance)
  }

  function advance(timestamp: number) {
    if (!playing.value) return
    const delta = Math.min(0.1, (timestamp - previousTimestamp) / 1000)
    previousTimestamp = timestamp
    currentTime.value = Math.min(duration.value, currentTime.value + delta * speed.value)
    void activateWindowAt(currentTime.value)
    prefetchNextWindow()
    if (currentTime.value >= duration.value) stopPlayback()
    else animationFrame = requestAnimationFrame(advance)
  }

  function stopPlayback() {
    playing.value = false
    cancelAnimationFrame(animationFrame)
  }

  function seek(value: number | string) {
    currentTime.value = Math.max(0, Math.min(duration.value, Number(value)))
    void activateWindowAt(currentTime.value)
    prefetchNextWindow()
  }

  async function handleFile(event: Event) {
    const input = event.target as HTMLInputElement
    const file = input.files?.[0]
    if (!file) return

    stopPlayback()
    resetImportedState()
    timeline.value = sampleTimeline
    currentTime.value = 0
    const generation = windowGeneration
    importing.value = true
    importFileName.value = file.name
    error.value = ''
    try {
      const imported = await api.importDemo(file, stage => {
        if (generation === windowGeneration) importStage.value = stage
      })
      if (generation !== windowGeneration) return
      demoId.value = imported.id
      manifest.value = imported.manifest
      activeWindow.value = imported.firstWindow
      windowCache.set(0, imported.firstWindow)
      applyWindow(imported.firstWindow)
      currentTime.value = 0
      prefetchNextWindow()
      void reloadWinReview()
    } catch (reason) {
      if (generation === windowGeneration)
        error.value = reason instanceof Error ? reason.message : '导入失败。'
    } finally {
      if (generation === windowGeneration) importing.value = false
      input.value = ''
    }
  }

  function loadSample() {
    stopPlayback()
    resetImportedState()
    timeline.value = sampleTimeline
    currentTime.value = 0
    error.value = ''
  }

  async function reloadWinReview() {
    if (!demoId.value || reviewLoading.value) return
    const generation = windowGeneration
    reviewLoading.value = true
    reviewError.value = ''
    try {
      const review = await api.loadWinReview(demoId.value)
      if (generation === windowGeneration) winReview.value = review
    } catch (reason) {
      if (generation === windowGeneration)
        reviewError.value = reason instanceof Error ? reason.message : '载入复盘曲线失败。'
    } finally {
      if (generation === windowGeneration) reviewLoading.value = false
    }
  }

  async function activateWindowAt(timeSeconds: number) {
    const targetIndex = windowIndexForTime(timeSeconds)
    if (targetIndex === null) return
    if (activeWindow.value?.index === targetIndex) {
      if (pendingWindowIndex !== null) activationSequence++
      pendingWindowIndex = null
      activatingWindow.value = false
      return
    }
    if (pendingWindowIndex === targetIndex) return

    const sequence = ++activationSequence
    pendingWindowIndex = targetIndex
    activatingWindow.value = true
    try {
      const window = await getWindow(targetIndex)
      if (sequence === activationSequence && windowIndexForTime(currentTime.value) === targetIndex)
        applyWindow(window)
    } catch (reason) {
      if (sequence === activationSequence)
        error.value = reason instanceof Error ? reason.message : '载入时间窗口失败。'
    } finally {
      if (sequence === activationSequence) {
        pendingWindowIndex = null
        activatingWindow.value = false
      }
    }
  }

  function prefetchNextWindow() {
    const info = manifest.value
    const targetIndex = windowIndexForTime(currentTime.value)
    if (!info || targetIndex === null || targetIndex + 1 >= info.windowCount) return
    const targetCoreTo = Math.min(info.metadata.durationSeconds, (targetIndex + 1) * info.windowSeconds)
    if (currentTime.value < targetCoreTo - 5) return
    void getWindow(targetIndex + 1).catch(() => undefined)
  }

  function getWindow(index: number): Promise<DemoWindow> {
    const cached = windowCache.get(index)
    if (cached) return Promise.resolve(cached)
    const existing = windowRequests.get(index)
    if (existing) return existing

    const id = demoId.value
    const generation = windowGeneration
    const request = api.loadDemoWindow(id, index).then(window => {
      if (generation === windowGeneration) {
        windowCache.set(index, window)
        trimWindowCache(index)
      }
      return window
    }).finally(() => {
      if (windowRequests.get(index) === request) windowRequests.delete(index)
    })
    windowRequests.set(index, request)
    return request
  }

  function applyWindow(window: DemoWindow) {
    const info = manifest.value
    if (!info) return
    activeWindow.value = window
    timeline.value = {
      metadata: info.metadata,
      events: info.events,
      frames: window.frames,
      utilityTracks: window.utilityTracks,
      utilityEffects: window.utilityEffects,
      playerUtilityStates: window.playerUtilityStates,
      playerEquipmentStates: window.playerEquipmentStates,
    }
  }

  function windowIndexForTime(timeSeconds: number): number | null {
    const info = manifest.value
    if (!info) return null
    return windowIndexAt(timeSeconds, info.windowSeconds, info.windowCount)
  }

  function trimWindowCache(preferredIndex: number) {
    if (windowCache.size <= 3) return
    for (const index of windowCache.keys()) {
      if (index !== activeWindow.value?.index && index !== preferredIndex) {
        windowCache.delete(index)
        if (windowCache.size <= 3) break
      }
    }
  }

  function resetImportedState() {
    windowGeneration++
    activationSequence++
    importing.value = false
    importStage.value = 'uploading'
    importFileName.value = ''
    pendingWindowIndex = null
    activatingWindow.value = false
    manifest.value = null
    activeWindow.value = null
    demoId.value = ''
    winReview.value = null
    reviewLoading.value = false
    reviewError.value = ''
    windowCache.clear()
    windowRequests.clear()
  }

  onScopeDispose(() => {
    stopPlayback()
    resetImportedState()
  })

  return {
    timeline, manifest, activeWindow, currentTime, playing, speed,
    importing, importStage, importFileName, activatingWindow, error,
    frameIndex, currentFrame, duration, togglePlayback, seek, handleFile, loadSample,
    winReview, reviewLoading, reviewError, reloadWinReview,
  }
}
