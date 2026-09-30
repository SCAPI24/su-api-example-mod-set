
/* ============================================================ 摘要规格面板
 *
 * 喂给 Laya 判定模型的那串状态摘要，现在是**一份数据文件**：
 *   <实例根>/PlayerAi/Digest/world.digest.json
 * 发哪些事实、按什么顺序、档位标签与阈值、谁进上线（wire）谁进人工（rich）、
 * 两份字符预算 —— 全在那份 JSON 里。改完**不用重编译、不用重启游戏**就生效。
 *
 * 这一屏回答三个问题：
 *   · 现在生效的是哪一份规格（id、版本、哈希、盘上是不是已经改了还没被读、路径、预算来源）；
 *   · 每个字段从哪来、什么格式、什么档位、进不进 wire；
 *   · 照这份规格编译出来到底长什么样（`state.digest` 原样预览，wire / rich 两份可切）。
 *
 * 与资源库 / 判定复盘同一套纪律：**判据只在游戏侧算一次**，编辑器只显示与转发。
 * 特别是"改了盘"这件事由游戏侧的 `ai.digest.status` 如实报（它刻意**不**在查询时刷新），
 * 编辑器绝不替它刷新 —— 刷新是那个「重读规格」按钮的显式动作。
 *
 * 文案一律走 `L()/F()`（中文兜底 + i18n.js 的 STRINGS.en）：真浏览器自检里有一条
 * "切到 English 后整页不能残留中文"，硬编码中文会被它当场抓住。
 */
