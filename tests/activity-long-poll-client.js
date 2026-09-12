const assert = (condition, message) => { if (!condition) throw new Error(message); };
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

const base = process.env.ACTIVITY_BASE;
const secret = process.env.ACTIVITY_SECRET;
const sessionId = process.env.ACTIVITY_SESSION;
const jobId = process.env.ACTIVITY_JOB;
const workspace = process.env.ACTIVITY_WORKSPACE;
if (!base || !secret || !sessionId || !jobId || !workspace) throw new Error('Activity test environment is incomplete');

async function api(path, options = {}) {
  const response = await fetch(base + path, {
    ...options,
    headers: {
      'X-Desktop-Secret': secret,
      'X-Workbench-Protocol': '2',
      ...(options.body ? { 'Content-Type': 'application/json; charset=utf-8' } : {}),
      ...(options.headers || {})
    }
  });
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = { raw: text }; }
  if (!response.ok) throw new Error(`${path} returned ${response.status}: ${data?.error || text}`);
  return data;
}

async function waitActivity(after, timeoutMs) {
  const started = performance.now();
  const data = await api(`/api/workbench/activity?after=${after}&timeoutMs=${timeoutMs}&sessionId=${encodeURIComponent(sessionId)}`);
  return { data, elapsedMs: Math.round(performance.now() - started) };
}

async function waitForSnapshot(pending, after, predicate, label, timeoutMs = 3000) {
  const started = performance.now();
  let revision = Number(after) || 0;
  let result = await pending;
  while (!predicate(result.data)) {
    revision = Math.max(revision, Number(result.data?.revision) || 0);
    const remaining = Math.ceil(timeoutMs - (performance.now() - started));
    assert(remaining > 0, `${label} did not appear before the deadline`);
    result = await waitActivity(revision, Math.max(250, remaining));
  }
  return { data: result.data, elapsedMs: Math.round(performance.now() - started) };
}

async function main() {
  const initial = await waitActivity(0, 1500);
  assert(Number(initial.data.revision) > 0, 'Initial activity revision is missing');
  assert(initial.elapsedMs < 1000, `Initial snapshot did not return immediately (${initial.elapsedMs}ms)`);

  const queueWait = waitActivity(initial.data.revision, 5000);
  await delay(250);
  const queued = await api('/api/task-queue', {
    method: 'POST',
    body: JSON.stringify({
      sessionId,
      text: '离线活动队列夹具',
      kind: 'queued',
      request: { workspace, sessionId, claudeSessionId: sessionId, resume: false, prompt: '离线活动队列夹具' }
    })
  });
  const queueActivity = await waitForSnapshot(queueWait, initial.data.revision,
    data => Number(data.revision) > Number(initial.data.revision) && Array.isArray(data.queue) && data.queue.some(item => item.id === queued.id),
    'Queue mutation');
  assert(queueActivity.elapsedMs < 3000, `Queue mutation did not wake the activity channel (${queueActivity.elapsedMs}ms)`);
  assert(Number(queueActivity.data.revision) > Number(initial.data.revision), 'Queue mutation did not advance the revision');

  let revision = Number(queueActivity.data.revision);
  const approvalWait = waitActivity(revision, 5000);
  await delay(250);
  const approval = await api('/api/permissions/request', {
    method: 'POST',
    body: JSON.stringify({ jobId, toolName: 'Write', input: { file_path: `${workspace}\\活动测试.txt`, content: 'fixture' } })
  });
  assert(approval.state === 'pending', 'Permission fixture was not queued for manual approval');
  const approvalActivity = await waitForSnapshot(approvalWait, revision,
    data => Array.isArray(data.approvals) && data.approvals.some(item => item.id === approval.id),
    'Approval creation');
  assert(approvalActivity.elapsedMs < 3000, `Approval creation did not wake the activity channel (${approvalActivity.elapsedMs}ms)`);

  revision = Number(approvalActivity.data.revision);
  const responseWait = waitActivity(revision, 5000);
  await delay(250);
  await api('/api/permissions/respond', { method: 'POST', body: JSON.stringify({ id: approval.id, behavior: 'allow', message: '' }) });
  const responseActivity = await waitForSnapshot(responseWait, revision,
    data => Array.isArray(data.approvals) && !data.approvals.some(item => item.id === approval.id),
    'Approval response');
  assert(responseActivity.elapsedMs < 3000, `Approval response did not wake the activity channel (${responseActivity.elapsedMs}ms)`);

  revision = Number(responseActivity.data.revision);
  await delay(1100);
  const stable = await waitActivity(revision, 1700);
  let heartbeat = stable;
  if (stable.data.changed) {
    revision = Number(stable.data.revision);
    const secondStable = await waitActivity(revision, 1700);
    assert(!secondStable.data.changed && secondStable.data.heartbeat, 'Stable activity request did not return a heartbeat');
    assert(secondStable.elapsedMs >= 1400, `Heartbeat returned too early (${secondStable.elapsedMs}ms)`);
    heartbeat = secondStable;
  } else {
    assert(stable.data.heartbeat, 'Stable activity response is not marked as heartbeat');
    assert(stable.elapsedMs >= 1400, `Heartbeat returned too early (${stable.elapsedMs}ms)`);
  }

  const metrics = await api('/api/workbench/metrics');
  const routeCount = Number(metrics.http?.byRoute?.['GET /api/workbench/activity'] || 0);
  assert(routeCount >= 5, `Activity requests were not counted by route (${routeCount})`);
  const polluted = (metrics.http?.slowRequests || []).some(item => item.route === '/api/workbench/activity');
  assert(!polluted, 'Long-poll heartbeat polluted slow request evidence');
  assert(Number(metrics.http?.currentSession?.maxMs || 0) < 1400, `Long-poll heartbeat polluted current-session latency (${metrics.http.currentSession.maxMs}ms)`);

  process.stdout.write(JSON.stringify({
    activityLongPoll: 'PASS',
    revision: Number(heartbeat.data.revision),
    queueWakeMs: queueActivity.elapsedMs,
    approvalWakeMs: approvalActivity.elapsedMs,
    approvalResponseWakeMs: responseActivity.elapsedMs,
    heartbeatMs: heartbeat.elapsedMs,
    routeCount,
    slowRequestPollution: false,
    currentSessionMaxMs: Number(metrics.http?.currentSession?.maxMs || 0)
  }, null, 2));
}

main().catch(error => { process.stderr.write((error && error.stack) || String(error)); process.exitCode = 1; });
