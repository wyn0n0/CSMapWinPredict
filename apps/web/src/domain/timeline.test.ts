import { describe, expect, it } from 'vitest'
import { findFrameIndex, findTickIndex, formatTime, playerPoseAt, utilityPointAt, windowIndexAt, winPredictionAt, type DemoFrame, type UtilityPoint, type WinPredictionPoint } from './timeline'
import { sampleTimeline } from '../data/sampleTimeline'

const frames = [0, 0.5, 1, 1.5].map((timeSeconds, index): DemoFrame => ({
  tick: index * 32,
  timeSeconds,
  players: [],
  round: { number: 1, phase: 'live', scoreT: 0, scoreCT: 0, elapsedSeconds: timeSeconds, remainingSeconds: 115 - timeSeconds, consecutiveLossesT: 0, consecutiveLossesCT: 0 },
  bomb: { state: 'unavailable', carrierId: null, defuserId: null, site: null, region: null, x: null, y: null, z: null, secondsToExplosion: null, secondsToDefuse: null },
  zones: [],
}))

describe('timeline utilities', () => {
  it('finds the frame at or immediately before a timestamp', () => {
    expect(findFrameIndex(frames, 0.9)).toBe(1)
    expect(findFrameIndex(frames, 99)).toBe(3)
    expect(findFrameIndex(frames, -1)).toBe(0)
  })

  it('formats playback time', () => {
    expect(formatTime(0)).toBe('00:00')
    expect(formatTime(125.8)).toBe('02:05')
  })

  it('finds the latest state at or before a demo tick', () => {
    const states = [{ tick: 10 }, { tick: 20 }, { tick: 35 }]
    expect(findTickIndex(states, 9)).toBe(-1)
    expect(findTickIndex(states, 20)).toBe(1)
    expect(findTickIndex(states, 99)).toBe(2)
  })

  it.each([
    [0, 90, 45], [359, 1, 360], [1, 359, 0], [179, -179, 180], [-179, 179, -180],
  ])('interpolates yaw from %s to %s along the shortest turn', (yaw, nextYaw, expected) => {
    const player = { ...sampleTimeline.frames[0].players[0], alive: true, yaw }
    const from = { ...frames[0], timeSeconds: 10, players: [player] }
    const to = { ...from, timeSeconds: 10.125, players: [{ ...player, yaw: nextYaw, x: player.x + 100, health: 1 }] }
    const before = structuredClone([from, to])
    expect(playerPoseAt(player, from, to, 10.0625).yaw).toBeCloseTo(expected)
    expect([from, to]).toEqual(before)
  })

  it('uses actual sample times and restores the same yaw when seeking backwards', () => {
    const player = { ...sampleTimeline.frames[0].players[0], alive: true, yaw: 10 }
    const from = { ...frames[0], timeSeconds: 10, players: [player] }
    const to = { ...from, timeSeconds: 10.09375, players: [{ ...player, yaw: 130 }] }
    for (const [time, expected] of [[9, 10], [10, 10], [10.0234375, 40], [10.09375, 130], [11, 130], [10.0234375, 40]])
      expect(playerPoseAt(player, from, to, time).yaw).toBeCloseTo(expected)
  })

  it('holds sampled pose at missing frames, round boundaries and life or team changes', () => {
    const player = { ...sampleTimeline.frames[0].players[0], alive: true, yaw: 10 }
    const from = { ...frames[0], players: [player] }
    const next = { ...player, yaw: 130 }
    const to = { ...from, timeSeconds: 0.125, players: [next] }
    const boundaries = [
      undefined, { ...to, timeSeconds: 0 }, { ...to, timeSeconds: -1 }, { ...to, players: [] },
      { ...to, round: { ...to.round, number: to.round.number + 1 } },
      { ...to, round: { ...to.round, phase: 'freeze' } },
      { ...to, players: [{ ...next, id: 'another-player' }] },
      { ...to, players: [{ ...next, alive: false }] },
      { ...to, players: [{ ...next, team: 'SPEC' as const }] },
    ]
    for (const boundary of boundaries) expect(playerPoseAt(player, from, boundary, 0.0625)).toBe(player)
    const dead = { ...player, alive: false }
    expect(playerPoseAt(dead, from, to, 0.0625)).toBe(dead)
    expect(playerPoseAt(player, from, { ...to, players: [{ ...next, id: 'other', yaw: 270 }, next] }, 0.0625).yaw).toBe(70)
  })

  it('interpolates position at actual sample times without blending gameplay state or mutating samples', () => {
    const player = { ...sampleTimeline.frames[0].players[0], alive: true, x: 10, y: -20, z: 30, yaw: 0 }
    const from = { ...frames[0], timeSeconds: 10, players: [player] }
    const next = { ...player, x: 34, y: 28, z: 42, yaw: 90, health: 1, weapon: null, kills: 99 }
    const to = { ...from, timeSeconds: 10.09375, players: [{ ...next, id: 'other', x: 9999 }, next] }
    const before = structuredClone([from, to])
    for (const [time, amount] of [[9, 0], [10, 0], [10.0234375, .25], [10.046875, .5], [11, 1], [10.0234375, .25]]) {
      expect(playerPoseAt(player, from, to, time)).toEqual({
        ...player, x: 10 + 24 * amount, y: -20 + 48 * amount, z: 30 + 12 * amount, yaw: 90 * amount,
      })
    }
    expect([from, to]).toEqual(before)
  })

  it('holds the sampled pose instead of gliding through relocations or invalid coordinates', () => {
    const player = { ...sampleTimeline.frames[0].players[0], alive: true, x: 0, y: 0, z: 0 }
    const from = { ...frames[0], players: [player] }
    for (const position of [{ x: 2000 }, { z: 2000 }, { x: NaN }]) {
      const to = { ...from, timeSeconds: .125, players: [{ ...player, ...position }] }
      expect(playerPoseAt(player, from, to, .0625)).toBe(player)
    }
  })

  it('interpolates a projectile between utility samples', () => {
    const points: UtilityPoint[] = [
      { tick: 100, timeSeconds: 1.5625, x: 0, y: 20, z: 10 },
      { tick: 104, timeSeconds: 1.625, x: 40, y: 60, z: 30 },
    ]
    expect(utilityPointAt(points, 102)).toMatchObject({ tick: 102, x: 20, y: 40, z: 20 })
    expect(utilityPointAt(points, 99)).toBeNull()
  })

  it('selects a bounded streaming window at time boundaries', () => {
    expect(windowIndexAt(-1, 30, 4)).toBe(0)
    expect(windowIndexAt(29.999, 30, 4)).toBe(0)
    expect(windowIndexAt(30, 30, 4)).toBe(1)
    expect(windowIndexAt(999, 30, 4)).toBe(3)
  })

  it('holds the latest reached prediction and preserves its actual sample time', () => {
    const predictions: WinPredictionPoint[] = [
      { tick: 640, timeSeconds: 10, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'live', tWin: .4, ctWin: .6 },
      { tick: 704, timeSeconds: 11, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'live', tWin: .6, ctWin: .4 },
      { tick: 768, timeSeconds: 12, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'post-plant', tWin: .8, ctWin: .2 },
    ]

    expect(winPredictionAt(predictions, 10, 'live', 600)).toEqual(predictions[0])
    expect(winPredictionAt(predictions, 10.5, 'live', 600)).toEqual(predictions[0])
    expect(winPredictionAt(predictions, 11, 'live', 600)).toEqual(predictions[1])
    expect(winPredictionAt(predictions, 11.5, 'post-plant', 600)).toBeNull()
    expect(winPredictionAt(predictions, 12, 'post-plant', 600)).toEqual(predictions[2])
    // Seeking backwards must not retain the later probability or timestamp.
    expect(winPredictionAt(predictions, 10.5, 'live', 600)).toEqual(predictions[0])
  })

  it('is unaffected by changing, appending or removing future predictions', () => {
    const past: WinPredictionPoint = { tick: 640, timeSeconds: 10, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'live', tWin: .4, ctWin: .6 }
    const future = { ...past, tick: 704, timeSeconds: 11, tWin: .9, ctWin: .1 }
    const variants = [
      [past],
      [past, future],
      [past, { ...future, tWin: .1, ctWin: .9 }],
      [past, { ...future, roundId: 's1-a2', segmentId: 1, phase: 'post-plant' }],
      [past, future, { ...future, tick: 768, timeSeconds: 12 }],
    ]
    for (const points of variants) {
      for (const time of [9.9, 10, 10.5, 10.999]) {
        expect(winPredictionAt(points, time, 'live', 600)).toEqual(time < 10 ? null : past)
      }
    }
    // A kill at 10.75 is first reflected by the sample at 11, never before it.
    expect(winPredictionAt([past, future], 10.75, 'live', 600)).toEqual(past)
    expect(winPredictionAt([past, future], 11, 'live', 600)).toEqual(future)
  })

  it('updates at an irregular event tick and keeps before-only evidence out of playback', () => {
    const regular: WinPredictionPoint = { tick: 640, timeSeconds: 10, roundId: 's0-a1', segmentId: 0,
      roundNumber: 1, phase: 'live', tWin: .4, ctWin: .6, triggers: ['regular'] }
    const before = { ...regular, tick: 648, timeSeconds: 10.125, tWin: .5, ctWin: .5, triggers: ['event-before'] }
    const after = { ...regular, tick: 651, timeSeconds: 651 / 64, tWin: .7, ctWin: .3, triggers: ['event-after'] }
    expect(winPredictionAt([regular, before, after], 650 / 64, 'live', 600)).toEqual(regular)
    expect(winPredictionAt([regular, before, after], 651 / 64, 'live', 600)).toEqual(after)
    expect(winPredictionAt([regular, before, after], 648 / 64, 'live', 600)).toEqual(regular)
  })

  it('rejects missing, future and expired predictions, retaining the inclusive age limit', () => {
    const predictions: WinPredictionPoint[] = [
      { tick: 640, timeSeconds: 10, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'live', tWin: .4, ctWin: .6 },
    ]

    expect(winPredictionAt([], 10, 'live', 600)).toBeNull()
    expect(winPredictionAt(predictions, 11.5, 'live', 600)).toEqual(predictions[0])
    expect(winPredictionAt(predictions, 11.501, 'live', 600)).toBeNull()
    expect(winPredictionAt(predictions, 9.9, 'live', 600)).toBeNull()
    expect(winPredictionAt(predictions, 10, 'live', 600, -1)).toBeNull()
    expect(winPredictionAt(predictions, 10, 'live', 600, 0)).toEqual(predictions[0])
    expect(winPredictionAt(predictions, 10.01, 'live', 600, 0)).toBeNull()
    for (const phase of ['warmup', 'team-intro', 'freeze', 'ended', 'unknown', 'post-plant']) {
      expect(winPredictionAt(predictions, 10.2, phase, 600)).toBeNull()
    }
  })

  it.each([
    { roundId: 's0-a2', segmentId: 0, roundNumber: 2 },
    { roundId: 's0-a2', segmentId: 0, roundNumber: 1 },
    { roundId: 's1-a2', segmentId: 1, roundNumber: 1 },
  ])('does not carry a prediction into a new attempt: $roundId / round $roundNumber', identity => {
    const old: WinPredictionPoint = { tick: 640, timeSeconds: 10, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'live', tWin: .4, ctWin: .6 }
    const next = { ...old, ...identity, tick: 704, timeSeconds: 11, tWin: .7, ctWin: .3 }
    const points = [old, next]
    expect(winPredictionAt(points, 10.5, 'live', 672)).toBeNull()
    expect(winPredictionAt(points, 11, 'live', 672)).toEqual(next)
    expect(winPredictionAt(points, 10.25, 'live', 600)).toEqual(old)
  })

  it('keeps the same value across overlapping windows', () => {
    const points: WinPredictionPoint[] = [29, 30, 31, 32].map((timeSeconds, index) => ({
      tick: timeSeconds * 64, timeSeconds, roundId: 's0-a1', segmentId: 0,
      roundNumber: 1, phase: 'live', tWin: .4 + index * .1, ctWin: .6 - index * .1,
    }))
    for (const time of [29.999, 30, 30.5]) {
      expect(winPredictionAt(points.slice(0, 3), time, 'live', 600))
        .toEqual(winPredictionAt(points, time, 'live', 600))
    }
  })
})
