import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { version, compare, releasePath, validationPath, selectVersion, notes, hash, publish, verifyArtifacts, GitHub, fingerprint as sourceFingerprint, validateRuntimeLock, releaseIntent } from './release.mjs';

const commit = 'a'.repeat(40);
const fingerprint = 'b'.repeat(64);
const defaults = { baseline: '1.3.8', tags: [], releases: [], commit, fingerprint };
const managed = (options = {}) => ({ tag_name: 'v1.3.9', draft: true, prerelease: false, body: `<!-- litemonitor-release:${JSON.stringify({ commit, fingerprint, version: '1.3.9' })} -->`, ...options });

test('锁文件保留每个框架的 Windows x64 目标，包括没有 NuGet 包的项目', () => {
    for (const target of ['net8.0', 'net8.0-windows7.0']) {
        assert.doesNotThrow(() => validateRuntimeLock({ dependencies: { [target]: {}, [`${target}/win-x64`]: {} } }, 'packages.lock.json'));
    }
});
test('重新生成锁文件时丢失或错用运行时目标会明确失败', () => {
    for (const dependencies of [{}, { 'net8.0': {} }, { 'net8.0': {}, 'net8.0/linux-x64': {} }, { 'net8.0': {}, 'net8.0/win-x64': {}, 'net8.0-windows7.0': {} }]) {
        assert.throws(() => validateRuntimeLock({ dependencies }, 'test/packages.lock.json'), /test\/packages.lock.json 缺少 win-x64/);
    }
});

