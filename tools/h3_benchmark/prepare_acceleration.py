"""Explicit manual experiment preparation. Writes only to a new artifact directory."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import shutil
import subprocess
import urllib.request
import zipfile

REPOS = {
    'plus': ('xmarre/Comfyui_Minimax_h3_latent_Upscaler-Plus', 'db76324d6bbf231bebcb9d794e133ef4d4d9ee87'),
    'spectrum': ('xmarre/ComfyUI-Spectrum-MiniMax-H3', '455bd357cb45637c8e852f7f448dc57b52de94f8'),
    'cache': ('duckyshell/ComfyUI-MiniMaxH3-FirstBlockCache', '725973c3bfd9de6dce249bc93dc5fe27f820df31'),
    'pdd': ('Jalen-Brunson/ComfyUI-MiniMax-H3-PDD-Acc', '311a65dd53832d8a5f8177a9d5fb923c09e35a90'),
    'larry': ('Larryvrh/ComfyUI-MiniMax-H3-Turbo', '4274783a23afcfdbea3b4876cb79effd6c510785'),
}


def get_json(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'Lumibelle-manual-benchmark'}), timeout=60) as response:
        return json.load(response)


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as source:
        for chunk in iter(lambda: source.read(8 * 1024**2), b''):
            digest.update(chunk)
    return digest.hexdigest()


def prepare(root, repo, references, ffmpeg):
    for source in references.values():
        if not source.is_file(): raise FileNotFoundError(source)
    root.mkdir(parents=True, exist_ok=False)
    settings = json.loads((repo/'App_Data/ai-settings.json').read_text('utf-8-sig'))['settings']
    (root/'h3-settings.json').write_text(json.dumps(settings['h3'], indent=2), 'utf-8')
    system = get_json(settings['comfyUrl'].rstrip('/')+'/system_stats')
    (root/'production-system.json').write_text(json.dumps(system, indent=2), 'utf-8')
    pins = {}
    for label, (repository, revision) in REPOS.items():
        commit = get_json('https://api.github.com/repos/'+repository+'/commits/'+revision)['sha']
        url = 'https://codeload.github.com/'+repository+'/zip/'+commit
        data = urllib.request.urlopen(url, timeout=120).read()
        destination = root/'vendor'/label
        destination.mkdir(parents=True)
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            for member in archive.infolist():
                target = (destination/member.filename).resolve()
                if not target.is_relative_to(destination.resolve()):
                    raise ValueError('Unsafe source archive')
            archive.extractall(destination)
        pins[label] = dict(repository=repository, commit=commit, archive_sha256=hashlib.sha256(data).hexdigest())
        (root/'pins.json').write_text(json.dumps(pins, indent=2), 'utf-8')
        print(label, commit, flush=True)
    evidence = {}
    (root/'inputs').mkdir()
    for name, source in references.items():
        destination = root/'inputs'/name
        shutil.copyfile(source, destination)
        evidence[name] = dict(source=str(source), sha256=sha(destination), bytes=destination.stat().st_size)
    voice=root/'inputs/voice.wav'
    subprocess.run([ffmpeg,'-hide_banner','-loglevel','error','-n','-i',str(root/'inputs/voice.mp3'),
                    '-ss','0','-t','4.248','-map','0:a:0','-vn','-map_metadata','-1','-ar','32000','-ac','2',
                    '-c:a','pcm_s16le','-fflags','+bitexact',str(voice)],check=True)
    evidence['voice.wav']=dict(source='voice.mp3',sha256=sha(voice),bytes=voice.stat().st_size,start=0,duration=4.248,sample_rate=32000,channels=2,codec='pcm_s16le')
    (root/'references.json').write_text(json.dumps(evidence, indent=2), 'utf-8')
    print('Prepared', root)


def download_weights(root):
    weights = [('pdd_acc', 'alibaba-pai/MiniMax-H3-Acc-LoRAs', '335001fb9e5455d68a0caa18ec2e319072150328', 'MiniMax-H3-Ref2VA-Acc-8Step.safetensors'),
               ('loras', 'larryvrh/MiniMax-H3-Turbo-Lora', '43a74557ac3f6539db8e0f2a959d03feb7a81480', 'minimax_h3_turbo_v4_step600_ema.safetensors')]
    receipt = []
    for folder, repository, revision, name in weights:
        metadata = get_json('https://huggingface.co/api/models/'+repository+'/revision/'+revision+'?blobs=true')
        entry = next(item for item in metadata['siblings'] if item['rfilename'] == name)
        revision = metadata['sha']
        target = root/'models'/folder/name
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            raise FileExistsError(target)
        temporary = target.with_suffix('.download')
        url = 'https://huggingface.co/'+repository+'/resolve/'+revision+'/'+name
        print('Downloading', name, entry.get('size'), flush=True)
        with urllib.request.urlopen(url, timeout=120) as response, temporary.open('wb') as output:
            shutil.copyfileobj(response, output, 8*1024**2)
        digest = sha(temporary)
        if temporary.stat().st_size != entry['size'] or digest != entry['lfs']['sha256']:
            raise ValueError('Weight checksum mismatch')
        temporary.rename(target)
        receipt.append(dict(repository=repository, commit=revision, name=name, path=target.relative_to(root).as_posix(), sha256=digest, bytes=entry['size']))
        (root/'weights.json').write_text(json.dumps(receipt, indent=2), 'utf-8')
        print('Verified', name, flush=True)


def hash_models(root):
    """Invoke inside the isolated container, without importing torch or allocating VRAM."""
    target=root/'preflight.json'
    if target.exists(): raise FileExistsError(target)
    settings=json.loads((root/'h3-settings.json').read_text('utf-8-sig'))
    folders={'model':'diffusion_models','encoder':'text_encoders','videoVae':'vae','audioVae':'vae',
             'turboLora':'loras','turbo8StepLora':'loras','latentUpscaler':'latent_upscale_models'}
    files=[]
    for label,folder in folders.items():
        path=Path('/comfyui/models')/folder/settings[label]
        print('Hashing',path,flush=True)
        files.append(dict(label=label,path=str(path),bytes=path.stat().st_size,sha256=sha(path)))
    core=['nodes.py','execution.py','comfy_extras/nodes_minimax_h3.py','comfy/ldm/minimax/model.py','comfy/model_management.py']
    target.write_text(json.dumps(dict(files=files,core_sha256={p:sha('/comfyui/'+p) for p in core}),indent=2),'utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('directory', type=Path)
    parser.add_argument('--weights', action='store_true')
    parser.add_argument('--hash-models', action='store_true')
    parser.add_argument('--face',type=Path)
    parser.add_argument('--outfit',type=Path)
    parser.add_argument('--voice',type=Path)
    parser.add_argument('--ffmpeg',default='ffmpeg')
    args = parser.parse_args()
    if args.hash_models:
        hash_models(args.directory.resolve())
    elif args.weights:
        download_weights(args.directory.resolve())
    else:
        if not all((args.face,args.outfit,args.voice)): parser.error('New experiments require --face, --outfit and --voice paths.')
        prepare(args.directory.resolve(), Path(__file__).resolve().parents[2],
                {'face.png':args.face,'outfit.png':args.outfit,'voice.mp3':args.voice},args.ffmpeg)
