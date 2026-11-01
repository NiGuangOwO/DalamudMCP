// Build-time only. Read the pinned, installed ESM packages; never run in the plugin.
import { writeFileSync, readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
const [modules, catalogPath, output] = process.argv.slice(2);
const network = await import(pathToFileURL(resolve(modules, '@ffcafe/elpis-network/dist/index.mjs')));
const metadata = await import(pathToFileURL(resolve(modules, '@ffcafe/elpis-struct/dist/index.mjs')));
const catalog = JSON.parse(readFileSync(catalogPath, 'utf8'));
const ids = new Map();
const structures = {};
function visit(ctor) {
  if (ids.has(ctor)) return ids.get(ctor);
  const id = `s${ids.size}`;
  ids.set(ctor, id);
  const row = { name: ctor.name, minimumLength: ctor.byteLength ?? 0,
    discriminator: metadata.getStructIf(ctor), enums: metadata.getEnums(ctor) ?? [], fields: [] };
  structures[id] = row;
  for (const [name, field] of Object.entries(metadata.getFields(ctor.prototype) ?? {})) {
    const child = metadata.getChildren(ctor.prototype)?.[name];
    row.fields.push({ name, ...field, type: metadata.FieldType[field.type],
      child: !child ? undefined : typeof child === 'function'
        ? { structure: visit(child), byteLength: child.byteLength }
        : { type: metadata.FieldType[child.type], byteLength: child.byteLength } });
  }
  return id;
}
const packets = Object.entries(network.PacketMap).sort(([a], [b]) => a.localeCompare(b, 'en')).map(([name, ctor]) => {
  const entry = catalog.packets[name];
  return { name, ...(entry ?? { category: 'unavailable', direction: 'unavailable', aliases: [] }),
    structure: visit(ctor), mappingAvailable: !!entry };
});
const result = { formatVersion: 1, source: catalog.source, packets, structures };
writeFileSync(output, JSON.stringify(result) + '\n');
console.log(JSON.stringify({ packets: packets.length, structures: ids.size, output }));
