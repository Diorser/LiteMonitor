import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const marker = /<!-- litemonitor-release:(\{[^\n]*\}) -->/;
export const hash = data => createHash('sha256').update(data).digest('hex');
export const git = (...args) => execFileSync('git', args, { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 }).trim();

export function version(value) {
    const match = /^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(value ?? '');
    if (!match) return null;
    const parts = match.slice(1).map(Number);
    return parts.every(n => Number.isSafeInteger(n) && n <= 65534) ? parts : null;
}
export const compare = (a, b) => {
    for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] - b[i];
    return 0;
};
export function releasePath(path) {
    if (/\.test\.mjs$/.test(path)) return false;
    if (/\.(md|txt)$/i.test(path) || path.startsWith('resources/screenshots/'))
        return path === '使用说明(必读).txt';
    return /^(src\/|Properties\/|LiteMonitor\.Updater\/|resources\/(assets|themes|lang|plugins)\/|automation\/)/.test(path)
        || /^(LiteMonitor\.(csproj|sln)|Directory\.Build\.(props|targets)|global\.json|NuGet\.Config|packages\.lock\.json|\.github\/workflows\/delivery\.yml)$/.test(path);
}
export const validationPath = path => releasePath(path) || path.startsWith('test/') || path.startsWith('.github/workflows/') || /^automation\/.*\.test\.mjs$/.test(path);
export function fingerprint(commit) {
    const entries = git('ls-tree', '-r', '-z', commit).split('\0').filter(entry => releasePath(entry.split('\t')[1] ?? ''));
    return hash(entries.sort().join('\0'));
}
export function provenance(release) {
    const match = marker.exec(release.body ?? '');
    if (!match) return null;
    try { return JSON.parse(match[1]); } catch { throw new Error(`Release ${release.tag_name} 的发布标记损坏`); }
}
export function selectVersion({ baseline, tags, releases, commit, fingerprint: currentFingerprint, previousFingerprint }) {
    const stable = releases.filter(r => !r.draft && !r.prerelease && version(r.tag_name))
        .sort((a, b) => compare(version(b.tag_name), version(a.tag_name)));
    const latest = stable[0];
    if (latest && previousFingerprint === currentFingerprint) return { needed: false, version: latest.tag_name.replace(/^v/, '') };
    const draft = releases.find(r => r.draft && !r.prerelease && provenance(r)?.commit === commit);
    if (draft) {
        const data = provenance(draft);
        if (data.fingerprint !== currentFingerprint || data.version !== draft.tag_name.replace(/^v/, '') || !version(draft.tag_name))
            throw new Error('已有草稿与当前源码不一致，拒绝复用');
        if (latest && compare(version(draft.tag_name), version(latest.tag_name)) <= 0)
            throw new Error('已有草稿版本落后于正式版本，拒绝倒退发布');
        return { needed: true, version: data.version };
    }
    const candidates = [baseline, ...tags, ...releases.map(r => r.tag_name)].map(version).filter(Boolean);
    if (!version(baseline)) throw new Error('版本基线必须为三段数字');
    const highest = candidates.sort(compare).at(-1);
    const next = [...highest];
    if (next[2] < 65534) next[2]++;
    else if (next[1] < 65534) { next[1]++; next[2] = 0; }
    else { next[0]++; next[1] = next[2] = 0; }
    if (!version(next.join('.'))) throw new Error('版本超出 Windows 文件版本范围');
    return { needed: true, version: next.join('.') };
}

