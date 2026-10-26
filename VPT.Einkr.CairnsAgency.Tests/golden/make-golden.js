// Regenerates the golden files the C# port is tested against, by running the desktop app's OWN JavaScript:
//
//   node golden/make-golden.js
//
// notes-cases.json   NotesStore (renderer/notes-store.js): results of write() sequences, serialized state, render()
// prune-cases.json   pruneOldToolResults / truncateMiddle / previewHead / formatToolCallArgs (renderer/agent-runner.js)
// datetime-cases.json describeCurrentDateTime (renderer/tool-definitions.js)
//
// If one of these starts failing after the renderer changed on purpose, re-run this and port the change.
const fs = require('fs');
const path = require('path');

const renderer = path.join(__dirname, '..', '..', 'VPT.Einkr.Desktop.Electron', 'renderer');
global.window = {};
const read = (f) => fs.readFileSync(path.join(renderer, f), 'utf8');

// ---------------- notes ----------------
const { NotesStore } = new Function(read('notes-store.js') + '\n;return { NotesStore };')();

const many = (n, f) => Array.from({ length: n }, (_, i) => f(i));
const notesCases = [
    { name: 'plain write', load: null, writes: [{ notes: ['A fact.', 'Another fact.'] }] },
    { name: 'replace-all', load: null, writes: [{ notes: ['one', 'two'] }, { notes: ['two', 'three'] }] },
    { name: 'user pinned, echoed with tag, duplicates', load: JSON.stringify([{ text: 'Josh likes C#', source: 'user' }, 'old model note']),
      writes: [{ notes: ['[user] Josh likes C#', 'josh likes c#', 'new note', 'NEW NOTE', '  '] }] },
    { name: 'tombstone refuses', load: JSON.stringify(['keep', { text: 'Stale Thing', source: 'removed' }]),
      writes: [{ notes: ['keep', 'stale thing', 'fresh'] }] },
    { name: 'too many', load: null, writes: [{ notes: many(41, (i) => `note ${i}`) }] },
    { name: 'exactly the cap', load: null, writes: [{ notes: many(40, (i) => `note ${i}`) }] },
    { name: 'too many chars', load: null, writes: [{ notes: many(3, (i) => 'x'.repeat(2500) + i) }] },
    { name: 'missing notes', load: null, writes: [{}, { notes: 'not an array' }] },
    { name: 'object entries', load: null, writes: [{ notes: [{ text: 'from object' }, 42, null, 'string'] }] },
    { name: 'garbage load', load: 'not json at all', writes: [{ notes: ['ok'] }] },
    { name: 'load non-array', load: '{"a":1}', writes: [{ notes: ['ok'] }] },
    { name: 'load user then model order', load: JSON.stringify(['m1', { text: 'u1', source: 'user' }, 'm2', { text: 'u2', source: 'user' }]), writes: [] },
    { name: 'unicode and quotes', load: null, writes: [{ notes: ['café — "quoted" <b>&</b>', 'emoji 🪨'] }] }
];

const notesOut = notesCases.map((c) => {
    const store = new NotesStore();
    store.load(c.load);
    const results = c.writes.map((w) => store.write(w));
    return { name: c.name, load: c.load, writes: c.writes, results, json: store.toJson(), snapshot: store.snapshot(), render: store.render() };
});
fs.writeFileSync(path.join(__dirname, 'notes-cases.json'), JSON.stringify(notesOut, null, 1));

// ---------------- agent-runner helpers ----------------
const runner = new Function(read('agent-runner.js') +
    '\n;return { pruneOldToolResults, truncateMiddle, previewHead, formatToolCallArgs, PRUNED_PLACEHOLDER };')();

const big = (n) => 'x'.repeat(n);
const asst = (calls) => ({ role: 'assistant', content: 'c', toolCalls: calls });
const tool = (n) => ({ role: 'tool', content: big(n) });

const pruneInputs = [
    { name: 'nothing to prune', protect: 40000, messages: [{ role: 'user', content: 'go' }, asst([1]), tool(1000), asst([1]), tool(1000)] },
    { name: 'old results pruned', protect: 40000,
      messages: [{ role: 'user', content: 'go' }, ...many(10, () => [asst([1]), tool(30000)]).flat(), asst([1]), tool(2000)] },
    { name: 'current round never blanked', protect: 1000,
      messages: [{ role: 'user', content: 'go' }, asst([1]), tool(90000), asst([1, 2]), tool(500000), tool(500000)] },
    { name: 'below minimum reclaim', protect: 1000, messages: [{ role: 'user', content: 'go' }, asst([1]), tool(20000), asst([1]), tool(20000), asst([1]), tool(20000)] },
    { name: 'already pruned skipped', protect: 40000,
      messages: [{ role: 'user', content: 'go' }, asst([1]), { role: 'tool', content: runner.PRUNED_PLACEHOLDER }, ...many(6, () => [asst([1]), tool(60000)]).flat()] }
];
const pruneOut = pruneInputs.map((c) => {
    const messages = JSON.parse(JSON.stringify(c.messages));
    const reclaimed = runner.pruneOldToolResults(messages, c.protect);
    return {
        name: c.name, protect: c.protect,
        input: c.messages.map((m) => ({ role: m.role, length: (m.content ?? '').length, pruned: m.content === runner.PRUNED_PLACEHOLDER })),
        reclaimed,
        output: messages.map((m) => (m.content === runner.PRUNED_PLACEHOLDER ? -1 : (m.content ?? '').length))
    };
});

const text = Array.from({ length: 3000 }, (_, i) => `line ${i} ${'y'.repeat(i % 37)}`).join('\n');
const truncOut = [10, 100, 500, 2500].map((limit) => ({ limit, result: runner.truncateMiddle(text, limit) }));
const previewOut = [0.3, 1, 50, 200, 6000, 100000].map((n) => ({ max: Math.floor(n), result: runner.previewHead(text, Math.floor(n)) }));
const argCases = [
    {}, { path: 'a/b.txt' }, { path: 'x'.repeat(100), n: 3, flag: true, nested: { a: 1 }, arr: [1, 2], nothing: null },
    { text: 'café' }
];
const argsOut = argCases.map((a) => ({ args: a, result: runner.formatToolCallArgs(a) }));

fs.writeFileSync(path.join(__dirname, 'prune-cases.json'), JSON.stringify({ prune: pruneOut, truncate: truncOut, preview: previewOut, args: argsOut }, null, 1));

// ---------------- date/time ----------------
const { describeCurrentDateTime } = new Function(read('tool-definitions.js') + '\n;return { describeCurrentDateTime };')();
const instants = ['2026-10-17T00:30:00Z', '2026-12-25T15:00:00Z', '2026-03-08T06:59:59Z', '2026-03-08T07:00:00Z', '2027-01-01T04:59:59Z', '2026-10-17T04:00:00Z', '2026-06-30T23:59:00Z'];
const zones = [['America/New_York', 'Eastern Standard Time'], ['America/Chicago', 'Central Standard Time'], ['America/Los_Angeles', 'Pacific Standard Time'], ['UTC', 'UTC']];
const dtOut = [];
for (const iso of instants) for (const [iana, windows] of zones)
    dtOut.push({ iso, iana, windows, text: describeCurrentDateTime(new Date(iso), iana) });
fs.writeFileSync(path.join(__dirname, 'datetime-cases.json'), JSON.stringify(dtOut, null, 1));

console.log(`golden: notes=${notesOut.length} prune=${pruneOut.length} truncate=${truncOut.length} preview=${previewOut.length} args=${argsOut.length} datetime=${dtOut.length}`);
