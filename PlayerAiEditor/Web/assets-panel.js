
/* ============================================================ 资源库 / Laya 配置（P6 / P3 第二步）
 *
 * 这两块面板都是"**转发**"而不是"自己算"：
 *   · 资源库的三态（磁盘版 / 内存版 / 缓存版）与"哪一版更新"全由游戏判定（`ai.asset.*`），
 *     编辑器只显示与转发 —— 否则界面说的和游戏实际用的会是两套判据；
 *   · Laya 配置就是一个文件（`<实例根>/PlayerAi/Laya.local.json`），编辑器直接读写信它，
 *     密钥**只写不回显**（留空＝不改，`-`＝清除）。
 *
 * 为什么单独一个 IIFE：不碰 app.js 内部状态（选中节点/撤销栈/画布），
 * 这样将来重构主界面不会把这两块一起带崩。
 *
 * 文案一律走 `L()/F()`（中文兜底 + i18n.js 的 STRINGS.en）：真浏览器自检里有一条
 * "切到 English 后整页不能残留中文"，硬编码中文会被它当场抓住（实测踩过）。
 */
(function () {
  'use strict';

  var state = { assets: null, laya: null };

  /* 与 app.js 同一套取文案的方式：`t` 取一条、`format` 带参数。
     兜底中文写在这里，英文在 i18n.js 的 STRINGS.en 里 —— 于是切英文时这两块面板也跟着切。 */
  var I18n = window.PlayerAiI18n || {
    t: function (key, fallback) { return fallback !== undefined ? fallback : key; },
    format: function (key, fallback) { return fallback !== undefined ? fallback : key; }
  };

  function L(key, fallback) { return I18n.t(key, fallback); }

  function F(key, fallback) {
    var args = Array.prototype.slice.call(arguments, 2);
    return I18n.format.apply(I18n, [key, fallback].concat(args));
  }

  function $(id) { return document.getElementById(id); }

  function api(path, options) {
    return fetch(path, options).then(function (response) {
      return response.json().catch(function () { return null; });
    });
  }

  function post(path, body) {
    return api(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    });
  }

  function status(text) {
    var node = $('statusText');
    if (node) node.textContent = text;
  }

  function badge(id, text, cls) {
    var node = $(id);
    if (!node) return;
    node.textContent = text;
    node.className = 'badge' + (cls ? ' ' + cls : '');
  }

  function hint(text) {
    var node = document.createElement('p');
    node.className = 'hint';
    node.textContent = text;
    return node;
  }

  function short(hash) {
    if (!hash) return '-';
    var text = String(hash);
    return text.length > 12 ? text.slice(0, 12) : text;
  }

  // ---------------------------------------------------------------- 资源库

  function refreshAssets() {
    var box = $('assetBox');
    if (box) {
      box.innerHTML = '';
      box.appendChild(hint(L('assets.loading', '读取中…')));
    }
    return api('/api/assets').then(function (data) {
      if (!data) {
        if (box) {
          box.innerHTML = '';
          box.appendChild(hint(L('assets.noEditor', '编辑器没有回应。')));
        }
        return null;
      }
      state.assets = data;
      renderAssets(data);
      return data;
    });
  }

  function renderAssets(data) {
    var box = $('assetBox');
    if (!box) return;

    if (!data.ok) {
      badge('assetBadge', L('assets.badge.offline', '资源库：游戏未连上'), 'dirty');
      box.innerHTML = '';
      // ⚠️ **不把 `data.reason` 直接写进 DOM**：它常常是**操作系统本地化过的**socket 文案
      //    （中文 Windows 上是"由于目标计算机积极拒绝…"）。那串字既不是我们的界面文案、
      //    也没法翻译，写进页面就等于"切英文之后还留着中文"（真浏览器自检会当场抓住）。
      //    原始原因留在控制台与 `/api/assets` 的 JSON 里，界面只给一句稳定的、可翻译的话。
      if (data.reason && window.console) console.warn('[assets] ' + data.reason);
      box.appendChild(hint(L('assets.offline',
        '拿不到资源库现状：游戏没在跑（起来之后再刷新）。')));
      return;
    }

    var records = data.records || [];
    var conflicts = (data.conflict && data.conflict.conflicts) || [];
    var dirty = 0;
    for (var i = 0; i < records.length; i++) if (records[i].dirty) dirty++;

    badge('assetBadge', conflicts.length
      ? F('assets.badge.conflicts', '资源库：{0} 份 / 未保存 {1} / 冲突 {2}',
          records.length, dirty, conflicts.length)
      : F('assets.badge', '资源库：{0} 份 / 未保存 {1}', records.length, dirty),
      dirty || conflicts.length ? 'dirty' : 'live');

    box.innerHTML = '';

    if (conflicts.length) {
      box.appendChild(hint(F('assets.conflict.head',
        '⚠ 有 {0} 条重载冲突（磁盘版与会话里的内存版都改过）——游戏默认保留内存，等你选：',
        conflicts.length)));
      for (var c = 0; c < conflicts.length; c++) {
        box.appendChild(conflictRow(conflicts[c]));
      }
    }

    var list = document.createElement('div');
    list.className = 'assetList';
    if (!records.length) {
      list.appendChild(hint(L('assets.empty',
        '内存库里还是空的：游戏跑起来、有活动树之后才会登记。')));
    }
    for (var r = 0; r < records.length; r++) list.appendChild(assetRow(records[r], data));
    box.appendChild(list);

    // 重取现状（§4.12）：谁在等退避、谁已经熔断
    var retry = data.retry;
    if (retry && retry.policy) {
      var policy = retry.policy;
      box.appendChild(hint(F('assets.retry.policy',
        '自动重取：{0}（最多 {1} 次，退避 {2}ms ×{3}，上限 {4}ms；窗口 {5}s 内超过 {6} 次就熔断）',
        policy.enabled ? L('assets.retry.on', '开') : L('assets.retry.off', '关'),
        policy.maxAttempts, policy.baseDelayMs, policy.multiplier, policy.maxDelayMs,
        policy.windowSeconds, policy.windowLimit)));

      var resources = retry.resources || [];
      for (var k = 0; k < resources.length; k++) {
        var item = resources[k];
        if (!item.pending && !item.exhausted) continue;
        box.appendChild(hint(item.exhausted
          ? F('assets.retry.exhausted', '⛔ 已熔断 {0}（{1}，第 {2}/{3} 次）',
              item.resource, item.kind, item.attempts, item.maxAttempts)
          : F('assets.retry.pending', '⏳ 等重取 {0}（{1}，第 {2}/{3} 次，还有 {4}ms）',
              item.resource, item.kind, item.attempts, item.maxAttempts, item.nextRetryMs)));
        if (item.exhausted) {
          box.appendChild(rowButton(L('assets.retry.reset', '解除熔断'),
            L('assets.retry.reset.title',
              '人处置过了（例如编辑器保存了新版本）——重取计数清零，下次失败重新开始'),
            function (event) {
              assetAction('retry', { reset: event.currentTarget.getAttribute('data-resource') });
            }, item.resource));
        }
      }
    }
  }

  function assetRow(record, data) {
    var row = document.createElement('div');
    row.className = 'assetRow' + (record.active ? ' active' : '')
      + (record.dirty ? ' dirty' : '');

    var name = document.createElement('div');
    name.className = 'name';
    name.textContent = (record.active ? '▶ ' : '') + record.name;
    row.appendChild(name);

    var stateLine = document.createElement('div');
    stateLine.className = 'state';
    stateLine.innerHTML = L('assets.row.disk', '磁盘') + ' <code>' + short(record.diskHash)
      + '</code> · ' + L('assets.row.memory', '内存') + ' <code>' + short(record.memoryHash)
      + '</code> ' + (record.dirty
        ? '<b>' + L('assets.row.dirty', '未保存') + '</b>'
        : L('assets.row.same', '一致'))
      + ' · ' + F('assets.row.generation', '代次 {0}', record.generation | 0)
      + ' · ' + F('assets.row.origin', '来源 {0}', record.origin || '-');
    row.appendChild(stateLine);

    var cacheLine = document.createElement('div');
    cacheLine.className = 'why';
    cacheLine.textContent = record.cacheFromCache
      ? F('assets.row.cacheWithBytes', '缓存：{0}（{1} 字节） — {2}',
          record.cacheChoice || 'none', record.cachePayloadBytes | 0,
          record.cacheExplanation || L('assets.row.noCache', '没有缓存'))
      : F('assets.row.cache', '缓存：{0} — {1}', record.cacheChoice || 'none',
          record.cacheExplanation || L('assets.row.noCache', '没有缓存'));
    row.appendChild(cacheLine);

    var actions = document.createElement('div');
    actions.className = 'actions';
    actions.appendChild(rowButton(L('assets.save', '保存 → Saves'),
      L('assets.save.title', '写成正式包（默认落 PlayerAi/Saves，不动来源包）'),
      function () { assetAction('save', { name: record.name }); }));
    actions.appendChild(rowButton(L('assets.overwrite', '覆盖来源包…'),
      L('assets.overwrite.title', '把内存版写回包目录（会先备份上一代）'),
      function () {
        if (!window.confirm(F('assets.overwrite.confirm',
          '用内存版覆盖「{0}」的包？旧的一代会被备份成 .vN.bak。', record.name))) return;
        assetAction('save', { name: record.name, dir: data.packageFolder, overwrite: true });
      }));
    actions.appendChild(rowButton(L('assets.cache', '写缓存'),
      L('assets.cache.title', '立刻把内存版写进 .autosave（影子副本）'),
      function () { assetAction('autosave', { name: record.name }); }));
    actions.appendChild(rowButton(L('assets.drop', '丢弃改动'),
      L('assets.drop.title', '丢掉内存里的改动，回到最近一次载入的磁盘版'),
      function () {
        if (!record.dirty) {
          status(F('assets.drop.clean', '「{0}」没有未保存的改动', record.name));
          return;
        }
        if (!window.confirm(F('assets.drop.confirm',
          '丢弃「{0}」内存里的改动，回到磁盘版？', record.name))) return;
        assetAction('drop', { name: record.name });
      }));
    actions.appendChild(rowButton(L('assets.restoreRow', '从缓存恢复'),
      L('assets.restoreRow.title', '按恢复决议把缓存/磁盘版装回内存库（不写盘）'),
      function () { assetAction('restore', { name: record.name }); }));
    row.appendChild(actions);
    return row;
  }

  function conflictRow(conflict) {
    var row = document.createElement('div');
    row.className = 'assetRow conflict';

    var name = document.createElement('div');
    name.className = 'name';
    name.textContent = F('assets.conflict.row', '冲突：{0}', conflict.file || conflict.path);
    row.appendChild(name);

    var stateLine = document.createElement('div');
    stateLine.className = 'state';
    stateLine.innerHTML = L('assets.row.disk', '磁盘') + ' <code>' + short(conflict.diskHash)
      + '</code> · ' + L('assets.row.memory', '内存') + ' <code>' + short(conflict.memoryHash)
      + '</code> · ' + L('assets.conflict.notify', '通知') + ' <code>'
      + short(conflict.notifiedHash) + '</code>';
    row.appendChild(stateLine);

    var why = document.createElement('div');
    why.className = 'why';
    why.textContent = conflict.reason || '';
    row.appendChild(why);

    var actions = document.createElement('div');
    actions.className = 'actions';
    actions.appendChild(rowButton(L('assets.conflict.disk', '用磁盘覆盖'),
      L('assets.conflict.disk.title', '丢掉内存里的改动，装载磁盘版'),
      function () {
        if (!window.confirm(L('assets.conflict.disk.confirm',
          '用磁盘版覆盖内存里的改动？未保存的内容会丢。'))) return;
        assetAction('conflict', { path: conflict.path, mode: 'disk' });
      }));
    actions.appendChild(rowButton(L('assets.conflict.keep', '保留内存'),
      L('assets.conflict.keep.title', '把这条通知丢掉，内存继续跑（磁盘那份不要了）'),
      function () { assetAction('conflict', { path: conflict.path, mode: 'keep' }); }));
    actions.appendChild(rowButton(L('assets.conflict.saveAs', '另存为新包…'),
      L('assets.conflict.saveAs.title', '把内存版写成包目录里的一个新包，两个都留下'),
      function () {
        var name = window.prompt(L('assets.conflict.saveAs.prompt', '新包的名字（不含扩展名）'),
          basename(conflict.path) + '_rescued');
        if (!name) return;
        assetAction('conflict', { path: conflict.path, mode: 'saveAs', newName: name });
      }));
    row.appendChild(actions);
    return row;
  }

  function basename(path) {
    var text = String(path || '');
    var cut = Math.max(text.lastIndexOf('/'), text.lastIndexOf('\\'));
    var name = cut >= 0 ? text.slice(cut + 1) : text;
    return name.replace(/\.[^.]+$/, '');
  }

  function rowButton(text, title, handler, dataResource) {
    var button = document.createElement('button');
    button.textContent = text;
    if (title) button.title = title;
    if (dataResource) button.setAttribute('data-resource', dataResource);
    button.addEventListener('click', handler);
    return button;
  }

  function assetAction(action, args) {
    status(F('assets.acting', '资源库：{0} …', action));
    return post('/api/asset', Object.assign({ action: action }, args || {})).then(function (data) {
      if (!data) {
        status(L('assets.noEditor', '编辑器没有回应。'));
        return null;
      }
      if (data.ok === false) {
        if (data.reason && window.console) console.warn('[assets] ' + action + ': ' + data.reason);
        status(F('assets.failed', '资源库：{0} 失败（{1}）', action, data.code || 'error'));
      } else {
        status(F('assets.done', '资源库：{0} 完成', action));
      }
      return refreshAssets();
    });
  }

  // ---------------------------------------------------------------- Laya 配置

  function loadLaya() {
    return api('/api/laya/config').then(function (data) {
      if (!data) return null;
      state.laya = data;
      fillLaya(data);
      return data;
    });
  }

  function fillLaya(data) {
    if ($('layaBaseUrl')) $('layaBaseUrl').value = data.baseUrl || '';
    if ($('layaModel')) $('layaModel').value = data.model || '';
    if ($('layaTimeout')) $('layaTimeout').value = data.timeoutMs || '';
    if ($('layaRefresh')) $('layaRefresh').value = data.refreshMs || '';
    if ($('layaBudget')) $('layaBudget').value = data.digestBudgetChars || '';
    if ($('layaEnabled')) $('layaEnabled').checked = data.enabled !== false;
    if ($('layaApiKey')) $('layaApiKey').value = '';

    var key = data.keySource === 'none'
      ? L('laya.key.none', '未配置密钥')
      : F('laya.key', '密钥 {0} {1}', data.keySource, data.keyMasked || '');

    badge('layaBadge', F('laya.badge', 'Laya：{0} · {1} · {2}',
      data.enabled === false ? L('laya.off', '已关闭') : L('laya.on', '已启用'),
      key,
      data.reachable ? L('laya.reachable', '端点可达') : L('laya.unreachable', '端点不可达')),
      data.reachable ? 'live' : 'dirty');

    var box = $('layaBox');
    if (!box) return;
    box.innerHTML = '';
    box.appendChild(hint(F('laya.file', '配置文件：{0}', data.configFile || '-')
      + (data.fileExists ? '' : L('laya.file.missing', '（还没有这个文件，保存一次就会创建）'))
      + (data.probeError ? F('laya.probeError', ' ｜ 探测：{0}', data.probeError) : '')));
  }

  function saveLaya() {
    var payload = {
      baseURL: $('layaBaseUrl') ? $('layaBaseUrl').value : '',
      model: $('layaModel') ? $('layaModel').value : '',
      timeoutMs: num('layaTimeout'),
      refreshMs: num('layaRefresh'),
      digestBudgetChars: num('layaBudget'),
      enabled: $('layaEnabled') ? $('layaEnabled').checked : true
    };

    // 密钥：留空＝不改（不会因为改个超时把密钥抹掉）；填 `-`＝清除
    var key = $('layaApiKey') ? $('layaApiKey').value : '';
    if (key === '-') payload.apiKey = '';
    else if (key) payload.apiKey = key;

    status(L('laya.saving', 'Laya：保存配置 …'));
    return post('/api/laya/config', payload).then(function (data) {
      if (!data) return null;
      fillLaya(data);
      status(data.saved
        ? F('laya.saved', 'Laya：配置已写入 {0}', data.path || '')
        : L('laya.saveFailed', 'Laya：保存失败'));
      return data;
    });
  }

  function num(id) {
    var node = $(id);
    if (!node) return undefined;
    var value = parseInt(node.value, 10);
    return isNaN(value) ? undefined : value;
  }

  // ---------------------------------------------------------------- 挂载

  function bind(id, handler) {
    var node = $(id);
    if (node) node.addEventListener('click', handler);
  }

  /**
   * **语言切换后重画这两块**（真浏览器自检那条"切英文后整页不能残留中文"就靠它）。
   *
   * 静态标签由 `app.js` 的 `applyLanguage()` 按 `data-i18n` 回填；但角标与每一行都是
   * **生成出来的**，必须用缓存下来的数据重画一遍 —— 不重画的话，切完英文这里还是中文。
   * 不重新发请求：`relabel` 只用内存里的最后一份数据。
   */
  function relabel() {
    if (state.assets) renderAssets(state.assets);
    if (state.laya) fillLaya(state.laya);
  }

  function boot() {
    bind('btnAssets', function () { refreshAssets(); });
    bind('btnAssetAutoSave', function () { assetAction('autosave', {}); });
    bind('btnAssetRestore', function () {
      if (!window.confirm(L('assets.restoreAll.confirm',
        '按恢复决议把缓存/磁盘版装回内存库？（只填内存，不写盘）'))) return;
      assetAction('restore', { all: true });
    });

    bind('btnLayaLoad', function () { loadLaya(); });
    bind('btnLayaSave', function () { saveLaya(); });
    bind('btnLayaProbe', function () {
      loadLaya().then(function () { status(L('laya.probed', 'Laya：连通性已探测')); });
    });

    // 首屏各拉一次：不打扰（游戏没跑就是"未连上"，界面自己会说）。
    loadLaya();
    refreshAssets();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();

  // 给真浏览器自检（selftest.html）留的钩子；`relabel` 由 app.js 在切换语言时调用
  window.PlayerAiEditorAssets = {
    refresh: refreshAssets,
    render: renderAssets,
    relabel: relabel,
    loadLaya: loadLaya,
    fillLaya: fillLaya,
    assetAction: assetAction,
    state: state
  };
})();
