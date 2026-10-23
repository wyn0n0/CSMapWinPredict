import { describe, expect, it } from 'vitest'
import { eventReviewTime, predictionQualityLabel, reviewRoundAt, reviewSegments, type WinReviewRound } from './winReview'
import type { WinPredictionPoint } from './timeline'

const point = (timeSeconds: number, tWin = .4, phase = 'live'): WinPredictionPoint => ({
  tick: timeSeconds * 64, timeSeconds, roundId: 's0-a1', segmentId: 0, roundNumber: 1,
  phase, tWin, ctWin: 1 - tWin,
})
const round = (points: WinPredictionPoint[]): WinReviewRound => ({
  roundId: 's0-a1', segmentId: 0, roundNumber: 1, startTick: 0, liveTick: 640,
  endTick: 1280, disposition: 'completed', winner: 'T', points,
})

describe('win review timeline', () => {
  it('holds the reached probability, breaks at expiry and never extends beyond round end', () => {
    const segments = reviewSegments(round([point(10), point(11, .8), point(15), point(19)]), 64, [])
    expect(segments.map(s => [s.from, s.to, s.point.tWin, s.connect])).toEqual([
      [10, 11, .4, .8], [11, 12.5, .8, null], [15, 16.5, .4, null], [19, 20, .4, null],
    ])
    // Future values only change the vertical jump at their own arrival, not earlier horizontals.
    const changed = reviewSegments(round([point(10), point(11, .1)]), 64, [])
    expect([changed[0]!.from, changed[0]!.to, changed[0]!.point.tWin]).toEqual([10, 11, .4])
  })

  it('leaves a gap between planting and the first post-plant sample', () => {
    const segments = reviewSegments(round([point(10), point(11, .8, 'post-plant')]), 64,
      [{ tick: 672, timeSeconds: 10.5, type: 'bomb-planted', title: '下包' }])
    expect(segments[0]!.to).toBe(10.5)
    expect(segments[0]!.connect).toBeNull()
    expect(segments[1]!.from).toBe(11)
  })

  it('isolates same-number restarts, foreign points and terminal predictions', () => {
    const first = round([point(10), { ...point(11), segmentId: 1 }, point(20)])
    const second = { ...round([]), roundId: 's1-a1', segmentId: 1, startTick: 1280, liveTick: 1344, endTick: 2000 }
    expect(reviewSegments(first, 64, [])).toHaveLength(1)
    expect(reviewRoundAt([first, second], 1280)?.roundId).toBe('s1-a1')
    expect(reviewRoundAt([first, second], 1200)?.roundId).toBe('s0-a1')
    expect(reviewRoundAt([first], -1)).toBeNull()
  })

  it('does not draw invalid probabilities and distinguishes missing from unknown quality', () => {
    expect(reviewSegments(round([point(10, NaN), point(11, 1.1)]), 64, [])).toEqual([])
    expect(predictionQualityLabel(null)).toContain('没有有效预测')
    expect(predictionQualityLabel(point(10))).toContain('未知')
    expect(predictionQualityLabel({ ...point(10), quality: { equipmentMissing: true, positionMissing: false } })).toContain('装备未知')
    expect(predictionQualityLabel({ ...point(10), quality: { equipmentMissing: false, positionMissing: false } })).toContain('数据完整')
  })

  it('event rewind stays within the same live round', () => {
    expect(eventReviewTime({ tick: 704, timeSeconds: 11, type: 'kill', title: '击杀' }, round([]), 64)).toBe(10)
    expect(eventReviewTime({ tick: 1152, timeSeconds: 18, type: 'kill', title: '击杀' }, round([]), 64)).toBe(15)
  })
})
