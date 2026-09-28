"""Local comparison page; only completed, uncontended controller receipts qualify."""
import argparse
import json
from pathlib import Path
from urllib.parse import quote
from metrics import GIB, atomic_json
from report import read_json, stage_category
from acceleration_graph import accelerator_evidence


def collect(root):
    budget = read_json(root/'budget.json', {})
    controllers = {item['Name']:item for item in budget.get('Runs',[])}
    rows=[]
    for path in sorted((root/'results').glob('*/manifest.json')):
        manifest=read_json(path)
        controller=controllers.get(manifest['name'],{})
        for run in manifest.get('runs',[]) or [dict(temperature='cold',status=manifest['status'])]:
            measurements=read_json(path.parent/run['temperature']/'measurements.json', {})
            valid = manifest['status']=='complete' and run['status']=='complete' and controller.get('Status')=='complete' and measurements.get('status')=='complete'
            stages={k:0. if valid else None for k in ('preparation','sampling','upscaling','decoding','archiving','saving')}
            for stage in measurements.get('stages',[]):
                category='archiving' if stage['name'].endswith('/SaveAnimatedWEBP') else stage_category(stage['name'])
                stages[category]=(stages[category] or 0.)+stage['seconds']
            def gib(field):
                value=measurements.get(field)
                return value/GIB if isinstance(value,(int,float)) else None
            log=path.parent/run['temperature']/'execution.log'
            # A native process abort can prevent its finally block from copying the log.
            if not log.exists(): log=root/(manifest['name']+'.stderr.log')
            evidence=accelerator_evidence(manifest['configuration'],log.read_text('utf-8',errors='replace')) if log.exists() else run.get('accelerator',{})
            row=dict(name=manifest['name'], configuration=manifest['configuration'], scene=manifest['scene'], seed=manifest['seed'],
                     frames=manifest['frames'], native=manifest['native'], temperature=run['temperature'],
                     status='complete' if valid else controller.get('Status',manifest['status']), valid=valid,
                     execution_seconds=run.get('execution_seconds'), worker_seconds=controller.get('Seconds'), stages=stages,
                     peak_gib=gib('sampled_peak_device_used'), free_gib=gib('sampled_minimum_device_free'), ram_gib=gib('peak_process_rss'),
                     classification=measurements.get('classification','unknown'), accelerator=evidence,
                     error=manifest.get('error'), dimensions=run.get('validation'),
                     evidence=quote(path.relative_to(root).as_posix(),safe='/'),
                     request=quote((path.parent/'request.json').relative_to(root).as_posix(),safe='/'),
                     video=quote((path.parent/run['file']).relative_to(root).as_posix(),safe='/') if valid and run.get('file') else None)
            rows.append(row)
    return rows


