import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const catalogPath = path.join(root, 'src', 'generated', 'catalog.generated.json');
const outputPath = path.join(root, 'src', 'generated', 'nodeIconCodes.ts');
const catalog = JSON.parse(readFileSync(catalogPath, 'utf8'));
const types = [...new Set(catalog.items.map((item) => String(item.type).trim().toLowerCase()))].sort();
const entries = types.map((type, index) => `  ${JSON.stringify(type)}: ${JSON.stringify(index.toString(36).padStart(3, '0').toUpperCase())}`);
const output = `// Generated from catalog.generated.json by scripts/generate-node-icon-codes.mjs.
// Keep the map stable and dependency-free so the icon component does not import the full catalog.
export const nodeIconCodes: Readonly<Record<string, string>> = Object.freeze({
${entries.join(',\n')}
});
`;

writeFileSync(outputPath, output);
