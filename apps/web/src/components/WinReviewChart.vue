<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import UiIcon from './UiIcon.vue'
import { formatTime, winPredictionAt, type TimelineEvent, type WinPredictionPoint } from '../domain/timeline'
import { comparisonStatusLabel, eventReviewTime, predictionQualityLabel, reviewRoundAt, reviewSegments, type WinReview } from '../domain/winReview'

const props = defineProps<{
  review: WinReview | null
  events: TimelineEvent[]
  tickRate: number
  currentTime: number
  currentPrediction: WinPredictionPoint | null
  loading: boolean
  error: string
  sourceLabel: string
  modelUnavailable: boolean
}>()
const emit = defineEmits<{ seek: [time: number]; retry: [] }>()
const hoveredTime = ref<number | null>(null)
const selectedComparisonId = ref<string | null>(null)
const round = computed(() => reviewRoundAt(props.review?.rounds ?? [], props.currentTime * props.tickRate))
watch(() => round.value?.roundId, () => { hoveredTime.value = null; selectedComparisonId.value = null })
const comparison = computed(() => round.value?.comparisons?.find(item => item.id === selectedComparisonId.value) ?? null)
const from = computed(() => (round.value?.liveTick ?? 0) / props.tickRate)
const to = computed(() => (round.value?.endTick ?? 0) / props.tickRate)
const width = 640, left = 44, right = 620, top = 18, bottom = 138
const x = (time: number) => left + Math.max(0, Math.min(1, (time - from.value) / Math.max(.001, to.value - from.value))) * (right - left)
const y = (probability: number) => bottom - probability * (bottom - top)
const roundEvents = computed(() => props.events.filter(event => round.value &&
  event.tick >= round.value.liveTick && event.tick <= round.value.endTick &&
  ['kill', 'bomb-planted', 'bomb-defuse-start', 'bomb-defuse-abort', 'bomb-defused', 'bomb-exploded'].includes(event.type)))
const segments = computed(() => round.value ? reviewSegments(round.value, props.tickRate, roundEvents.value) : [])
const curve = computed(() => segments.value.map(segment =>
  `M${x(segment.from)},${y(segment.point.tWin)} H${x(segment.to)}` +
  (segment.connect === null ? '' : ` V${y(segment.connect)}`)).join(' '))
const incomplete = computed(() => segments.value.filter(segment => !segment.point.quality ||
  segment.point.quality.equipmentMissing || segment.point.quality.positionMissing))
const selectedTime = computed(() => hoveredTime.value ?? props.currentTime)
const inspected = computed(() => {
  if (!round.value) return null
  if (hoveredTime.value === null) {
    const current = props.currentPrediction
    return current ? round.value.points.find(point => point.tick === current.tick &&
      point.roundId === current.roundId && point.segmentId === current.segmentId) ?? null : null
  }
  const segment = segments.value.find(item => item.from <= selectedTime.value && selectedTime.value < item.to)
  return segment ? winPredictionAt(round.value.points, selectedTime.value, segment.point.phase, round.value.liveTick) : null
})
const cursorVisible = computed(() => !!round.value && props.currentTime >= from.value && props.currentTime <= to.value)
const currentDot = computed(() => props.currentPrediction && round.value &&
  props.currentPrediction.roundId === round.value.roundId && props.currentPrediction.segmentId === round.value.segmentId
  ? props.currentPrediction : null)
const outcome = computed(() => round.value && selectedTime.value >= to.value
  ? round.value.winner ? `回合已结束 · ${round.value.winner} 获胜` : '该回合未完整结算或已作废'
  : '')
const emptyLabel = computed(() => props.modelUnavailable ? '模型当前不可用，回放与事件仍可查看'
  : round.value?.disposition !== 'completed' ? '该回合未完整结算或已作废，没有模型曲线'
  : '本回合没有有效预测点')