def build(root):
    rows=collect(root)
    atomic_json(root/'comparison.json',rows)
    body='''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>H3 acceleration comparisons</title><style>
:root{color-scheme:dark;font:15px system-ui;background:#16141a;color:#eee8f1}body{max-width:1450px;margin:auto;padding:24px}h1{margin-bottom:6px}p{color:#bfb6c9;line-height:1.5}button,select{background:#302838;color:inherit;border:1px solid #64546c;border-radius:7px;padding:9px;cursor:pointer}button:hover{background:#4b3955}a{color:#d8b7f3}header{margin-bottom:24px}.refs{display:flex;gap:12px;align-items:center;flex-wrap:wrap}.refs img{height:110px;border-radius:7px}.pair{display:grid;grid-template-columns:1fr 1fr;gap:20px;margin:20px 0}.pane{background:#221d28;border:1px solid #483c51;border-radius:12px;padding:14px;min-width:0}.pane select{width:100%}video{width:100%;aspect-ratio:7/4;background:#09070b;margin-top:12px}small{color:#bbb}dl{display:grid;grid-template-columns:1fr 1fr;gap:7px}dt,dd{margin:0}dd{text-align:right}table{border-collapse:collapse;width:100%;font-size:13px}th,td{padding:10px;text-align:left;border-bottom:1px solid #403747}th button{border:0;padding:0;background:transparent}tr[data-valid="false"]{color:#a899ad}.scroll{overflow:auto}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px}.actions{display:flex;flex-wrap:wrap;gap:10px}.muted{color:#ac9db5}@media(max-width:700px){body{padding:12px}.pair{grid-template-columns:1fr}h1{font-size:24px}}
</style><header><h1>H3 acceleration comparisons</h1><p>RTX 4000 Ada · 20 GiB · current W4A8 Ref2VA · SageAttention · neutral image and voice references</p>
<p>Matched prompt, references and seed. Upscaled preview samples at 832 × 480 and outputs 1344 × 768. Native samples directly at 1344 × 768. Cold means a fresh process; disk caches may be warm. Warm repeats disable node-output caching and execute sampling again.</p>
<details><summary>Frozen references and measurement notes</summary><div class="refs"><img src="inputs/face.png" alt="Face reference"><img src="inputs/outfit.png" alt="Outfit reference"><audio controls preload="none" src="inputs/voice.wav"></audio></div>
<p>Device peaks are sampled lower bounds, including other GPU users. PyTorch counters describe this process only and can be unavailable with cudaMallocAsync. At least 2 GiB observed free is provisionally comfortable. Video runtime includes preparation, sampling, upscaling, decode and saving; offline validation is excluded. Worker duration also includes startup and validation. Never compare interrupted runs.</p>
<p><a href="pins.json">Package revisions</a> · <a href="preflight.json">Checkpoint hashes</a> · <a href="weights.json">Extra weights</a> · <a href="references.json">Reference hashes</a> · <a href="budget.json">All attempts</a></p></details></header>
<div class="actions"><button id="play">Play both from start</button><button id="pause">Pause both</button><button onclick="location.reload()">Refresh results</button><span class="muted">Audio starts muted; unmute one clip to compare speech.</span></div>
<div class="pair"><section class="pane" id="left"></section><section class="pane" id="right"></section></div>
<h2>Measurements</h2><div class="scroll"><table><thead id="head"></thead><tbody id="rows"></tbody></table></div>
<h2>Review notes</h2><p>Review complete clips for likeness, outfit retention, framing, motion, flicker, detail, spoken words, voice similarity and lip synchronization. Measurements alone do not establish quality.</p><div id="notes"></div>
<script id="data" type="application/json">DATA</script><script>
const data=JSON.parse(document.getElementById('data').textContent);const good=data.filter(r=>r.video);const esc=s=>String(s??'—').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const title=r=>`${r.scene} · ${r.configuration} · ${r.frames}f · ${r.native?'Native':'Upscaled'} · ${r.seed} · ${r.temperature}`;
const num=(n,suffix='')=>n==null?'—':Number(n).toFixed(2)+suffix;
function pane(id,index){const el=document.getElementById(id);el.innerHTML='<label>Compare clip<select aria-label="'+id+' comparison clip">'+good.map((r,i)=>`<option value="${i}" ${i===index?'selected':''}>${esc(title(r))}</option>`).join('')+'</select></label><div class="detail"></div>';const sel=el.querySelector('select');function draw(){const r=good[Number(sel.value)];if(!r){el.querySelector('.detail').textContent='No completed clips yet.';return;}el.querySelector('.detail').innerHTML=`<video controls muted loop playsinline preload="metadata" src="${r.video}"></video><p>${num(r.execution_seconds,' s total')} · ${r.dimensions.width} × ${r.dimensions.height} · ${(r.frames/24).toFixed(2)} s clip · ${num(r.free_gib,' GiB free')} · ${r.classification}</p><dl>${Object.entries(r.stages).map(([k,v])=>`<dt>${k}</dt><dd>${num(v,' s')}</dd>`).join('')}</dl><details><summary>Configuration and diagnostics</summary><p><a href="${r.request}">Exact workflow</a> · <a href="${r.evidence}">Manifest</a></p><pre>${esc(JSON.stringify(r.accelerator,null,2))}</pre></details>`;}sel.onchange=draw;draw();}
pane('left',0);pane('right',Math.min(1,good.length-1));document.getElementById('play').onclick=()=>document.querySelectorAll('video').forEach(v=>{v.currentTime=0;v.play().catch(()=>{});});document.getElementById('pause').onclick=()=>document.querySelectorAll('video').forEach(v=>v.pause());
const columns=[['configuration','Recipe'],['scene','Scene'],['temperature','Process'],['frames','Frames'],['seed','Seed'],['execution_seconds','Total s'],['sampling','Sampling s'],['peak_gib','Peak GiB'],['free_gib','Free GiB'],['ram_gib','RAM GiB'],['status','Status']];let sort='configuration',direction=1;
document.getElementById('head').innerHTML='<tr>'+columns.map(([k,t])=>`<th><button data-key="${k}">${t}</button></th>`).join('')+'</tr>';
function table(){const get=(r,k)=>k==='sampling'?r.stages.sampling:r[k];const sorted=[...data].sort((a,b)=>{let x=get(a,sort),y=get(b,sort);if(x==null)return y==null?0:1;if(y==null)return -1;return direction*(typeof x==='number'?x-y:String(x).localeCompare(String(y)));});document.getElementById('rows').innerHTML=sorted.map(r=>'<tr data-valid="'+r.valid+'">'+columns.map(([k])=>'<td>'+esc(typeof get(r,k)==='number'&&!['frames','seed'].includes(k)?num(get(r,k)):get(r,k))+'</td>').join('')+'</tr>').join('');document.querySelectorAll('th button').forEach(b=>b.parentElement.setAttribute('aria-sort',b.dataset.key===sort?(direction===1?'ascending':'descending'):'none'));}document.getElementById('head').onclick=e=>{const k=e.target.dataset.key;if(k){direction=sort===k?-direction:1;sort=k;table();}};table();
fetch('review-notes.json').then(r=>r.ok?r.json():[]).then(notes=>document.getElementById('notes').innerHTML=notes.map(n=>`<section class="pane"><strong>${esc(n.title)}</strong><p>${esc(n.text)}</p></section>`).join('')).catch(()=>{});
</script></html>'''
    body=body.replace('DATA',json.dumps(rows).replace('<','\\u003c'))
    (root/'clips.html').write_text(body,'utf-8')
    print(f'{len(rows)} attempts/passes; {sum(r["valid"] for r in rows)} validated comparison clips')


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('directory',type=Path)
    build(parser.parse_args().directory)
