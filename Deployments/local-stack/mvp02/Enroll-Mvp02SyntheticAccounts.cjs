const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const path = require('node:path');
const { randomBytes, randomUUID } = require('node:crypto');

const origin = 'https://app.mvp02.zeka-m1.test:24446';
const sessionRoute = '/api/v1/browser-session';
const registrationRoute = sessionRoute + '/onboarding/registrations';
const mailpit = 'http://127.0.0.1:18025';
const requestedRoot = process.argv[2];
if (!requestedRoot || !path.isAbsolute(requestedRoot)) process.exit(1);
const privateRoot = fs.realpathSync(requestedRoot);
const project = path.basename(privateRoot);
if (!/^zeka-mvp02-[a-f0-9]{8}$/.test(project) || privateRoot.toLowerCase() !== path.resolve(requestedRoot).toLowerCase()) process.exit(1);
const fixtureId = project.slice('zeka-mvp02-'.length);
const accountFile = path.join(privateRoot, 'synthetic-accounts.json');
const bindingFile = path.join(privateRoot, 'synthetic-auth-bindings.json');
const ca = fs.readFileSync(path.join(privateRoot, 'tls', 'ca.pem'));
const fixtureRoles = ['ownerA', 'ownerB', 'assigned'];
let stage = 'initialize';

function guarded(condition, reason) {
  if (!condition) throw new Error(reason);
}

function exchange(url, { method = 'GET', headers = {}, payload } = {}) {
  const body = payload === undefined ? undefined : Buffer.from(JSON.stringify(payload), 'utf8');
  if (body) {
    headers['Content-Type'] = 'application/json';
    headers['Content-Length'] = body.length;
  }
  const tls = url.origin === origin;
  guarded(tls || url.origin === mailpit, 'Unexpected request origin');
  const options = { method, headers, timeout: 8000, agent: false };
  if (tls) {
    options.ca = ca;
    options.rejectUnauthorized = true;
    options.family = 4;
    options.lookup = (_hostname, _options, callback) => callback(null, '127.0.0.1', 4);
  }
  return new Promise((resolve, reject) => {
    const request = (tls ? https : http).request(url, options, response => {
      const chunks = [];
      let length = 0;
      response.on('data', chunk => {
        length += chunk.length;
        if (length > 262144) response.destroy(new Error('Body limit exceeded'));
        else chunks.push(chunk);
      });
      response.on('error', reject);
      response.on('end', () => {
        resolve({
          status: response.statusCode,
          cookies: response.headers['set-cookie'] || [],
          text: Buffer.concat(chunks).toString('utf8')
        });
      });
    });
    request.on('timeout', () => request.destroy(new Error('Request timeout')));
    request.on('error', reject);
    request.end(body);
  });
}

function mustStatus(response, expected) {
  guarded(response.status === expected, 'Unexpected response ' + response.status + ', expected ' + expected);
}

function csrfAndAuth() {
  const jar = new Map();
  return async (route, { payload, csrf, bearer, idempotencyKey } = {}) => {
    const headers = { Origin: origin, Accept: 'application/json' };
    const cookies = [...jar.entries()].filter(([name]) =>
      name !== '__Secure-ZekaOnboarding' || route.startsWith(sessionRoute + '/onboarding/'));
    if (cookies.length) headers.Cookie = cookies.map(([, cookie]) => cookie).join('; ');
    if (csrf) headers['X-Zeka-CSRF'] = csrf;
    if (bearer) headers.Authorization = 'Bearer ' + bearer;
    if (idempotencyKey) headers['Idempotency-Key'] = idempotencyKey;
    const result = await exchange(new URL(route, origin), { method: payload === undefined ? 'GET' : 'POST', headers, payload });
    for (const cookie of result.cookies) {
      const first = cookie.split(';', 1)[0];
      const cookieName = first.split('=', 1)[0];
      if (/^__Secure-Zeka[A-Za-z-]+$/.test(cookieName)) jar.set(cookieName, first);
    }
    return result;
  };
}

async function csrfFor(send, bearer) {
  const result = await send(sessionRoute + '/csrf', { bearer });
  mustStatus(result, 200);
  const value = JSON.parse(result.text);
  guarded(value.contractVersion === 1 && typeof value.csrfToken === 'string' && value.csrfToken.length > 10,
    'Invalid antiforgery response');
  return value.csrfToken;
}

async function tokenFor(email) {
  const search = new URL('/api/v1/search', mailpit);
  search.searchParams.set('query', 'to:' + email);
  search.searchParams.set('limit', '5');
  for (let attempt = 0; attempt < 25; attempt++) {
    const result = await exchange(search);
    mustStatus(result, 200);
    const summaries = JSON.parse(result.text).messages;
    guarded(Array.isArray(summaries), 'Unexpected Mailpit search response');
    const relevant = summaries.filter(item => Array.isArray(item.To) &&
      item.To.some(recipient => recipient.Address === email));
    guarded(relevant.length < 2, 'Fixture has multiple verification messages');
    if (relevant.length === 1) {
      const id = relevant[0].ID;
      guarded(typeof id === 'string' && /^[A-Za-z0-9-]+$/.test(id), 'Invalid Mailpit message identifier');
      const message = await exchange(new URL('/api/v1/message/' + id, mailpit));
      mustStatus(message, 200);
      const { Text } = JSON.parse(message.text);
      guarded(typeof Text === 'string', 'Verification message text missing');
      const links = Text.match(/https:\/\/[^\s<>]+/g) || [];
      const verified = links.map(value => new URL(value)).filter(url =>
        url.origin === origin && url.username === '' && url.password === '' &&
        url.search === '' && url.hash.startsWith('#token='));
      guarded(verified.length === 1, 'Unexpected verification link identity');
      const fields = new URLSearchParams(verified[0].hash.slice(1));
      const token = fields.get('token');
      guarded(fields.size === 1 && typeof token === 'string' && /^[A-Za-z0-9._-]{83,126}$/.test(token),
        'Unexpected verification token format');
      return token;
    }
    await new Promise(resolve => setTimeout(resolve, 800));
  }
  throw new Error('Mailpit verification wait expired');
}

