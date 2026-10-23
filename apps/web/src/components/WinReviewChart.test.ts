import { createSSRApp } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { expect, it } from 'vitest'
import WinReviewChart from './WinReviewChart.vue'
import type { WinReview } from '../domain/winReview'

const events = [{ tick: 651, timeSeconds: 651 / 64, type: 'bomb-defuse-start', title: '开始拆包' },
  { tick: 655, timeSeconds: 655 / 64, type: 'bomb-defuse-abort', title: '取消拆包' }]
const review: WinReview = { schemaVersion: 2, rounds: [{ roundId: 'r1', segmentId: 0, roundNumber: 1,
  startTick: 0, liveTick: 640, endTick: 1280, disposition: 'completed', winner: 'T', points: [],
  comparisons: [{ id: 'r1-651', events, before: null, after: null, status: 'before-missing', tDeltaPercentagePoints: null }] }] }
const render = (value: WinReview) => renderToString(createSSRApp(WinReviewChart, { review: value, events, tickRate: 64,
  currentTime: 10, currentPrediction: null, loading: false, error: '', sourceLabel: 'test', modelUnavailable: false }))

it('makes short defuse taps selectable and identifies grouped review data', async () => {
  const html = await render(review)
  expect(html).toContain('开始拆包，开打后 0.2 秒，回看前 3 秒')
  expect(html).toContain('取消拆包，开打后 0.2 秒，回看前 3 秒')
  expect(html).toContain('相邻间隔不超过 1 秒的事件合并为一段')
  expect(html).not.toContain('没有事件前后对比')
})

it('keeps old curves usable and explains missing comparison data', async () => {
  const html = await render({ schemaVersion: 1, rounds: [{ ...review.rounds[0]!, comparisons: undefined }] })
  expect(html).toContain('当前数据没有事件前后对比')
  expect(html).toContain('请重新导入以生成补点')
})
