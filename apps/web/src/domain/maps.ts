export interface MapConfig {
  name: string
  label: string
  posX: number
  posY: number
  scale: number
  imageUrl?: string
  lowerImageUrl?: string
  lowerAltitudeMax?: number
}

export interface MapPoint {
  x: number
  y: number
}

export const mapConfigs: Record<string, MapConfig> = {
  de_cache: {
    name: 'de_cache',
    label: 'Cache',
    posX: -2000,
    posY: 3250,
    scale: 5.5,
    imageUrl: '/radars/simpleradar/de_cache.webp',
  },
  de_dust2: {
    name: 'de_dust2',
    label: 'Dust II',
    posX: -2476,
    posY: 3239,
    scale: 4.4,
    imageUrl: '/radars/simpleradar/de_dust2.webp',
  },
  de_mirage: {
    name: 'de_mirage',
    label: 'Mirage',
    posX: -3230,
    posY: 1713,
    scale: 5,
    imageUrl: '/radars/simpleradar/de_mirage.webp',
  },
  de_nuke: {
    name: 'de_nuke',
    label: 'Nuke',
    posX: -3453,
    posY: 2887,
    scale: 7,
    imageUrl: '/radars/simpleradar/de_nuke.webp',
    lowerImageUrl: '/radars/simpleradar/de_nuke_lower.webp',
    lowerAltitudeMax: -495,
  },
}

export const fallbackMap: MapConfig = {
  name: 'unknown',
  label: 'Unknown map',
  posX: -2560,
  posY: 2560,
  scale: 5,
}

export function getMapConfig(name: string): MapConfig {
  return mapConfigs[name] ?? { ...fallbackMap, name, label: name || fallbackMap.label }
}

export function worldToMap(point: MapPoint, config: MapConfig): MapPoint {
  return {
    x: (point.x - config.posX) / config.scale,
    y: (config.posY - point.y) / config.scale,
  }
}

export function mapToWorld(point: MapPoint, config: MapConfig): MapPoint {
  return {
    x: config.posX + point.x * config.scale,
    y: config.posY - point.y * config.scale,
  }
}
