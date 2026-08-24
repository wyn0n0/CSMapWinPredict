<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import BrandMark from './components/BrandMark.vue'
import EventFeed from './components/EventFeed.vue'
import RadarMap from './components/RadarMap.vue'
import { sampleTimeline } from './data/sampleTimeline'
import {
  findFrameIndex,
  findTickIndex,
  formatTime,
  windowIndexAt,
  type DemoManifest,
  type DemoTimeline,
  type DemoWindow,
  type PlayerEquipmentState,
} from './domain/timeline'
import { importDemo, loadDemoWindow, type ImportStage } from './services/api'

const timeline = ref<DemoTimeline>(sampleTimeline)
const manifest = ref<DemoManifest | null>(null)
const activeWindow = ref<DemoWindow | null>(null)
const demoId = ref('')
const currentTime = ref(0)
const playing = ref(false)
const speed = ref(1)
const showNames = ref(true)
const showTrails = ref(true)
const showProjectiles = ref(true)
const showEffects = ref(true)
const showInventory = ref(true)
const importing = ref(false)
const importStage = ref<ImportStage>('uploading')
const importFileName = ref('')
const activatingWindow = ref(false)
const error = ref('')
const windowCache = new Map<number, DemoWindow>()
const windowRequests = new Map<number, Promise<DemoWindow>>()
let animationFrame = 0
let previousTimestamp = 0
let windowGeneration = 0
let activationSequence = 0
let pendingWindowIndex: number | null = null

const stageLabels: Record<ImportStage, string> = {
  uploading: '正在上传…',
  queued: '等待解析…',
  parsing: '正在解析…',
  chunking: '正在生成窗口…',
  loading: '正在载入首屏…',
}

const frameIndex = computed(() => findFrameIndex(timeline.value.frames, currentTime.value))
const currentFrame = computed(() => timeline.value.frames[frameIndex.value])
const duration = computed(() => timeline.value.metadata.durationSeconds)
const importStageLabel = computed(() => stageLabels[importStage.value])
const totalFrameCount = computed(() => manifest.value?.frameCount ?? timeline.value.frames.length)
const globalFrameNumber = computed(() => activeWindow.value
  ? activeWindow.value.firstFrameIndex + frameIndex.value + 1
  : frameIndex.value + 1)
const windowLabel = computed(() => manifest.value && activeWindow.value
  ? `窗口 ${activeWindow.value.index + 1} / ${manifest.value.windowCount}`
  : '')
