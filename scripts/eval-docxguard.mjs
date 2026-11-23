import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { copyFile, mkdtemp, readFile, readdir, realpath, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const [source, caseFile] = process.argv.slice(2);
if (!source || !caseFile || process.argv.length !== 4) {
  console.error('Usage: node scripts/eval-docxguard.mjs <source.docx> <cases.json>');
  process.exit(2);
}

const repo = fileURLToPath(new URL('../', import.meta.url));
const localDotnet = join(repo, '.dotnet', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
const dotnet = existsSync(localDotnet) ? localDotnet : 'dotnet';
const cli = join(repo, 'src', 'DocxGuard.Cli', 'bin', 'Release', 'net10.0', 'DocxGuard.Cli.dll');
assert.ok(existsSync(cli), 'Build the DocxGuard CLI in Release before running the evaluation.');
const cases = JSON.parse((await readFile(caseFile, 'utf8')).replace(/^\uFEFF/u, ''));
assert.ok(Array.isArray(cases) && cases.length > 0, 'Supply at least one evaluation case.');
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

const original = await realpath(source);
const originalHash = sha256(await readFile(original));
const tempRoot = await realpath(tmpdir());
const workspace = await mkdtemp(join(tempRoot, 'docxguard-eval-'));
const sourcePath = 'sample.docx';
let server;

try {
  await copyFile(original, join(workspace, sourcePath));
  server = spawn(dotnet, [cli, 'mcp', '--workspace', workspace, '--allow-apply'], {
    stdio: ['pipe', 'pipe', 'ignore'],
    windowsHide: true,
  });
  server.stdin.on('error', () => server.kill());
  const closed = new Promise((done, fail) => {
    server.once('error', fail);
    server.once('close', done);
  });
  // Keep startup errors observed even if waiting for a protocol response.
  closed.catch(() => {});
  const replies = createInterface({ input: server.stdout })[Symbol.asyncIterator]();
  // A failed or stalled protocol call should not leave the child behind.
  const timeout = setTimeout(() => server.kill(), 120_000);
  timeout.unref();
  let id = 0;

  async function request(method, params) {
    const sent = ++id;
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: sent, method, params }) + '\n');
    const reply = await replies.next();
    assert.ok(!reply.done, 'MCP closed stdout before replying.');
    const message = JSON.parse(reply.value);
    assert.equal(message.id, sent, 'MCP reply has an unexpected request ID.');
    assert.ok(message.result, 'MCP did not return a result.');
    return message.result;
  }

  async function call(name, args) {
    return request('tools/call', { name, arguments: args });
  }

  function requireCase(condition, message) {
    if (!condition) throw new Error(message);
  }

  function rejected(result, code) {
    return result.isError === true
      && result.structuredContent?.findings?.some(finding => finding.code === code);
  }

  function recordFindings(info, result) {
    if (!result.isError) return;
    info.findings = (result.structuredContent?.findings ?? []).map(({ code, requestIndex, paragraphId }) =>
      ({ code, requestIndex, paragraphId }));
  }

  const init = await request('initialize', {
    protocolVersion: '2025-11-25',
    capabilities: {},
    clientInfo: { name: 'DocxGuard evaluation', version: '1.0.0' },
  });
  assert.equal(init.serverInfo.name, 'DocxGuard');
  server.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n');
  const inspection = await call('docxguard_inspect', { sourcePath, limit: 1 });
  assert.ok(!inspection.isError && inspection.structuredContent.status === 'passed', 'Source inspection was rejected.');

  const results = [];
  for (const [index, test] of cases.entries()) {
    const { name, mode, requests, expectedCode } = test;
    assert.match(name, /^[a-z0-9][a-z0-9-]{0,63}$/u);
    assert.ok(['success', 'reject-plan', 'reject-tampered'].includes(mode), 'Unknown evaluation mode.');
    assert.ok(Array.isArray(requests) && requests.length > 0, 'Each case needs replacement requests.');
    assert.ok(mode === 'success' || typeof expectedCode === 'string', 'Rejection cases need an expected code.');

    const destination = 'result-' + index;
    const before = (await readdir(workspace)).sort();
    const info = { name, mode, passed: false };
    const started = performance.now();
    try {
      const planned = await call('docxguard_plan', { sourcePath, requests });
      recordFindings(info, planned);
      if (mode === 'reject-plan') {
        requireCase(rejected(planned, expectedCode), 'The plan was not rejected with the expected code.');
        info.rejection = expectedCode;
      } else {
        requireCase(!planned.isError, 'The tool rejected a plan expected to succeed.');
        const planJson = planned.content[0].text;
        const preview = await call('docxguard_dry_run', { sourcePath, planJson });
        recordFindings(info, preview);
        const report = preview.structuredContent?.buildReport;
        requireCase(!preview.isError && report?.status === 'passed', 'Preview did not pass.');
        requireCase(report.operations?.length === requests.length, 'The report has an unexpected operation count.');
        requireCase(report.operations.every(operation => operation.expectedOutputParagraphSha256
          && operation.expectedOutputParagraphSha256 === operation.actualOutputParagraphSha256),
        'The final paragraph hashes did not match the planned result.');
        requireCase(report.checks?.every(check => check.status === 'passed'), 'A build check did not pass.');
        requireCase(JSON.stringify((await readdir(workspace)).sort()) === JSON.stringify(before), 'Preview left files on disk.');

        if (mode === 'reject-tampered') {
          const tampered = JSON.parse(planJson);
          tampered.plan.operations[0].replacementText += ' [changed after preview]';
          const response = await call('docxguard_apply', {
            sourcePath, planJson: JSON.stringify(tampered), outputDirectory: destination,
          });
          requireCase(rejected(response, expectedCode), 'The tampered plan was not rejected with the expected code.');
          info.rejection = expectedCode;
        } else {
          const response = await call('docxguard_apply', { sourcePath, planJson, outputDirectory: destination });
          recordFindings(info, response);
          requireCase(!response.isError && response.structuredContent?.publication?.status === 'published', 'Publication failed.');
          const outputDirectory = join(workspace, destination);
          requireCase(JSON.stringify((await readdir(outputDirectory)).sort())
            === '["build-report.json","output.docx"]', 'Publication did not contain exactly two files.');
          const outputHash = sha256(await readFile(join(outputDirectory, 'output.docx')));
          const savedReport = JSON.parse(await readFile(join(outputDirectory, 'build-report.json'), 'utf8'));
          requireCase(report.outputPackageSha256 === outputHash && savedReport.outputPackageSha256 === outputHash,
            'Preview, published DOCX, and saved report disagree on the output hash.');
          const changed = savedReport.entries.filter(entry => entry.sourceSha256 !== entry.outputSha256);
          requireCase(changed.length === 1 && changed[0].isMainDocument === true, 'An entry outside the main document changed.');
          info.operations = report.operations.length;
          info.unchangedEntries = savedReport.entries.length - changed.length;
        }
      }

      if (mode !== 'success')
        requireCase(JSON.stringify((await readdir(workspace)).sort()) === JSON.stringify(before), 'Rejected case left artifacts.');
      info.passed = true;
    } catch (error) {
      info.error = error.message;
    }
    info.elapsedMs = Math.round(performance.now() - started);
    results.push(info);
  }

  const untouched = sha256(await readFile(original)) === originalHash
    && sha256(await readFile(join(workspace, sourcePath))) === originalHash;
  console.log(JSON.stringify({ fixture: inspection.structuredContent.summary, untouched, cases: results }, null, 2));
  process.exitCode = untouched && results.every(test => test.passed) ? 0 : 1;
  server.stdin.end();
  await closed;
  clearTimeout(timeout);
} finally {
  if (server?.pid && server.exitCode === null && server.signalCode === null) {
    const exited = new Promise(done => server.once('close', done));
    server.kill();
    await exited;
  }
  const actual = await realpath(workspace);
  if (dirname(actual) === tempRoot && basename(actual).startsWith('docxguard-eval-'))
    await rm(actual, { recursive: true });
}
