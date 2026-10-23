<script setup lang="ts">
import { computed, ref } from 'vue'
import { useReplay } from './composables/useReplay'
import BrandMark from './components/BrandMark.vue'
import UiIcon from './components/UiIcon.vue'
import { getMapConfig } from './domain/maps'
import EventFeed from './components/EventFeed.vue'
import RadarMap from './components/RadarMap.vue'
import WinProbabilityCard from './components/WinProbabilityCard.vue'
import WinReviewChart from './components/WinReviewChart.vue'
import { sampleWinPredictions } from './data/sampleTimeline'
import {
  findTickIndex,
  formatTime,
  winPredictionAt,
  windowIndexAt,
  type PlayerEquipmentState,
} from './domain/timeline'
import type { ImportStage } from './services/api'

const {
  timeline, manifest, activeWindow, currentTime, playing, speed,
  importing, importStage, importFileName, activatingWindow, error,
  frameIndex, currentFrame, duration, togglePlayback, seek, handleFile, loadSample,
  winReview, reviewLoading, reviewError, reloadWinReview,
} = useReplay()
const showNames = ref(true)
const sampleReview = {
  schemaVersion: 1,
  rounds: [{
    roundId: sampleWinPredictions[0]!.roundId, segmentId: 0, roundNumber: 12,
    startTick: 0, liveTick: 0, endTick: 90 * 64, disposition: 'completed', winner: 'T',
    points: sampleWinPredictions.filter(point => point.tick < 90 * 64),
  }],
}
const showTrails = ref(true)
const showProjectiles = ref(true)
const showEffects = ref(true)
const showInventory = ref(true)
const theme = ref<'blue' | 'jade'>('blue')
const fileInput = ref<HTMLInputElement | null>(null)
const mapConfig = computed(() => getMapConfig(timeline.value.metadata.mapName))

const stageLabels: Record<ImportStage, string> = {
  uploading: '正在上传…',
  queued: '等待解析…',
  parsing: '正在解析…',
  predicting: '正在计算胜率…',
  chunking: '正在生成窗口…',
  loading: '正在载入首屏…',
}

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
const predictionPoints = computed(() => activeWindow.value?.winPredictions
  ?? (manifest.value ? [] : sampleWinPredictions))
// Event ticks identify attempts even when the legacy replay and model round numbers differ.
// A score rollback starts a new semantic segment at round-live, including partial demos.
const roundBoundaries = computed(() => timeline.value.events.filter(event =>
  event.type === 'round-start' || event.type === 'round-live' || event.type === 'round-end'))
const predictionWindowReady = computed(() => !manifest.value || activeWindow.value?.index === windowIndexAt(
  currentTime.value, manifest.value.windowSeconds, manifest.value.windowCount,
))
const currentWinPrediction = computed(() => {
  if (importing.value || !predictionWindowReady.value) return null
  const boundaryIndex = findTickIndex(roundBoundaries.value, currentTime.value * timeline.value.metadata.tickRate)
  return winPredictionAt(
    predictionPoints.value,
    currentTime.value,
    currentFrame.value?.round.phase ?? '',
    roundBoundaries.value[boundaryIndex]?.tick ?? 0,
  )
})
const winPredictionState = computed<'ready' | 'waiting' | 'unavailable'>(() => {
  if (manifest.value && manifest.value.winPrediction?.status !== 'ready') return 'unavailable'
  return currentWinPrediction.value ? 'ready' : 'waiting'
})
const winPredictionStatusLabel = computed(() => {
  if (winPredictionState.value === 'unavailable') return '模型当前不可用'
  if (winPredictionState.value === 'ready') return '按采样更新'
  if (!predictionWindowReady.value) return '等待回放窗口'
  const phase = currentFrame.value?.round.phase ?? ''
  if (phase !== 'live' && phase !== 'post-plant') return `${roundPhaseLabel.value}阶段不预测`
  if (manifest.value?.winPrediction?.sampleCount === 0) return '没有有效回合预测'
  return '等待当前阶段采样'
})
const winPredictionSourceLabel = computed(() => {
  if (!manifest.value) return '示例曲线 · 非模型输出'
  const metadata = manifest.value.winPrediction
  if (!metadata) return '此 Demo 没有预测数据'
  if (metadata.status !== 'ready') return metadata.error ?? '预测服务未返回模型结果'
  const model = metadata.selectedModel ?? metadata.semanticVersion
  const version = metadata.artifactSha256 ? ` · 版本 ${metadata.artifactSha256.slice(0, 12)}` : ''
  const sampling = metadata.samplingMode === 'regular-plus-events-v1'
    ? `${metadata.sampleIntervalSeconds}s 采样＋关键事件补点` : `${metadata.sampleIntervalSeconds}s 采样`
  return `${model} · ${sampling}${version}`
})