export function notes(commits, repository, previous, commit) {
    const groups = { '新增': [], '修复': [], '其他变更': [] };
    const seen = new Set();
    for (const { sha, subject, paths } of commits) {
        if (!paths.some(releasePath) || /^(Merge |Bump version|chore(?:\([^)]*\))?:?\s*(?:release|version))/i.test(subject)) continue;
        const title = subject.trim().replace(/\s+/g, ' ');
        const key = title.toLowerCase().replace(/\s*\(#\d+\)$/, '');
        if (seen.has(key)) continue;
        seen.add(key);
        const group = /^(feat|add|新增|增加)(\b|[:：(])/i.test(title) ? '新增'
            : /^(fix|bugfix|prevent|修复|修正)(\b|[:：(])/i.test(title) ? '修复' : '其他变更';
        const safe = (title.length > 240 ? title.slice(0, 240) + '…' : title).replace(/[\[\]<>`]/g, '');
        groups[group].push(`- ${safe}（[${sha.slice(0, 7)}](https://github.com/${repository}/commit/${sha})）`);
    }
    let body = previous ? '本次更新基于上一个正式版本之后的实际代码变更。\n' : '本仓库首次自动发布，包含当前源码和完整运行资源。\n';
    for (const [group, items] of Object.entries(groups)) {
        if (items.length) body += `\n### ${group}\n\n${items.slice(0, 40).join('\n')}\n${items.length > 40 ? '\n更多变更见完整对比。\n' : ''}`;
    }
    if (!seen.size) body += '\n本次更新涉及构建或运行资源，详细内容请查看源码对比。\n';
    body += '\n### 下载与更新\n\nWindows x64 便携版，包含 .NET 运行时、从源码构建的更新器及主题、语言、插件和硬件组件。解压后运行 `LiteMonitor.exe`；硬件监测仍需管理员权限及 PawnIO 驱动。校验 ZIP 时使用同名 `.sha256` 文件。\n';
    body += previous ? `\n[完整变更](https://github.com/${repository}/compare/${previous}...${commit})\n` : `\n[对应源码](https://github.com/${repository}/tree/${commit})\n`;
    return body;
}

export class GitHub {
    constructor(repository, token = process.env.GH_TOKEN, fetcher = fetch) {
        if (!/^[\w.-]+\/[\w.-]+$/.test(repository)) throw new Error('无效的 GitHub 仓库名');
        this.repository = repository;
        this.fetcher = fetcher;
        this.headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'LiteMonitor-release', 'X-GitHub-Api-Version': '2022-11-28' };
        if (token) this.headers.Authorization = `Bearer ${token}`;
    }
    async request(path, { method = 'GET', body, binary = false, upload = false, missing = false } = {}) {
        const url = path.startsWith('https://uploads.github.com/') ? path : `https://api.github.com/repos/${this.repository}/${path}`;
        for (let attempt = 0; ; attempt++) {
            try {
                const response = await this.fetcher(url, {
                    method, signal: AbortSignal.timeout(upload || binary ? 180000 : 30000),
                    headers: { ...this.headers, ...(binary ? { Accept: 'application/octet-stream' } : {}), ...(body ? { 'Content-Type': upload ? 'application/octet-stream' : 'application/json' } : {}) },
                    body: body ? (upload ? body : JSON.stringify(body)) : undefined
                });
                if (missing && response.status === 404) return null;
                if (!response.ok) {
                    const error = new Error(`GitHub ${method} ${path}: HTTP ${response.status} ${(await response.text()).slice(0, 500)}`);
                    error.retryable = response.status === 429 || response.status >= 500;
                    throw error;
                }
                if (response.status === 204) return null;
                return binary ? Buffer.from(await response.arrayBuffer()) : response.json();
            } catch (error) {
                if (method !== 'GET' || attempt >= 2 || error.retryable === false || /HTTP [4]/.test(error.message) && !error.retryable) throw error;
                await new Promise(r => setTimeout(r, 1000 * (attempt + 1)));
            }
        }
    }
    async releases() {
        const all = [];
        for (let page = 1; ; page++) {
            const batch = await this.request(`releases?per_page=100&page=${page}`);
            all.push(...batch);
            if (batch.length < 100) return all;
        }
    }
}

export function verifyArtifacts(plan, directory) {
    if (!version(plan.version) || plan.tag !== `v${plan.version}` || !/^[a-f0-9]{40}$/.test(plan.commit) || !/^[a-f0-9]{64}$/.test(plan.fingerprint)) throw new Error('发布计划无效');
    const name = `LiteMonitor-${plan.tag}-win-x64.zip`;
    const zip = readFileSync(join(directory, name));
    const digest = hash(zip);
    const checksum = readFileSync(join(directory, `${name}.sha256`), 'utf8').trim();
    const manifest = JSON.parse(readFileSync(join(directory, 'version.json'), 'utf8'));
    const url = `https://github.com/${plan.repository}/releases/download/${plan.tag}/${name}`;
    if (checksum !== `${digest}  ${name}` || manifest.version !== plan.version || manifest.sha256 !== digest || manifest.downloadUrl !== url)
        throw new Error('ZIP、SHA256 与更新清单不一致');
    return [name, `${name}.sha256`, 'version.json'].map(name => ({ name, data: readFileSync(join(directory, name)) }));
}

export async function publish(plan, directory, client, sourceFingerprint = fingerprint) {
    const files = verifyArtifacts(plan, directory);
    if (sourceFingerprint(plan.commit) !== plan.fingerprint) throw new Error('发布包对应的源码指纹不一致');
    const branch = await client.request('branches/master');
    if (branch.commit.sha !== plan.commit) {
        console.log('master 已前进，跳过过期构建；后续运行会汇总尚未发布的变更。');
        return 'stale';
    }
    let release = (await client.releases()).find(r => r.tag_name === plan.tag);
    if (release) {
        const data = provenance(release);
        if (!data || data.commit !== plan.commit || data.fingerprint !== plan.fingerprint || data.version !== plan.version || release.prerelease)
            throw new Error('版本已被其他内容占用，拒绝覆盖');
        if (release.name !== `LiteMonitor ${plan.tag}`) throw new Error('已有 Release 标题与版本不一致');
    } else {
        // 先创建草稿保存事务身份，再创建 Tag；中断后可从同一草稿继续。
        const body = readFileSync(join(directory, 'notes.md'), 'utf8') + `\n<!-- litemonitor-release:${JSON.stringify({ commit: plan.commit, fingerprint: plan.fingerprint, version: plan.version })} -->`;
        release = await client.request('releases', { method: 'POST', body: { tag_name: plan.tag, target_commitish: plan.commit, name: `LiteMonitor ${plan.tag}`, body, draft: true, prerelease: false } });
    }
    const assets = await client.request(`releases/${release.id}/assets?per_page=100`);
    for (const file of files) {
        let asset = assets.find(a => a.name === file.name);
        if (asset) {
            const digest = asset.digest?.replace(/^sha256:/, '') ?? hash(await client.request(`releases/assets/${asset.id}`, { binary: true }));
            if (asset.size !== file.data.length || digest !== hash(file.data)) {
                if (!release.draft) throw new Error(`正式 Release 附件校验失败：${file.name}`);
                await client.request(`releases/assets/${asset.id}`, { method: 'DELETE' });
                asset = null;
            }
        }
        if (!asset) {
            if (!release.draft) throw new Error(`正式 Release 缺少附件：${file.name}`);
            asset = await client.request(`${release.upload_url.split('{')[0]}?name=${encodeURIComponent(file.name)}`, { method: 'POST', body: file.data, upload: true });
        }
        const remoteHash = asset.digest?.replace(/^sha256:/, '') ?? hash(await client.request(`releases/assets/${asset.id}`, { binary: true }));
        if (asset.state !== 'uploaded' || asset.size !== file.data.length || remoteHash !== hash(file.data)) throw new Error(`上传校验失败：${file.name}`);
    }
    let ref = await client.request(`git/ref/tags/${plan.tag}`, { missing: true });
    if (!ref) ref = await client.request('git/refs', { method: 'POST', body: { ref: `refs/tags/${plan.tag}`, sha: plan.commit } });
    if (ref.object.type !== 'commit' || ref.object.sha !== plan.commit) throw new Error('Tag 已指向其他提交，拒绝移动');
    // 发布前再次确认 master，避免构建期间的新提交被旧版本覆盖。
    if ((await client.request('branches/master')).commit.sha !== plan.commit) return 'stale';
    if (release.draft) await client.request(`releases/${release.id}`, { method: 'PATCH', body: { draft: false, make_latest: 'true' } });
    const completed = await client.request(`releases/${release.id}`);
    if (completed.draft || completed.prerelease || completed.tag_name !== plan.tag || completed.name !== `LiteMonitor ${plan.tag}`)
        throw new Error('GitHub 未确认正式发布成功');
    console.log(`发布已确认：https://github.com/${plan.repository}/releases/tag/${plan.tag}`);
    return 'published';
}

async function planRelease() {
    const repository = process.env.GITHUB_REPOSITORY ?? 'ChenBit/LiteMonitor';
    const commit = git('rev-parse', 'HEAD');
    const currentFingerprint = fingerprint(commit);
    const baseline = /<Version Condition="'\$\(ReleaseVersion\)' == ''">([^<]+)<\/Version>/.exec(readFileSync('Directory.Build.props', 'utf8'))?.[1];
    const event = process.env.GITHUB_EVENT_PATH ? JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8')) : {};
    const official = ['push', 'workflow_dispatch', 'schedule'].includes(process.env.GITHUB_EVENT_NAME) && process.env.GITHUB_REF === 'refs/heads/master';
    let selection = { needed: false, version: baseline };
    let previous = '';
    if (official) {
        const client = new GitHub(repository);
        const releases = await client.releases();
        const latest = releases.filter(r => !r.draft && !r.prerelease && version(r.tag_name)).sort((a, b) => compare(version(b.tag_name), version(a.tag_name)))[0];
        let previousFingerprint;
        if (latest) {
            previous = latest.tag_name;
            const sha = git('rev-list', '-1', previous);
            git('merge-base', '--is-ancestor', sha, commit);
            previousFingerprint = fingerprint(sha);
        }
        selection = selectVersion({ baseline, tags: git('tag', '--list').split('\n'), releases, commit, fingerprint: currentFingerprint, previousFingerprint });
        if (!selection.needed && latest) {
            const required = [`LiteMonitor-v${selection.version}-win-x64.zip`, `LiteMonitor-v${selection.version}-win-x64.zip.sha256`, 'version.json'];
            if (!required.every(name => latest.assets.some(a => a.name === name && a.state === 'uploaded' && a.size > 0))) throw new Error('已有正式 Release 附件不完整，请检查发布记录');
        }
    }
    let base = event.pull_request?.base?.sha ?? event.before;
    if (!base || /^0+$/.test(base)) {
        try { base = git('merge-base', 'origin/master', commit); } catch { base = git('rev-parse', `${commit}^`); }
    }
    const paths = git('diff', '--name-only', '-z', base, commit).split('\0');
    let build = official ? selection.needed : paths.some(validationPath);
    if (process.env.GITHUB_EVENT_NAME === 'push' && !official && process.env.GITHUB_REF?.startsWith('refs/heads/ci/')) {
        const branch = process.env.GITHUB_REF.slice('refs/heads/'.length);
        const owner = repository.split('/')[0];
        const existing = await new GitHub(repository).request(`pulls?state=open&base=master&head=${encodeURIComponent(`${owner}:${branch}`)}&per_page=1`);
        if (existing.length) { build = false; console.log('该分支已有 PR，由 PR 运行验证合并结果。'); }
    }
    const plan = { repository, commit, fingerprint: currentFingerprint, version: selection.version, tag: `v${selection.version}`, build, release: official && selection.needed, previous, date: git('show', '-s', '--format=%cs', commit) };
    mkdirSync('artifacts', { recursive: true });
    writeFileSync('artifacts/plan.json', JSON.stringify(plan, null, 2) + '\n');
    const range = previous ? `${previous}..${commit}` : (base === commit ? commit : `${base}..${commit}`);
    const commits = git('log', '--no-merges', '--format=%H%x09%s', range).split('\n').filter(Boolean).map(line => {
        const [sha, ...subject] = line.split('\t');
        return { sha, subject: subject.join('\t'), paths: git('diff-tree', '--root', '--no-commit-id', '--name-only', '-r', '-z', sha).split('\0') };
    });
    writeFileSync('artifacts/notes.md', notes(commits, repository, previous, commit));
    if (process.env.GITHUB_OUTPUT) for (const key of ['build', 'release', 'version', 'commit']) appendFileSync(process.env.GITHUB_OUTPUT, `${key}=${plan[key]}\n`);
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `构建：${build ? '需要' : '跳过（没有待构建变更）'}；正式发布：${plan.release ? plan.tag : '跳过'}。\n`);
    console.log(JSON.stringify(plan, null, 2));
}

async function main() {
    if (process.argv[2] === 'plan') return planRelease();
    if (process.argv[2] === 'publish') {
        const directory = resolve(process.argv[3] ?? 'artifacts');
        const plan = JSON.parse(readFileSync(join(directory, 'plan.json'), 'utf8'));
        if (!plan.release || plan.repository !== process.env.GITHUB_REPOSITORY || plan.commit !== process.env.GITHUB_SHA || process.env.GITHUB_REF !== 'refs/heads/master') throw new Error('发布上下文不匹配');
        const client = new GitHub(plan.repository);
        for (let attempt = 0; ; attempt++) {
            try { return await publish(plan, directory, client); }
            catch (error) {
                if (attempt >= 2) throw error;
                console.error(`发布事务失败，按同一版本重试：${error.message}`);
                await new Promise(r => setTimeout(r, 3000 * (attempt + 1)));
            }
        }
    }
    throw new Error(`用法：node ${basename(process.argv[1])} plan|publish [artifacts]`);
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main().catch(error => { console.error(error); process.exitCode = 1; });
