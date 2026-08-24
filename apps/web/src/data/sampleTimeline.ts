import { mapToWorld, mapConfigs, type MapPoint } from '../domain/maps'
import type {
  DemoFrame,
  DemoTimeline,
  MapZoneOccupancy,
  PlayerEquipmentState,
  PlayerSnapshot,
  PlayerUtilityState,
  Team,
  UtilityAreaPoint,
  UtilityPoint,
} from '../domain/timeline'

interface SamplePlayer {
  id: string
  name: string
  team: Team
  route: MapPoint[]
  phase: number
}

const map = mapConfigs.de_mirage
const samplePlayers: SamplePlayer[] = [
  { id: 't1', name: 'Kestrel', team: 'T', phase: 0, route: [{ x: 535, y: 812 }, { x: 490, y: 645 }, { x: 390, y: 475 }, { x: 252, y: 302 }] },
  { id: 't2', name: 'Morrow', team: 'T', phase: 0.03, route: [{ x: 565, y: 802 }, { x: 654, y: 710 }, { x: 786, y: 592 }, { x: 884, y: 391 }] },
  { id: 't3', name: 'Rook', team: 'T', phase: 0.06, route: [{ x: 515, y: 830 }, { x: 536, y: 650 }, { x: 512, y: 482 }, { x: 490, y: 318 }] },
  { id: 't4', name: 'Sable', team: 'T', phase: 0.09, route: [{ x: 586, y: 818 }, { x: 680, y: 713 }, { x: 804, y: 574 }, { x: 900, y: 383 }] },
  { id: 't5', name: 'Vex', team: 'T', phase: 0.12, route: [{ x: 550, y: 842 }, { x: 451, y: 677 }, { x: 348, y: 489 }, { x: 246, y: 310 }] },
  { id: 'ct1', name: 'Atlas', team: 'CT', phase: 0.01, route: [{ x: 892, y: 337 }, { x: 850, y: 381 }, { x: 825, y: 424 }, { x: 790, y: 468 }] },
  { id: 'ct2', name: 'Bloom', team: 'CT', phase: 0.04, route: [{ x: 904, y: 321 }, { x: 760, y: 276 }, { x: 604, y: 285 }, { x: 508, y: 354 }] },
  { id: 'ct3', name: 'Cipher', team: 'CT', phase: 0.07, route: [{ x: 882, y: 354 }, { x: 688, y: 281 }, { x: 450, y: 267 }, { x: 283, y: 294 }] },
  { id: 'ct4', name: 'Drift', team: 'CT', phase: 0.1, route: [{ x: 916, y: 346 }, { x: 826, y: 420 }, { x: 738, y: 500 }, { x: 680, y: 552 }] },
  { id: 'ct5', name: 'Echo', team: 'CT', phase: 0.13, route: [{ x: 895, y: 308 }, { x: 721, y: 274 }, { x: 492, y: 259 }, { x: 302, y: 286 }] },
]

const duration = 90
const sampleRate = 4
const frames: DemoFrame[] = Array.from({ length: duration * sampleRate + 1 }, (_, index) => {
  const timeSeconds = index / sampleRate
  const progress = Math.min(1, timeSeconds / 72)
  const players = samplePlayers.map((player, playerIndex) => makeSnapshot(player, progress, timeSeconds, playerIndex))
  return {
    tick: Math.round(timeSeconds * 64),
    timeSeconds,
    players,
    round: {
      number: 12,
      phase: timeSeconds >= 90 ? 'ended' : timeSeconds >= 74 ? 'post-plant' : 'live',
      scoreT: 6,
      scoreCT: 5,
      elapsedSeconds: timeSeconds,
      remainingSeconds: Math.max(0, 90 - timeSeconds),
      consecutiveLossesT: 0,
      consecutiveLossesCT: 1,
    },
    bomb: {
      state: timeSeconds >= 90 ? 'exploded' : timeSeconds >= 74 ? 'planted' : 'carried',
      carrierId: timeSeconds < 74 ? 't2' : null,
      defuserId: null,
      site: timeSeconds >= 74 ? 'B' : null,
      region: timeSeconds >= 74 ? 'B Site' : 'T Spawn',
      x: players.find(player => player.id === 't2')?.x ?? null,
      y: players.find(player => player.id === 't2')?.y ?? null,
      z: 0,
      secondsToExplosion: timeSeconds >= 74 && timeSeconds < 90 ? 90 - timeSeconds : null,
      secondsToDefuse: null,
    },
    zones: summarizeZones(players),
  }
})

