import { describe, expect, it } from 'vitest'
import { mapConfigs, mapToWorld, worldToMap } from './maps'

describe('overview coordinate conversion', () => {
  it('maps the overview origin to pixel zero', () => {
    const map = mapConfigs.de_mirage
    expect(worldToMap({ x: map.posX, y: map.posY }, map)).toEqual({ x: 0, y: 0 })
  })

  it('round-trips world coordinates', () => {
    const map = mapConfigs.de_mirage
    const world = { x: -1200.5, y: 842.25 }
    const restored = mapToWorld(worldToMap(world, map), map)
    expect(restored.x).toBeCloseTo(world.x)
    expect(restored.y).toBeCloseTo(world.y)
  })
})

