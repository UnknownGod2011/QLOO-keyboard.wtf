import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Read-only, loopback development server. Not a production API or OAuth owner.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const site = path.join(root, 'production/site');
const fixture = path.join(root, 'scripts/connectors/ui-fixture.html');
const types = { '.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.svg':'image/svg+xml', '.webp':'image/webp', '.png':'image/png' };
const server = http.createServer((req, res) => {
  try {
    const url = new URL(req.url, 'http://127.0.0.1');
    let relative = decodeURIComponent(url.pathname).replace(/^\/+/, '');
    if (relative === 'favicon.ico') relative = 'favicon.svg';
    if (relative === '__fixture-catalog') {
      // Test metadata derives from the one server registry, never a second
      // manually maintained production catalog. Availability is simulated.
      const source = fs.readFileSync(path.join(root, 'staging/backend/KeyboardWtf.Staging.Api/Connectors/ConnectorRegistry.cs'), 'utf8');
      const matches = [...source.matchAll(/new\("([a-z]+)", "([^"]+)", "([^"]+)", "([^"]+)", "([^"]+)"/g)];
      const helpSource = source.slice(source.indexOf('Help ='), source.indexOf('Activity ='));
      const help = new Map([...helpSource.matchAll(/"([a-z_]+)" => "([^"]+)"/g)].map(match=>[match[1],match[2]]));
      const catalog = matches.map((match,index)=>{
        const definition=source.slice(match.index,matches[index+1]?.index || source.indexOf('public ConnectorDefinition Get'));
        return {id:match[1],name:match[2],family:match[3],icon:match[1],description:match[5],searchTerms:[match[1],match[2]],
          capabilities:[...definition.matchAll(/(?:Read|Write)\("([a-z_]+)"/g)].map(cap=>help.get(cap[1])).filter(Boolean),
          availability:/\[\], true\)/.test(definition) ? 'coming_soon':'available'};
      });
      res.writeHead(200, {'Content-Type':'application/json','Cache-Control':'no-store'}); res.end(JSON.stringify(catalog)); return;
    }
    let file;
    if (relative === '__connector-fixture') file = fixture;
    else {
      if (!relative) relative = 'index.html';
      if (!path.extname(relative)) relative += '.html';
      file = path.resolve(site, relative);
      if (!file.startsWith(site + path.sep)) { res.writeHead(403); res.end(); return; }
    }
    if (!fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { 'Content-Type':types[path.extname(file)] || 'application/octet-stream', 'Cache-Control':'no-store' });
    fs.createReadStream(file).pipe(res);
  } catch { res.writeHead(400); res.end(); }
});
server.listen(4196, '127.0.0.1', () => console.log('Connector-only preview: http://127.0.0.1:4196/__connector-fixture'));
