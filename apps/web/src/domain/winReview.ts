import type { TimelineEvent, WinPredictionPoint } from './timeline'

export interface WinReviewRound {
  roundId: string
  segmentId: number
  roundNumber: number | null
  startTick: number
  liveTick: number
  endTick: number
  disposition: string
  winner: string | null
  points: WinPredictionPoint[]
  comparisons?: WinReviewComparison[] | null
}

export interface WinReviewComparison {
  id: string
  events: TimelineEvent[]
  before: WinPredictionPoint | null
  after: WinPredictionPoint | null
  status: string
  tDeltaPercentagePoints: number | null
}

export function comparisonStatusLabel(status: string) {
  return ({
    'round-ended': '这段事件包含回合结算，只显示结果，不补成 100% 胜率。',
    'round-unavailable': '该回合未完整结算或已作废，无法对比。',
    'before-missing': '缺少紧邻事件前的可靠状态，暂不计算变化。',
    'after-unconfirmed': '事件后的状态尚不能确认，暂不计算变化。',
    'quality-unknown': '前后状态的数据完整性未知，暂不计算变化。',
    'data-incomplete': '前后状态存在装备或位置缺失，暂不计算变化。',
  } as Record<string, string>)[status] ?? '没有可用的前后对比。'
}

export interface WinReview {
  schemaVersion: number
  rounds: WinReviewRound[]
}

export function reviewRoundAt(rounds: WinReviewRound[], tick: number) {
  for (let index = rounds.length - 1; index >= 0; index--)
    if (rounds[index]!.startTick <= tick) return rounds[index]!
  return null
}

// Each horizontal segment is supported only by its already-observed sample.
// Unknown time, phase transitions and round endings are never interpolated over.
export function reviewSegments(round: WinReviewRound, tickRate: number, events: TimelineEvent[]) {
  const end = round.endTick / tickRate
  const points = round.points.filter(point => point.roundId === round.roundId &&
    point.segmentId === round.segmentId && point.tick >= round.liveTick && point.tick < round.endTick &&
    (!point.triggers?.length || point.triggers.some(trigger => trigger === 'regular' || trigger === 'event-after')))
  return points.flatMap((point, index) => {
    if (!Number.isFinite(point.tWin) || point.tWin < 0 || point.tWin > 1 ||
      !Number.isFinite(point.timeSeconds) || !['live', 'post-plant'].includes(point.phase)) return []
    const next = points[index + 1]
    const phaseEnd = point.phase === 'live'
      ? events.find(event => event.type === 'bomb-planted' && event.timeSeconds > point.timeSeconds)?.timeSeconds
      : undefined
    const to = Math.min(end, point.timeSeconds + 1.5, next?.timeSeconds ?? end, phaseEnd ?? end)
    return [{ point, from: point.timeSeconds, to,
      connect: next && next.timeSeconds === to && next.phase === point.phase &&
        Number.isFinite(next.tWin) && next.tWin >= 0 && next.tWin <= 1 ? next.tWin : null }]
  })
}

export function predictionQualityLabel(point: WinPredictionPoint | null) {
  if (!point) return '此时没有有效预测；空白处不补猜'
  if (!point.quality) return '该采样点的数据完整性未知'
  const missing = [point.quality.equipmentMissing ? '装备' : '', point.quality.positionMissing ? '位置' : ''].filter(Boolean)
  return missing.length ? `部分玩家${missing.join('和')}未知，预测信息不完整` : '该采样点装备与位置数据完整'
}

export function eventReviewTime(event: TimelineEvent, round: WinReviewRound, tickRate: number) {
  return Math.max(round.liveTick / tickRate, event.timeSeconds - 3)
}