const timeLabel = (time: number) => `开打后 ${Math.max(0, time - from.value).toFixed(1)} 秒`
function pointerTime(event: MouseEvent) {
  const bounds = (event.currentTarget as SVGSVGElement).getBoundingClientRect()
  const fraction = ((event.clientX - bounds.left) / bounds.width * width - left) / (right - left)
  return from.value + Math.max(0, Math.min(1, fraction)) * (to.value - from.value)
}
function chooseRound(event: Event) {
  const selected = props.review?.rounds.find(item => item.roundId === (event.target as HTMLSelectElement).value)
  if (selected) emit('seek', selected.liveTick / props.tickRate)
}
function seekEvent(event: TimelineEvent) {
  hoveredTime.value = null
  const group = round.value?.comparisons?.find(item => item.events.some(e =>
    e.tick === event.tick && e.type === event.type && e.title === event.title))
  selectedComparisonId.value = group?.id ?? null
  if (round.value) emit('seek', eventReviewTime(group?.events[0] ?? event, round.value, props.tickRate))
}
function seekComparisonPoint(point: WinPredictionPoint) {
  hoveredTime.value = null
  emit('seek', point.timeSeconds)
}
const percent = (value: number) => `${(value * 100).toFixed(2)}%`
const deltaLabel = (value: number) => `${value > 0 ? '+' : ''}${value.toFixed(2)} 个百分点`
function seekSlider(event: Event) {
  hoveredTime.value = null
  emit('seek', Number((event.target as HTMLInputElement).value))
}
</script>

