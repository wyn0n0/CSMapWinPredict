export type Team = 'T' | 'CT' | 'SPEC'
export type UtilityType = 'smoke' | 'flash' | 'he' | 'fire' | 'molotov' | 'incendiary' | 'decoy' | string

export interface PlayerSnapshot {
  id: string
  name: string
  team: Team
  alive: boolean
  health: number
  x: number
  y: number
  z: number
  yaw: number
  velocityX: number
  velocityY: number
  velocityZ: number
  region: string
  weapon: string | null
  kills: number
  deaths: number
}

export interface DemoFrame {
  tick: number
  timeSeconds: number
  players: PlayerSnapshot[]
  round: RoundSnapshot
  bomb: BombSnapshot
  zones: MapZoneOccupancy[]
}

export interface RoundSnapshot {
  number: number
  phase: 'warmup' | 'team-intro' | 'freeze' | 'live' | 'post-plant' | 'ended' | string
  scoreT: number
  scoreCT: number
  elapsedSeconds: number
  remainingSeconds: number
  consecutiveLossesT: number
  consecutiveLossesCT: number
}

export interface BombSnapshot {
  state: 'unavailable' | 'carried' | 'dropped' | 'planting' | 'planted' | 'defusing' | 'defused' | 'exploded' | string
  carrierId: string | null
  defuserId: string | null
  site: string | null
  region: string | null
  x: number | null
  y: number | null
  z: number | null
  secondsToExplosion: number | null
  secondsToDefuse: number | null
}

export interface MapZoneOccupancy {
  region: string
  tAlive: number
  ctAlive: number
  tTotal: number
  ctTotal: number
}

export interface UtilityPoint {
  tick: number
  timeSeconds: number
  x: number
  y: number
  z: number
}

export interface UtilityTrack {
  id: string
  type: UtilityType
  throwerId: string | null
  throwerName: string | null
  team: Team
  startTick: number
  endTick: number
  detonateTick: number | null
  trajectory: UtilityPoint[]
}

export interface UtilityAreaPoint {
  x: number
  y: number
  z: number
}

export interface UtilityEffectSample extends UtilityPoint {
  radius: number
  area: UtilityAreaPoint[]
}

export interface UtilityEffectTrack {
  id: string
  type: 'smoke' | 'fire' | string
  throwerId: string | null
  throwerName: string | null
  team: Team
  startTick: number
  endTick: number
  samples: UtilityEffectSample[]
}

export interface CarriedUtility {
  type: UtilityType
  count: number
}

export interface PlayerUtilityState {
  tick: number
  timeSeconds: number
  playerId: string
  items: CarriedUtility[]
}

export interface EquipmentItem {
  name: string
  category: string
  count: number
  clipAmmo: number
  reserveAmmo: number
}

export interface PlayerEquipmentState {
  tick: number
  timeSeconds: number
  playerId: string
  money: number
  armor: number
  hasHelmet: boolean
  hasDefuser: boolean
  currentEquipmentValue: number
  roundStartEquipmentValue: number
  cashSpentThisRound: number
  items: EquipmentItem[]
}

export interface TimelineEvent {
  tick: number
  timeSeconds: number
  type: 'round-start' | 'round-end' | 'kill' | 'bomb-planted' | 'bomb-defused' | string
  title: string
  detail?: string | null
}

export interface DemoMetadata {
  fileName: string
  mapName: string
  tickRate: number
  sampleRate: number
  totalTicks: number
  durationSeconds: number
}

export interface DemoTimeline {
  metadata: DemoMetadata
  frames: DemoFrame[]
  utilityTracks: UtilityTrack[]
  utilityEffects: UtilityEffectTrack[]
  playerUtilityStates: PlayerUtilityState[]
  playerEquipmentStates: PlayerEquipmentState[]
  events: TimelineEvent[]
}

export interface DemoManifest {
  id: string
  metadata: DemoMetadata
  events: TimelineEvent[]
  frameCount: number
  utilityTrackCount: number
  utilityEffectCount: number
  playerUtilityStateCount: number
  playerEquipmentStateCount: number
  windowSeconds: number
  windowCount: number
}

export interface DemoWindow {
  index: number
  coreFromSeconds: number
  coreToSeconds: number
  dataFromSeconds: number
  dataToSeconds: number
  firstFrameIndex: number
  totalFrameCount: number
  frames: DemoFrame[]
  utilityTracks: UtilityTrack[]
  utilityEffects: UtilityEffectTrack[]
  playerUtilityStates: PlayerUtilityState[]
  playerEquipmentStates: PlayerEquipmentState[]
}

export function findTickIndex<T extends { tick: number }>(items: T[], tick: number): number {
  if (items.length === 0) return -1

  let low = 0
  let high = items.length - 1
  while (low <= high) {
    const middle = Math.floor((low + high) / 2)
    if (items[middle].tick <= tick) low = middle + 1
    else high = middle - 1
  }
  return high
}

export function utilityPointAt(points: UtilityPoint[], tick: number): UtilityPoint | null {
  const index = findTickIndex(points, tick)
  if (index < 0) return null
  const from = points[index]
  const to = points[index + 1]
  if (!to || to.tick === from.tick || tick <= from.tick) return from

  const amount = Math.min(1, Math.max(0, (tick - from.tick) / (to.tick - from.tick)))
  return {
    tick,
    timeSeconds: from.timeSeconds + (to.timeSeconds - from.timeSeconds) * amount,
    x: from.x + (to.x - from.x) * amount,
    y: from.y + (to.y - from.y) * amount,
    z: from.z + (to.z - from.z) * amount,
  }
}

export function findFrameIndex(frames: DemoFrame[], timeSeconds: number): number {
  if (frames.length === 0) return 0

  let low = 0
  let high = frames.length - 1
  while (low <= high) {
    const middle = Math.floor((low + high) / 2)
    if (frames[middle].timeSeconds <= timeSeconds) low = middle + 1
    else high = middle - 1
  }
  return Math.max(0, Math.min(high, frames.length - 1))
}

export function windowIndexAt(timeSeconds: number, windowSeconds: number, windowCount: number): number {
  if (windowCount <= 1 || windowSeconds <= 0) return 0
  return Math.min(windowCount - 1, Math.max(0, Math.floor(timeSeconds / windowSeconds)))
}

export function formatTime(seconds: number): string {
  const value = Math.max(0, Math.floor(seconds))
  return `${Math.floor(value / 60).toString().padStart(2, '0')}:${(value % 60).toString().padStart(2, '0')}`
}
