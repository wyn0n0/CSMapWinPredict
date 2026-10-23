<script setup lang="ts">
import { computed } from 'vue'
import UiIcon from './UiIcon.vue'
import { formatTime, type TimelineEvent } from '../domain/timeline'

const props = defineProps<{ events: TimelineEvent[]; currentTime: number }>()
const emit = defineEmits<{ seek: [time: number] }>()

const orderedEvents = computed(() => [...props.events].sort((a, b) => b.timeSeconds - a.timeSeconds))
</script>

<template>
  <section class="event-panel panel">
    <div class="panel-title-row">
      <div class="title-with-icon">
        <UiIcon name="chart" />
        <h2>关键事件</h2>
      </div>
      <span class="event-count">{{ events.length }}</span>
    </div>
    <div class="event-list">
      <button
        v-for="event in orderedEvents"
        :key="`${event.tick}-${event.type}-${event.title}`"
        class="event-item"
        :class="[{ future: event.timeSeconds > currentTime }, `event-${event.type}`]"
        @click="emit('seek', event.timeSeconds)"
      >
        <span class="event-dot" />
        <span class="event-copy">
          <strong>{{ event.title }}</strong>
          <small>{{ event.detail || event.type }}</small>
        </span>
        <time>{{ formatTime(event.timeSeconds) }}{{ event.timeSeconds > currentTime ? ' · 后续' : '' }}</time>
      </button>
    </div>
    <p v-if="!events.length" class="event-empty">当前 Demo 没有事件记录，仍可使用时间轴回放。</p>
  </section>
</template>

