import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { dotnetGroups, planTests, runTests } from '../../tools/run-tests.mjs'

test('several groups build once and aliases do not run the same verifier twice', () => {
  const commands = planTests(['api', 'semantics', 'api', 'situation:export:index', 'situation:stage6:index'])
  assert.equal(commands.filter(c => c[1] === 'build').length, 1)
  assert.equal(commands.length, 4)
  assert.ok(commands[0].includes('.artifacts/dotnet'))
})

test('web-only skips .NET and review includes its browser logic checks once', () => {
  assert.equal(planTests(['web', 'web']).length, 1)
  const commands = planTests(['situation:stage4:review', 'situation:stage4:review'])
  assert.equal(commands.length, 3)
  assert.ok(commands[2].includes('tests/situation-review-ui/core.test.mjs'))
})

test('unknown groups fail before execution and a failed build stops the suite', () => {
  let calls = 0
  assert.equal(runTests(['api', 'unknown'], () => { calls++; return { status: 0 } }), 2)
  assert.equal(calls, 0)
  assert.equal(runTests(['api', 'semantics'], () => { calls++; return { status: 7 } }), 7)
  assert.equal(calls, 1)
})

test('mapped flags exist and stage six includes the current source checks', () => {
  const source = readFileSync(new URL('../CsDemoMap.Api.Tests/Program.cs', import.meta.url), 'utf8')
  for (const flag of Object.values(dotnetGroups).flat()) assert.ok(source.includes(`"${flag}"`), flag)
  assert.ok(dotnetGroups['situation:stage6'].includes('--verify-situation-stage6-source'))
  assert.deepEqual(dotnetGroups['situation:export'], ['--verify-situation-stage-six-automatic'])
})

test('CLI help covers dispatcher commands', () => {
  const source = readFileSync(new URL('../../apps/cli/DeveloperCommandDispatcher.cs', import.meta.url), 'utf8')
  const help = readFileSync(new URL('../../apps/cli/DeveloperCommandHelp.cs', import.meta.url), 'utf8')
  const commands = source.match(/--(?:export|verify|freeze|serve|sample|analyze|inspect|trace|rebuild|run|create)-[a-z0-9-]+/g)
  for (const command of new Set(commands)) assert.ok(help.includes(command), command)
})
