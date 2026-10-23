import { afterEach, expect, it, vi } from 'vitest'
import { loadWinReview } from './api'

afterEach(() => vi.unstubAllGlobals())

it('loads the independently versioned review endpoint', async () => {
  const fetcher = vi.fn(async () => new Response(JSON.stringify({ schemaVersion: 1, rounds: [] })))
  vi.stubGlobal('fetch', fetcher)
  expect(await loadWinReview('demo')).toEqual({ schemaVersion: 1, rounds: [] })
  expect(fetcher).toHaveBeenCalledWith('/api/demos/demo/win-review')
})

it('accepts event comparisons while retaining v1 compatibility', async () => {
  const payload = { schemaVersion: 2, rounds: [] }
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(payload))))
  expect(await loadWinReview('demo')).toEqual(payload)
})

it('reports missing review files and rejects incompatible payloads', async () => {
  vi.stubGlobal('fetch', vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify({ error: '请重新导入 Demo' }), { status: 404 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ schemaVersion: 999, rounds: [] }))))
  await expect(loadWinReview('demo')).rejects.toThrow('请重新导入 Demo')
  await expect(loadWinReview('demo')).rejects.toThrow('版本不支持')
})
