namespace LiteMonitor.src.WebServer
{
    public static class WebPageContent
    {
        public const string IndexHtml = @"
<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    {{FAVICON}}
    <title>LiteMonitor WebServer</title>
    <style>
        :root {
            --bg: #09090b;
            --card: #141417;
            --border: #27272a;
            --text-main: #f4f4f5;
            --text-sub: #71717a;
            
            /* 基础三色 */
            --c-0: #10b981; /* Green */
            --c-1: #f59e0b; /* Orange */
            --c-2: #ef4444; /* Red */
        }

        body { 
            margin: 0; padding: 20px; 
            background: var(--bg); color: var(--text-main); 
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
        }

        /* --- Header --- */
        .header {
            max-width: 1200px; margin: 0 auto 30px;
            display: flex; justify-content: space-between; align-items: flex-end;
            padding-bottom: 20px; border-bottom: 1px solid var(--border);
            gap: 20px;
        }
        .brand { 
            font-size: 1.6rem; font-weight: 800; letter-spacing: 1px; 
            white-space: nowrap; flex-shrink: 0; line-height: 1;
        }
        .brand span { color: var(--c-0); }
        
        .sys-info { 
            font-family: 'Consolas', monospace; color: var(--text-sub); 
            font-size: 0.9rem; 
            display: flex; gap: 10px; flex-wrap: wrap; justify-content: flex-end;
        }
        .tag { 
            background: #1f1f22; 
            padding: 5px 12px; 
            border-radius: 6px; 
            border: 1px solid #333;
            display: flex; align-items: center; 
            white-space: nowrap; 
        }
        .tag b { color: var(--text-main); margin-right: 6px; opacity: 0.5; font-weight: normal; }

        /* --- Grid --- */
        .dashboard {
            max-width: 1200px; margin: 0 auto;
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(min(450px, 100%), 1fr));
            gap: 24px;
        }

        /* --- Card --- */
        .card {
            background: var(--card); 
            border: 1px solid var(--border); /* 始终保持基础边框，不再变色 */
            border-radius: 16px; padding: 24px;
            display: flex; flex-direction: column; gap: 20px;
            position: relative; overflow: hidden;
            box-shadow: 0 4px 20px rgba(0,0,0,0.2);
            /* 移除 transition border-color，因为不再改边框 */
        }
        
        /* 顶部彩色条：指示颜色的主要元素 */
        .card::before {
            content: ''; position: absolute; top: 0; left: 0; right: 0; height: 3px;
            background: var(--card-color, var(--border));
            box-shadow: 0 0 15px var(--card-color, transparent);
            opacity: 0.8;
            transition: background 0.3s;
        }
        .card-head { 
            font-size: 1.1rem; font-weight: 700; color: var(--text-sub); 
            text-transform: uppercase; letter-spacing: 1px;
        }

        /* --- Layout Standard (CPU/GPU) --- */
        .layout-std { display: flex; gap: 30px; align-items: center; }
        
