<script setup lang="ts">
import { computed } from 'vue'
import { formatTime, type TimelineEvent } from '../domain/timeline'

const props = defineProps<{ events: TimelineEvent[]; currentTime: number }>()
const emit = defineEmits<{ seek: [time: number] }>()

const orderedEvents = computed(() => [...props.events].sort((a, b) => b.timeSeconds - a.timeSeconds))
</script>

<template>
  <section class="event-panel panel">
    <div class="panel-title-row">
      <div>
        <span class="eyebrow">MATCH LOG</span>
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
        <time>{{ formatTime(event.timeSeconds) }}</time>
      </button>
    </div>
  </section>
</template>

