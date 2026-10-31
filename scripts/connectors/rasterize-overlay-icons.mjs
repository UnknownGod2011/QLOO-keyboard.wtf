// Development-only, mechanical rasterization of the existing licensed local
// service marks. The desktop ships PNG resources; no renderer/dependency/network
// is installed on a customer's machine. Pass an existing sharp module if needed.
import { readFile, mkdir } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const modulePath = process.argv[2];
const { default: sharp } = await import(modulePath ? pathToFileURL(path.resolve(modulePath)).href : 'sharp');
const names = ['gmail', 'googlecalendar', 'googledrive', 'googledocs', 'googleforms',
  'googlemeet', 'youtube', 'github', 'slack', 'discord', 'reddit', 'linkedin'];
const output = path.join(root, 'src/Resources/icons/connectors');
await mkdir(output, { recursive: true });
for (const name of names) {
  const svg = (await readFile(path.join(root, `production/site/connector-icons/${name}.svg`), 'utf8'))
    .replaceAll('currentColor', '#c8d9ff');
  await sharp(Buffer.from(svg), { density: 144 }).resize(48, 48).png()
    .toFile(path.join(output, `${name}.png`));
}
console.log(`Rasterized ${names.length} existing local provider marks; no downloads.`);
