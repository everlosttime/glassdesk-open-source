import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

export function statistics(values) {
  const sorted = values.filter(Number.isFinite).sort((a, b) => a - b);
  if (!sorted.length) return null;
  const rank = p => sorted[Math.max(0, Math.ceil(sorted.length * p) - 1)];
  return {count: sorted.length, p50: rank(.5), p95: rank(.95), p99: rank(.99), maximum: sorted.at(-1),
    mean: sorted.reduce((a,b) => a+b, 0) / sorted.length};
}

export function summarizeRun(report, traces) {
  if (!report.valid) throw Error('invalid fixture run');
  const indexed = new Map();
  for (const trace of traces) {
    if (trace.schemaVersion !== 2 || !(trace.qpcFrequency > 0) || !(trace.hwnd > 0)) throw Error('invalid trace metadata');
    if (trace.processId !== report.processId) throw Error('trace process does not match fixture');
    if (indexed.has(trace.requestId)) throw Error('duplicate trace request ID');
    indexed.set(trace.requestId, trace);
  }
  const operations = report.observations.map(o => {
    const trace = indexed.get(o.requestId);
    if (!trace) throw Error(`missing trace for request ${o.requestId}`);
    if (trace.direction !== o.direction || trace.outcome !== 'completed' || !o.settled || trace.samplesTruncated)
      throw Error(`incomplete/mismatched operation ${o.requestId}`);
    if (trace.animationQpc < trace.requestQpc || trace.endQpc < trace.animationQpc) throw Error('negative trace interval');
    const gaps = trace.frameSamples.map(f => f.gapMilliseconds).filter(g => g > 0);
    const stages = Object.fromEntries((trace.preparationStages ?? []).map(s => [s.stage, s.milliseconds]));
    return {...o, gapMilliseconds: statistics(gaps), callbacks: trace.frameSamples.length,
      animationMilliseconds: trace.animationMilliseconds,
      callbackUpdatesPerSecond: trace.frameSamples.length * 1000 / trace.animationMilliseconds,
      entryToAnimationMilliseconds: (trace.animationQpc - trace.requestQpc) * 1000 / trace.qpcFrequency,
      firstCallbackMilliseconds: trace.firstCallbackMilliseconds ?? null,
      finalizationMilliseconds: trace.finalizationMilliseconds ?? 0, stages,
      revealCaptureMilliseconds: trace.surfaceMetrics?.revealCaptureMilliseconds,
      revealRemapMilliseconds: trace.surfaceMetrics?.revealRemapMilliseconds,
      revealFilterMilliseconds: trace.surfaceMetrics?.revealFilterMilliseconds,
      _trace: trace};
  });
  const directions = Object.fromEntries(['open','close'].map(direction => {
    const active = operations.filter(o => o.scenario === 'repeat' && o.direction === direction);
    const gaps = active.flatMap(o => o._trace.frameSamples.map(f => f.gapMilliseconds).filter(g => g > 0));
    const stages = [...new Set(active.flatMap(o => Object.keys(o.stages)))];
    return [direction, {operations: active.length, callbacks: active.reduce((n,o) => n+o.callbacks, 0),
      gapMilliseconds: statistics(gaps), callbackUpdatesPerSecond: statistics(active.map(o => o.callbackUpdatesPerSecond)),
      dispatchMilliseconds: statistics(active.map(o => o.dispatchMilliseconds)),
      entryToAnimationMilliseconds: statistics(active.map(o => o.entryToAnimationMilliseconds)),
      entryToFirstWpfPaintMilliseconds: statistics(active.map(o => o.entryToFirstWpfPaintMilliseconds)),
      animationMilliseconds: statistics(active.map(o => o.animationMilliseconds)),
      finalizationMilliseconds: statistics(active.map(o => o.finalizationMilliseconds)),
      firstPaintReady: active.filter(o => o.firstPaintMaterialReady === true).length,
      gapsAbove8_33: gaps.filter(g => g > 8.33).length, gapsAbove16_67: gaps.filter(g => g > 16.67).length,
      gapsAbove25: gaps.filter(g => g > 25).length,
      cpuUpdateMilliseconds: statistics(active.flatMap(o => o._trace.frameSamples.map(f => f.updateMilliseconds))),
      frameWork: Object.fromEntries(['geometryMilliseconds','glassMilliseconds','shadowMilliseconds','contentMilliseconds']
        .map(key => [key, statistics(active.flatMap(o => o._trace.frameSamples.map(f => f[key])))])),
      stages: Object.fromEntries(stages.map(s => [s, statistics(active.map(o => o.stages[s]))]))}];
  }));
  const unpreparedFirstPaintOperations = operations.filter(o => o.direction === 'open'
    && o.entryToFirstWpfPaintMilliseconds != null && o.firstPaintMaterialReady !== true)
    .map(o => ({scenario: o.scenario, requestId: o.requestId}));
  return {displayAcceptance: 'unmeasured',
    qualityStatus: unpreparedFirstPaintOperations.length ? 'failed-first-paint-readiness' : 'readiness-only-not-full-visual-acceptance',
    unpreparedFirstPaintOperations, directions,
    operations: operations.map(({_trace, activeCapture, metrics, ...o}) => o),
    traceCount: traces.length, observedOperations: operations.length,
    reversalTraces: traces.filter(t => t.outcome === 'reversed').length,
    otherOutcomes: traces.filter(t => !['completed','reversed'].includes(t.outcome)).map(t => ({requestId:t.requestId, outcome:t.outcome}))};
}

