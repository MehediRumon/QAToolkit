'use strict';
/**
 * ums-helpers — shared UMS automation helpers for QA Toolkit Playwright scripts.
 *
 * DEPLOY: automatic — at startup the app copies every ScriptLibs/*.js file to
 *   <Playwright:WorkingDirectory>\node_modules\  (see Program.cs).
 *   The runner sets NODE_PATH to that node_modules folder, so every script
 *   can simply `require('ums-helpers')`.
 *
 * USAGE in a script:
 *   const ums = require('ums-helpers');
 *   const base = ums.umsBase(server);                  // 2 → https://ums-2.osl.team
 *   await ums.login(page, base, username, password);
 *   const ids = await ums.resolveCascade(page, base, {
 *     org: cascadeOrg, program: cascadeProgram,
 *     session: cascadeSession, course: cascadeCourse,
 *   }, console.log);
 *   await ums.applyCascade(page, ids, ums.SELS_FORM()); // or ums.SELS_SEARCH
 *
 * BONUS — name your script params exactly:
 *   servers, username, password, cascadeOrg, cascadeProgram, cascadeSession, cascadeCourse
 * and the QA Toolkit parameter form shows a "Load UMS dropdowns" button that
 * turns the cascade fields into live Organization → Program → Session → Course
 * dropdowns (loaded server-side with the same credentials).
 */

// ── Server base URL ──────────────────────────────────────────────────────────
// 2 → https://ums-2.osl.team   |   0 / '' → https://ums.osl.team
function umsBase(server) {
  const s = String(server ?? '').trim();
  return (!s || s === '0') ? 'https://ums.osl.team' : `https://ums-${s}.osl.team`;
}

// Normalize a servers param that may be an array [1,3], a number 2, or "1,3"
function serverList(servers) {
  return Array.isArray(servers)
    ? servers
    : String(servers ?? '').split(',').map(x => x.trim()).filter(x => x !== '');
}

// ── Login ────────────────────────────────────────────────────────────────────
// Navigates to the server and logs in if the login page appears.
async function login(page, base, username, password) {
  await page.goto(`${base}/`);
  await page.waitForLoadState('domcontentloaded');
  if (page.url().includes('/Account/Login')) {
    await page.fill('#UserName', username);
    await page.fill('#Password', password);
    await page.click("button[type='submit']");
    await page.waitForLoadState('domcontentloaded');
    if (page.url().includes('/Account/Login')) {
      throw new Error('UMS login failed — check username/password.');
    }
  }
}

// ── Low-level helpers ────────────────────────────────────────────────────────
// Wait until a <select> has more than one option (its AJAX load finished).
async function waitOpts(page, sel, ms = 8000) {
  await page.waitForFunction(
    s => document.querySelectorAll(s + ' option').length > 1, sel, { timeout: ms }
  ).catch(() => {});
}

