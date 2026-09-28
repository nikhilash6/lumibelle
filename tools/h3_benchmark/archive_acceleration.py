"""Export compact, media-free benchmark evidence for version control."""
import argparse
import json
from pathlib import Path
from acceleration_report import collect
from metrics import atomic_json
from report import read_json
from runner import hash_file


def archive(root, destination):
    destination.mkdir(parents=True, exist_ok=False)
    rows=collect(root)
    attempts=[]
    for row in rows:
        directory=root/'results'/row['name']
        manifest=read_json(directory/'manifest.json')
        run=next((r for r in manifest.get('runs',[]) if r['temperature']==row['temperature']),{})
        compact=dict(row)
        for field in ('video','request','evidence'):
            compact.pop(field,None)
        compact['accelerator']={k:v for k,v in row['accelerator'].items() if k!='diagnostics'}
        compact['measurements']=read_json(directory/row['temperature']/'measurements.json')
        compact['output']={k:v for k,v in run.items() if k in ('sha256','validation','audio_sha256','audio_samples','archives')}
        compact['files_sha256']={name:hash_file(directory/name) for name in ('manifest.json','request.json') if (directory/name).exists()}
        for name in ('memory.jsonl','execution.log','history.json','events.json'):
            file=directory/row['temperature']/name
            if file.exists(): compact['files_sha256'][row['temperature']+'/'+name]=hash_file(file)
        compact['harness_sha256']=manifest['harness_sha256']
        compact['started_utc']=manifest['started_utc']
        attempts.append(compact)
    atomic_json(destination/'results.json',dict(units=dict(times='seconds',memory='bytes unless named GiB',frame_rate='24 fps'),attempts=attempts))
    for name in ('pins.json','weights.json','preflight.json','h3-settings.json','budget.json','review-notes.json'):
        atomic_json(destination/name,read_json(root/name))
    references=read_json(root/'references.json')
    for value in references.values(): value.pop('source',None)
    atomic_json(destination/'reference-hashes.json',references)
    workflows=[]
    for path in sorted((root/'results').glob('*/request.json')):
        request=read_json(path)
        workflows.append(dict(run=path.parent.name,configuration=request['configuration'],scene=request['scene'],seed=request['seed'],
                              expected=request['expected'],workflow=request['workflow'],
                              application_request_sha256=request['application_request_sha256']))
    atomic_json(destination/'workflows.json',workflows)
    print('Archived',len(attempts),'passes to',destination)


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('directory',type=Path);parser.add_argument('destination',type=Path)
    args=parser.parse_args();archive(args.directory,args.destination)