</script>

<template>
  <div class="app-frame" :data-theme="theme">
    <a class="skip-link" href="#replay-workspace">跳转到回放工作台</a>
    <header class="topbar">
      <div class="brand"><BrandMark /><strong>DEMO<span>/</span>MAP</strong><small>COUNTER-STRIKE REPLAY LAB</small></div>
      <div class="header-actions">
        <button class="icon-button theme-button" :aria-pressed="theme === 'jade'" aria-label="切换青玉配色" title="切换雾蓝 / 青玉配色" @click="theme = theme === 'blue' ? 'jade' : 'blue'"><UiIcon name="palette" /></button>
        <button class="ghost-button" @click="loadSample"><UiIcon name="reset" /><span>载入示例</span></button>
        <button class="import-button" :disabled="importing" @click="fileInput?.click()"><UiIcon name="upload" /><span>{{ importing ? importStageLabel : '导入 .DEM' }}</span></button>
        <input ref="fileInput" class="file-input" type="file" accept=".dem" aria-label="选择 Demo 文件" tabindex="-1" :disabled="importing" @change="handleFile" />
      </div>
    </header>

    <main id="replay-workspace" class="workspace">
      <div class="page-intro">
        <div><span class="eyebrow">MATCH INSIGHTS <i>/</i> {{ timeline.metadata.mapName.replace('de_', '').toUpperCase() }}</span><h1>回合复盘<span>.</span></h1><p>从地图到每一次交火，看见局势的变化。</p></div>
        <div class="file-meta"><span class="file-icon"><UiIcon name="file" /></span><div><small>{{ manifest ? '当前 Demo' : '当前示例' }}</small><strong :title="timeline.metadata.fileName">{{ timeline.metadata.fileName }}</strong></div><span class="file-extension">.DEM</span></div>
      </div>

      <div v-if="error" class="error-banner" role="alert"><strong>导入未完成</strong><span>{{ error }} 请检查文件与解析服务后重试。</span><button class="icon-button" aria-label="关闭导入错误提示" @click="error = ''">×</button></div>
      <div v-if="importing" class="import-progress" role="status"><span class="progress-spinner" /><strong>{{ importStageLabel }}</strong><span>{{ importFileName }}</span><small>大文件解析需要一些时间，请保持页面打开。</small></div>

      <section class="match-summary panel" aria-label="当前对局摘要">
        <div class="map-summary"><div class="map-thumbnail"><img v-if="mapConfig.imageUrl" :src="mapConfig.imageUrl" alt="" /><UiIcon v-else name="radar" /></div><div><small>COMPETITIVE</small><strong>{{ mapConfig.label.toUpperCase() }}</strong><span>{{ timeline.metadata.tickRate }} TICK · {{ timeline.metadata.sampleRate }} FPS</span></div></div>
        <div class="score-summary"><span class="side-t"><small>T</small><b>{{ currentFrame?.round.scoreT ?? 0 }}</b></span><i>:</i><span class="side-ct"><b>{{ currentFrame?.round.scoreCT ?? 0 }}</b><small>CT</small></span></div>
        <div class="round-summary"><small>ROUND {{ currentFrame?.round.number ?? 0 }}</small><strong>第 {{ currentFrame?.round.number ?? 0 }} 回合</strong><div><span class="phase-pill">{{ roundPhaseLabel }}</span><time>{{ formatTime(currentFrame?.round.remainingSeconds ?? 0) }}</time></div></div>
        <div class="team-summary"><div><span class="team-symbol side-t">▲</span><b>{{ teamStats.t.alive }}</b><span>T 存活</span><small>{{ teamStats.t.health }} HP</small></div><div><span class="team-symbol side-ct">●</span><b>{{ teamStats.ct.alive }}</b><span>CT 存活</span><small>{{ teamStats.ct.health }} HP</small></div></div>
        <div class="bomb-summary"><span class="bomb-symbol" aria-hidden="true">C4</span><div><small>C4 状态</small><strong>{{ bombLabel }}</strong><span>{{ bombDetail || '暂无位置信息' }}</span></div></div>
        <WinProbabilityCard :prediction="currentWinPrediction" :state="winPredictionState" :status-label="winPredictionStatusLabel" :source-label="winPredictionSourceLabel" />
      </section>

      <div class="workspace-grid">
        <section class="radar-panel panel" aria-label="雷达回放">
          <div class="panel-title-row"><div class="title-with-icon"><UiIcon name="radar" /><h2>雷达回放</h2><span class="live-pill">REPLAY</span></div><div class="legend"><span class="legend-t">T</span><span class="legend-ct">CT</span><span class="legend-utility">道具</span><span class="legend-dead">阵亡</span></div></div>
          <div class="layer-controls" role="group" aria-label="雷达图层">
            <label class="toggle-row" title="显示 Steam 昵称"><input v-model="showNames" type="checkbox" /><i aria-hidden="true" /><span>玩家名称</span></label>
            <label class="toggle-row" title="最近 9 秒路径"><input v-model="showTrails" type="checkbox" /><i aria-hidden="true" /><span>移动轨迹</span></label>
            <label class="toggle-row" title="飞行位置与已行进路线"><input v-model="showProjectiles" type="checkbox" /><i aria-hidden="true" /><span>投掷物轨迹</span></label>
            <label class="toggle-row" title="持续范围和燃烧点"><input v-model="showEffects" type="checkbox" /><i aria-hidden="true" /><span>烟雾与燃烧</span></label>
            <label class="toggle-row" title="玩家当前完整投掷物库存"><input v-model="showInventory" type="checkbox" /><i aria-hidden="true" /><span>携带道具</span></label>
          </div>
          <div class="map-stage"><div class="map-stage-status"><span :class="{ 'is-playing': playing }"><i />{{ activatingWindow ? '载入窗口…' : playing ? '正在回放' : '已暂停' }}</span><small>{{ timeline.metadata.mapName.toUpperCase() }}</small></div>
            <RadarMap :map-name="timeline.metadata.mapName" :frames="timeline.frames" :frame-index="frameIndex" :current-time="currentTime" :tick-rate="timeline.metadata.tickRate" :utility-tracks="timeline.utilityTracks" :utility-effects="timeline.utilityEffects" :player-utility-states="timeline.playerUtilityStates" :show-names="showNames" :show-trails="showTrails" :show-projectiles="showProjectiles" :show-effects="showEffects" :show-inventory="showInventory" />
          </div>
          <div class="transport">
            <button class="play-button" :aria-label="playing ? '暂停' : '播放'" @click="togglePlayback"><span v-if="playing" class="pause-icon" /><span v-else class="play-icon" /></button>
            <span class="transport-time">{{ formatTime(currentTime) }}</span>
            <input class="timeline-slider" type="range" min="0" :max="duration" step="0.05" :value="currentTime" aria-label="整场回放位置" :aria-valuetext="`${formatTime(currentTime)} / ${formatTime(duration)}`" :style="{ '--progress': `${duration ? currentTime / duration * 100 : 0}%` }" @input="seek(($event.target as HTMLInputElement).value)" />
            <span class="transport-time muted">{{ formatTime(duration) }}</span>
            <select v-model="speed" class="speed-select" aria-label="播放速度"><option :value="0.5">0.5×</option><option :value="1">1×</option><option :value="2">2×</option><option :value="4">4×</option></select>
          </div>
          <div class="radar-footer"><span>整场时间轴 <span v-if="windowLabel">· {{ windowLabel }}</span></span><span>点击右侧事件，回看关键时刻 <UiIcon name="arrow" /></span></div>
        </section>
        <WinReviewChart :review="importing ? null : manifest ? winReview : sampleReview" :events="timeline.events" :tick-rate="timeline.metadata.tickRate" :current-time="currentTime" :current-prediction="currentWinPrediction" :loading="importing || reviewLoading" :error="reviewError" :source-label="winPredictionSourceLabel" :model-unavailable="winPredictionState === 'unavailable'" @seek="seek" @retry="reloadWinReview" />
      </div>

      <div class="details-grid">
        <section class="intel-panel panel" aria-label="对局数据">
          <div class="panel-title-row"><div class="title-with-icon"><UiIcon name="layers" /><h2>经济与装备</h2></div><span class="section-caption">当前回放帧</span></div>
          <div class="table-scroll" role="region" aria-label="双方经济与装备表，可横向滚动" tabindex="0"><table class="economy-table"><thead><tr><th scope="col">队伍</th><th scope="col">剩余经济</th><th scope="col">装备价值</th><th scope="col">道具</th><th scope="col">护甲 / 头盔</th><th scope="col">拆弹器</th></tr></thead><tbody>
            <tr><th scope="row"><span class="team-badge team-t">T</span></th><td>${{ economyStats.t.money.toLocaleString() }}</td><td>${{ economyStats.t.equipment.toLocaleString() }}</td><td>{{ economyStats.t.grenades }}</td><td>{{ economyStats.t.armor }} / {{ economyStats.t.helmets }}</td><td>—</td></tr>
            <tr><th scope="row"><span class="team-badge team-ct">CT</span></th><td>${{ economyStats.ct.money.toLocaleString() }}</td><td>${{ economyStats.ct.equipment.toLocaleString() }}</td><td>{{ economyStats.ct.grenades }}</td><td>{{ economyStats.ct.armor }} / {{ economyStats.ct.helmets }}</td><td>{{ economyStats.ct.defusers }}</td></tr>
          </tbody></table></div>
          <div class="round-facts"><div><small>连续失利</small><strong><span class="side-t">T {{ currentFrame?.round.consecutiveLossesT ?? 0 }}</span><i>/</i><span class="side-ct">CT {{ currentFrame?.round.consecutiveLossesCT ?? 0 }}</span></strong><span>可用于估计下一回合奖励</span></div><div class="zone-list"><small>区域人数 <span>T / CT</span></small><div><span v-for="zone in occupiedZones" :key="zone.region">{{ zone.region }} <b>{{ zone.tAlive }}</b><em>/</em><i>{{ zone.ctAlive }}</i></span><span v-if="!occupiedZones.length" class="zone-empty">当前没有区域人数数据</span></div></div></div>
        </section>
        <EventFeed :events="timeline.events" :current-time="currentTime" @seek="seek" />
      </div>

      <details class="diagnostics"><summary><UiIcon name="info" /><span>回放数据与诊断</span><small>Tick {{ currentFrame?.tick ?? 0 }} · {{ timeline.metadata.sampleRate }} Hz</small><span class="details-chevron">⌄</span></summary><div class="diagnostics-body"><div class="metric-grid"><div><small>DEMO TICK</small><strong>{{ currentFrame?.tick ?? 0 }}</strong></div><div><small>FRAME</small><strong>{{ globalFrameNumber }} / {{ totalFrameCount }}</strong></div><div><small>PLAYERS</small><strong>{{ currentFrame?.players.length ?? 0 }}</strong></div><div><small>SAMPLE</small><strong>{{ timeline.metadata.sampleRate }} Hz</strong></div></div><p>数据来源：DemoFile.Game.Cs 0.44.1。世界坐标保留在时间线中，渲染时转换为 overview 坐标。</p><p>{{ winPredictionSourceLabel }}</p></div></details>
      <footer class="page-footer"><span>DEMO / MAP <i>·</i> REPLAY LAB</span><p>胜率基于双方 Demo 状态，不代表选手当时掌握的信息。</p></footer>
    </main>
  </div>
</template>
