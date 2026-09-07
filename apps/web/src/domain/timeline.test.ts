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

  it('interpolates fresh predictions only within one round phase', () => {
    const predictions: WinPredictionPoint[] = [
      { tick: 640, timeSeconds: 10, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'live', tWin: .4, ctWin: .6 },
      { tick: 704, timeSeconds: 11, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'live', tWin: .6, ctWin: .4 },
      { tick: 768, timeSeconds: 12, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'post-plant', tWin: .8, ctWin: .2 },
    ]

    expect(winPredictionAt(predictions, 10.5, 'live')).toMatchObject({ tWin: .5, ctWin: .5, tick: 672 })
    expect(winPredictionAt(predictions, 11.5, 'post-plant')).toBeNull()
    expect(winPredictionAt(predictions, 12, 'post-plant')).toMatchObject({ tWin: .8, ctWin: .2 })
  })

  it('does not show stale predictions outside active round phases', () => {
    const predictions: WinPredictionPoint[] = [
      { tick: 640, timeSeconds: 10, roundId: 's0-a1', segmentId: 0, roundNumber: 1, phase: 'live', tWin: .4, ctWin: .6 },
    ]

    expect(winPredictionAt(predictions, 12, 'live')).toBeNull()
    expect(winPredictionAt(predictions, 10.2, 'freeze')).toBeNull()
    expect(winPredictionAt(predictions, 9.9, 'live')).toBeNull()
  })})
