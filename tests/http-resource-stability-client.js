const base = process.env.RESOURCE_BASE;
const secret = process.env.RESOURCE_SECRET;
const count = Math.max(1, Number(process.env.RESOURCE_COUNT || 1200));
const concurrency = Math.max(1, Number(process.env.RESOURCE_CONCURRENCY || 12));
if (!base || !secret) throw new Error('HTTP resource stability environment is incomplete');
const headers = { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' };

async function worker(index) {
  for (let current = index; current < count; current += concurrency) {
    const path = current % 5 === 0
      ? '/api/workbench/activity?after=0&timeoutMs=1000'
      : '/api/bootstrap';
    const response = await fetch(base + path, { headers });
    if (!response.ok) throw new Error(`${path} returned ${response.status}`);
    await response.arrayBuffer();
  }
}

Promise.all(Array.from({ length: concurrency }, (_, index) => worker(index)))
  .then(() => process.stdout.write(JSON.stringify({ requests: count, concurrency })))
  .catch(error => { process.stderr.write((error && error.stack) || String(error)); process.exitCode = 1; });
