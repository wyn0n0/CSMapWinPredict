<script setup lang="ts">
import { computed } from 'vue'
import { getMapConfig, worldToMap } from '../domain/maps'
import {
  findTickIndex,
  utilityPointAt,
  type DemoFrame,
  type PlayerUtilityState,
  type UtilityEffectTrack,
  type UtilityTrack,
  type UtilityType,
} from '../domain/timeline'

const props = defineProps<{
  mapName: string
  frames: DemoFrame[]
  frameIndex: number
  currentTime: number
  tickRate: number
  utilityTracks: UtilityTrack[]
  utilityEffects: UtilityEffectTrack[]
  playerUtilityStates: PlayerUtilityState[]
  showNames: boolean
  showTrails: boolean
  showProjectiles: boolean
  showEffects: boolean
  showInventory: boolean
}>()

const map = computed(() => getMapConfig(props.mapName))
const frame = computed(() => props.frames[props.frameIndex] ?? { players: [] })
const currentTick = computed(() => Math.round(props.currentTime * props.tickRate))

const utilityStatesByPlayer = computed(() => {
  const grouped = new Map<string, PlayerUtilityState[]>()
  for (const state of props.playerUtilityStates) {
    const states = grouped.get(state.playerId) ?? []
    states.push(state)
    grouped.set(state.playerId, states)
  }
  for (const states of grouped.values()) states.sort((a, b) => a.tick - b.tick)
  return grouped
})

const players = computed(() => frame.value.players.map(player => ({
  ...player,
  point: worldToMap(player, map.value),
  utilities: playerUtilitiesAt(player.id),
})))

const trails = computed(() => {
  if (!props.showTrails) return []
  const start = Math.max(0, props.frameIndex - 36)
  return players.value.map(player => {
    const points = props.frames.slice(start, props.frameIndex + 1)
      .map(item => item.players.find(candidate => candidate.id === player.id))
      .filter(candidate => candidate?.alive)
      .map(candidate => worldToMap(candidate!, map.value))
      .map(point => `${point.x},${point.y}`)
      .join(' ')
    return { id: player.id, team: player.team, points }
  })
})

const activeProjectiles = computed(() => {
  if (!props.showProjectiles) return []
  const tick = currentTick.value
  return props.utilityTracks.flatMap(track => {
    if (tick < track.startTick || tick >= track.endTick) return []
    const position = utilityPointAt(track.trajectory, tick)
    if (!position) return []

    const travelled = track.trajectory.filter(point => point.tick <= tick)
    const projected = [...travelled, position]
      .map(point => worldToMap(point, map.value))
      .map(point => `${point.x},${point.y}`)
      .join(' ')
    return [{
      ...track,
      point: worldToMap(position, map.value),
      points: projected,
      label: utilityLabel(track.type),
    }]
  })
})

const activeEffects = computed(() => {
  if (!props.showEffects) return []
  const tick = currentTick.value
  return props.utilityEffects.flatMap(effect => {
    if (tick < effect.startTick || tick >= effect.endTick) return []
    const sampleIndex = findTickIndex(effect.samples, tick)
    if (sampleIndex < 0) return []
    const sample = effect.samples[sampleIndex]
    const area = (sample.area.length > 0 ? sample.area : [sample]).map(point => worldToMap(point, map.value))
    return [{
      ...effect,
      point: worldToMap(sample, map.value),
      radius: sample.radius / map.value.scale,
      area,
    }]
  })
})

function playerUtilitiesAt(playerId: string) {
  const states = utilityStatesByPlayer.value.get(playerId) ?? []
  const index = findTickIndex(states, currentTick.value)
  return index >= 0 ? states[index].items : []
}

function utilityLabel(type: UtilityType): string {
  return ({ smoke: 'S', flash: 'F', he: 'H', fire: 'M', molotov: 'M', incendiary: 'I', decoy: 'D' } as Record<string, string>)[type] ?? '?'
}

function inventoryOffset(index: number, length: number): number {
  return (index - (length - 1) / 2) * 19
}
</script>

