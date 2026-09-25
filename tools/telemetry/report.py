#!/usr/bin/env python3
"""Build an offline, self-contained study report from validated aggregate summaries.

Never pools percentiles: summaries contain quantiles, not mergeable histograms.
Match IDs deduplicate uploads. Build/schema/protocol are separate cohorts.
"""
import argparse
from collections import defaultdict
import html
import json
from pathlib import Path
from collector import valid

WEAPONS = ['Power Beam', 'Volt Driver', 'Missile', 'Battlehammer', 'Imperialist', 'Judicator', 'Magmaul', 'Shock Coil', 'Omega', 'Platform', 'Enemy', 'Alt contact / bomb']
RTT = ['0–50', '50–100', '100–150', '150–200', '200–250', '250–300', '300–400', '400+', 'Unknown']
JITTER = ['0–10', '10–25', '25–50', '50–80', '80+', 'Unknown']


def read_matches(directory):
    matches, seen, rejected, duplicates = [], set(), [], 0
    for path in sorted(directory.rglob('match-*.json')):
        try:
            item = json.loads(path.read_text(), parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
            if not valid(item): raise ValueError('unsupported or invalid schema')
            h = item['header']; key = (h['schema'], h['protocol'], h['buildCommit'], h['matchSessionId'])
            if key in seen:
                duplicates += 1; continue
            seen.add(key); matches.append(item)
        except (OSError, ValueError, TypeError, KeyError) as error:
            rejected.append({'file': path.name, 'reason': str(error)})
    return matches, rejected, duplicates


def distribution(samples):
    present = [s for s in samples if s and s['count'] > 0]
    count = sum(s['count'] for s in present)
    return {'count': count, 'mean': sum(s['count'] * s['mean'] for s in present) / count if count else None,
            'maximum': max((s['maximum'] for s in present), default=None),
            'matchP50Range': [min(s['p50'] for s in present), max(s['p50'] for s in present)] if present else None,
            'matchP95Range': [min(s['p95'] for s in present), max(s['p95'] for s in present)] if present else None,
            'matchP99Range': [min(s['p99'] for s in present), max(s['p99'] for s in present)] if present else None}


def summarize(matches):
    cells = defaultdict(list)
    for match in matches:
        for cell in match['lagComp']:
            cells[(cell['weapon'], cell['rttBucket'], cell['jitterBucket'])].append(cell)
    lag = []
    for key, values in sorted(cells.items()):
        row = dict(zip(('weapon', 'rttBucket', 'jitterBucket'), key))
        for metric in ('requested', 'plausible', 'displacement'): row[metric] = distribution([v[metric] for v in values])
        for metric in ('globalClamps', 'shadowClamps', 'hitsOutside', 'rescuesOutside', 'missesOutside', 'hitsInside', 'rescuesInside', 'missesInside', 'unknownOutcomes'):
            row[metric] = sum(v[metric] for v in values) if all(metric in v for v in values) else None
        lag.append(row)
    net = [m['networkDetails'] for m in matches if 'networkDetails' in m]
    life = [m['lifecycleDetails'] for m in matches if 'lifecycleDetails' in m]
    details = [m['combatDetails'] for m in matches if 'combatDetails' in m]
    sums = lambda source, field, size: [sum(m[field][i] for m in source) for i in range(size)] if source else None
    return {'matches': len(matches), 'maps': sorted({m['header']['map'] for m in matches}),
            'durationSeconds': sum(m['durationSeconds'] for m in matches), 'lagComp': lag,
            'networkSamples': len(net), 'rttBuckets': sums(net, 'rttBuckets', 9), 'jitterBuckets': sums(net, 'jitterBuckets', 6),
            'rtt': distribution([n['rttMilliseconds'] for n in net]), 'jitter': distribution([n['jitterMilliseconds'] for n in net]),
            'combatAckLatency': distribution([m['combatAckLatency'] for m in matches]),
            'formDuration': distribution([m['formDuration'] for m in matches]),
            'serverStepMilliseconds': distribution([m['serverStepMilliseconds'] for m in matches]),
            'joinMilliseconds': distribution([m['joinMilliseconds'] for m in life]),
            'loadMilliseconds': distribution([m['loadMilliseconds'] for m in life]),
            'bootstrapMilliseconds': distribution([m['bootstrapMilliseconds'] for m in life]),
            'rejoinMilliseconds': distribution([m['rejoinMilliseconds'] for m in life]),
            'forcedForms': sum(m['forcedForms'] for m in matches), 'droppedTicks': sum(m['droppedTicks'] for m in matches),
            'eventsDropped': sum(m['counters']['eventsDropped'] for m in matches),
            'writerFailures': sum(m['counters']['writerFailures'] for m in matches),
            'claims': sums(matches, 'claims', 16),
            'combat': {key: sum(m[key] for m in details) for key in details[0]} if details else None,
            'shadowOutcomes': sums([m for m in matches if 'shadowOutcomes' in m], 'shadowOutcomes', 7)}


def render(data, title):
    payload = json.dumps(data, allow_nan=False).replace('<', '\\u003c').replace('&', '\\u0026')
    return '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>''' + html.escape(title) + '''</title><style>
body{font:16px system-ui;margin:0;background:#101820;color:#e7edf3}main{max-width:1180px;margin:auto;padding:32px}h1{font-size:30px}h2{font-size:21px;margin-top:32px}p{line-height:1.6;color:#b8c7d5}select{padding:9px;background:#1e303e;color:white;border:1px solid #7fa4b7;max-width:100%}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:12px}.card{padding:18px;background:#1e303e;border-radius:8px}.card strong{display:block;font-size:25px;color:#6ce0c1}table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;padding:9px;border-bottom:1px solid #334552} .scroll{overflow:auto}.bar{display:inline-block;background:#6ce0c1;height:12px}code{overflow-wrap:anywhere}.notice{padding:15px;border-left:4px solid #f3bb64;background:#2b2c26}
</style><main><h1>''' + html.escape(title) + '''</h1><p>Anonymous server measurements. Select one build/schema cohort. Scripted sessions and human matches must be supplied as separate input directories.</p><select id="cohort" aria-label="Build cohort"></select><p id="coverage" class="notice"></p><div id="cards" class="cards"></div><h2>Connection samples</h2><div id="network" class="scroll"></div><h2>Timing and reconciliation</h2><p>Means are weighted by sample count. Percentile ranges are the lowest and highest per-match quantiles; they are not pooled population percentiles. Unknown values are never treated as zero.</p><div id="timing" class="scroll"></div><h2>Lag policy by weapon and connection</h2><p>Hit/rescue counts describe observed current-policy outcomes inside or outside the proposed window. They do not establish a counterfactual miss, causality or fairness. Unknown geometry remains unknown.</p><div id="lag" class="scroll"></div><h2>Combat and data quality</h2><div id="quality"></div><p id="sources"></p></main><script>
const data=''' + payload + ''';
const $=id=>document.getElementById(id), esc=x=>String(x).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const rate=(part,total)=>total>0?(100*part/total).toFixed(2)+'%':'Unknown';
const quantiles=d=>['matchP50Range','matchP95Range','matchP99Range'].map(k=>d[k]?d[k].map(n).join('–'):'Unknown').join(' / ');
const n=x=>x===null||x===undefined?'Unknown':Number(x).toLocaleString(undefined,{maximumFractionDigits:2});
function table(headers,rows){return '<table><thead><tr>'+headers.map(x=>'<th>'+esc(x)+'</th>').join('')+'</tr></thead><tbody>'+rows.map(r=>'<tr>'+r.map(x=>'<td>'+esc(x)+'</td>').join('')+'</tr>').join('')+'</tbody></table>'}
function show(){const c=data.cohorts[$('cohort').value]; if(!c)return; const s=c.summary;
$('coverage').textContent=s.matches+' matches across '+s.maps.length+' maps. '+(s.matches<data.minimumMatches?'Sample below the configured study target. ':'Match-count target reached; map, weapon, connection and unknown-outcome coverage still require review. ')+'Enforcement remains disabled.';
$('cards').innerHTML=[['Matches',s.matches],['Shot timing samples',s.lagComp.reduce((a,b)=>a+b.requested.count,0)],['Dropped telemetry',s.eventsDropped],['Forced form corrections',s.forcedForms]].map(x=>'<div class="card"><strong>'+n(x[1])+'</strong>'+esc(x[0])+'</div>').join('');
const rtt=['0–50','50–100','100–150','150–200','200–250','250–300','300–400','400+','Unknown'],jitter=['0–10','10–25','25–50','50–80','80+','Unknown'];
$('network').innerHTML=table(['RTT ms','Samples','Jitter ms','Samples'],rtt.map((v,i)=>[v,n(s.rttBuckets?.[i]),jitter[i]??'',i<6?n(s.jitterBuckets?.[i]):'']));
const timing=['rtt','jitter','combatAckLatency','formDuration','serverStepMilliseconds','joinMilliseconds','loadMilliseconds','bootstrapMilliseconds','rejoinMilliseconds'];
$('timing').innerHTML=table(['Metric (ms, except form frames)','Samples','Mean','p50 range','p95 range','p99 range','Max'],timing.map(k=>{const v=s[k];return [k,n(v.count),n(v.mean),...['matchP50Range','matchP95Range','matchP99Range'].map(q=>v[q]?v[q].map(n).join(' – '):'Unknown'),n(v.maximum)]}));
const weapons=''' + json.dumps(WEAPONS) + ''';
$('lag').innerHTML=table(['Weapon','RTT','Jitter','Shots','Requested mean; p50/p95/p99 ranges','Plausible mean; p50/p95/p99 ranges','Hits outside/observed (%)','Rescues outside/observed (%)','Displacement mean/max'],s.lagComp.map(v=>[weapons[v.weapon]??v.weapon,rtt[v.rttBucket],jitter[v.jitterBucket],n(v.requested.count),n(v.requested.mean)+'; '+quantiles(v.requested),n(v.plausible.mean)+'; '+quantiles(v.plausible),n(v.hitsOutside)+' / '+n(v.hitsInside===null?null:v.hitsInside+v.hitsOutside)+' ('+rate(v.hitsOutside,v.hitsInside===null?null:v.hitsInside+v.hitsOutside)+')',n(v.rescuesOutside)+' / '+n(v.rescuesInside===null?null:v.rescuesInside+v.rescuesOutside)+' ('+rate(v.rescuesOutside,v.rescuesInside===null?null:v.rescuesInside+v.rescuesOutside)+')',n(v.displacement.mean)+' / '+n(v.displacement.maximum)]));
$('quality').innerHTML=table(['Measure','Value'],[['Dropped ticks',n(s.droppedTicks)],['Writer failures',n(s.writerFailures)],['Unknown shadow geometry',n(s.shadowOutcomes?.[6])],['Settled reported predictions',n(s.combat?.settledPredictions)],['Damage corrections',n(s.combat?.damageCorrections)],['Health corrections',n(s.combat?.healthCorrections)],['Headshot corrections',n(s.combat?.headshotCorrections)],['Claim rescued',n(s.claims?.[0])],['Claim already resolved',n(s.claims?.[1])],['Claim rescue rate',rate(s.claims?.[0],s.claims?.reduce((a,b)=>a+b,0))],['Claim rejection rate',rate(s.claims?.slice(2,13).reduce((a,b)=>a+b,0),s.claims?.reduce((a,b)=>a+b,0))]]);
$('sources').textContent='Cohort: '+c.key+'; maps: '+s.maps.join(', ')+'. Excluded invalid files: '+data.rejected.length+'; duplicate uploads: '+data.duplicates+'.';}
data.cohorts.forEach((c,i)=>{let o=document.createElement('option');o.value=i;o.textContent=c.key;$('cohort').append(o)});$('cohort').onchange=show;show();if(!data.cohorts.length)$('coverage').textContent='No valid match summaries. No study conclusions are available.';
</script></html>'''


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);p.add_argument('--output',type=Path,required=True);p.add_argument('--minimum-matches',type=int,default=1000);p.add_argument('--title',default='Project Prime network study');a=p.parse_args()
    matches,rejected,duplicates=read_matches(a.directory);groups=defaultdict(list)
    for m in matches:
        h=m['header'];groups[f"Protocol {h['protocol']} · schema {h['schema']} · {h['buildCommit']}"].append(m)
    data={'cohorts':[{'key':k,'summary':summarize(v)} for k,v in sorted(groups.items())], 'rejected':rejected,'duplicates':duplicates,'minimumMatches':max(1,a.minimum_matches)}
    a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(render(data,a.title));a.output.with_suffix('.json').write_text(json.dumps(data,indent=2,allow_nan=False)+'\n')
    print(f'{len(matches)} valid matches, {len(groups)} build cohorts, {len(rejected)} rejected files, {duplicates} duplicate uploads; wrote {a.output}')

if __name__=='__main__':main()