function makeSnapshot(player: SamplePlayer, progress: number, time: number, index: number): PlayerSnapshot {
  const routeProgress = Math.min(0.999, Math.max(0, progress + player.phase))
  const segmentFloat = routeProgress * (player.route.length - 1)
  const segment = Math.floor(segmentFloat)
  const local = segmentFloat - segment
  const from = player.route[segment]
  const to = player.route[Math.min(segment + 1, player.route.length - 1)]
  const wobble = Math.sin(time * 0.45 + index) * 7
  const mapPoint = {
    x: from.x + (to.x - from.x) * local + wobble,
    y: from.y + (to.y - from.y) * local + Math.cos(time * 0.38 + index) * 5,
  }
  const world = mapToWorld(mapPoint, map)
  const alive = !(player.id === 'ct3' && time >= 62) && !(player.id === 't5' && time >= 68)

  return {
    id: player.id,
    name: player.name,
    team: player.team,
    alive,
    health: alive ? Math.max(18, 100 - (time > 50 ? (index * 13) % 70 : 0)) : 0,
    x: world.x,
    y: world.y,
    z: index % 3 === 0 ? 32 : 0,
    yaw: Math.atan2(-(to.y - from.y), to.x - from.x) * 180 / Math.PI,
    velocityX: 0,
    velocityY: 0,
    velocityZ: 0,
    region: routeProgress < .2 ? (player.team === 'T' ? 'T Spawn' : 'CT Spawn')
      : routeProgress < .58 ? 'Middle'
      : index % 2 === 0 ? 'A Site' : 'B Site',
    weapon: player.team === 'T' ? (index % 2 ? 'ak47' : 'galilar') : (index % 2 ? 'm4a1_silencer' : 'famas'),
    kills: player.id === 't2' && time >= 62 ? 1 : 0,
    deaths: alive ? 0 : 1,
  }
}

function summarizeZones(players: PlayerSnapshot[]): MapZoneOccupancy[] {
  const regions = new Map<string, PlayerSnapshot[]>()
  for (const player of players) regions.set(player.region, [...(regions.get(player.region) ?? []), player])
  return [...regions.entries()].map(([region, members]) => ({
    region,
    tAlive: members.filter(player => player.team === 'T' && player.alive).length,
    ctAlive: members.filter(player => player.team === 'CT' && player.alive).length,
    tTotal: members.filter(player => player.team === 'T').length,
    ctTotal: members.filter(player => player.team === 'CT').length,
  }))
}

function utilityPoint(timeSeconds: number, x: number, y: number, z = 24): UtilityPoint {
  const world = mapToWorld({ x, y }, map)
  return { tick: Math.round(timeSeconds * 64), timeSeconds, x: world.x, y: world.y, z }
}

function areaPoint(x: number, y: number, z = 0): UtilityAreaPoint {
  const world = mapToWorld({ x, y }, map)
  return { x: world.x, y: world.y, z }
}

const playerUtilityStates: PlayerUtilityState[] = samplePlayers.map((player, index) => ({
  tick: 0,
  timeSeconds: 0,
  playerId: player.id,
  items: [
    { type: 'smoke', count: 1 },
    { type: 'flash', count: index % 3 === 0 ? 2 : 1 },
    { type: 'he', count: 1 },
    { type: player.team === 'T' ? 'molotov' : 'incendiary', count: 1 },
  ],
}))