function validGuid(value) {
  return typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
    value !== '00000000-0000-0000-0000-000000000000';
}

async function enroll(account, alreadyVerified) {
  const send = csrfAndAuth();
  if (!alreadyVerified) {
    stage = account.role + '/registration';
    const csrf = await csrfFor(send);
    mustStatus(await send(registrationRoute, {
      csrf,
      payload: { email: account.email, password: account.password, firstName: 'Fixture', lastName: account.role }
    }), 202);

    stage = account.role + '/mail-verification';
    const token = await tokenFor(account.email);
    mustStatus(await send(registrationRoute + '/email-verification/exchange', { csrf, payload: { token } }), 204);
  }

  stage = account.role + '/sign-in';
  const response = await send(sessionRoute + '/sign-in', {
    csrf: await csrfFor(send), payload: { email: account.email, password: account.password }
  });
  mustStatus(response, 200);
  const session = JSON.parse(response.text);
  guarded(session.contractVersion === 1 && validGuid(session.subjectId) &&
    typeof session.accessToken === 'string' && session.accessToken.split('.').length === 3,
    'Unexpected genuine session response');
  if (!account.organisationName) return { subjectId: session.subjectId };

  stage = account.role + '/organisation-creation';
  const created = await send(sessionRoute + '/organisations', {
    csrf: await csrfFor(send, session.accessToken),
    bearer: session.accessToken,
    idempotencyKey: account.idempotencyKey,
    payload: { displayName: account.organisationName }
  });
  mustStatus(created, 201);
  const organisation = JSON.parse(created.text);
  guarded(organisation.contractVersion === 1 && organisation.subjectId === session.subjectId &&
    validGuid(organisation.organisationId) && validGuid(organisation.membershipId), 'Unexpected owner binding');
  return { subjectId: session.subjectId, organisationId: organisation.organisationId,
    membershipId: organisation.membershipId, name: account.organisationName };
}

async function main() {
  const resume = process.argv.length === 4 && process.argv[3] === '--resume-after-owner-a-verification';
  guarded(process.argv.length === 3 || resume, 'Unexpected enrollment action');
  guarded(!fs.existsSync(bindingFile) && fs.existsSync(accountFile) === resume,
    'Expected fresh or verified owner A account state is unavailable');
  stage = 'gateway-auth-preflight';
  await csrfFor(csrfAndAuth());
  stage = 'mailpit-preflight';
  const mailSearch = new URL('/api/v1/search', mailpit);
  mailSearch.searchParams.set('query', 'to:mvp02-admission-probe@example.invalid');
  mailSearch.searchParams.set('limit', '1');
  const mailResponse = await exchange(mailSearch);
  mustStatus(mailResponse, 200);
  guarded(Array.isArray(JSON.parse(mailResponse.text).messages), 'Unexpected Mailpit search response');
  const tag = randomBytes(6).toString('hex');
  const accounts = resume ? JSON.parse(fs.readFileSync(accountFile, 'utf8')) : fixtureRoles.map((role, i) => ({
    role,
    email: 'mvp02-' + fixtureId + '-' + role.toLowerCase() + '-' + tag + '@example.invalid',
    password: randomBytes(24).toString('base64url') + '!7aA',
    organisationName: i < 2 ? 'MVP02 Fixture ' + (i === 0 ? 'Alpha' : 'Beta') : undefined,
    idempotencyKey: i < 2 ? randomUUID() : undefined
  }));
  guarded(Array.isArray(accounts) && accounts.length === fixtureRoles.length && accounts.every((account, i) =>
    account.role === fixtureRoles[i] && new RegExp('^mvp02-' + fixtureId + '-[a-z]+-[0-9a-f]{12}@example\\.invalid$').test(account.email) &&
    typeof account.password === 'string' && account.password.length >= 32 &&
    (i === 2 || validGuid(account.idempotencyKey))), 'Existing synthetic account configuration mismatch');
  if (!resume) fs.writeFileSync(accountFile, JSON.stringify(accounts, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' });

  const bindings = { project };
  for (const account of accounts) {
    bindings[account.role] = await enroll(account, resume && account.role === 'ownerA');
    process.stdout.write(account.role + ' enrollment passed\n');
  }
  guarded(bindings.ownerA.organisationId !== bindings.ownerB.organisationId &&
    new Set(fixtureRoles.map(role => bindings[role].subjectId)).size === fixtureRoles.length,
    'Synthetic identities or organisations are not distinct');
  stage = 'write-bindings';
  fs.writeFileSync(bindingFile, JSON.stringify(bindings, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' });
  process.stdout.write('AUTH_BINDINGS_READY\n');
}

main().catch(error => {
  const safeStatus = /^Unexpected response \d+, expected \d+$/.test(error.message) ? ' (' + error.message + ')' : '';
  const safeCode = typeof error.code === 'string' && /^[A-Z_0-9]+$/.test(error.code) ? ' (' + error.code + ')' : '';
  process.stderr.write('MVP02 synthetic enrollment stopped at ' + stage + safeStatus + safeCode + '\n');
  process.exitCode = 1;
});
