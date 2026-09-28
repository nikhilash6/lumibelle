// Run against an already-started DEBUG desktop host and a disposable copied project.
// LUMIBELLE_NATIVE_PROJECT and LUMIBELLE_NATIVE_LIBRARY must NEVER point at the live library.
import { chromium, expect } from '@playwright/test';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
const project = process.env.LUMIBELLE_NATIVE_PROJECT;
const library = process.env.LUMIBELLE_NATIVE_LIBRARY;
if (!project || !library) throw new Error('Supply a disposable library and project ID. This test edits its script and prompt.');
// Deliberately require a fixture marker; an explicit path alone can still be the user's real library.
if ((await readFile(path.join(library, '.native-smoke-fixture'), 'utf8')).trim() !== 'disposable')
  throw new Error('The selected library is not marked as a disposable native smoke fixture.');
const browser = await chromium.connectOverCDP(process.env.LUMIBELLE_NATIVE_CDP || 'http://127.0.0.1:9228');
const page = browser.contexts()[0].pages()[0];
const errors = []; page.on('pageerror', e => errors.push(e.message));
const saved = async file => JSON.parse(await readFile(path.join(library, project, file), 'utf8'));
const navigate = async studio => {
  await page.evaluate(href => { const a = document.createElement('a'); a.href = href; document.body.append(a); a.click(); a.remove(); }, `/projects/${project}/${studio}`);
};
const marker = ` Native smoke ${Date.now()}.`;
try {
  if(await page.locator('.cut-chooser').isVisible()) await page.locator('.cut-chooser').getByRole('button',{name:'Cancel',exact:true}).click();
  await navigate('script');
  const script = page.getByRole('textbox', {name:'Screenplay',exact:true}); await expect(script).toBeVisible();
  await script.press('Control+End'); await script.pressSequentially(marker);
  await script.press('Control+s');
  await expect.poll(async () => JSON.stringify((await saved('script.json')).blocks)).toContain(marker.trim());
  await script.press('Control+z'); await script.press('Control+s');
  await expect.poll(async () => JSON.stringify((await saved('script.json')).blocks)).not.toContain(marker.trim());
  await navigate('shots');
  await page.locator('[data-workspace-group=center]').getByRole('tab',{name:'Prompt',exact:true}).click();
  const prompt=page.getByRole('textbox',{name:'H3 prompt',exact:true}); await expect(prompt).toBeVisible();
  await prompt.press('Control+End'); await prompt.pressSequentially(marker);
  await expect.poll(async()=>JSON.stringify(await saved('production.json'))).toContain(marker.trim());
  await prompt.press('Control+z');
  await expect.poll(async()=>JSON.stringify(await saved('production.json'))).not.toContain(marker.trim());
  const images = await page.locator('.compact-reference img').evaluateAll(imgs => imgs.map(i => ({loaded:i.complete && i.naturalWidth>0,src:i.getAttribute('src')})));
  expect(images.length).toBeGreaterThan(0); expect(images.every(i=>i.loaded)).toBeTruthy();
  await page.locator('[data-workspace-group=center]').getByRole('tab',{name:'Takes',exact:true}).click();
  await page.locator('.unified-take-card .take-preview').first().click();
  const video=page.locator('.shot-review-dialog video'); await expect(video).toBeVisible();
  await expect.poll(()=>video.evaluate(v=>v.readyState)).toBeGreaterThanOrEqual(2);
  const media=await video.getAttribute('src');
  const ranges=await page.evaluate(async url=> {
    const partial=await fetch(url,{headers:{Range:'bytes=16-47'}});
    const head=await fetch(url,{method:'HEAD'});
    const missing=await fetch('/media/projects/00000000-0000-0000-0000-000000000001/takes/00000000-0000-0000-0000-000000000002');
    return {status:partial.status,range:partial.headers.get('content-range'),bytes:(await partial.arrayBuffer()).byteLength,head:head.status,length:head.headers.get('content-length'),missing:missing.status};
  },media);
  expect(ranges.status).toBe(206); expect(ranges.bytes).toBe(32); expect(ranges.head).toBe(200); expect(ranges.missing).toBe(404);
  await video.evaluate(async v=>{v.muted=true; await v.play();});
  await expect.poll(()=>video.evaluate(v=>v.currentTime)).toBeGreaterThan(.2);
  const duration=await video.evaluate(v=>{v.pause();v.currentTime=v.duration*.7;return v.duration;});
  await expect.poll(()=>video.evaluate(v=>v.currentTime)).toBeGreaterThan(duration*.6);
  await expect(page.locator('.take-paused-frame')).toBeVisible({timeout:15000});
  await page.screenshot({path:'artifacts/native-take.png'});
  await page.locator('.shot-review-dialog').getByRole('button',{name:'Close',exact:true}).click();
  await navigate('cut');
  await page.locator('.cut-workspace-toolbar').getByRole('button',{name:'Choose takes',exact:true}).click();
  const chooser=page.locator('.cut-chooser');
  const takeChoices=chooser.getByRole('combobox',{name:/^Take for /});
  const first=takeChoices.first(); const current=await first.inputValue();
  const option=await first.locator('option').evaluateAll((items,value)=>items.find(x=>x.value && x.value!==value)?.value,current);
  if(option) await first.selectOption(option);
  await expect(chooser.getByRole('button',{name:/Apply changes/})).toBeEnabled();
  await chooser.getByRole('button',{name:/Apply changes/}).click();
  await expect(page.locator('.timeline-clip').first()).toBeVisible();
  const play=page.locator('.cut-player [data-action=play]'); await expect(play).toBeEnabled(); await play.click();
  await expect.poll(()=>page.locator('.cut-player video').evaluateAll(videos=>Math.max(...videos.map(v=>v.currentTime)))).toBeGreaterThan(.2);
  await page.screenshot({path:'artifacts/native-cut.png'});
  expect(errors).toEqual([]);
  await mkdir('artifacts',{recursive:true}); await writeFile('artifacts/native-smoke-result.json',JSON.stringify({passed:true,ranges,images:images.length,duration,errors},null,2));
  console.log('Native editors, save, Undo, references, ranged media, playback, seeking, frame retrieval and Cut passed.');
} catch(error) { console.error('WebView errors:', errors); throw error; }
finally { await browser.close(); }
