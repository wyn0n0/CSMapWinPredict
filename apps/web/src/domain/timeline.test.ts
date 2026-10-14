import { describe, expect, it } from 'vitest'
import { findFrameIndex, findTickIndex, formatTime, utilityPointAt, windowIndexAt, winPredictionAt, type DemoFrame, type UtilityPoint, type WinPredictionPoint } from './timeline'

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
