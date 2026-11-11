import fs from 'node:fs';
import path from 'node:path';
import {summarizeRun} from './launcher-open-close-analysis.mjs';
const root = path.resolve(process.argv[2]);
const result = [];
for (const name of fs.readdirSync(root)) {
  const directory = path.join(root, name);
  if (!fs.existsSync(path.join(directory, 'report.json'))) continue;
  const report = JSON.parse(fs.readFileSync(path.join(directory,'report.json'), 'utf8'));
  const traces = fs.readdirSync(path.join(directory,'traces')).filter(f=>f.endsWith('.json'))
    .map(f=>JSON.parse(fs.readFileSync(path.join(directory,'traces',f),'utf8')));
  const summary = summarizeRun(report,traces);
  const row = {mode:report.ablation,material:report.material,qualityChangedForDiagnostic:report.qualityChangedForDiagnostic,
    displayedFrames:'unmeasured',...summary};
  result.push(row);
  console.log(`${row.material} ${row.mode}: open ${row.directions.open.callbackUpdatesPerSecond.p50.toFixed(1)} callbacks/s P95 ${row.directions.open.gapMilliseconds.p95.toFixed(2)} ms; close ${row.directions.close.callbackUpdatesPerSecond.p50.toFixed(1)} callbacks/s P95 ${row.directions.close.gapMilliseconds.p95.toFixed(2)} ms`);
}
fs.writeFileSync(path.join(root,'analysis.json'),JSON.stringify({note:'Diagnostic removals/frozen textures are not acceptable product variants or visual acceptance.',result},null,2));