<template>
  <div class="radar-shell">
    <div class="radar-coordinate top">N</div>
    <div class="radar-coordinate right">E</div>
    <svg class="radar" viewBox="0 0 1024 1024" role="img" :aria-label="`${map.label} 小地图`">
      <defs>
        <linearGradient id="radar-bg" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stop-color="#252a2d" />
          <stop offset="1" stop-color="#111416" />
        </linearGradient>
        <filter id="player-shadow" x="-100%" y="-100%" width="300%" height="300%">
          <feGaussianBlur in="SourceAlpha" stdDeviation="5" />
          <feOffset dy="3" />
          <feComponentTransfer><feFuncA type="linear" slope="0.7" /></feComponentTransfer>
          <feMerge><feMergeNode /><feMergeNode in="SourceGraphic" /></feMerge>
        </filter>
        <radialGradient id="smoke-fill">
          <stop offset="0" stop-color="#aeb7bd" stop-opacity=".78" />
          <stop offset=".72" stop-color="#7d878e" stop-opacity=".52" />
          <stop offset="1" stop-color="#525b61" stop-opacity=".08" />
        </radialGradient>
        <pattern id="grid" width="64" height="64" patternUnits="userSpaceOnUse">
          <path d="M64 0H0V64" fill="none" stroke="#fff" stroke-opacity=".035" stroke-width="1" />
        </pattern>
      </defs>

      <rect width="1024" height="1024" rx="24" fill="url(#radar-bg)" />
      <rect width="1024" height="1024" rx="24" fill="url(#grid)" />

      <image
        v-if="map.imageUrl"
        class="radar-map-image"
        :href="map.imageUrl"
        width="1024"
        height="1024"
        preserveAspectRatio="none"
      />

      <g v-if="!map.imageUrl" class="map-geometry">
        <path d="M168 265 271 170 474 194 523 126 702 116 865 218 829 397 908 483 844 695 680 735 589 908 381 890 309 769 132 708 185 522 116 414Z" />
        <path d="M207 294 320 223 462 237 512 188 660 166 803 244 767 386 842 482 782 637 641 669 548 830 402 821 341 711 193 664 239 520 174 420Z" class="inner" />
        <path d="M258 345H394V463H258ZM664 284H818V431H664ZM455 446H626V577H455ZM374 618H523V770H374ZM643 561H757V667H643Z" class="rooms" />
        <path d="M323 389H493M566 350H700M543 565V700M525 500 675 603M389 460 476 621" class="routes" />
      </g>

      <g v-if="!map.imageUrl" class="site-labels">
        <g transform="translate(231 325)"><circle r="33" /><text>A</text></g>
        <g transform="translate(796 354)"><circle r="33" /><text>B</text></g>
        <text x="490" y="930" class="spawn-label">T SPAWN</text>
        <text x="736" y="104" class="spawn-label">CT SPAWN</text>
      </g>

      <g class="utility-effect-layer">
        <g v-for="effect in activeEffects" :key="effect.id" :class="['utility-effect', `effect-${effect.type}`]">
          <title>{{ effect.throwerName || '未知玩家' }} · {{ effect.type }}</title>
          <circle v-if="effect.type === 'smoke'" :cx="effect.point.x" :cy="effect.point.y" :r="effect.radius" />
          <g v-else>
            <circle v-for="(point, index) in effect.area" :key="index" :cx="point.x" :cy="point.y" :r="effect.radius" />
          </g>
        </g>
      </g>

      <g class="utility-track-layer">
        <g v-for="utility in activeProjectiles" :key="utility.id" :class="['utility-track', `utility-${utility.type}`, `team-${utility.team.toLowerCase()}`]">
          <title>{{ utility.throwerName || '未知玩家' }} · {{ utility.type }}</title>
          <polyline :points="utility.points" />
          <g :transform="`translate(${utility.point.x} ${utility.point.y})`" class="utility-marker">
            <circle r="11" />
            <text>{{ utility.label }}</text>
          </g>
        </g>
      </g>

      <g class="trail-layer">
        <polyline v-for="trail in trails" :key="trail.id" :points="trail.points" :class="['trail', `team-${trail.team.toLowerCase()}`]" />
      </g>

      <g v-for="player in players" :key="player.id" :transform="`translate(${player.point.x} ${player.point.y})`" :class="['player', `team-${player.team.toLowerCase()}`, { dead: !player.alive }]">
        <g :transform="`rotate(${player.yaw})`" filter="url(#player-shadow)">
          <path v-if="player.alive" d="M0-24 13 9 0 5-13 9Z" />
          <circle v-else r="11" />
          <path v-if="!player.alive" d="M-6-6 6 6M6-6-6 6" class="death-cross" />
        </g>
        <text v-if="showNames" y="-33" class="player-name">{{ player.name }}</text>
        <g v-if="showInventory && player.utilities.length > 0" class="utility-inventory">
          <g
            v-for="(item, index) in player.utilities"
            :key="item.type"
            :transform="`translate(${inventoryOffset(index, player.utilities.length)} ${showNames ? -53 : -34})`"
            :class="`inventory-${item.type}`"
          >
            <rect x="-8" y="-7" width="16" height="14" rx="3" />
            <text>{{ utilityLabel(item.type) }}{{ item.count > 1 ? item.count : '' }}</text>
          </g>
        </g>
        <g v-if="player.alive" class="health-bar" transform="translate(-18 19)">
          <rect width="36" height="4" rx="2" class="health-bg" />
          <rect :width="36 * player.health / 100" height="4" rx="2" />
        </g>
      </g>
    </svg>
    <div class="radar-caption">
      <span>{{ map.label }}</span>
      <small>{{ map.name }} · {{ map.imageUrl ? 'Simple Radar' : '1024 overview fallback' }}</small>
    </div>
  </div>
</template>