        .ring-wrap { 
            position: relative; width: 130px; height: 130px; flex-shrink: 0; 
        }
        .ring-container {
            display: flex; flex-direction: column; align-items: center;
            flex-shrink: 0;
        }
        .ring-svg { transform: rotate(-90deg); width: 100%; height: 100%; }
        .ring-bg { fill: none; stroke: #27272a; stroke-width: 3.5; }
        
        .ring-val { 
            fill: none; stroke: var(--item-color, var(--c-0)); 
            stroke-width: 3.5; stroke-linecap: round; 
            transition: stroke-dasharray 0.6s ease; 
        }
        .ring-data { 
            position: absolute; inset: 0; 
            display: flex; flex-direction: column; justify-content: center; align-items: center; 
        }
        .rd-name { 
            font-size: 0.9rem; color: var(--text-sub); margin-top: 8px; 
            font-weight: 600; text-transform: uppercase; letter-spacing: 1px;
            text-align: center;
        }
        .rd-val { font-size: 2.2rem; font-weight: 800; line-height: 1; margin-top: 10px;}
        .rd-unit { font-size: 1rem; color: var(--text-sub); margin-top: 2px; }

        .detail-list { flex: 1; display: flex; flex-direction: column; justify-content: center; gap: 10px; }
        .d-row { display: flex; flex-direction: column; gap: 4px; }
        
        .d-info { display: flex; justify-content: space-between; align-items: baseline; }
        .d-lbl { font-size: 1rem; color: var(--text-sub); }
        
        .d-val-box { 
            font-family: 'Consolas', monospace; 
            font-weight: 700; 
            font-size: 1.3rem; 
            color: var(--item-color, #fff);
            white-space: nowrap; /* 增加这一行 */
        }
        .d-unit { font-size: 1rem; color: var(--text-sub); font-weight: normal; margin-left: 2px; }
        
        .d-bar-bg { height: 6px; background: #27272a; border-radius: 3px; overflow: hidden; width: 100%; }
        .d-bar-fill { height: 100%; width: 0%; border-radius: 3px; transition: width 0.4s; background: var(--item-color, var(--c-0)); }

        /* --- Layout Big (Net/Disk) --- */
        .layout-big { display: flex; text-align: center; align-items: center; height: 100%; }
        .big-item { flex: 1; display: flex; flex-direction: column; gap: 5px; position: relative; }
        .big-item:first-child::after {
            content: ''; position: absolute; right: 0; top: 10%; bottom: 10%; width: 1px; background: var(--border);
        }
        .big-lbl { font-size: 1rem; color: var(--text-sub); font-weight: 600; text-transform: uppercase; }
        .big-val { font-size: 2.6rem; font-weight: 900; line-height: 1.1; font-family: 'Consolas', monospace; color: var(--item-color, #fff); }
        .big-unit { font-size: 1.1rem; color: var(--item-color, var(--text-sub)); font-weight: 700; opacity: 0.8; }

        /* --- Layout Dash (Info) --- */
        .full-width { grid-column: 1 / -1; }
        .layout-dash { 
            display: grid; 
            /* PC端优化：增加最小宽度到 200px，防止内容挤压换行 */
            grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
            gap: 16px; 
            align-items: stretch;
        }
        .dash-item {
            display: flex; flex-direction: column; 
            background: rgba(255,255,255,0.03); 
            padding: 12px 15px; border-radius: 10px;
            /* 移除固定宽度，交给 Grid 控制 */
            border: 1px solid var(--border);
            overflow: hidden;
        }
        .dash-lbl { font-size: 0.85rem; color: var(--text-sub); margin-bottom: 6px; text-transform: uppercase; letter-spacing: 0.5px; }
        .dash-val { 
            /* 字体大小自适应：最小 0.9rem，最大 1.2rem */
            font-size: clamp(0.9rem, 3.5vw, 1.2rem); 
            color: var(--text-main); font-weight: 600; 
            /* ★★★ 修复：DASH 值包含中文，使用微软雅黑优化显示 ★★★ */
            font-family: 'Microsoft YaHei UI', 'Microsoft YaHei', 'Segoe UI', Roboto, sans-serif;
            /* ★★★ 修复：防止数字/单词被强制截断，允许在必要时换行 ★★★ */
            white-space: pre-wrap; 
            word-break: break-word; /* 单词完整时不拆分 */
            overflow-wrap: anywhere; /* 只有长字符串（如IP）放不下才强制拆分 */
            line-height: 1.2;
        }
        /* ★★★ 修复：DASH 监控项变色 (同步主界面) ★★★ */
         .dash-val.is-0 { color: var(--c-0); } /* Normal (Green) */
         .dash-val.is-1 { color: var(--c-1); }
         .dash-val.is-2 { color: var(--c-2); }
         
         /* --- Color Definitions --- */

        /* --- Color Definitions --- */
        /* Card Status (Top Bar Color) */
        .cs-0 { --card-color: var(--c-0); }
        .cs-1 { --card-color: var(--c-1); }
        /* 【修复】红色告警使用呼吸灯光效，而不是改边框 */
        .cs-2 { --card-color: var(--c-2); animation: glow-pulse 2s infinite; }
        
        /* Item Status (Values & Bars) */
        .is-0 { --item-color: var(--c-0); }
        .is-1 { --item-color: var(--c-1); }
        .is-2 { --item-color: var(--c-2); }

        /* 【核心修复】呼吸光晕动画：只改变 box-shadow，不改变 border-color */
        @keyframes glow-pulse {
            0% { box-shadow: 0 4px 20px rgba(0,0,0,0.2); }
            50% { box-shadow: 0 0 30px rgba(239, 68, 68, 0.25); } /* 红色柔和光晕 */
            100% { box-shadow: 0 4px 20px rgba(0,0,0,0.2); }
        }

        @media (max-width: 600px) {
            .header { flex-direction: column; align-items: flex-start; gap: 10px; }
            .sys-info { justify-content: flex-start; width: 100%; font-size: 0.8rem; }
            .dashboard { grid-template-columns: 1fr; padding: 10px; }
            
            /* 强制保持左右布局，并缩小圆环比例 */
            .layout-std { flex-direction: row; gap: 15px; align-items: center; }
            .ring-wrap { width: 100px; height: 100px; } /* 手机端缩小圆环 */
            .rd-val { font-size: 1.5rem; }
            .rd-name { font-size: 0.9rem; margin-top: 5px; }
            .ring-container { flex-shrink: 0; }

            /* ★★★ DASH 手机端优化：强制双列 ★★★ */
            .layout-dash {
                /* 强制一行两列，平分宽度，确保至少显示两个 */
                grid-template-columns: repeat(2, 1fr);
                gap: 8px;
            }
            .dash-item {
                padding: 8px 10px; /* 减小内边距 */
            }
            .dash-lbl { font-size: 0.75rem; } /* 稍微缩小标签字体 */
        }
    </style>
</head>
<body>

    <div class='header'>
        <div class='brand'><span>⚡</span>Lite<span>Monitor</span></div>
        <div class='sys-info'>
            <div class='tag'><b>HOST</b> <span id='sys-host'>--</span></div>
            <div class='tag'><b>IP</b> <span id='sys-ip'>--</span></div>
            <div class='tag'><b>RUNTIME</b> <span id='sys-uptime'>--</span></div>
             <div class='tag'>
                <div id='status-dot' style='width:8px; height:8px; border-radius:50%; background:var(--text-sub); margin-right:6px;'></div>
                <span id='status-text' style='font-weight:700; font-size:0.8rem;'>--</span>
            </div>
        </div>
    </div>

    <div class='dashboard' id='board'></div>

    <script>
        const board = document.getElementById('board');
        const cards = {};

        const statusDot = document.getElementById('status-dot');
        const statusText = document.getElementById('status-text');
        let ws = null;
        let reconnectTimer = null;

        function connect() {
            if (ws) return;
            const protocol = location.protocol === 'https:' ? 'wss://' : 'ws://';
            ws = new WebSocket(protocol + location.host);

            ws.onopen = () => {
                statusDot.style.background = 'var(--c-0)';
                statusText.innerText = 'LIVE';
                if (reconnectTimer) { clearInterval(reconnectTimer); reconnectTimer = null; }
            };

            ws.onmessage = (event) => {
                try {
                    const d = JSON.parse(event.data);
                    if (d.sys) {
                        document.getElementById('sys-host').innerText = d.sys.host;
                        document.getElementById('sys-ip').innerText = `${d.sys.ip}:${d.sys.port}`;
                        document.getElementById('sys-uptime').innerText = d.sys.uptime;
                    }
                    if (d.items) render(d.items);
                } catch(e) { console.error(e); }
            };

            ws.onclose = () => {
                statusDot.style.background = 'var(--c-2)';
                statusText.innerText = 'OFFLINE';
                ws = null;
                if (!reconnectTimer) reconnectTimer = setInterval(connect, 2000);
            };

            ws.onerror = () => ws && ws.close();
        }

        function render(items) {
            const groups = {}; 
            const orderList = [];

            items.forEach(i => {
                const gid = i.gid || 'OTHER';
                if (!groups[gid]) {
                    groups[gid] = { name: i.gn, core: null, subs: [], maxSts: 0 };
                    orderList.push(gid);
                }

                if (i.primary && i.sts > groups[gid].maxSts) groups[gid].maxSts = i.sts;

                const isBigMode = ['NET', 'DISK', 'DATA'].includes(gid);
                const isLoad = i.k.includes('Load') || (i.u === '%' && !i.k.includes('Fan'));
                
                if (!isBigMode && isLoad && !groups[gid].core) {
                    groups[gid].core = i;
                } else {
                    groups[gid].subs.push(i);
                }
            });

            // ★★★ 核心修复：确保 DASH 组始终在最上方 ★★★
            const dashIdx = orderList.indexOf('DASH');
            if (dashIdx > -1) {
                orderList.splice(dashIdx, 1);
                orderList.unshift('DASH');
            }

            orderList.forEach(gid => {
                const grp = groups[gid];
                const isBig = ['NET', 'DISK', 'DATA'].includes(gid);
                const isDash = gid === 'DASH'; // 识别 DASH 组

                if (!cards[gid]) {
                    const div = document.createElement('div');
                    
                    let content = '';
                    if (isDash) {
                        // DASH 布局
                        content = `<div class='layout-dash' id='dash-${gid}'></div>`;
                    } else if (isBig) {
                        content = `<div class='layout-big' id='big-${gid}'></div>`;
                    } else {
                        let ringHtml = '';
                        if (grp.core) {
                            ringHtml = `
                            <div class='ring-container'>
                                <div class='ring-wrap' id='rw-${gid}'>
                                    <svg class='ring-svg' viewBox='0 0 36 36'>
                                        <path class='ring-bg' d='M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831' />
                                        <path class='ring-val' id='rp-${gid}' stroke-dasharray='0, 100' d='M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831' />
                                    </svg>
                                    <div class='ring-data'>
                                        <div class='rd-val' id='rv-${gid}'>0</div>
                                        <div class='rd-unit' id='ru-${gid}'>%</div>
                                    </div>
                                </div>
                                <div class='rd-name' id='rn-${gid}'>--</div>
                            </div>`;
                        }
                        content = `<div class='layout-std'>${ringHtml}<div class='detail-list' id='list-${gid}'></div></div>`;
                    }

                    div.innerHTML = `<div class='card-head'>${grp.name}</div>${content}`;
                    board.appendChild(div);
                    
                    // ★★★ DASH 全宽显示 ★★★
                    if (isDash) div.className = 'card full-width';

                    cards[gid] = { 
                        el: div, isBig, isDash,
                        cont: isDash ? div.querySelector(`#dash-${gid}`) : (isBig ? div.querySelector(`#big-${gid}`) : div.querySelector(`#list-${gid}`)),
                        rows: {},
                        core: grp.core ? { 
                            wrap: div.querySelector(`#rw-${gid}`),
                            p: div.querySelector(`#rp-${gid}`), 
                            v: div.querySelector(`#rv-${gid}`), 
                            u: div.querySelector(`#ru-${gid}`),
                            n: div.querySelector(`#rn-${gid}`)
                        } : null
                    };
                }

                // 更新样式
                if (!isDash) cards[gid].el.className = `card cs-${grp.maxSts}`;
                
                const cObj = cards[gid];

                if (isDash) {
                     grp.subs.forEach(item => {
                        let r = cObj.rows[item.k];
                        if (!r) {
                            const el = document.createElement('div');
                            el.className = 'dash-item';
                            el.innerHTML = `
                                <div class='dash-lbl'>${item.n}</div>
                                <div class='dash-val'>--</div>
                            `;
                            cObj.cont.appendChild(el);
                            cObj.rows[item.k] = { 
                                el, 
                                v: el.querySelector('.dash-val')
                            };
                            r = cObj.rows[item.k];
                        }
                        // 更新数值
                        let valStr = item.v;
                        if (item.u && item.u !== '') valStr += ' ' + item.u;
                        if (r.v.innerText !== valStr) r.v.innerText = valStr;
                        
                        // ★★★ 修复：应用颜色状态 (is-0, is-1, is-2) ★★★
                        // 先移除旧的状态类
                        r.v.classList.remove('is-0', 'is-1', 'is-2');
                        // 添加新的状态类 (如果 sts >= 0)
                        if (item.sts >= 0) r.v.classList.add(`is-${item.sts}`);

                     });
                } else if (isBig) {
                    grp.subs.forEach((item, idx) => {
                        if (idx > 1) return; 
                        let r = cObj.rows[item.k];
                        if (!r) {
                            const el = document.createElement('div');
                            el.innerHTML = `
                                <div class='big-lbl'>${item.n}</div>
                                <div class='big-val'>--</div>
                                <div class='big-unit'>--</div>
                            `;
                            cObj.cont.appendChild(el);
                            cObj.rows[item.k] = { 
                                el, 
                                v: el.querySelector('.big-val'), 
                                u: el.querySelector('.big-unit') 
                            };
                            r = cObj.rows[item.k];
                        }
                        r.el.className = `big-item is-${item.sts}`;
                        if (r.v.innerText !== item.v) r.v.innerText = item.v;
                        if (r.u.innerText !== item.u) r.u.innerText = item.u;
                    });
                } else {
                    if (grp.core && cObj.core) {
                        cObj.core.wrap.className = `ring-wrap is-${grp.core.sts}`;
                        cObj.core.v.innerText = grp.core.v;
                        cObj.core.u.innerText = grp.core.u;
                        cObj.core.n.innerText = grp.core.n;
                        let pct = Math.min(Math.max(grp.core.pct, 0), 100);
                        cObj.core.p.setAttribute('stroke-dasharray', `${pct}, 100`);
                    }

                    grp.subs.forEach(item => {
                        let r = cObj.rows[item.k];
                        if (!r) {
                            const el = document.createElement('div');
                            el.innerHTML = `
                                <div class='d-info'>
                                    <span class='d-lbl'>${item.n}</span>
                                    <div><span class='d-val-box'>--</span><span class='d-unit'></span></div>
                                </div>
                                <div class='d-bar-bg'><div class='d-bar-fill'></div></div>
                            `;
                            cObj.cont.appendChild(el);
                            cObj.rows[item.k] = { 
                                el,
                                v: el.querySelector('.d-val-box'), 
                                u: el.querySelector('.d-unit'),
                                b: el.querySelector('.d-bar-fill')
                            };
                            r = cObj.rows[item.k];
                        }
                        r.el.className = `d-row is-${item.sts}`;
                        if (r.v.innerText !== item.v) r.v.innerText = item.v;
                        if (r.u.innerText !== item.u) r.u.innerText = item.u;
                        r.b.style.width = Math.min(Math.max(item.pct, 0), 100) + '%';
                    });
                }
            });
        }

        connect();
    </script>
</body>
</html>";
    }
}