test('仅接受 Windows 可表达的三段正式版本', () => {
    assert.deepEqual(version('v1.3.9'), [1, 3, 9]);
    for (const invalid of ['v1.3.9-rc.1', '1.2', '1.02.3', '1.2.3.4', '1.2.65535']) assert.equal(version(invalid), null);
    assert.ok(compare(version('1.10.0'), version('1.9.99')) > 0);
});
test('首次发布使用明确的程序版本，不额外加一', () => assert.deepEqual(selectVersion(defaults), { needed: true, version: '1.3.8' }));
test('1.3.10 之后声明 1.4.0 时准确使用 1.4.0', () => {
    assert.deepEqual(selectVersion({ ...defaults, baseline: '1.4.0', tags: ['v1.3.9', 'v1.3.10'] }), { needed: true, version: '1.4.0' });
});
test('功能版与主版本声明优先于默认补丁递增', () => {
    for (const baseline of ['1.4.0', '2.0.0']) assert.equal(selectVersion({ ...defaults, baseline, tags: ['v1.3.10'] }).version, baseline);
});
test('声明版本已发布后继续自动递增且不重复占用已有 Tag', () => {
    assert.equal(selectVersion({ ...defaults, baseline: '1.4.0', tags: ['v1.4.0'] }).version, '1.4.1');
    assert.equal(selectVersion({ ...defaults, baseline: '1.4.0', tags: ['v1.4.0', 'v1.4.1'] }).version, '1.4.2');
});
test('同一源码已成功发布时不再生成下一补丁版本', () => {
    assert.equal(selectVersion({ ...defaults, baseline: '1.4.0', releases: [managed({ tag_name: 'v1.4.0', draft: false })], previousFingerprint: fingerprint }).needed, false);
});
test('新的明确版本声明即使源码指纹相同也不会被旧版跳过', () => {
    assert.equal(selectVersion({ ...defaults, baseline: '1.4.0', releases: [managed({ tag_name: 'v1.3.10', draft: false })], previousFingerprint: fingerprint }).version, '1.4.0');
});
test('重试草稿不得低于声明的版本，非法基线不会被跳过逻辑掩盖', () => {
    assert.throws(() => selectVersion({ ...defaults, baseline: '1.4.0', releases: [managed()] }), /低于声明版本/);
    assert.throws(() => selectVersion({ ...defaults, baseline: 'v1.4.0', releases: [managed({ draft: false })], previousFingerprint: fingerprint }), /基线/);
});
test('从维护者现有更新说明识别版本，按数值选择且不依赖路径顺序', () => {
    const doc = v => ({ path: `resources/更新说明-${v}.txt`, text: `LiteMonitor ${v} 更新说明\n\n真实更新内容` });
    const intent = releaseIntent('1.3.8', [doc('1.9.0'), doc('1.10.0'), doc('1.4.0')]);
    assert.equal(intent.version, '1.10.0');
    assert.equal(releaseIntent('1.3.8', [doc('1.4.0')]).version, '1.4.0');
    assert.equal(releaseIntent('2.0.0', [doc('1.4.0')]).version, '2.0.0');
    assert.equal(releaseIntent('1.4.0', []).version, '1.4.0');
});
test('更新说明文件名与标题不一致时阻止错误版本发布', () => {
    assert.throws(() => releaseIntent('1.3.8', [{ path: 'resources/更新说明-1.4.0.txt', text: 'LiteMonitor 1.3.10 更新说明' }]), /标题与文件名版本不一致/);
    assert.throws(() => releaseIntent('1.3.8', [{ path: 'resources/更新说明-1.04.0.txt', text: '' }]), /文件名版本无效/);
});
test('明确版本的功能说明加入发布日志，不虚构功能或丢弃已有声明', () => {
    const declared = 'LiteMonitor 1.4.0 更新说明\n\n电源监控、分层透明度、多内存温度';
    const text = notes([], 'ChenBit/LiteMonitor', 'v1.3.10', commit, declared);
    assert.ok(text.includes(declared));
    assert.match(text, /### 本版本更新说明/);
    assert.doesNotMatch(notes([], 'ChenBit/LiteMonitor', 'v1.4.0', commit), /LiteMonitor 1.4.0 更新说明/);
});
test('Tag、正式 Release、草稿共同保留版本，按数值递增', () => {
    assert.equal(selectVersion({ ...defaults, tags: ['v1.9.9', 'v1.10.8', 'preview'], releases: [{ tag_name: 'v1.10.9', draft: true }] }).version, '1.10.10');
});
test('超出补丁范围时向次版本进位', () => assert.equal(selectVersion({ ...defaults, tags: ['v1.3.65534'] }).version, '1.4.0'));
test('同提交中断重试复用草稿版本', () => assert.equal(selectVersion({ ...defaults, releases: [managed()] }).version, '1.3.9'));
test('已发布的相同源码或仅文档差异跳过发布', () => assert.equal(selectVersion({ ...defaults, releases: [managed({ draft: false })], previousFingerprint: fingerprint }).needed, false));
test('草稿与源码、版本不符时失败', () => {
    assert.throws(() => selectVersion({ ...defaults, releases: [managed({ tag_name: 'v1.4.0' })] }), /不一致/);
    assert.throws(() => selectVersion({ ...defaults, releases: [managed()], fingerprint: 'c'.repeat(64) }), /不一致/);
});
test('普通文档不构建，运行资源和包构建脚本需要发布', () => {
    for (const path of ['README.md', 'README.en.md', 'docs/RELEASING.md', 'resources/screenshots/overview.png', 'resources/version.json']) assert.equal(releasePath(path), false, path);
    for (const path of ['src/System/UpdateChecker.cs', 'resources/plugins/Holiday.json', 'resources/assets/driver.zip', 'automation/build.ps1', 'Directory.Build.props', '.github/workflows/delivery.yml', 'resources/更新说明-1.4.0.txt']) assert.equal(releasePath(path), true, path);
    assert.equal(validationPath('test/节假日回退/回归程序.cs'), true);
    assert.equal(releasePath('test/节假日回退/回归程序.cs'), false);
});
test('中文日志按实际提交归类、去重并过滤无关记录', () => {
    const entries = [
        ['feat: 新增插件', ['src/plugin.cs']], ['fix: 修复版本比较', ['src/update.cs']],
        ['fix: 修复版本比较', ['src/update.cs']], ['docs: 更新说明', ['README.md']],
        ['Merge pull request #1', ['src/a.cs']], ['Bump version to 1.3.8', ['LiteMonitor.csproj']]
    ].map(([subject, paths]) => ({ sha: commit, subject, paths }));
    const text = notes(entries, 'ChenBit/LiteMonitor', 'v1.3.8', commit);
    assert.match(text, /### 新增/);
    assert.match(text, /### 修复/);
    assert.equal(text.match(/修复版本比较/g).length, 1);
    assert.doesNotMatch(text, /更新说明|Merge pull|Bump version/);
    assert.match(notes([], 'ChenBit/LiteMonitor', '', commit), /首次自动发布/);
});
test('实际 Git 树指纹忽略文档，但识别源码修改和删除', t => {
    const directory = mkdtempSync(join(tmpdir(), 'litemonitor-fingerprint-'));
    const original = process.cwd();
    const command = (...args) => execFileSync('git', args, { cwd: directory, encoding: 'utf8' }).trim();
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    command('init', '-q');
    command('config', 'user.name', 'Release tests');
    command('config', 'user.email', 'tests@example.invalid');
    const record = () => { command('add', '.'); command('commit', '-qm', 'test'); return command('rev-parse', 'HEAD'); };
    writeFileSync(join(directory, 'LiteMonitor.csproj'), '<Project />');
    writeFileSync(join(directory, 'README.md'), 'first');
    const initial = record();
    try {
        process.chdir(directory);
        const first = sourceFingerprint(initial);
        writeFileSync(join(directory, 'README.md'), 'docs only');
        assert.equal(sourceFingerprint(record()), first);
        writeFileSync(join(directory, 'LiteMonitor.csproj'), '<Project Sdk="Microsoft.NET.Sdk" />');
        const changed = sourceFingerprint(record());
        assert.notEqual(changed, first);
        rmSync(join(directory, 'LiteMonitor.csproj'));
        assert.notEqual(sourceFingerprint(record()), changed);
    } finally { process.chdir(original); }
});

function fixture(t) {
    const directory = mkdtempSync(join(tmpdir(), 'litemonitor-release-'));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    const plan = { repository: 'ChenBit/LiteMonitor', version: '1.3.9', tag: 'v1.3.9', commit, fingerprint };
    const name = 'LiteMonitor-v1.3.9-win-x64.zip';
    const zip = Buffer.from('ZIP fixture for transaction tests');
    writeFileSync(join(directory, name), zip);
    writeFileSync(join(directory, name + '.sha256'), `${hash(zip)}  ${name}\n`);
    writeFileSync(join(directory, 'version.json'), JSON.stringify({ version: plan.version, sha256: hash(zip), downloadUrl: `https://github.com/${plan.repository}/releases/download/${plan.tag}/${name}` }));
    writeFileSync(join(directory, 'notes.md'), '真实变更\n');
    return { directory, plan, name };
}
class MemoryGitHub {
    constructor() { this.release = null; this.assets = []; this.ref = null; this.head = commit; this.events = []; this.failAfter = ''; this.nextId = 10; }
    async releases() { return this.release ? [this.release] : []; }
    async request(path, options = {}) {
        const method = options.method ?? 'GET';
        this.events.push(`${method} ${path}`);
        let result;
        if (path === 'branches/master') return { commit: { sha: this.head } };
        if (path === 'releases' && method === 'POST') result = this.release = { ...options.body, id: 1, upload_url: 'https://uploads.github.com/repos/ChenBit/LiteMonitor/releases/1/assets{?name,label}' };
        else if (path === 'releases/1/assets?per_page=100') return [...this.assets];
        else if (path.startsWith('https://uploads.github.com/')) {
            const asset = { id: this.nextId++, name: new URL(path).searchParams.get('name'), size: options.body.length, digest: 'sha256:' + hash(options.body), state: 'uploaded', data: options.body };
            this.assets.push(asset); result = asset;
        } else if (path.startsWith('git/ref/tags/')) return this.ref;
        else if (path === 'git/refs') result = this.ref = { object: { type: 'commit', sha: options.body.sha } };
        else if (path === 'releases/1' && method === 'PATCH') { Object.assign(this.release, options.body); result = this.release; }
        else if (path === 'releases/1') return { ...this.release };
        else if (path.startsWith('releases/assets/')) {
            const id = Number(path.split('/').at(-1));
            if (method === 'DELETE') { this.assets = this.assets.filter(a => a.id !== id); return null; }
            return this.assets.find(a => a.id === id).data;
        } else throw new Error(`Unmocked ${method} ${path}`);
        if (this.failAfter === `${method} ${path}` || this.failAfter === 'upload' && path.startsWith('https://uploads.github.com/')) {
            this.failAfter = ''; throw new Error('Simulated lost response after side effect');
        }
        return result;
    }
}
test('产物哈希或清单版本错误时在创建草稿前失败', async t => {
    const f = fixture(t), client = new MemoryGitHub();
    writeFileSync(join(f.directory, f.name), 'corrupt');
    assert.throws(() => verifyArtifacts(f.plan, f.directory), /不一致/);
    await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint), /不一致/);
    assert.equal(client.events.length, 0);
});
test('草稿全部附件验证完成后才创建 Tag 并公开，重试不重复发布', async t => {
    const f = fixture(t), client = new MemoryGitHub();
    assert.equal(await publish(f.plan, f.directory, client, () => fingerprint), 'published');
    assert.equal(client.assets.length, 3);
    const upload = client.events.findLastIndex(x => x.startsWith('POST https://uploads'));
    assert.ok(client.events.indexOf('POST git/refs') > upload);
    assert.ok(client.events.indexOf('PATCH releases/1') > upload);
    await publish(f.plan, f.directory, client, () => fingerprint);
    assert.equal(client.events.filter(x => x === 'POST releases').length, 1);
    assert.equal(client.events.filter(x => x === 'PATCH releases/1').length, 1);
    assert.equal(client.assets.length, 3);
});
for (const failure of ['POST releases', 'upload', 'POST git/refs', 'PATCH releases/1']) {
    test(`网络中断后恢复同一事务：${failure}`, async t => {
        const f = fixture(t), client = new MemoryGitHub();
        client.failAfter = failure;
        await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint), /lost response/);
        assert.equal(await publish(f.plan, f.directory, client, () => fingerprint), 'published');
        assert.equal(client.assets.length, 3);
        assert.equal(client.events.filter(x => x === 'POST releases').length, 1);
    });
}
test('修复草稿中的残缺附件，正式发布后拒绝覆盖', async t => {
    const f = fixture(t), client = new MemoryGitHub();
    client.failAfter = 'upload';
    await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint));
    client.assets[0].digest = 'sha256:' + '0'.repeat(64);
    await publish(f.plan, f.directory, client, () => fingerprint);
    assert.ok(client.events.some(x => x.startsWith('DELETE releases/assets/')));
    client.assets[0].digest = 'sha256:' + '0'.repeat(64);
    await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint), /正式 Release/);
});
test('拒绝版本或 Tag 冲突和源码不匹配', async t => {
    const f = fixture(t), client = new MemoryGitHub();
    await assert.rejects(publish(f.plan, f.directory, client, () => 'wrong'), /源码/);
    client.ref = { object: { type: 'commit', sha: 'c'.repeat(40) } };
    await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint), /Tag/);
    assert.equal(client.release.draft, true);
    client.release.body = 'manual release';
    await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint), /占用/);
});
test('master 前进时不创建过期版本', async t => {
    const f = fixture(t), client = new MemoryGitHub();
    client.head = 'c'.repeat(40);
    assert.equal(await publish(f.plan, f.directory, client, () => fingerprint), 'stale');
    assert.equal(client.release, null);
});
test('标题不符的已有草稿不会被公开', async t => {
    const f = fixture(t), client = new MemoryGitHub();
    client.failAfter = 'POST releases';
    await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint));
    client.release.name = 'wrong version';
    await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint), /标题/);
    assert.equal(client.release.draft, true);
    assert.equal(client.assets.length, 0);
});
test('草稿附件没有服务端 digest 时下载并验证内容', async t => {
    const f = fixture(t), client = new MemoryGitHub();
    client.failAfter = 'upload';
    await assert.rejects(publish(f.plan, f.directory, client, () => fingerprint));
    delete client.assets[0].digest;
    await publish(f.plan, f.directory, client, () => fingerprint);
    assert.ok(client.events.some(x => x.startsWith('GET releases/assets/')));
});
test('API 权限错误明确失败，不当作没有历史版本', async () => {
    const client = new GitHub('ChenBit/LiteMonitor', '', async () => new Response('{"message":"denied"}', { status: 403 }));
    await assert.rejects(client.releases(), /HTTP 403/);
});