export function analyzeDirectory(directory) {
  if (fs.existsSync(path.join(directory, 'EXCLUDED.json'))) throw Error('excluded experiment');
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'manifest.json'), 'utf8').replace(/^\uFEFF/, ''));
  if (manifest.runs.length !== manifest.batches * 4) throw Error('incomplete A/B manifest');
  let moduleHashes;
  const runs = manifest.runs.map(run => {
    if (run.exitCode !== 0) throw Error('failed fixture exit');
    const report = JSON.parse(fs.readFileSync(path.join(run.directory, 'report.json'), 'utf8'));
    const hashes = JSON.stringify(report.modules.map(m => m.sha256));
    if (moduleHashes && hashes !== moduleHashes) throw Error('mixed runtime modules');
    moduleHashes = hashes;
    const traces = fs.readdirSync(path.join(run.directory, 'traces')).filter(f => f.endsWith('.json'))
      .map(f => JSON.parse(fs.readFileSync(path.join(run.directory, 'traces', f), 'utf8')));
    const summary = summarizeRun(report, traces);
    if (summary.directions.open.operations !== manifest.cycles || summary.directions.close.operations !== manifest.cycles)
      throw Error('wrong repeat sample count');
    return {...run, refresh: report.refresh, placementReuse: report.placementReuse,
      displayCadence: report.displayCadence, modules: report.modules, ...summary};
  });
  const comparisons = [];
  for (let batch = 1; batch <= manifest.batches; batch++) for (const material of ['FrostedGlass','AndroidLiquidGlass']) {
    const a = runs.find(r => r.batch === batch && r.material === material && r.arm === 'A');
    const b = runs.find(r => r.batch === batch && r.material === material && r.arm === 'B');
    if (!a || !b || JSON.stringify(a.refresh) !== JSON.stringify(b.refresh)) throw Error('unpaired run or changed refresh');
    for (const direction of ['open','close']) {
      const x = a.directions[direction], y = b.directions[direction];
      comparisons.push({batch, material, direction,
        callbackP95A: x.gapMilliseconds.p95, callbackP95B: y.gapMilliseconds.p95,
        callbackP95ChangePercent: (y.gapMilliseconds.p95/x.gapMilliseconds.p95-1)*100,
        prepareP50A: x.entryToAnimationMilliseconds.p50, prepareP50B: y.entryToAnimationMilliseconds.p50,
        firstWpfPaintP95A: x.entryToFirstWpfPaintMilliseconds?.p95 ?? null,
        firstWpfPaintP95B: y.entryToFirstWpfPaintMilliseconds?.p95 ?? null,
        displayAcceptance: 'unmeasured'});
    }
  }
  return {schemaVersion: 1, candidate: manifest.candidate, quantile: 'nearest rank ceil(N*p), gaps exclude first callback and idle',
    scope: 'component fixture; callbacks/CPU WPF paint only, not displayed frames or product click latency',
    displayAcceptance: 'unmeasured', power: manifest.power, runs, comparisons};
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const directory = path.resolve(process.argv[2]);
  const result = analyzeDirectory(directory);
  fs.writeFileSync(path.join(directory, 'analysis.json'), JSON.stringify(result, null, 2));
  for (const c of result.comparisons) console.log(`${c.material} batch ${c.batch} ${c.direction}: callback P95 ${c.callbackP95A.toFixed(2)} -> ${c.callbackP95B.toFixed(2)} ms; prepare P50 ${c.prepareP50A.toFixed(2)} -> ${c.prepareP50B.toFixed(2)} ms; display unmeasured`);
}