// POST a form-urlencoded request from inside the logged-in page context
// (cookies/auth are included automatically). Returns parsed JSON or raw text.
async function postForm(page, url, bodyObj) {
  const body = Object.entries(bodyObj)
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`).join('&');
  return page.evaluate(async ({ url, body }) => {
    const resp = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
        'X-Requested-With': 'XMLHttpRequest',
      },
      body,
    });
    const txt = await resp.text();
    try { return JSON.parse(txt); } catch (_) { return txt; }
  }, { url, body });
}

// Normalize any UMS list response to [{id, name}].
// UMS wraps lists in varying keys (returnProgramList, returnSessionList,
// returnCourse, …) — take the first array-valued property of the object.
function normList(raw) {
  let arr = Array.isArray(raw) ? raw : null;
  if (!arr && raw && typeof raw === 'object') {
    arr = Object.values(raw).find(v => Array.isArray(v)) || [];
  }
  return (Array.isArray(arr) ? arr : []).map(it => ({
    id:   String(it.Value ?? it.value ?? it.Id ?? it.id ?? ''),
    name: String(it.Text ?? it.text ?? it.Name ?? it.name ?? '').trim(),
  })).filter(x => x.id && x.id !== '0' && x.name);
}

// Pick an item by name — exact match first, then partial (case-insensitive).
// Throws with the full list of available names when nothing matches.
function pickByName(list, wanted, label) {
  const w = String(wanted).trim().toLowerCase();
  const found = list.find(x => x.name.toLowerCase() === w)
             || list.find(x => x.name.toLowerCase().includes(w));
  if (!found) {
    const avail = list.map(x => x.name).join(' | ').slice(0, 500);
    throw new Error(`${label} "${wanted}" not found. Available: ${avail || '(empty list)'}`);
  }
  return found;
}

// ── Cascade resolution (names → IDs, via CommonAjax API) ─────────────────────
// Must be called on a logged-in page where #OrganizationId is populated
// (easiest: page.goto(`${base}/Exam/Exams/CreateExam`) first).
// names = { org, program, session, course }   log = optional logger fn
async function resolveCascade(page, base, names, log) {
  const s = log || (() => {});
  await waitOpts(page, '#OrganizationId');
  const orgOpts = await page.locator('#OrganizationId option').evaluateAll(els =>
    els.map(o => ({ id: o.value, name: (o.textContent || '').trim() }))
       .filter(o => o.id && o.id !== '0')
  );
  const org = pickByName(orgOpts, names.org, 'Organization');
  const prog = pickByName(normList(await postForm(page,
    `${base}/Administration/CommonAjax/LoadProgram`,
    { organizationIds: org.id, isAuthorized: 'true' })), names.program, 'Program');
  const sess = pickByName(normList(await postForm(page,
    `${base}/Administration/CommonAjax/LoadSession`,
    { programIds: prog.id, isAuthorized: 'true' })), names.session, 'Session');
  const course = pickByName(normList(await postForm(page,
    `${base}/Administration/CommonAjax/LoadCourse`,
    { programIds: prog.id, sessionIds: sess.id })), names.course, 'Course');
  s(`Cascade: ${org.name}(${org.id}) > ${prog.name}(${prog.id}) > ${sess.name}(${sess.id}) > ${course.name}(${course.id})`);
  return { org, prog, sess, course };
}

// ── Applying resolved IDs to page dropdowns ──────────────────────────────────
// Select by resolved ID, waiting for the page's own cascading JS to load the
// option. Falls back to label match if the value is missing on that page.
async function selectValueWhenReady(page, sel, item, ms = 10000) {
  const exists = await page.locator(sel).count().catch(() => 0);
  if (!exists) return;
  const ok = await page.waitForFunction(
    ({ sel, value }) => !!document.querySelector(`${sel} option[value="${value}"]`),
    { sel, value: item.id }, { timeout: ms }
  ).then(() => true).catch(() => false);
  if (ok) {
    await page.locator(sel).selectOption({ value: item.id });
  } else {
    await waitOpts(page, sel);
    const opts = await page.locator(sel + ' option').allTextContents();
    const target = opts.find(o => o.trim() === item.name)
                || opts.find(o => o.includes(item.name)) || opts[1];
    await page.locator(sel).selectOption({ label: target });
  }
  await page.waitForTimeout(600);
}

// Apply all four resolved IDs to a page's cascade dropdowns.
// sels = SELS_FORM() for entry forms, SELS_SEARCH for search/filter forms.
async function applyCascade(page, ids, sels) {
  await selectValueWhenReady(page, sels.org,    ids.org);
  await selectValueWhenReady(page, sels.prog,   ids.prog);
  await selectValueWhenReady(page, sels.sess,   ids.sess);
  await selectValueWhenReady(page, sels.course, ids.course);
}

// Selector sets used across UMS pages (override any key if a page differs,
// e.g. SELS_FORM({ course: '#course' }) on the CreateExam page).
const SELS_FORM = (ov = {}) => ({
  org:    ov.org    || '#OrganizationId',
  prog:   ov.prog   || '#ProgramId',
  sess:   ov.sess   || '#SessionId',
  course: ov.course || '#CourseId',
});
const SELS_SEARCH = { org: '#Organization', prog: '#Program', sess: '#Session', course: '#Course' };

// Response matcher for page.waitForResponse against a UMS server.
const isUmsResp = (base, pathMatch, method = 'POST') => res => {
  if (!res.url().startsWith(base + '/')) return false;
  if (method && res.request().method() !== method) return false;
  return typeof pathMatch === 'string' ? res.url().includes(pathMatch) : pathMatch.test(res.url());
};

module.exports = {
  umsBase,
  serverList,
  login,
  waitOpts,
  postForm,
  normList,
  pickByName,
  resolveCascade,
  selectValueWhenReady,
  applyCascade,
  SELS_FORM,
  SELS_SEARCH,
  isUmsResp,
};
