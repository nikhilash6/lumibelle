const states = new WeakMap();
function image(url) { return new Promise((resolve, reject) => { const i = new Image(); i.onload = () => resolve(i); i.onerror = reject; i.src = url; }); }
export async function attach(canvas, stage, dotnet, url) {
    const s = { canvas, stage, dotnet, image: await image(url), drawing: null, options: {}, selection: null };
    states.set(canvas, s);
    const point = e => { const r = canvas.getBoundingClientRect(); return { x: Math.max(0, Math.min(1, (e.clientX-r.left)/r.width)), y: Math.max(0, Math.min(1, (e.clientY-r.top)/r.height)) }; };
    s.down = e => {
        if (!s.selection || s.options.view === 'input' || !e.isPrimary || e.button > 1) return;
        e.preventDefault(); canvas.setPointerCapture(e.pointerId);
        s.pan = s.options.tool === 'pan' || e.button === 1 ? { x: e.clientX, y: e.clientY, left: stage.scrollLeft, top: stage.scrollTop } : null;
        if (!s.pan) s.drawing = { protect: s.options.layer === 'protect', erase: s.options.tool === 'erase', rectangle: s.options.tool === 'rectangle' || s.options.layer === 'context', size: s.options.brush, points: [point(e)] };
    };
    s.move = e => {
        if (s.pan) { stage.scrollLeft = s.pan.left+s.pan.x-e.clientX; stage.scrollTop = s.pan.top+s.pan.y-e.clientY; return; }
        if (!s.drawing) return;
        const p = point(e); const points = s.drawing.points;
        if (s.drawing.rectangle) points[1] = p;
        else if (points.length < 8192) { const last = points[points.length-1]; if (Math.hypot(last.x-p.x,last.y-p.y) > .001) points.push(p); }
        draw(s);
    };
    s.up = async e => {
        if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
        s.pan = null;
        if (!s.drawing) return;
        const stroke = s.drawing; s.drawing = null;
        if (stroke.rectangle) stroke.points[1] = point(e);
        if (s.options.layer === 'context') {
            const [a,b] = stroke.points; if (Math.abs(a.x-b.x) < .001 || Math.abs(a.y-b.y) < .001) { draw(s); return; }
            await dotnet.invokeMethodAsync('Context', { x: Math.min(a.x,b.x), y: Math.min(a.y,b.y), width: Math.abs(a.x-b.x), height: Math.abs(a.y-b.y) });
        } else await dotnet.invokeMethodAsync('Stroke', stroke);
    };
    s.cancel = () => { s.drawing = null; s.pan = null; draw(s); };
    canvas.addEventListener('pointerdown', s.down); canvas.addEventListener('pointermove', s.move); canvas.addEventListener('pointerup', s.up); canvas.addEventListener('pointercancel', s.cancel);
}
export async function render(canvas, selection, options, preview) {
    const s = states.get(canvas); if (!s) return;
    const version = s.version = (s.version || 0)+1;
    const prepared = preview ? await image(preview) : null;
    if (version !== s.version) return;
    s.selection = selection; s.options = options; s.preview = prepared; draw(s);
}
function draw(s) {
    const {canvas, selection, options, stage} = s; if (!selection) return;
    const source = s.preview || s.image;
    const width = Math.min(1600, source.naturalWidth); const height = Math.round(width*source.naturalHeight/source.naturalWidth);
    canvas.width = width; canvas.height = height;
    const display = Math.max(100, Math.min(stage.clientWidth, stage.clientHeight*width/height))*options.zoom; canvas.style.width = display+'px'; canvas.style.height = display*height/width+'px';
    const ctx = canvas.getContext('2d'); ctx.drawImage(source,0,0,width,height);
    if (options.view !== 'selection') return;
    const strokes = [...selection.strokes, ...(s.drawing && options.layer !== 'context' ? [s.drawing] : [])];
    for (const protect of [false,true]) {
        if (!protect && (selection.mode === 0 || selection.mode === 'Protect')) continue;
        const layer = document.createElement('canvas'); layer.width=width; layer.height=height;
        const c=layer.getContext('2d');
        for (const stroke of strokes.filter(x=>x.protect===protect)) {
            c.globalCompositeOperation=stroke.erase?'destination-out':'source-over'; c.fillStyle=c.strokeStyle=protect?'#ad63c9':'#388fde'; c.lineCap=c.lineJoin='round';
            const p=stroke.points;
            if(stroke.rectangle && p.length>1) c.fillRect(Math.min(p[0].x,p[1].x)*width,Math.min(p[0].y,p[1].y)*height,Math.abs(p[0].x-p[1].x)*width,Math.abs(p[0].y-p[1].y)*height);
            else { c.lineWidth=stroke.size*Math.min(width,height); c.beginPath(); c.moveTo(p[0].x*width,p[0].y*height); for(const pt of p.slice(1))c.lineTo(pt.x*width,pt.y*height); if(p.length===1)c.lineTo(p[0].x*width+.01,p[0].y*height); c.stroke(); }
        }
        ctx.globalAlpha=.48;ctx.drawImage(layer,0,0);ctx.globalAlpha=1;
    }
    let crop=selection.context;
    if(options.layer==='context' && s.drawing?.points.length===2) {
        const [a,b]=s.drawing.points;crop={x:Math.min(a.x,b.x),y:Math.min(a.y,b.y),width:Math.abs(a.x-b.x),height:Math.abs(a.y-b.y)};
    }
    ctx.strokeStyle='#fff';ctx.lineWidth=2;ctx.setLineDash([8,6]);ctx.strokeRect(crop.x*width,crop.y*height,crop.width*width,crop.height*height);
}
export function detach(canvas) { const s=states.get(canvas);if(!s)return;for(const [name,key] of [['pointerdown','down'],['pointermove','move'],['pointerup','up'],['pointercancel','cancel']])canvas.removeEventListener(name,s[key]);states.delete(canvas); }
