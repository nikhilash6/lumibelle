import { Editor, Node, mergeAttributes } from '@tiptap/core';
import Document from '@tiptap/extension-document';
import Text from '@tiptap/extension-text';
import Bold from '@tiptap/extension-bold';
import Italic from '@tiptap/extension-italic';
import { UndoRedo } from '@tiptap/extensions';
import { Plugin, PluginKey, TextSelection } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';
import { closeHistory } from '@tiptap/pm/history';

const kinds = ['Act', 'Scene', 'Action', 'Character', 'Dialogue', 'Parenthetical', 'Transition'];
const instances = new WeakMap();
const highlightKey = new PluginKey('lumibelle-applied-changes');
const highlightPlugin = () => new Plugin({
  key: highlightKey,
  state: {
    init: () => DecorationSet.empty,
    apply: (tr, previous) => tr.getMeta(highlightKey) ?? (tr.docChanged ? DecorationSet.empty : previous)
  },
  props: { decorations: state => highlightKey.getState(state) }
});
const uuid = () => crypto.randomUUID();
const block = (kind, text = '') => ({ id: uuid(), kind, spans: [{ text, bold: false, italic: false }] });

// The editor tree is a view of the portable screenplay format, never the disk format.
export function toEditor(blocks) {
  return { type: 'doc', content: (blocks.length ? blocks : [block('Action')]).map(b => ({
    type: 'screenplay', attrs: { id: b.id, kind: b.kind },
    content: b.spans.filter(s => s.text).map(s => ({ type: 'text', text: s.text,
      marks: [...(s.bold ? [{ type: 'bold' }] : []), ...(s.italic ? [{ type: 'italic' }] : [])] }))
  })) };
}
export function fromEditor(doc) {
  return (doc.content || []).map(b => ({ id: b.attrs.id, kind: b.attrs.kind,
    spans: (b.content || []).map(s => ({ text: s.text || '', bold: !!s.marks?.some(m => m.type === 'bold'), italic: !!s.marks?.some(m => m.type === 'italic') })) }));
}
const Screenplay = Node.create({
  name: 'screenplay', group: 'block', content: 'text*', defining: true,
  addAttributes() { return { id: { default: null, parseHTML: () => null }, kind: { default: 'Action',
    parseHTML: el => kinds.includes(el.dataset.kind) ? el.dataset.kind : ({ H1: 'Act', H2: 'Scene', H3: 'Character' }[el.tagName] || 'Action') } }; },
  parseHTML() { return [{ tag: 'p' }, { tag: 'h1' }, { tag: 'h2' }, { tag: 'h3' }, { tag: 'div[data-kind]' }]; },
  renderHTML({ node, HTMLAttributes }) { return ['p', mergeAttributes(HTMLAttributes, { 'data-kind': node.attrs.kind, 'data-block-id': node.attrs.id, class: `script-block script-${node.attrs.kind.toLowerCase()}` }), 0]; },
  addKeyboardShortcuts() {
    return {
      Enter: () => {
        const { $from } = this.editor.state.selection;
        const prior = $from.parent.attrs.kind;
        const next = prior === 'Character' || prior === 'Parenthetical' ? 'Dialogue'
          : prior === 'Dialogue' && $from.parent.textContent.length ? 'Character' : 'Action';
        return this.editor.chain().splitBlock().updateAttributes('screenplay', { kind: next, id: uuid() }).run();
      },
      'Shift-Enter': () => this.editor.commands.insertContent('\n'),
      ...Object.fromEntries(kinds.map((kind, i) => [`Mod-Alt-${i + 1}`, () => this.editor.commands.updateAttributes('screenplay', { kind })]))
    };
  },
  addProseMirrorPlugins() { return [highlightPlugin(), new Plugin({ appendTransaction: (transactions, oldState, state) => {
    if (!transactions.some(t => t.docChanged)) return null;
    const seen = new Set(); const tr = state.tr;
    state.doc.forEach((node, pos) => {
      let id = node.attrs.id;
      if (!id || seen.has(id)) { id = uuid(); tr.setNodeMarkup(pos, undefined, { ...node.attrs, id }); }
      seen.add(id);
    });
    return tr.docChanged ? tr : null;
  } })]; }
});

