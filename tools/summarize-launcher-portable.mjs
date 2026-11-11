import fs from 'node:fs';
import path from 'node:path';
import {analyzeDirectory} from './launcher-open-close-analysis.mjs';

const directory = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3]);
const analysis = analyzeDirectory(directory);
fs.writeFileSync(path.join(directory, 'analysis.json'), JSON.stringify(analysis, null, 2));
const median = values => {
  const sorted = [...values].sort((a,b) => a-b), middle = Math.floor(sorted.length/2);
  return sorted.length%2 ? sorted[middle] : (sorted[middle-1]+sorted[middle])/2;
};
const materials = Object.fromEntries(['FrostedGlass','AndroidLiquidGlass'].map(material => [material,
  Object.fromEntries(['A','B'].map(arm => {
    const runs = analysis.runs.filter(r => r.material===material && r.arm===arm).sort((a,b) => a.batch-b.batch);
    return [arm, {
      firstOpen: runs.map(r => {
        const first = r.operations.find(o => o.scenario==='first' && o.direction==='open');
        return {batch:r.batch, entryToFirstWpfPaintMilliseconds:first.entryToFirstWpfPaintMilliseconds,
          materialReady:first.firstPaintMaterialReady};
      }),
      directions: Object.fromEntries(['open','close'].map(direction => {
        const samples = runs.map(r => r.directions[direction]);
        return [direction, {
          operations:samples.reduce((n,s) => n+s.operations,0),
          callbackP95ByBatch:samples.map(s => s.gapMilliseconds.p95),
          medianBatchCallbackP95:median(samples.map(s => s.gapMilliseconds.p95)),
          callbackP99ByBatch:samples.map(s => s.gapMilliseconds.p99),
          medianBatchCallbackRateP50:median(samples.map(s => s.callbackUpdatesPerSecond.p50)),
          entryToFirstWpfPaintP95ByBatch:samples.map(s => s.entryToFirstWpfPaintMilliseconds?.p95 ?? null),
          preparationP50ByBatch:samples.map(s => s.entryToAnimationMilliseconds.p50),
          gapsAbove25:samples.reduce((n,s) => n+s.gapsAbove25,0),
          firstPaintReady:samples.reduce((n,s) => n+s.firstPaintReady,0),
        }];
      })),
      qualityStatuses:runs.map(r => r.qualityStatus),
      unpreparedFirstPaintOperations:runs.flatMap(r => r.unpreparedFirstPaintOperations),
    }];
  }))]));
const result = {schemaVersion:1,scope:analysis.scope,displayAcceptance:'unmeasured',
  quantile:analysis.quantile,aggregation:'Median of three per-batch P95s; raw runs retained in analysis.json. These are callbacks, not displayed frames.',
  moduleHashes:analysis.runs[0].modules,materials};
fs.writeFileSync(output, JSON.stringify(result,null,2));
for (const [material,arms] of Object.entries(materials)) for (const direction of ['open','close'])
  console.log(`${material} ${direction}: median batch callback P95 ${arms.A.directions[direction].medianBatchCallbackP95.toFixed(2)} -> ${arms.B.directions[direction].medianBatchCallbackP95.toFixed(2)} ms`);
