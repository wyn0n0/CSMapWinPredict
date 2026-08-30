import type { DemoManifest, DemoWindow } from '../domain/timeline'

interface DemoImportAccepted {
  id: string
  status: string
}

interface DemoImportStatus extends DemoImportAccepted {
  fileName: string
  fileSizeBytes: number
  error: string | null
  manifest: DemoManifest | null
}

export interface ImportedDemo {
  id: string
  manifest: DemoManifest
  firstWindow: DemoWindow
}

export interface OfflineDemoFile {
  fileName: string
  fileSizeBytes: number
  lastWriteTimeUtc: string
}

export interface OfflineDemoCatalog {
  rootPath: string
  count: number
  files: OfflineDemoFile[]
}

export type ImportStage = 'uploading' | 'queued' | 'parsing' | 'chunking' | 'loading'

export async function importDemo(
  file: File,
  onStage: (stage: ImportStage) => void = () => {},
): Promise<ImportedDemo> {
  onStage('uploading')
  const body = new FormData()
  body.append('file', file)

  const response = await fetch('/api/demos/import', { method: 'POST', body })
  const accepted = await readJson<DemoImportAccepted>(response, '上传失败')
  onStage(normalizeStage(accepted.status))
  return waitForImportedDemo(accepted, onStage)
}

export async function listOfflineDemos(): Promise<OfflineDemoCatalog> {
  const response = await fetch('/api/demos/offline', { cache: 'no-store' })
  return readJson<OfflineDemoCatalog>(response, '读取离线 Demo 列表失败')
}

export async function importOfflineDemo(
  fileName: string,
  onStage: (stage: ImportStage) => void = () => {},
): Promise<ImportedDemo> {
  const response = await fetch('/api/demos/offline/import', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ fileName }),
  })
  const accepted = await readJson<DemoImportAccepted>(response, '离线 Demo 导入失败')
  onStage(normalizeStage(accepted.status))
  return waitForImportedDemo(accepted, onStage)
}

async function waitForImportedDemo(
  accepted: DemoImportAccepted,
  onStage: (stage: ImportStage) => void,
): Promise<ImportedDemo> {
  let status: DemoImportStatus
  do {
    await delay(1000)
    const statusResponse = await fetch(`/api/demos/${accepted.id}/status`, { cache: 'no-store' })
    status = await readJson<DemoImportStatus>(statusResponse, '读取解析状态失败')
    if (status.status === 'failed')
      throw new Error(status.error ? `无法解析 demo：${status.error}` : 'Demo 解析失败。')
    if (status.status !== 'completed') onStage(normalizeStage(status.status))
  } while (status.status !== 'completed' || !status.manifest)

  onStage('loading')
  const firstWindow = await loadDemoWindow(accepted.id, 0)
  return { id: accepted.id, manifest: status.manifest, firstWindow }
}

export async function loadDemoWindow(id: string, index: number): Promise<DemoWindow> {
  const response = await fetch(`/api/demos/${id}/windows/${index}`)
  const window = await readJson<DemoWindow>(response, '载入时间窗口失败')
  return {
    ...window,
    frames: window.frames ?? [],
    utilityTracks: window.utilityTracks ?? [],
    utilityEffects: window.utilityEffects ?? [],
    playerUtilityStates: window.playerUtilityStates ?? [],
    playerEquipmentStates: window.playerEquipmentStates ?? [],
  }
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  const payload = await response.json().catch(() => null) as T | { error?: string } | null
  if (!response.ok) {
    const message = payload && typeof payload === 'object' && 'error' in payload ? payload.error : undefined
    throw new Error(message ?? `${fallback}（HTTP ${response.status}）`)
  }
  if (!payload) throw new Error(`${fallback}：响应为空。`)
  return payload as T
}

function normalizeStage(status: string): ImportStage {
  return status === 'queued' || status === 'parsing' || status === 'chunking' ? status : 'queued'
}

function delay(milliseconds: number) {
  return new Promise(resolve => window.setTimeout(resolve, milliseconds))
}