const equipmentStatesByPlayer = computed(() => {
  const grouped = new Map<string, PlayerEquipmentState[]>()
  for (const state of timeline.value.playerEquipmentStates) {
    const states = grouped.get(state.playerId) ?? []
    states.push(state)
    grouped.set(state.playerId, states)
  }
  for (const states of grouped.values()) states.sort((a, b) => a.tick - b.tick)
  return grouped
})
const currentEquipment = computed(() => {
  const tick = currentFrame.value?.tick ?? 0
  const result = new Map<string, PlayerEquipmentState>()
  for (const [playerId, states] of equipmentStatesByPlayer.value) {
    const index = findTickIndex(states, tick)
    if (index >= 0) result.set(playerId, states[index])
  }
  return result
})
const economyStats = computed(() => {
  const summarize = (team: 'T' | 'CT') => {
    const members = (currentFrame.value?.players ?? []).filter(player => player.team === team)
    const states = members.flatMap(player => {
      const state = currentEquipment.value.get(player.id)
      return state ? [state] : []
    })
    return {
      money: states.reduce((total, state) => total + state.money, 0),
      equipment: states.reduce((total, state) => total + state.currentEquipmentValue, 0),
      armor: states.filter(state => state.armor > 0).length,
      helmets: states.filter(state => state.hasHelmet).length,
      defusers: states.filter(state => state.hasDefuser).length,
      grenades: states.reduce((total, state) => total + state.items
        .filter(item => item.category === 'grenade')
        .reduce((count, item) => count + item.count, 0), 0),
    }
  }
  return { t: summarize('T'), ct: summarize('CT') }
})
const bombLabel = computed(() => ({
  unavailable: '未出现', carried: '携带中', dropped: '已掉落', planting: '正在下包',
  planted: '已安放', defusing: '正在拆除', defused: '已拆除', exploded: '已爆炸',
} as Record<string, string>)[currentFrame.value?.bomb.state ?? 'unavailable'] ?? currentFrame.value?.bomb.state)
const bombDetail = computed(() => {
  const bomb = currentFrame.value?.bomb
  if (!bomb) return ''
  const location = bomb.site ? `${bomb.site} 区` : bomb.region
  const countdown = bomb.state === 'defusing' && bomb.secondsToDefuse != null
    ? `拆除 ${formatTime(bomb.secondsToDefuse)}`
    : bomb.secondsToExplosion != null
      ? `爆炸 ${formatTime(bomb.secondsToExplosion)}`
      : ''
  return [location, countdown].filter(Boolean).join(' · ')
})
const roundPhaseLabel = computed(() => ({
  warmup: '热身', 'team-intro': '队伍介绍', freeze: '冻结时间', live: '进行中',
  'post-plant': '下包后', ended: '已结束',
} as Record<string, string>)[currentFrame.value?.round.phase ?? ''] ?? '未知')
const occupiedZones = computed(() => (currentFrame.value?.zones ?? [])
  .filter(zone => zone.tAlive + zone.ctAlive > 0)
  .sort((a, b) => b.tAlive + b.ctAlive - a.tAlive - a.ctAlive)
  .slice(0, 5))