(function () {
  'use strict';

  var state = { status: null, compiled: null, reload: null };

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

  function badge(text, cls) {
    var node = $('digestBadge');
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

  /** 预算留空 = 0 = "用规格里自己那份"（编辑器不替用户猜一个数字，游戏侧认这个约定）。 */
  function budget() {
    var node = $('digestBudget');
    var value = node ? parseInt(node.value, 10) : 0;
    if (isNaN(value) || value <= 0) return 0;
    if (value > 2000) value = 2000;
    return value;
  }

  function wireMode() {
    var node = $('digestWire');
    return !!(node && node.checked);
  }

  // ---------------------------------------------------------------- 读取与渲染

  function refresh() {
    var box = $('digestBox');
    if (box) {
      box.innerHTML = '';
      box.appendChild(hint(L('digest.loading', '读取中…')));
    }
    return api('/api/digest/status').then(function (data) {
      state.status = data;
      render(data);
      return data;
    });
  }

  function render(data) {
    var box = $('digestBox');
    if (!box) return;
    box.innerHTML = '';

    if (!data) {
      badge(L('digest.badge.noEditor', '摘要规格：编辑器没有回应'), 'dirty');
      box.appendChild(hint(L('digest.noEditor', '编辑器没有回应。')));
      return;
    }
    if (data.ok === false) {
      badge(L('digest.badge.offline', '摘要规格：游戏未连上'), 'dirty');
      // 同资源库 / 复盘：**不把 `reason` 写进 DOM**（多半是操作系统本地化的 socket 文案，
      // 切英文之后会变成残留中文，真浏览器自检会当场抓住）。原始原因留在控制台。
      if (data.reason && window.console) console.warn('[digest] ' + data.reason);
      box.appendChild(hint(L('digest.offline',
        '拿不到摘要规格：游戏没在跑（起来之后再刷新）。')));
      return;
    }

    var budgets = data.budgets || {};
    var changed = data.changedOnDisk === true;

    // 角标：id / 版本 / 哈希，外加"盘上已经改了但还没被读"这个最要紧的状态
    var text = F('digest.badge', '摘要 {0} v{1} · {2}',
      data.id || '-', data.version || '-', short(data.hash));
    if (changed) text += ' ' + L('digest.badge.changed', '· 盘上已改，点「重读规格」生效');
    badge(text, changed ? 'dirty' : 'live');

    // 路径 + 是不是内置兜底
    box.appendChild(hint(F('digest.path', '规格文件：{0}', data.path || '-')
      + (data.builtin ? ' ' + L('digest.builtin', '（内置兜底）') : '')));

    // 两份预算 + **预算的权威来源**（数据里写了却不生效是最坏的一种坑）
    box.appendChild(hint(F('digest.budgets',
      '预算：上线 {0} 字 / 人工 {1} 字 · 来源：{2}',
      budgets.wire | 0, budgets.rich | 0, data.budgetSource || '-')));

    // 上一次重读失败的原因：错误就在界面上，不用去翻日志
    if (data.lastError) {
      var failed = document.createElement('div');
      failed.className = 'why';
      failed.textContent = F('digest.lastError', '上次重读失败：{0}', data.lastError);
      box.appendChild(failed);
    }

    var fields = data.fields || [];
    box.appendChild(hint(F('digest.fields.header',
      '字段 {0} 个（顺序就是发送顺序）', data.fieldCount | 0)));

    var list = document.createElement('div');
    list.className = 'assetList';
    for (var i = 0; i < fields.length; i++) list.appendChild(fieldRow(fields[i]));
    box.appendChild(list);
  }

  /** 一个字段一行：key（名字）/ 来源·格式·两份条件（state）/ 说明 + 档位（why）。 */
  function fieldRow(field) {
    var line = document.createElement('div');
    line.className = 'assetRow';

    var name = document.createElement('div');
    name.className = 'name';
    name.textContent = field.key || '-';
    line.appendChild(name);

    var meta = document.createElement('div');
    meta.className = 'state';
    meta.textContent = F('digest.field.meta', '来源 {0} · 格式 {1} · 人工 {2} · 上线 {3}',
      field.source || '-', field.format || '-',
      field.rich || L('digest.field.always', '总是发'),
      field.wire || L('digest.field.sameAsRich', '同人工那份'));
    line.appendChild(meta);

    var bands = field.bands && field.bands.length
      ? field.bands.join(' | ')
      : L('digest.field.noBands', '（没有档位）');

    var why = document.createElement('div');
    why.className = 'why';
    why.textContent = (field.description || '-') + ' · ' + F('digest.field.bands', '档位：{0}', bands);
    line.appendChild(why);
    return line;
  }

  // ---------------------------------------------------------------- 编译预览

  function compile() {
    var query = '/api/digest?wire=' + (wireMode() ? 'true' : 'false');
    var size = budget();
    if (size > 0) query += '&budget=' + size;

    var section = compiledSection();
    if (section) {
      section.innerHTML = '';
      section.appendChild(hint(L('digest.compiling', '编译中…')));
    }
    return api(query).then(function (data) {
      state.compiled = data;
      renderCompiled(data);
      return data;
    });
  }

  /** 编译结果挂在状态表**下面**（刷新状态时整块重画，所以每次现找现建）。 */
  function compiledSection() {
    var box = $('digestBox');
    if (!box) return null;
    var node = document.getElementById('digestCompiled');
    if (!node) {
      node = document.createElement('div');
      node.id = 'digestCompiled';
      node.className = 'assetList';
      box.appendChild(node);
    }
    return node;
  }

  function renderCompiled(data) {
    var section = compiledSection();
    if (!section) return;
    section.innerHTML = '';

    if (!data) {
      section.appendChild(hint(L('digest.noEditor', '编辑器没有回应。')));
      return;
    }
    if (data.ok === false) {
      // 同样不把 `reason` 写进 DOM
      if (data.reason && window.console) console.warn('[digest] ' + data.reason);
      section.appendChild(hint(L('digest.offline',
        '拿不到摘要规格：游戏没在跑（起来之后再刷新）。')));
      return;
    }

    var head = document.createElement('div');
    head.className = 'assetRow';
    var title = document.createElement('div');
    title.className = 'name';
    title.textContent = data.wire
      ? L('digest.compiled.wire', '编译预览（上线 wire）')
      : L('digest.compiled.rich', '编译预览（人工 rich）');
    head.appendChild(title);
    var meta = document.createElement('div');
    meta.className = 'state';
    meta.textContent = F('digest.compiled.meta', '{0} 字 / 预算 {1} 字 · 规格 {2} v{3} · {4}',
      data.chars | 0, data.budgetChars | 0, data.spec || '-', data.specVersion || '-',
      short(data.specHash));
    head.appendChild(meta);
    section.appendChild(head);

    var line = document.createElement('div');
    line.className = 'assetRow';
    var why = document.createElement('div');
    why.className = 'why';
    why.textContent = data.digest || L('digest.compiled.empty', '（这份摘要此刻是空的）');
    line.appendChild(why);
    section.appendChild(line);
  }

  // ---------------------------------------------------------------- 动作

  /** 重读规格：改完 JSON 立刻生效，不等下一次判定。 */
  function reload() {
    return api('/api/digest/reload').then(function (data) {
      state.reload = data;
      if (!data) {
        status(L('digest.reload.noEditor', '摘要规格：编辑器没有回应'));
        return null;
      }
      if (data.ok === false) {
        // ⚠️ 游戏侧重读失败时 `ok` 就是 false（编辑器**不会**把它覆盖成 true）——
        //    这一条必须如实说失败，否则"改了没生效"会被界面说成"好了"。
        if (data.reason && window.console) console.warn('[digest] ' + data.reason);
        status(data.code === 'game_unreachable'
          ? L('digest.reload.offline', '摘要规格：游戏没在跑，重读不了')
          : F('digest.reload.failed', '摘要规格：重读失败（{0}）', data.error || data.code || '-'));
      } else {
        status(F('digest.reload.ok', '摘要规格：已重读 · {0}', short(data.hash)));
      }
      return refresh();
    });
  }

  // ---------------------------------------------------------------- 挂载

  function bind(id, handler) {
    var node = $(id);
    if (node) node.addEventListener('click', handler);
  }

  /** 语言切换后重画（生成出来的角标与每一行都要跟着切；不重新发请求）。 */
  function relabel() {
    if (state.status) render(state.status);
    if (state.compiled) renderCompiled(state.compiled);
  }

  function boot() {
    bind('btnDigestStatus', function () { refresh(); });
    bind('btnDigestReload', function () { reload(); });
    bind('btnDigestCompile', function () { compile(); });
    if ($('digestBox')) {
      $('digestBox').innerHTML = '';
      $('digestBox').appendChild(hint(L('digest.initial',
        '点「刷新规格」看看现在生效的是哪一份、都有哪些字段。')));
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();

  // 给真浏览器自检（selftest.html）与 app.js 的语言切换留钩子
  window.PlayerAiEditorDigest = {
    refresh: refresh,
    render: render,
    relabel: relabel,
    compile: compile,
    reload: reload,
    state: state
  };
})();
