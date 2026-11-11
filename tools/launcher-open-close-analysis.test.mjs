import test from 'node:test';
import assert from 'node:assert/strict';
const api = await import('./launcher-open-close-analysis.mjs').catch(() => ({}));

test('nearest-rank P95 excludes a single top outlier among twenty gaps', () => {
  assert.equal(typeof api.statistics, 'function');
  assert.equal(api.statistics(Array.from({length: 20}, (_, i) => i + 1)).p95, 19);
});

const report = () => ({valid: true, processId: 10, cycles: 1, refresh: {Hertz: 240}, observations: [
  {scenario: 'repeat', cycle: 1, direction: 'open', requestId: 1, settled: true, dispatchMilliseconds: 2,
    entryToFirstWpfPaintMilliseconds: 3, firstPaintMaterialReady: true}
]});
const trace = () => ({schemaVersion: 2, processId: 10, hwnd: 100, requestId: 1, direction: 'open',
  outcome: 'completed', qpcFrequency: 1000, requestQpc: 100, animationQpc: 102, endQpc: 122,
  animationMilliseconds: 20, frameSamples: [
    {qpc: 106, gapMilliseconds: 0, updateMilliseconds: 1},
    {qpc: 110, gapMilliseconds: 4, updateMilliseconds: 1},
    {qpc: 114, gapMilliseconds: 4, updateMilliseconds: 1}], preparationStages: [], surfaceMetrics: {}});

test('fast callbacks cannot pass a displayed-frame acceptance gate', () => {
  assert.equal(typeof api.summarizeRun, 'function');
  const result = api.summarizeRun(report(), [trace()]);
  assert.equal(result.displayAcceptance, 'unmeasured');
  assert.equal(result.directions.open.gapMilliseconds.p95, 4);
  assert.equal(result.directions.open.entryToAnimationMilliseconds.p50, 2);
});

test('a completed lifecycle with an unprepared cold first paint fails quality', () => {
  const r = report();
  r.observations[0].scenario = 'first';
  r.observations[0].firstPaintMaterialReady = false;
  assert.equal(api.summarizeRun(r, [trace()]).qualityStatus, 'failed-first-paint-readiness');
});

test('missing trace cannot silently reduce the sample count', () => {
  assert.equal(typeof api.summarizeRun, 'function');
  assert.throws(() => api.summarizeRun(report(), []), /missing trace/);
});

test('a trace from a different process is rejected even with a matching request ID', () => {
  assert.equal(typeof api.summarizeRun, 'function');
  assert.throws(() => api.summarizeRun(report(), [{...trace(), processId: 99}]), /process/);
});

test('reversed and static observations do not contaminate repeat animation gaps', () => {
  assert.equal(typeof api.summarizeRun, 'function');
  const r = report();
  r.observations.push({...r.observations[0], scenario: 'reverse', requestId: 2});
  const t = {...trace(), requestId: 2, frameSamples: [{qpc: 110, gapMilliseconds: 1000, updateMilliseconds: 5}]};
  assert.equal(api.summarizeRun(r, [trace(), t]).directions.open.gapMilliseconds.maximum, 4);
});