const playerEquipmentStates: PlayerEquipmentState[] = samplePlayers.map((player, index) => ({
  tick: 0,
  timeSeconds: 0,
  playerId: player.id,
  money: 800 + index * 250,
  armor: index % 3 === 0 ? 100 : 0,
  hasHelmet: index % 3 === 0,
  hasDefuser: player.team === 'CT' && index % 2 === 0,
  currentEquipmentValue: 3400 + index * 180,
  roundStartEquipmentValue: 3400 + index * 180,
  cashSpentThisRound: 3000 + index * 150,
  items: [
    { name: player.team === 'T' ? 'weapon_ak47' : 'weapon_m4a1_silencer', category: 'rifle', count: 1, clipAmmo: 30, reserveAmmo: 90 },
    { name: player.team === 'T' ? 'weapon_glock' : 'weapon_usp_silencer', category: 'pistol', count: 1, clipAmmo: 20, reserveAmmo: 120 },
    { name: 'weapon_smokegrenade', category: 'grenade', count: 1, clipAmmo: 0, reserveAmmo: 0 },
    { name: 'weapon_flashbang', category: 'grenade', count: index % 3 === 0 ? 2 : 1, clipAmmo: 0, reserveAmmo: 0 },
  ],
}))

export const sampleTimeline: DemoTimeline = {
  metadata: {
    fileName: 'sample-mirage.dem',
    mapName: 'de_mirage',
    tickRate: 64,
    sampleRate,
    totalTicks: duration * 64,
    durationSeconds: duration,
  },
  frames,
  utilityTracks: [
    {
      id: 'sample-smoke', type: 'smoke', throwerId: 't2', throwerName: 'Morrow', team: 'T',
      startTick: 18 * 64, endTick: 19 * 64, detonateTick: 19 * 64,
      trajectory: [utilityPoint(18, 560, 790), utilityPoint(18.35, 610, 690, 130), utilityPoint(18.7, 675, 590, 86), utilityPoint(19, 735, 505)],
    },
    {
      id: 'sample-fire', type: 'fire', throwerId: 'ct2', throwerName: 'Bloom', team: 'CT',
      startTick: 40 * 64, endTick: 41 * 64, detonateTick: 41 * 64,
      trajectory: [utilityPoint(40, 705, 305), utilityPoint(40.35, 640, 385, 145), utilityPoint(40.7, 575, 465, 92), utilityPoint(41, 515, 535)],
    },
  ],
  utilityEffects: [
    {
      id: 'sample-smoke-effect', type: 'smoke', throwerId: 't2', throwerName: 'Morrow', team: 'T',
      startTick: 19 * 64, endTick: 37 * 64,
      samples: [{ ...utilityPoint(19, 735, 505), radius: 144, area: [] }],
    },
    {
      id: 'sample-fire-effect', type: 'fire', throwerId: 'ct2', throwerName: 'Bloom', team: 'CT',
      startTick: 41 * 64, endTick: 48 * 64,
      samples: [{
        ...utilityPoint(41, 515, 535, 0), radius: 48,
        area: [areaPoint(515, 535), areaPoint(532, 548), areaPoint(498, 552), areaPoint(522, 570), areaPoint(485, 528)],
      }],
    },
  ],
  playerUtilityStates,
  playerEquipmentStates,
  events: [
    { tick: 0, timeSeconds: 0, type: 'round-start', title: '回合开始', detail: '示例数据 · 第 12 回合' },
    { tick: 62 * 64, timeSeconds: 62, type: 'kill', title: 'Morrow → Cipher', detail: 'ak47 · 爆头' },
    { tick: 68 * 64, timeSeconds: 68, type: 'kill', title: 'Atlas → Vex', detail: 'm4a1_silencer' },
    { tick: 74 * 64, timeSeconds: 74, type: 'bomb-planted', title: '炸弹已安放', detail: 'Morrow · B 区' },
    { tick: 90 * 64, timeSeconds: 90, type: 'round-end', title: '回合结束', detail: 'T 获胜 · 炸弹爆炸' },
  ],
}