<template>
  <section class="win-review" aria-label="回合胜率复盘">
    <section class="review-chart panel">
    <div class="review-heading">
      <div class="title-with-icon"><UiIcon name="chart" /><h2>回合胜率走势</h2></div>
      <select v-if="review?.rounds.length" :value="round?.roundId ?? ''" aria-label="选择复盘回合" @change="chooseRound">
        <option disabled value="">选择回合</option>
        <option v-for="item in review.rounds" :key="item.roundId" :value="item.roundId">
          第 {{ item.roundNumber ?? '?' }} 回合 · {{ formatTime(item.liveTick / tickRate) }}{{ item.disposition === 'completed' ? '' : ' · 未完整/作废' }}
        </option>
      </select>
    </div>
    <p class="review-source">{{ sourceLabel }}</p>
    <p v-if="loading" class="review-empty" role="status">正在载入整回合曲线…</p>
    <div v-else-if="error" class="review-empty" role="status">{{ error }} <button @click="emit('retry')">重试</button></div>
    <p v-else-if="!round" class="review-empty">{{ review ? '此处没有可复盘回合，请选择回合或继续播放。' : '暂无复盘曲线数据。' }}</p>
    <template v-else>
      <div class="review-legend"><span><i class="t-key" />T 方胜率 · CT 为互补值</span><span>完整回合走势，含游标之后的内容</span></div>
      <svg :viewBox="`0 0 ${width} 188`" class="review-plot" role="img" aria-label="T 方回合胜率阶梯图；可点击定位，或使用下方回合进度滑块"
        @click="emit('seek', pointerTime($event))" @mousemove="hoveredTime = pointerTime($event)" @mouseleave="hoveredTime = null">
        <rect :x="left" :y="top" :width="right - left" :height="bottom - top" class="plot-bg" />
        <g v-for="value in [1, .5, 0]" :key="value" class="plot-grid">
          <line :x1="left" :x2="right" :y1="y(value)" :y2="y(value)" />
          <text :x="left - 8" :y="y(value) + 4" text-anchor="end">{{ value * 100 }}%</text>
        </g>
        <rect v-for="segment in incomplete" :key="segment.point.tick" :x="x(segment.from)" :y="top"
          :width="Math.max(0, x(segment.to) - x(segment.from))" :height="bottom - top" class="quality-band" />
        <path :d="curve" class="win-curve" pathLength="1" />
        <line v-if="cursorVisible" :x1="x(currentTime)" :x2="x(currentTime)" :y1="top" :y2="bottom" class="playhead" />
        <circle v-if="currentDot && cursorVisible" :cx="x(currentTime)" :cy="y(currentDot.tWin)" r="4" class="current-dot" />
        <line v-if="hoveredTime !== null" :x1="x(hoveredTime)" :x2="x(hoveredTime)" :y1="top" :y2="bottom" class="inspect-line" />
        <g v-for="(event, index) in roundEvents" :key="`${event.tick}-${index}`" class="plot-event" @click.stop="seekEvent(event)">
          <title>{{ event.title }} · {{ timeLabel(event.timeSeconds) }} · 点击回看前 3 秒</title>
          <line :x1="x(event.timeSeconds)" :x2="x(event.timeSeconds)" :y1="top" :y2="bottom" />
          <circle :cx="x(event.timeSeconds)" cy="173" r="7" :class="event.type === 'kill' ? 'kill-mark' : 'bomb-mark'" />
        </g>
        <g class="plot-times">
          <text :x="left" y="155">开打 0 秒</text>
          <text :x="x((from + to) / 2)" y="155" text-anchor="middle">{{ Math.round((to - from) / 2) }} 秒</text>
          <text :x="right" y="155" text-anchor="end">{{ (to - from).toFixed(1) }} 秒</text>
        </g>
      </svg>
      <input class="review-slider" type="range" :min="from" :max="to" step="0.05" :value="Math.max(from, Math.min(to, currentTime))"
        aria-label="回合回放位置" :aria-valuetext="timeLabel(currentTime)" @input="seekSlider" />
      <div class="review-readout">
        <strong>{{ hoveredTime === null ? '回放位置' : '查看位置' }} · {{ timeLabel(selectedTime) }}</strong>
        <span v-if="inspected">T {{ Math.round(inspected.tWin * 100) }}% / CT {{ 100 - Math.round(inspected.tWin * 100) }}% · 采样于 {{ formatTime(inspected.timeSeconds) }}（{{ inspected.timeSeconds.toFixed(3) }} 秒）</span>
        <span v-else>{{ outcome || (selectedTime < from ? '冻结期，尚未开打' : '此时没有有效预测') }}</span>
        <small :class="{ 'quality-warning': inspected && (!inspected.quality || inspected.quality.equipmentMissing || inspected.quality.positionMissing) }">{{ predictionQualityLabel(inspected) }}</small>
      </div>
      <p v-if="!round.points.length" class="review-empty" role="status">{{ emptyLabel }}</p>
      <p class="review-note">点击曲线定位。断线表示无有效预测，淡黄色底表示数据不完整或未知。</p>
    </template>
    </section>

    <section class="comparison-panel panel" aria-label="关键事件分析">
      <div class="review-heading"><div class="title-with-icon"><UiIcon name="radar" /><h2>事件前后对比</h2></div><span class="section-caption">关键时刻 · 局势变化</span></div>
      <template v-if="round && !loading && !error">
      <div v-if="roundEvents.length" class="review-events" aria-label="本回合关键事件，点击回看前 3 秒">
        <button v-for="(event, index) in roundEvents" :key="`${event.tick}-${index}`" :title="event.detail ?? undefined"
          :aria-pressed="comparison?.events.some(item => item.tick === event.tick && item.type === event.type && item.title === event.title) ?? false"
          :aria-label="`${event.title}，${timeLabel(event.timeSeconds)}，回看前 3 秒`" @click="seekEvent(event)">
          <i :class="event.type === 'kill' ? 'kill-key' : 'bomb-key'" />{{ event.title }}<small>{{ timeLabel(event.timeSeconds) }}</small>
        </button>
      </div>
      <div v-if="comparison" class="event-comparison" aria-label="事件前后胜率对比" aria-live="polite">
        <strong>{{ comparison.events.length > 1 ? `这段交火／操作（${comparison.events.length} 个事件）` : comparison.events[0]?.title }} · 前后对比</strong>
        <p class="comparison-events">{{ comparison.events.map(event => `${timeLabel(event.timeSeconds)} ${event.title}`).join(' · ') }}</p>
        <template v-if="comparison.status === 'ready' && comparison.before && comparison.after && comparison.tDeltaPercentagePoints !== null">
          <div class="comparison-values">
            <div class="comparison-side side-t"><span class="comparison-team">T 方</span><div><span><small>事件前</small><strong>{{ percent(comparison.before.tWin) }}</strong></span><UiIcon name="arrow" /><span><small>事件后</small><strong>{{ percent(comparison.after.tWin) }}</strong></span></div><b>{{ deltaLabel(comparison.tDeltaPercentagePoints) }}</b></div>
            <div class="comparison-side side-ct"><span class="comparison-team">CT 方</span><div><span><small>事件前</small><strong>{{ percent(comparison.before.ctWin) }}</strong></span><UiIcon name="arrow" /><span><small>事件后</small><strong>{{ percent(comparison.after.ctWin) }}</strong></span></div><b>{{ deltaLabel(-comparison.tDeltaPercentagePoints) }}</b></div>
          </div>
          <p>这是这段局面的整体变化，包含期间的时间、走位与交火，不等于某位选手的个人贡献。</p>
          <p>前后数值按标出的时刻补算，可能与上一秒的曲线值不同。</p>
        </template>
        <p v-else class="comparison-unavailable">{{ comparisonStatusLabel(comparison.status) }}</p>
        <p v-if="comparison.status === 'round-ended' && round.winner">回合结果：{{ round.winner }} 获胜</p>
        <div class="comparison-actions">
          <button v-if="comparison.before" @click="seekComparisonPoint(comparison.before)">查看事件前 · {{ timeLabel(comparison.before.timeSeconds) }}（tick {{ comparison.before.tick }}）</button>
          <button v-if="comparison.after && comparison.status !== 'round-ended'" @click="seekComparisonPoint(comparison.after)">查看事件后 · {{ timeLabel(comparison.after.timeSeconds) }}（tick {{ comparison.after.tick }}）</button>
        </div>
      </div>
      <p v-else-if="round.comparisons?.length" class="review-note">点击关键事件查看前后变化；相邻间隔不超过 1 秒的事件合并为一段。</p>
      <p v-else-if="roundEvents.length" class="review-note">当前数据没有事件前后对比；真实 Demo 请重新导入以生成补点。</p>
      <div v-if="!comparison" class="comparison-placeholder"><UiIcon name="chart" /><strong>{{ roundEvents.length ? '从一次交火，读懂局势变化' : '本回合暂无关键事件' }}</strong><span>{{ roundEvents.length ? '选择上方事件回看前 3 秒，查看可用的前后采样。' : '继续播放，或选择其他回合查看。' }}</span></div>
      <p class="review-note">胜率基于双方 Demo 状态，不代表选手当时掌握的信息。</p>
    </template>
    <p v-else class="review-empty">{{ loading ? '正在准备本回合事件…' : '选择可用回合后，在这里查看关键事件与前后变化。' }}</p>
    </section>
  </section>
