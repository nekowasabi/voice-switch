// Train-only failure breakdown for one variant. Reads results.jsonl rows whose id is in _state.train_ids.
// usage: node analyze_train.mjs <variant-dir-name> [examples]
import { readFileSync } from 'node:fs';
const flow = new URL('.', import.meta.url).pathname;
const v = process.argv[2], nEx = +(process.argv[3] || 0);
const st = JSON.parse(readFileSync(flow + '_state.json', 'utf8'));
const train = new Set(st.train_ids), test = new Set(st.test_ids);
const rows = readFileSync(`${flow}${v}/results.jsonl`, 'utf8').trim().split('\n').map(JSON.parse);
const mean = s => { const r = rows.filter(x => s.has(x.prompt_id)); return (r.reduce((a, x) => a + x.grade.pass, 0) / r.length).toFixed(3); };
console.log(`${v}: train ${mean(train)} test ${mean(test)}`);
const tr = rows.filter(r => train.has(r.prompt_id) && r.rep === 0);
const cat = {}, ex = {};
for (const r of tr) {
  const k = r.tags[0], vs = r.meta.verdicts;
  const h = vs.find(x => /^(early-wake|wake|early-dictate|dictate)@/.test(x));
  let c;
  if (k === 'nonwake') c = r.grade.pass ? 'clean' : 'falsewake:' + h.split('@')[0];
  else if (!h) c = 'miss';
  else if (r.grade.pass) c = 'pass';
  else { const l = r.grade.latency_ms; c = `late:${h.split('@')[0]}:${l < 400 ? '300-400' : l < 600 ? '400-600' : l < 1000 ? '600-1000' : '>1s'}`; }
  const key = `${k} ${c}`; cat[key] = (cat[key] || 0) + 1; (ex[key] ||= []).push(`${r.prompt_id} end=${r.meta.wake_end_ms} lat=${r.grade.latency_ms} ${JSON.stringify(vs)}`);
}
for (const k of Object.keys(cat).sort()) { console.log(`${cat[k]}\t${k}`); for (const e of ex[k].slice(0, nEx)) console.log('    ' + e); }
