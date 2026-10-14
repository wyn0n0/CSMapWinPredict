/** Bounded test groups; one isolated .NET build per invocation. */
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

export const dotnetGroups = {
  "situation:stage4:freeze": [
    "--verify-situation-review-freezer"
  ],
  "situation:raycast": [
    "--verify-situation-raycast"
  ],
  "situation:stage4:review": [
    "--verify-situation-review-automatic"
  ],
  "api": [
    "--verify-win-data-pipeline"
  ],
  "semantics": [
    "--verify-semantics"
  ],
  "situation:stage3": [
    "--verify-situation-stage-three-automatic"
  ],
  "situation:stage4:split": [
    "--verify-situation-stage-four-split"
  ],
  "situation:stage4:contracts": [
    "--verify-situation-stage-four-contracts"
  ],
  "situation:stage4:eligibility": [
    "--verify-situation-stage-four-eligibility"
  ],
  "situation:stage4:selection": [
    "--verify-situation-stage-four-selection"
  ],
  "situation:stage4:pilot": [
    "--verify-situation-stage-four-pilot"
  ],
  "situation:stage4:review-candidates": [
    "--verify-situation-review-candidates-automatic"
  ],
  "situation:stage6:index": [
    "--verify-situation-stage-six-index"
  ],
  "situation:stage6:writer": [
    "--verify-situation-stage-six-writer"
  ],
  "situation:stage6:validator": [
    "--verify-situation-stage-six-validator"
  ],
  "situation:stage6:integration": [
    "--verify-situation-stage-six-integration"
  ],
  "situation:stage6": [
    "--verify-situation-stage6-source"
  ],
  "inference:dotnet": [
    "--verify-win-inference"
  ],
  "situation:export:index": [
    "--verify-situation-stage-six-index"
  ],
  "situation:export:writer": [
    "--verify-situation-stage-six-writer"
  ],
  "situation:export:validator": [
    "--verify-situation-stage-six-validator"
  ],
  "situation:export:integration": [
    "--verify-situation-stage-six-integration"
  ],
  "situation:export": [
    "--verify-situation-stage-six-automatic"
  ]
}
const root = fileURLToPath(new URL('../', import.meta.url))
const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm'
const testDll = '.artifacts/dotnet/bin/CsDemoMap.Api.Tests/release/CsDemoMap.Api.Tests.dll'

export function planTests(groups = ['web', 'api', 'semantics']) {
  if (!groups.length) groups = ['web', 'api', 'semantics']
  for (const group of groups) {
    if (group !== 'web' && !Object.hasOwn(dotnetGroups, group))
      throw new Error(`Unknown test group: ${group}`)
  }
  const commands = []
  if (groups.some(group => Object.hasOwn(dotnetGroups, group))) {
    commands.push(['dotnet', 'build', 'tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj',
      '-c', 'Release', '--artifacts-path', '.artifacts/dotnet', '--ignore-failed-sources', '-p:NuGetAudit=false'])
  }
  const seen = new Set()
  for (const group of groups) {
    if (group === 'web') {
      if (!seen.has('web')) commands.push([npm, 'run', 'test', '--workspace', '@cs-demo-map/web'])
      seen.add('web')
      continue
    }
    for (const flag of dotnetGroups[group]) {
      if (!seen.has(flag)) commands.push(['dotnet', testDll, flag])
      seen.add(flag)
    }
    if (group === 'situation:stage4:review' && !seen.has('review-ui')) {
      commands.push([process.execPath, '--test', 'tests/situation-review-ui/core.test.mjs'])
      seen.add('review-ui')
    }
  }
  return commands
}

export function runTests(args, run = spawnSync) {
  if (args.includes('--help')) {
    console.log('Usage: npm run test:groups -- <group> [group ...]\nGroups: web, ' + Object.keys(dotnetGroups).join(', '))
    console.log('situation:stage6 = v8 source/purpose/sharding checks; situation:export = historical export pipeline.')
    return 0
  }
  let commands
  try { commands = planTests(args) }
  catch (error) { console.error(error.message); return 2 }
  for (const [command, ...commandArgs] of commands) {
    console.log(`> ${command} ${commandArgs.join(' ')}`)
    const result = run(command, commandArgs, { cwd: root, stdio: 'inherit', shell: command.endsWith('.cmd') })
    if (result.error) { console.error(result.error.message); return 1 }
    if (result.status !== 0) return result.status ?? 1
  }
  return 0
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  process.exitCode = runTests(process.argv.slice(2))
