import { Editor, Node } from '@tiptap/core';
import Document from '@tiptap/extension-document';
import Text from '@tiptap/extension-text';
import { UndoRedo } from '@tiptap/extensions';
import { Plugin } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';

// Each line owns its original separator. Decorations never rewrite the document.
const Line = Node.create({
  name: 'promptLine', group: 'block', content: 'text*', whitespace: 'pre',
  addAttributes: () => ({ ending: { default: '\n' } }),
  parseHTML: () => [{ tag: 'div[data-prompt-line]' }],
  renderHTML: () => ['div', { 'data-prompt-line': '' }, 0],
});
function documentFor(text) {
  const lines = text.split(/(\r\n|\n|\r)/);
  const content = [];
  for (let i = 0; i < lines.length; i += 2)
    content.push({ type: 'promptLine', attrs: { ending: lines[i + 1] ?? '' }, content: lines[i] ? [{ type: 'text', text: lines[i] }] : [] });
  return { type: 'doc', content };
}
function plain(doc) {
  const lines = [];
  doc.forEach((line, _, i) => lines.push(line.textContent + (i === doc.childCount - 1 ? '' : line.attrs.ending || '\n')));
  return lines.join('');
}
const instances = new WeakMap();
const histories = new Map();
export function mount(element, dotnet, text, historyKey = null) {
  if (!(element instanceof HTMLElement) || !element.isConnected) return;
  if (historyKey && histories.has(historyKey)) {
    const item = histories.get(historyKey);
    instances.set(element, item);
    item.mount(element, dotnet, text);
    return;
  }
  let replacing = false, sending = false, pending = null, sequence = 0, acknowledged = text;
  let lastUndo, lastRedo, retry;
  const reportHistory = editor => {
    const undo = editor.can().undo(), redo = editor.can().redo();
    if (!dotnet || undo === lastUndo && redo === lastRedo) return;
    lastUndo = undo; lastRedo = redo;
    void dotnet.invokeMethodAsync('OnHistoryChanged', undo, redo).catch(() => {});
  };
  const send = async () => {
    if (!dotnet || sending || pending === null) return;
    sending = true;
    const value = pending, revision = sequence;
    try {
      await dotnet.invokeMethodAsync('OnPromptInput', value, revision);
      acknowledged = value;
      if (sequence === revision) pending = null;
    } finally { sending = false; }
    if (pending !== null) await send();
  };
  const editor = new Editor({
    element, extensions: [Document.extend({ content: 'promptLine+' }), Text, Line, UndoRedo],
    content: documentFor(text), injectCSS: false,
    editorProps: {
      attributes: { role: 'textbox', 'aria-label': 'H3 prompt', 'aria-multiline': 'true', spellcheck: 'false' },
      handlePaste(view, event) {
        event.preventDefault();
        const value = event.clipboardData.getData('text/plain');
        const nodes = view.state.schema.nodeFromJSON(documentFor(value));
        editor.commands.insertContent(nodes.toJSON().content, { parseOptions: { preserveWhitespace: 'full' } });
        return true;
      },
      handleDOMEvents: {
        mousedown(_, event) {
          if (!event.target.closest('[data-picture],[data-video]')) return false;
          // Preview activation is a control interaction, not a text-selection gesture.
          // Do not leave a ProseMirror mouse-selection session active across the dialog.
          event.preventDefault(); return true;
        },
        click(_, event) {
          const tag = event.target.closest('[data-picture],[data-video]');
          if (!tag) return false;
          event.preventDefault();
          void dotnet.invokeMethodAsync(tag.dataset.video ? 'OnVideo' : 'OnPicture', Number(tag.dataset.video || tag.dataset.picture));
          return true;
        },
      },
    },
    onUpdate() { if (!replacing) { sequence++; pending = plain(editor.state.doc); void send().catch(() => {}); } },
    onTransaction({ editor }) { reportHistory(editor); },
  });
  const decorations = new Plugin({ props: { decorations(state) {
    const decorations = [];
    state.doc.descendants((node, position) => {
      if (node.type.name !== 'promptLine') return;
      const value = node.textContent;
      if (/^(subject_definitions|summary|retention_analysis|detailed_description|overall_soundscape|non_diegetic_music):\s*$/.test(value))
        decorations.push(Decoration.node(position, position + node.nodeSize, { class: 'prompt-section-heading' }));
      if (value.includes('<d>')) decorations.push(Decoration.node(position, position + node.nodeSize, { class: 'prompt-dialogue' }));
      for (const match of value.matchAll(/<(Picture|Audio|Video|Subject) (\d+)>/g))
        decorations.push(Decoration.inline(position + 1 + match.index, position + 1 + match.index + match[0].length,
          { class: 'prompt-reference', ...(match[1] === 'Picture' ? { 'data-picture': match[2], title: 'Preview selected crop' } : match[1] === 'Video' ? { 'data-video': match[2], title: 'Preview reference video' } : {}) }));
    });
    return DecorationSet.create(state.doc, decorations);
  } } });
  editor.registerPlugin(decorations);
  const warn = event => { if (pending !== null) { event.preventDefault(); event.returnValue = ''; } };
  const listen = () => {
    retry = setInterval(() => { if (pending !== null) void send().catch(() => {}); }, 1500);
    window.addEventListener('beforeunload', warn);
  };
  const unlisten = () => { clearInterval(retry); window.removeEventListener('beforeunload', warn); };
  const item = { editor,
    mount(target, client, value) {
      dotnet = client; lastUndo = lastRedo = undefined;
      editor.mount(target);
      editor.registerPlugin(decorations);
      // Reopening uses the current saved text, while retaining this shot's history.
      item.replace(value, true);
      listen(); reportHistory(editor);
    },
    async flush() { while (sending || pending !== null) { if (!sending) await send(); else await new Promise(resolve => setTimeout(resolve, 10)); } },
    dispose() { unlisten(); dotnet = null; if (historyKey) editor.unmount(); else editor.destroy(); },
    destroy() { unlisten(); dotnet = null; editor.destroy(); },
    replace(value, reopening = false) {
      if (pending !== null || value === plain(editor.state.doc) || !reopening && value === acknowledged) return;
      replacing = true; editor.commands.setContent(documentFor(value), { emitUpdate: false }); replacing = false; acknowledged = value;
    } };
  instances.set(element, item);
  if (historyKey) histories.set(historyKey, item);
  listen(); reportHistory(editor);
}
export function releaseHistory(scope) {
  for (const [key, item] of histories) if (key.startsWith(scope + ':')) { item.destroy(); histories.delete(key); }
}
export async function flush(element) { const item = instances.get(element); if (!item) return null; await item.flush(); return plain(item.editor.state.doc); }
export function replace(element, value) { instances.get(element)?.replace(value); }
export function command(element, value) { instances.get(element)?.editor.chain().focus()[value]().run(); }
export function dispose(element) { instances.get(element)?.dispose(); instances.delete(element); }
