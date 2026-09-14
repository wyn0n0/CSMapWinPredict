import { describe, expect, it } from 'vitest'
import { mapConfigs, mapToWorld, worldToMap, worldYawToMapRotation } from './maps'

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

describe('world yaw conversion', () => {
  it.each([
    { yaw: 0, rotation: 90 },
    { yaw: 90, rotation: 0 },
    { yaw: 180, rotation: 270 },
    { yaw: -90, rotation: 180 },
    { yaw: 450, rotation: 0 },
  ])('maps world yaw $yaw to SVG rotation $rotation', ({ yaw, rotation }) => {
    expect(worldYawToMapRotation(yaw)).toBe(rotation)
  })
})
