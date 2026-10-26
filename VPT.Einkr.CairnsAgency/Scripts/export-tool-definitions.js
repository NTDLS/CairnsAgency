// Regenerates Resources/tool-definitions.json from the Electron renderer's own tool-definition files, so the
// agency offers the model EXACTLY the schemas the desktop app does (names, descriptions, parameters,
// categories). Re-run whenever tool-definitions.js or web-tool-definitions.js changes:
//
//   node Scripts/export-tool-definitions.js
//
const fs = require('fs');
const path = require('path');

const renderer = path.join(__dirname, '..', '..', 'VPT.Einkr.Desktop.Electron', 'renderer');
global.window = {};

const load = (file, names) => new Function(
    fs.readFileSync(path.join(renderer, file), 'utf8') + `\n;return { ${names.join(', ')} };`)();

const tools = load('tool-definitions.js', ['TOOL_DEFINITIONS', 'TOOL_OUTPUT_TOOL_DEFINITIONS', 'UTILITY_TOOL_DEFINITIONS']);
const web = load('web-tool-definitions.js', ['WEB_TOOL_DEFINITIONS']);

const out = {
    // The file/discovery/write/shell/notes/... tools, each carrying its category.
    local: tools.TOOL_DEFINITIONS,
    web: web.WEB_TOOL_DEFINITIONS,
    toolOutput: tools.TOOL_OUTPUT_TOOL_DEFINITIONS,
    utility: tools.UTILITY_TOOL_DEFINITIONS
};

fs.writeFileSync(path.join(__dirname, '..', 'Resources', 'tool-definitions.json'), JSON.stringify(out, null, 2));
console.log(`exported: local=${out.local.length} web=${out.web.length} toolOutput=${out.toolOutput.length} utility=${out.utility.length}`);