</template>

<style scoped>
.win-review { min-width: 0; display: grid; gap: 20px; animation: enter .85s .18s var(--ease) both; }
.review-chart, .comparison-panel { padding: 23px; min-width: 0; }
.review-heading, .review-legend { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
.review-heading select, button { border: 1px solid var(--line); border-radius: 7px; background: var(--surface); padding: 7px 9px; cursor: pointer; font: inherit; color: var(--ink); }
.review-heading select { max-width: 100%; font-size: 10px; }
.review-source { color: var(--muted); font-size: 9px; line-height: 1.6; overflow-wrap: anywhere; margin-top: 13px; }
.review-note { color: var(--muted); font-size: 10px; line-height: 1.8; overflow-wrap: anywhere; margin: 13px 0 0; }
.review-legend { margin-top: 17px; font-size: 9px; color: var(--muted); gap: 8px; }
.review-legend span { display: inline-flex; align-items: center; gap: 6px; }
.t-key, .kill-key, .bomb-key { display: inline-block; width: 6px; height: 6px; border-radius: 50%; background: var(--t-bright); flex: 0 0 auto; }
.t-key { width: 13px; height: 3px; }.kill-key { background: var(--danger); }.bomb-key { background: var(--t-bright); }
.review-plot { display: block; width: 100%; margin: 14px 0 4px; cursor: crosshair; overflow: visible; }
.plot-bg { fill: var(--surface); }.plot-grid line { stroke: var(--line); stroke-dasharray: 3 4; }
.plot-grid text, .plot-times text { fill: var(--muted); font: 10px var(--mono); }
.win-curve { fill: none; stroke: var(--t-bright); stroke-width: 2; vector-effect: non-scaling-stroke; stroke-dasharray: 1; stroke-dashoffset: 0; animation: trace 1.3s .2s var(--ease) both; }
@keyframes trace { from { stroke-dashoffset: 1; } to { stroke-dashoffset: 0; } }
.quality-band { fill: #f8d978; opacity: .18; }
.playhead { stroke: var(--brand); stroke-width: 1.25; stroke-dasharray: 3 3; }.current-dot { fill: var(--t-bright); stroke: #fff; stroke-width: 2; filter: drop-shadow(0 0 3px #de9a3255); }
.inspect-line { stroke: var(--muted); stroke-dasharray: 3 3; }
.plot-event { cursor: pointer; }.plot-event line { stroke: var(--muted); opacity: .12; }.kill-mark { fill: var(--danger); }.bomb-mark { fill: var(--t-bright); }
.review-slider { width: 100%; margin: 2px 0 16px; accent-color: var(--brand); cursor: pointer; height: 16px; }
.review-readout { display: grid; gap: 6px; padding: 12px 14px; background: #f5f8fc; border-left: 2px solid var(--brand); border-radius: 0 7px 7px 0; font-size: 11px; font-variant-numeric: tabular-nums; }
.review-readout strong { font-weight: 500; }.review-readout span { line-height: 1.6; }.review-readout small { color: var(--muted); font-size: 10px; }.review-readout .quality-warning { color: #8c671c; }
.review-empty { color: var(--muted); font-size: 12px; padding: 28px 0; line-height: 1.8; min-height: 100px; }
.review-chart > .review-empty { min-height: 300px; display: flex; align-items: center; justify-content: center; flex-direction: column; gap: 12px; background: var(--bg); border-radius: 10px; margin-top: 16px; text-align: center; padding: 28px; }
.review-events { display: flex; gap: 7px; flex-wrap: wrap; margin: 18px -4px 0; max-height: 148px; overflow: auto; padding: 4px; }
.review-events button { display: flex; align-items: center; gap: 7px; font-size: 10px; max-width: 100%; flex-wrap: wrap; text-align: left; line-height: 1.5; }
.review-events button small { color: var(--muted); font-size: 9px; }.review-events button:hover { background: var(--brand-soft); border-color: var(--brand); }.review-events button[aria-pressed="true"] { color: var(--brand); background: var(--brand-soft); border-color: var(--brand); box-shadow: 0 2px 6px rgba(var(--brand-rgb), .07); }
.event-comparison { margin-top: 16px; font-size: 12px; line-height: 1.7; animation: enter .35s var(--ease) both; }
.event-comparison > strong { font-weight: 600; }.event-comparison p { margin: 11px 0; color: var(--muted); font-size: 10px; overflow-wrap: anywhere; }
.comparison-values { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 12px; margin: 16px 0; }
.comparison-side { padding: 14px; border-radius: 10px; background: var(--t-soft); }.comparison-side.side-ct { background: var(--ct-soft); }
.comparison-team { font: 600 11px var(--mono); }.comparison-side > div { display: flex; justify-content: space-between; gap: 6px; align-items: center; margin-top: 10px; }.comparison-side > div > .ui-icon { width: 16px; color: var(--muted); }
.comparison-side small { display: block; color: var(--muted); font-size: 9px; }.comparison-side strong { display: block; margin-top: 4px; font: 600 clamp(17px, 1.65vw, 27px) var(--mono); font-variant-numeric: tabular-nums; }.comparison-side b { display: block; margin-top: 13px; font-size: 10px; font-weight: 500; }
.event-comparison .comparison-unavailable { padding: 14px; border-radius: 8px; color: #8c671c; background: var(--t-soft); }
.comparison-actions { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; margin-top: 15px; }.comparison-actions button { font-size: 10px; text-align: left; line-height: 1.6; background: var(--brand-soft); border-color: transparent; color: var(--brand); }.comparison-actions button:hover { border-color: var(--brand); }
.comparison-placeholder { display: grid; justify-items: center; gap: 9px; margin-top: 18px; padding: 26px 15px; background: linear-gradient(135deg, var(--brand-soft), #f8fafc); border: 1px dashed var(--line); border-radius: 10px; text-align: center; }
.comparison-placeholder > .ui-icon { color: var(--brand); margin-bottom: 3px; width: 25px; height: 25px; }.comparison-placeholder strong { font-size: 13px; font-weight: 500; }.comparison-placeholder span { color: var(--muted); font-size: 11px; line-height: 1.7; }
@media (max-width: 1080px) and (min-width: 701px) { .win-review { grid-template-columns: 1fr 1fr; align-items: start; }.review-chart, .comparison-panel { padding: 20px; }.review-heading h2 { font-size: 16px; }.review-legend { font-size: 8px; }.comparison-values { grid-template-columns: 1fr; }.comparison-side strong { font-size: 25px; }.comparison-actions { grid-template-columns: 1fr; } }
@media (max-width: 480px) { .review-chart, .comparison-panel { padding: 20px 16px; }.review-heading { gap: 10px; }.review-heading select { font-size: 9px; max-width: 145px; }.section-caption { font-size: 9px; }.review-readout { font-size: 10px; }.comparison-values { grid-template-columns: 1fr; gap: 8px; }.comparison-side { padding: 14px; }.comparison-side strong { font-size: 24px; }.comparison-actions { grid-template-columns: 1fr; }.comparison-placeholder { padding: 23px 12px; } }
</style>
