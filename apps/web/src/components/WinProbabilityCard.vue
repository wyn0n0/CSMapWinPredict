<script setup lang="ts">
import { computed } from 'vue'
import type { WinPredictionPoint } from '../domain/timeline'

const props = defineProps<{
  prediction: WinPredictionPoint | null
  state: 'ready' | 'waiting' | 'unavailable'
  statusLabel: string
  sourceLabel: string
}>()

const tPercent = computed(() => Math.round((props.prediction?.tWin ?? 0) * 100))
const ctPercent = computed(() => 100 - tPercent.value)
const accessibleLabel = computed(() => props.prediction
  ? `第 ${props.prediction.roundNumber} 回合实时胜率，T 方 ${tPercent.value}%，CT 方 ${ctPercent.value}%`
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
    </div>

    <div v-else class="prediction-empty" role="status">
      <strong>{{ statusLabel }}</strong>
      <span :title="sourceLabel">{{ sourceLabel }}</span>
    </div>
  </div>
</template>

<style scoped>
.win-card {
  margin-top: 18px;
  padding-top: 16px;
  border-top: 1px solid rgba(255, 255, 255, .08);
}

.win-card-heading,
.probability-values,
.prediction-meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.win-card-heading > div {
  display: grid;
  gap: 2px;
}

.win-card-heading small {
  color: #6f7984;
  font-size: 8px;
  font-weight: 800;
  letter-spacing: .13em;
}

.win-card-heading strong {
  color: #e9edf1;
  font-size: 14px;
}

.prediction-status {
  display: flex;
  align-items: center;
  gap: 5px;
  max-width: 108px;
  color: #aab3bc;
  font-size: 9px;
  white-space: nowrap;
}

.prediction-status i {
  width: 6px;
  height: 6px;
  flex: 0 0 auto;
  border-radius: 50%;
  background: #69727c;
}

.state-ready .prediction-status i {
  background: var(--accent);
  box-shadow: 0 0 8px rgba(214, 255, 63, .55);
}

.state-unavailable .prediction-status i {
  background: #e06060;
}

.probability-body {
  margin-top: 14px;
}

.probability-values span {
  display: flex;
  align-items: baseline;
  gap: 6px;
}

.probability-values small {
  font-size: 10px;
  font-weight: 900;
}

.probability-values strong {
  font-family: "Roboto Mono", "SFMono-Regular", Consolas, monospace;
  font-size: 22px;
  line-height: 1;
}

.side-t { color: var(--t); }
.side-ct { color: var(--ct); }

.probability-track {
  display: flex;
  height: 7px;
  margin-top: 9px;
  overflow: hidden;
  border-radius: 2px;
  background: #242a30;
}

.probability-track span {
  min-width: 0;
  transition: width 180ms linear;
}

.probability-t { background: var(--t); }
.probability-ct { background: var(--ct); }

.prediction-meta {
  margin-top: 7px;
  color: #66717c;
  font-size: 8px;
  font-weight: 700;
  letter-spacing: .06em;
}

.prediction-meta span:last-child,
.prediction-empty span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.prediction-meta span:last-child {
  max-width: 145px;
  text-align: right;
}

.prediction-empty {
  display: grid;
  gap: 4px;
  min-height: 58px;
  margin-top: 12px;
  padding: 12px;
  place-content: center stretch;
  border: 1px dashed rgba(255, 255, 255, .1);
  background: rgba(255, 255, 255, .02);
  text-align: center;
}

.prediction-empty strong {
  color: #9da6af;
  font-size: 11px;
}

.prediction-empty span {
  color: #626c76;
  font-size: 9px;
}

.state-unavailable .prediction-empty strong {
  color: #c98080;
}
</style>