function selection(editor) {
  const { $anchor, $head } = editor.state.selection;
  if (!$anchor.parent.attrs.id || !$head.parent.attrs.id) return null;
  return { anchorBlockId: $anchor.parent.attrs.id, anchorOffset: $anchor.parentOffset,
    headBlockId: $head.parent.attrs.id, headOffset: $head.parentOffset };
}
function captureDomSelection(view) {
  // Browsers can defer selectionchange until after focus moves to the assistant.
  // Capture the native range on blur while it still belongs to the screenplay.
  const range = window.getSelection();
  if (!range?.anchorNode || !range.focusNode || !view.dom.contains(range.anchorNode) || !view.dom.contains(range.focusNode)) return;
  const anchor = view.posAtDOM(range.anchorNode, range.anchorOffset);
  const head = view.posAtDOM(range.focusNode, range.focusOffset);
  const next = TextSelection.create(view.state.doc, anchor, head);
  if (!next.eq(view.state.selection)) view.dispatch(view.state.tr.setSelection(next));
}
export function mount(element, reference, blocks, initialSelection = null) {
  destroy(element);
  const entry = { reference, version: 0, sequence: 0, savedVersion: 0, timer: null, disposed: false, sending: false, queued: false };
  const deliver = async () => {
    if (entry.disposed) return;
    if (entry.sending) { entry.queued = true; return; }
    entry.sending = true; entry.queued = false;
    let deadline;
    try {
      // A JS interop acknowledgement can be lost when the circuit disconnects.
      // Bound that wait so one orphaned promise cannot block every later snapshot.
      // Version/sequence checks on the server reject a late, older delivery.
      await Promise.race([
        reference.invokeMethodAsync('EditorChanged', read(element)),
        new Promise(resolve => { deadline = setTimeout(resolve, 3000); })
      ]);
    } catch { /* The browser retains the document; the next retry sends its latest state. */ }
    finally {
      clearTimeout(deadline); entry.sending = false;
      if (entry.queued && !entry.disposed) void deliver();
    }
  };
  const notify = () => {
    clearTimeout(entry.timer);
    entry.timer = setTimeout(deliver, 120);
  };
  entry.editor = new Editor({ element, extensions: [Document, Text, Screenplay, Bold, Italic, UndoRedo],
    content: toEditor(blocks), editorProps: { attributes: { role: 'textbox', 'aria-label': 'Screenplay', 'aria-multiline': 'true', spellcheck: 'true' },
      handleScrollToSelection: view => {
        const pane = element.closest('.workspace-pane-body'); if (!pane) return false;
        const caret = view.coordsAtPos(view.state.selection.head), bounds = pane.getBoundingClientRect();
        if (caret.top < bounds.top + 16) pane.scrollTop += caret.top - bounds.top - 16;
        else if (caret.bottom > bounds.bottom - 16) pane.scrollTop += caret.bottom - bounds.bottom + 16;
        return true;
      },
      handleDOMEvents: { blur: view => { captureDomSelection(view); return false; }, paste: (_view, event) => {
        // Strip external embedded content; preserve only text and allowed emphasis through the schema.
        const html = event.clipboardData?.getData('text/html');
        if (!html) return false;
        const clean = new DOMParser().parseFromString(html, 'text/html');
        clean.querySelectorAll('script,style,iframe,img,video,audio,object,embed,svg').forEach(n => n.remove());
        clean.querySelectorAll('*').forEach(n => [...n.attributes].forEach(a => { if (!['data-kind'].includes(a.name)) n.removeAttribute(a.name); }));
        event.preventDefault(); entry.editor.commands.insertContent(clean.body.innerHTML); return true;
      } } },
    onUpdate: () => { entry.version++; void reference.invokeMethodAsync('EditorDirty', entry.version).catch(() => {}); notify(); }, onSelectionUpdate: notify
  });
  instances.set(element, entry);
  if (initialSelection) {
    const points = new Map(); entry.editor.state.doc.forEach((node, pos) => points.set(node.attrs.id, { node, pos }));
    const anchor = points.get(initialSelection.anchorBlockId), head = points.get(initialSelection.headBlockId);
    if (anchor && head) {
      const offset = (point, value) => point.pos + 1 + Math.min(point.node.content.size, Math.max(0, Number.isFinite(value) ? value : 0));
      entry.editor.view.dispatch(entry.editor.state.tr.setSelection(TextSelection.create(entry.editor.state.doc,
        offset(anchor, initialSelection.anchorOffset), offset(head, initialSelection.headOffset))).setMeta('addToHistory', false));
    }
  }

  entry.unload = event => { if (entry.version > entry.savedVersion) { event.preventDefault(); event.returnValue = ''; } };
  window.addEventListener('beforeunload', entry.unload);
  entry.retry = setInterval(() => { if (entry.version > entry.savedVersion) notify(); }, 2000);
  notify();
}
export function read(element) {
  const e = instances.get(element); return { version: e.version, sequence: ++e.sequence, blocks: fromEditor(e.editor.getJSON()), selection: selection(e.editor), bold: e.editor.isActive('bold'), italic: e.editor.isActive('italic') };
}
export function replace(element, blocks, resetHistory = false) {
  const e = instances.get(element);
  if (resetHistory) { const ref = e.reference; mount(element, ref, blocks); return read(element); }
  e.editor.view.dispatch(closeHistory(e.editor.state.tr));
  e.editor.commands.setContent(toEditor(blocks));
  e.editor.view.dispatch(closeHistory(e.editor.state.tr));
  return read(element);
}
export function acknowledge(element, version) { const e = instances.get(element); if (e) e.savedVersion = Math.max(e.savedVersion, version); }
export function highlight(element, ranges) {
  const editor = instances.get(element)?.editor; if (!editor) return;
  const nodes = new Map();
  editor.state.doc.forEach((node, pos) => nodes.set(node.attrs.id, { node, pos }));
  const decorations = []; let first = null;
  for (const range of ranges) {
    const found = nodes.get(range.blockId); if (!found) continue;
    const { node, pos } = found;
    if (range.start < 0 || range.end > node.content.size || range.end < range.start) continue;
    first ??= pos;
    decorations.push(range.start === range.end
      ? Decoration.node(pos, pos + node.nodeSize, { class: 'script-change-boundary', title: 'Applied structural change' })
      : Decoration.inline(pos + 1 + range.start, pos + 1 + range.end, { class: 'script-applied-change', title: 'Applied change' }));
  }
  editor.view.dispatch(editor.state.tr.setMeta(highlightKey, DecorationSet.create(editor.state.doc, decorations)).setMeta('addToHistory', false));
  if (first !== null) requestAnimationFrame(() => {
    const pane = element.closest('.workspace-pane-body'), node = editor.view.nodeDOM(first);
    if (pane && node instanceof HTMLElement) pane.scrollTop += node.getBoundingClientRect().top - pane.getBoundingClientRect().top - 30;
  });
}
export function editable(element, value) { instances.get(element)?.editor.setEditable(value, false); }
export function command(element, name, value) {
  const editor = instances.get(element).editor;
  if (name !== 'undo' && name !== 'redo') editor.view.dispatch(closeHistory(editor.state.tr));
  if (name === 'kind') editor.chain().focus().updateAttributes('screenplay', { kind: value }).run();
  if (name === 'bold') editor.chain().focus().toggleBold().run();
  if (name === 'italic') editor.chain().focus().toggleItalic().run();
  if (name === 'undo') editor.chain().focus().undo().run();
  if (name === 'redo') editor.chain().focus().redo().run();
  if (name === 'addScene') {
    editor.chain().focus().insertContentAt(editor.state.doc.content.size, toEditor([block('Scene', 'INT. LOCATION — DAY'), block('Action')]).content).run();
  }
  if (name === 'addAct') {
    const headings = [];
    editor.state.doc.forEach((node, pos) => { if (node.attrs.kind === 'Scene' || node.attrs.kind === 'Act') headings.push({ kind: node.attrs.kind, pos }); });
    const current = headings.filter(h => h.pos <= editor.state.selection.from).at(-1);
    // An act begins at the current scene; the scene blocks themselves stay intact.
    const at = current?.kind === 'Scene' ? current.pos : current?.kind === 'Act'
      ? headings.find(h => h.kind === 'Act' && h.pos > current.pos)?.pos ?? editor.state.doc.content.size : 0;
    const heading = block('Act', `ACT ${headings.filter(h => h.kind === 'Act').length + 1}`);
    editor.chain().focus().insertContentAt(at, toEditor([heading]).content)
      .setTextSelection({ from: at + 1, to: at + 1 + heading.spans[0].text.length }).run();
  }
  if (name !== 'undo' && name !== 'redo') editor.view.dispatch(closeHistory(editor.state.tr));
  return read(element);
}
export function jump(element, id) {
  const editor = instances.get(element).editor;
  editor.state.doc.forEach((node, pos) => {
    if (node.attrs.id !== id) return;
    editor.chain().setTextSelection(pos + 1).focus(null, { scrollIntoView: false }).run();
    // Outline navigation should reveal the section's opening, not just its caret at the bottom edge.
    const pane = element.closest('.workspace-pane-body'), heading = editor.view.nodeDOM(pos);
    if (pane && heading instanceof HTMLElement) {
      pane.scrollTop += heading.getBoundingClientRect().top - pane.getBoundingClientRect().top - 24;
    }
  });
}
export function splitScene(element) {
  const editor = instances.get(element).editor;
  editor.view.dispatch(closeHistory(editor.state.tr));
  editor.chain().focus().splitBlock().updateAttributes('screenplay', { id: uuid(), kind: 'Action' }).run();
  const pos = editor.state.selection.$from.before();
  editor.commands.insertContentAt(pos, toEditor([block('Scene', 'INT. LOCATION — DAY')]).content);
  editor.view.dispatch(closeHistory(editor.state.tr));
  return read(element);
}
export function destroy(element) {
  const e = instances.get(element); if (!e) return;
  e.disposed = true; clearTimeout(e.timer); clearInterval(e.retry); window.removeEventListener('beforeunload', e.unload); e.editor.destroy(); instances.delete(element);
}

export function rename(element, id) {
  const editor = instances.get(element).editor;
  editor.state.doc.forEach((node, pos) => { if (node.attrs.id === id) editor.chain().setTextSelection({ from: pos + 1, to: pos + 1 + node.content.size }).focus().scrollIntoView().run(); });
}
