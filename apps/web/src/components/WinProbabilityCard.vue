<script setup lang="ts">
import { computed } from 'vue'
import { formatTime, type WinPredictionPoint } from '../domain/timeline'

const props = defineProps<{
  prediction: WinPredictionPoint | null
  state: 'ready' | 'waiting' | 'unavailable'
  statusLabel: string
  sourceLabel: string
}>()

const tPercent = computed(() => Math.round((props.prediction?.tWin ?? 0) * 100))
const ctPercent = computed(() => 100 - tPercent.value)
const sampleTimeLabel = computed(() => props.prediction
  ? `采样于 ${formatTime(props.prediction.timeSeconds)} · ${props.prediction.timeSeconds.toFixed(3)} 秒`
  : '')
const accessibleLabel = computed(() => props.prediction
  ? `第 ${props.prediction.roundNumber} 回合实时胜率，T 方 ${tPercent.value}%，CT 方 ${ctPercent.value}%，${sampleTimeLabel.value}`
  : props.statusLabel)
</script>

<template>
  <div class="win-card" :class="`state-${state}`">
    <div class="win-card-heading">
      <div>
        <small>ROUND WIN PROBABILITY</small>
        <strong>实时胜率</strong>
      </div>
      <span class="prediction-status"><i />{{ statusLabel }}</span>
    </div>

    <div
      v-if="prediction"
      class="probability-body"
      role="img"
      :aria-label="accessibleLabel"
    >
      <div class="probability-values">
        <span class="side-t"><small>T</small><strong>{{ tPercent }}%</strong></span>
        <span class="side-ct"><strong>{{ ctPercent }}%</strong><small>CT</small></span>
      </div>
      <div class="probability-track" aria-hidden="true">
        <span class="probability-t" :style="{ width: `${tPercent}%` }" />
        <span class="probability-ct" :style="{ width: `${ctPercent}%` }" />
      </div>
      <div class="prediction-meta">
        <span>ROUND {{ prediction.roundNumber }}</span>
        <span :title="sourceLabel">{{ sourceLabel }}</span>
      </div>
      <div class="prediction-meta">{{ sampleTimeLabel }}</div>
    </div>

    <div v-else class="prediction-empty" role="status">
      <strong>{{ statusLabel }}</strong>
      <span :title="sourceLabel">{{ sourceLabel }}</span>
    </div>
  </div>
</template>

<style scoped>
.win-card { min-width: 0; }
.win-card-heading, .probability-values, .prediction-meta { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.win-card-heading > div { display: grid; gap: 2px; }
.win-card-heading small { display: none; }
.win-card-heading strong { color: var(--muted); font-size: 10px; font-weight: 500; }
.prediction-status { display: flex; align-items: center; gap: 4px; color: var(--muted); font-size: 8px; white-space: nowrap; }
.prediction-status i { width: 4px; height: 4px; flex: 0 0 auto; border-radius: 50%; background: var(--muted); }
.state-ready .prediction-status i { background: var(--success); }.state-unavailable .prediction-status i { background: var(--danger); }
.probability-body { margin-top: 11px; }
.probability-values span { display: flex; align-items: baseline; gap: 4px; }
.probability-values small { font-size: 10px; font-weight: 500; }
.probability-values strong { font: 650 25px/1 var(--mono); font-variant-numeric: tabular-nums; }
.side-t { color: var(--t); }.side-ct { color: var(--ct); }
.probability-track { display: flex; height: 4px; gap: 2px; margin-top: 10px; overflow: hidden; border-radius: 4px; background: var(--line); }
.probability-track span { min-width: 0; }.probability-t { background: var(--t-bright); }.probability-ct { background: var(--ct-bright); }
.prediction-meta { margin-top: 7px; color: var(--muted); font-size: 8px; line-height: 1.5; font-variant-numeric: tabular-nums; }
.prediction-meta > span:first-child { display: none; }.prediction-meta > span:last-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.prediction-empty { display: grid; gap: 5px; min-height: 65px; margin-top: 10px; padding: 9px; border: 1px dashed var(--line); border-radius: 7px; background: var(--bg); }
.prediction-empty strong { color: var(--muted); font-size: 11px; font-weight: 500; }.prediction-empty span { color: var(--muted); font-size: 9px; overflow-wrap: anywhere; }.state-unavailable .prediction-empty strong { color: var(--danger); }
@media (max-width: 1280px) { .win-card-heading { flex-wrap: wrap; gap: 3px; }.probability-values strong { font-size: 23px; } }
</style>