const teamStats = computed(() => {
  const players = currentFrame.value?.players ?? []
  const summarize = (team: 'T' | 'CT') => {
    const members = players.filter(player => player.team === team)
    return {
      alive: members.filter(player => player.alive).length,
      health: members.reduce((total, player) => total + player.health, 0),
    }
  }
  return { t: summarize('T'), ct: summarize('CT') }
})

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
  importing.value = true
  importFileName.value = file.name
  error.value = ''
  try {
    const imported = await importDemo(file, stage => { importStage.value = stage })
    demoId.value = imported.id
    manifest.value = imported.manifest
    activeWindow.value = imported.firstWindow
    windowCache.set(0, imported.firstWindow)
    applyWindow(imported.firstWindow)
    currentTime.value = 0
    prefetchNextWindow()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '导入失败。'
  } finally {
    importing.value = false
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
  const request = loadDemoWindow(id, index).then(window => {
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
  pendingWindowIndex = null
  activatingWindow.value = false
  manifest.value = null
  activeWindow.value = null
  demoId.value = ''
  windowCache.clear()
  windowRequests.clear()
}

onBeforeUnmount(() => {
  stopPlayback()
  resetImportedState()
})
</script>

<template>
  <div class="app-frame">
    <header class="topbar">
      <div class="brand">
        <BrandMark />
        <div>
          <strong>DEMO<span>/MAP</span></strong>
          <small>COUNTER-STRIKE REPLAY LAB</small>
        </div>
      </div>
      <div class="file-meta">
        <span class="status-dot" />
        <div>
          <small>当前数据</small>
          <strong>{{ timeline.metadata.fileName }}</strong>
        </div>
      </div>
      <div class="header-actions">
        <button class="ghost-button" @click="loadSample">载入示例</button>
        <label class="import-button" :class="{ busy: importing }">
          <span>{{ importing ? importStageLabel : '导入 .DEM' }}</span>
          <input type="file" accept=".dem" :disabled="importing" @change="handleFile" />
        </label>
      </div>
    </header>

    <div v-if="error" class="error-banner">
      <strong>导入未完成</strong>
      <span>{{ error }} 请确认 API 已在 5088 端口启动。</span>
      <button @click="error = ''">×</button>
    </div>

    <div v-if="importing" class="import-progress" role="status">
      <span class="progress-spinner" />
      <strong>{{ importStageLabel }}</strong>
      <span>{{ importFileName }}</span>
      <small>大文件上传完成后会在后台解析并生成 30 秒压缩窗口。</small>
    </div>

    <main class="workspace">
      <aside class="left-column">
        <section class="match-card panel">
          <span class="eyebrow">REPLAY OVERVIEW</span>
          <h1>{{ timeline.metadata.mapName.replace('de_', '').toUpperCase() }}</h1>
          <div class="match-tags">
            <span>{{ timeline.metadata.tickRate }} TICK</span><span>{{ timeline.metadata.sampleRate }} FPS</span><span>ROUND {{ currentFrame?.round.number ?? 0 }}</span>
          </div>
          <div class="scoreboard">
            <div class="team-row team-row-t">
              <span class="team-badge">T</span>
              <div><strong>TERRORISTS</strong><small>{{ teamStats.t.alive }} 存活 · {{ teamStats.t.health }} HP</small></div>
              <b>{{ teamStats.t.alive }}</b>
            </div>
            <div class="team-row team-row-ct">
              <span class="team-badge">CT</span>
              <div><strong>COUNTER-TERRORISTS</strong><small>{{ teamStats.ct.alive }} 存活 · {{ teamStats.ct.health }} HP</small></div>
              <b>{{ teamStats.ct.alive }}</b>
            </div>
          </div>
        </section>

        <section class="settings-panel panel">
          <div class="panel-title-row"><div><span class="eyebrow">LAYERS</span><h2>雷达图层</h2></div></div>
          <label class="toggle-row"><span>玩家名称<small>显示 Steam 昵称</small></span><input v-model="showNames" type="checkbox" /><i /></label>
          <label class="toggle-row"><span>移动轨迹<small>最近 9 秒路径</small></span><input v-model="showTrails" type="checkbox" /><i /></label>
          <label class="toggle-row"><span>投掷物轨迹<small>飞行位置与已行进路线</small></span><input v-model="showProjectiles" type="checkbox" /><i /></label>
          <label class="toggle-row"><span>烟雾与燃烧<small>持续范围和燃烧点</small></span><input v-model="showEffects" type="checkbox" /><i /></label>
          <label class="toggle-row"><span>携带道具<small>玩家当前完整投掷物库存</small></span><input v-model="showInventory" type="checkbox" /><i /></label>
        </section>

        <section class="data-note">
          <span>DATA SOURCE</span>
          <strong>DemoFile.Game.Cs 0.44.1</strong>
          <small>世界坐标保留在时间线中，渲染时转换为 overview 坐标。</small>
        </section>
      </aside>

      <section class="map-column">
        <div class="map-toolbar">
          <div><span class="live-pill">REPLAY</span><strong>{{ formatTime(currentTime) }}</strong><small>/ {{ formatTime(duration) }}</small><small v-if="windowLabel" class="window-label">{{ activatingWindow ? '载入窗口…' : windowLabel }}</small></div>
          <div class="legend"><span class="legend-t">T</span><span class="legend-ct">CT</span><span class="legend-utility">道具</span><span class="legend-dead">阵亡</span></div>
        </div>
        <RadarMap
          :map-name="timeline.metadata.mapName"
          :frames="timeline.frames"
          :frame-index="frameIndex"
          :current-time="currentTime"
          :tick-rate="timeline.metadata.tickRate"
          :utility-tracks="timeline.utilityTracks"
          :utility-effects="timeline.utilityEffects"
          :player-utility-states="timeline.playerUtilityStates"
          :show-names="showNames"
          :show-trails="showTrails"
          :show-projectiles="showProjectiles"
          :show-effects="showEffects"
          :show-inventory="showInventory"
        />
        <div class="transport panel">
          <button class="play-button" :aria-label="playing ? '暂停' : '播放'" @click="togglePlayback">
            <span v-if="playing" class="pause-icon" />
            <span v-else class="play-icon" />
          </button>
          <span class="transport-time">{{ formatTime(currentTime) }}</span>
          <input
            class="timeline-slider"
            type="range"
            min="0"
            :max="duration"
            step="0.05"
            :value="currentTime"
            :style="{ '--progress': `${duration ? currentTime / duration * 100 : 0}%` }"
            @input="seek(($event.target as HTMLInputElement).value)"
          />
          <span class="transport-time muted">{{ formatTime(duration) }}</span>
          <select v-model="speed" class="speed-select" aria-label="播放速度">
            <option :value="0.5">0.5×</option><option :value="1">1×</option><option :value="2">2×</option><option :value="4">4×</option>
          </select>
        </div>
      </section>

      <aside class="right-column">
        <EventFeed :events="timeline.events" :current-time="currentTime" @seek="seek" />
        <section class="intel-panel panel">
          <div class="panel-title-row"><div><span class="eyebrow">ROUND STATE</span><h2>对局数据</h2></div><span class="phase-pill">{{ roundPhaseLabel }}</span></div>
          <div class="round-score">
            <div><small>T</small><strong>{{ currentFrame?.round.scoreT ?? 0 }}</strong></div>
            <span>ROUND {{ currentFrame?.round.number ?? 0 }}</span>
            <div><strong>{{ currentFrame?.round.scoreCT ?? 0 }}</strong><small>CT</small></div>
          </div>
          <div class="intel-grid">
            <div><small>剩余时间</small><strong>{{ formatTime(currentFrame?.round.remainingSeconds ?? 0) }}</strong></div>
            <div><small>C4</small><strong>{{ bombLabel }}</strong><span>{{ bombDetail }}</span></div>
            <div><small>T 经济 / 装备</small><strong>${{ economyStats.t.money.toLocaleString() }}</strong><span>${{ economyStats.t.equipment.toLocaleString() }} · {{ economyStats.t.grenades }} 道具 · {{ economyStats.t.armor }} 甲 / {{ economyStats.t.helmets }} 头盔</span></div>
            <div><small>CT 经济 / 装备</small><strong>${{ economyStats.ct.money.toLocaleString() }}</strong><span>${{ economyStats.ct.equipment.toLocaleString() }} · {{ economyStats.ct.grenades }} 道具 · {{ economyStats.ct.armor }} 甲 / {{ economyStats.ct.helmets }} 头盔 · {{ economyStats.ct.defusers }} 钳</span></div>
            <div><small>连续失利</small><strong>T {{ currentFrame?.round.consecutiveLossesT ?? 0 }} · CT {{ currentFrame?.round.consecutiveLossesCT ?? 0 }}</strong><span>可用于估计下一回合奖励</span></div>
          </div>
          <div class="zone-list">
            <small>区域人数</small>
            <span v-for="zone in occupiedZones" :key="zone.region">{{ zone.region }} <b>{{ zone.tAlive }}</b>/<i>{{ zone.ctAlive }}</i></span>
          </div>
        </section>
        <section class="frame-panel panel">
          <span class="eyebrow">FRAME INSPECTOR</span>
          <div class="metric-grid">
            <div><small>DEMO TICK</small><strong>{{ currentFrame?.tick ?? 0 }}</strong></div>
            <div><small>FRAME</small><strong>{{ globalFrameNumber }} / {{ totalFrameCount }}</strong></div>
            <div><small>PLAYERS</small><strong>{{ currentFrame?.players.length ?? 0 }}</strong></div>
            <div><small>SAMPLE</small><strong>{{ timeline.metadata.sampleRate }} Hz</strong></div>
          </div>
        </section>
      </aside>
    </main>
  </div>
</template>
