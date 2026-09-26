
/* ============================================================ 判定复盘面板（P4）
 *
 * 这一块把游戏里的 `ai.laya.review` 摆到界面上：**刚才那几轮判得怎么样**。
 *
 * 为什么它值得单独一块：
 *   · 判定记录只活在游戏进程里（内存环），命令行 `ai.laya.review` 是唯一的入口 ——
 *     而用户真正在用的界面是编辑器，不是命令行；
 *   · §5.4 的结论是**摘要本身就是最主要的调参旋钮**，所以这一屏的重点不是"答了什么"，
 *     而是"**当时喂进去的那串字**"与耗时/ token：点一行就能看到原文摘要。
 *
 * 与资源库 / Laya 配置同一套纪律：**判据只在游戏侧算**，编辑器只转述与显示。
 * 文案一律走 `L()/F()`（中文兜底 + i18n.js 的 STRINGS.en）：真浏览器自检里有一条
 * "切到 English 后整页不能残留中文"，硬编码中文会被它当场抓住。
 */
(function () {
  'use strict';

  var state = { review: null, openSeq: 0 };

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

  function status(text) {
    var node = $('statusText');
    if (node) node.textContent = text;
  }

  function badge(text, cls) {
    var node = $('reviewBadge');
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

  function count() {
    var node = $('reviewCount');
    var value = node ? parseInt(node.value, 10) : 10;
    if (isNaN(value) || value < 0) value = 10;
    if (value > 200) value = 200;
    return value;
  }

  // ---------------------------------------------------------------- 读取与渲染

  function refresh() {
    var box = $('reviewBox');
    if (box) {
      box.innerHTML = '';
      box.appendChild(hint(L('review.loading', '读取中…')));
    }
    return api('/api/laya/review?count=' + count()).then(function (data) {
      state.review = data;
      render(data);
      return data;
    });
  }

  function render(data) {
    var box = $('reviewBox');
    if (!box) return;
    box.innerHTML = '';

    if (!data) {
      badge(L('review.badge.noEditor', '复盘：编辑器没有回应'), 'dirty');
      box.appendChild(hint(L('review.noEditor', '编辑器没有回应。')));
      return;
    }
    if (data.ok === false) {
      badge(L('review.badge.offline', '复盘：游戏未连上'), 'dirty');
      // 同资源库：**不把 `reason` 写进 DOM**（多半是操作系统本地化的 socket 文案，
      // 切英文之后会变成残留中文，真浏览器自检会当场抓住）。
      if (data.reason && window.console) console.warn('[review] ' + data.reason);
      box.appendChild(hint(L('review.offline', '拿不到判定记录：游戏没在跑（起来之后再刷新）。')));
      return;
    }

    var summary = data.summary || {};
    var recent = data.recent || null;
    var digests = data.digests || null;
    var markdown = data.markdown || null;

    var total = summary.total | 0;
    var failed = summary.failed | 0;
    var sent = summary.sent | 0;

    badge(total
      ? F('review.badge', '复盘：{0} 次 / 失败 {1} / 均值 {2}ms', total, failed, summary.avgMs | 0)
      : L('review.badge.empty', '复盘：还没有记录'),
      failed ? 'dirty' : (total ? 'live' : ''));

    if (!total) {
      box.appendChild(hint(L('review.empty',
        '还没有判定记录：让行为树在游戏里跑一会儿（例如 ai.tree.load name=demo.laya），再刷新。')));
      return;
    }

    // 聚合一行：真往返 / 缓存 / 被拒 / token / p95
    box.appendChild(hint(F('review.summary',
      '真实往返 {0} · 命中去重 {1} · 请求前被拒 {2} · 失败 {3} · token {4} · 延迟 均 {5}ms / p95 {6}ms / 最大 {7}ms',
      sent, summary.cached | 0, summary.refused | 0, failed, summary.inputTokens | 0,
      summary.avgMs | 0, summary.p95Ms | 0, summary.maxMs | 0)));

    if (digests) {
      var digestList = document.createElement('div');
      digestList.className = 'assetList';
      for (var d = 0; d < digests.length; d++) {
        digestList.appendChild(hint(digests[d]));
      }
      box.appendChild(digestList);
      return;
    }

    if (markdown) {
      var pre = document.createElement('pre');
      pre.className = 'reviewMd';
      pre.textContent = markdown;
      box.appendChild(pre);
      return;
    }

    var list = document.createElement('div');
    list.className = 'assetList';
    for (var i = 0; recent && i < recent.length; i++) list.appendChild(row(recent[i]));
    box.appendChild(list);
  }

  function row(record) {
    var line = document.createElement('div');
    line.className = 'assetRow' + (record.ok ? '' : ' dirty')
      + (state.openSeq === record.seq ? ' active' : '');

    var name = document.createElement('div');
    name.className = 'name';
    name.textContent = F('review.row.title', '#{0} {1} · {2}',
      record.seq, record.bank || '-', record.time || '');
    line.appendChild(name);

    var stateLine = document.createElement('div');
    stateLine.className = 'state';
    stateLine.textContent = (record.ok
      ? F('review.row.answer', '答案 {0}', record.answer || '-')
      : F('review.row.failed', '失败 {0}', record.errorCode || '-'))
      + ' · ' + F('review.row.cost', '摘要 {0} 字 · {1}ms · {2} token',
        record.digestChars | 0, record.elapsedMs | 0, record.inputTokens | 0)
      + ' · ' + F('review.row.kind', '来源 {0}', record.kind);
    line.appendChild(stateLine);

    // 展开/收起原文摘要：调摘要长度时最有用的一屏（§5.4）
    var actions = document.createElement('div');
    actions.className = 'actions';
    var toggle = document.createElement('button');
    toggle.textContent = state.openSeq === record.seq
      ? L('review.row.hideDigest', '收起摘要')
      : L('review.row.showDigest', '看原文摘要');
    toggle.title = L('review.row.showDigest.title',
      '当时真的发出去的那串状态摘要（改摘要长度时看它）');
    toggle.addEventListener('click', function () {
      state.openSeq = state.openSeq === record.seq ? 0 : record.seq;
      render(state.review);
    });
    actions.appendChild(toggle);
    line.appendChild(actions);

    if (state.openSeq === record.seq) {
      var digest = document.createElement('div');
      digest.className = 'why';
      digest.textContent = record.digest || '';
      line.appendChild(digest);
    }
    return line;
  }

  // ---------------------------------------------------------------- 动作

  function copyMarkdown() {
    return api('/api/laya/review?format=md&count=' + count()).then(function (data) {
      var text = data && data.markdown ? data.markdown : null;
      if (!text) {
        status(L('review.md.empty', '复盘：没有可复制的表'));
        return null;
      }
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text);
      }
      status(F('review.md.copied', '复盘：markdown 表已复制（{0} 行）', text.split('\n').length - 3));
      return text;
    });
  }

  function clearAll() {
    if (!window.confirm(L('review.clear.confirm',
      '清空游戏里的判定记录？（只清内存里的复盘环，不影响正在跑的行为树）'))) return null;
    return api('/api/laya/review?clear=true&count=' + count()).then(function (data) {
      status(L('review.cleared', '复盘：记录已清空'));
      state.openSeq = 0;
      state.review = data;
      render(data);
      return data;
    });
  }

  // ---------------------------------------------------------------- 挂载

  function bind(id, handler) {
    var node = $(id);
    if (node) node.addEventListener('click', handler);
  }

  /** 语言切换后重画（生成出来的角标与每一行都要跟着切；不重新发请求）。 */
  function relabel() {
    if (state.review) render(state.review);
  }

  function boot() {
    bind('btnReview', function () { refresh(); });
    bind('btnReviewMd', function () { copyMarkdown(); });
    bind('btnReviewClear', function () { clearAll(); });
    if ($('reviewBox')) {
      $('reviewBox').innerHTML = '';
      $('reviewBox').appendChild(hint(L('review.initial',
        '点「刷新复盘」看看游戏里最近几次判定。')));
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();

  // 给真浏览器自检（selftest.html）与 app.js 的语言切换留钩子
  window.PlayerAiEditorReview = {
    refresh: refresh,
    render: render,
    relabel: relabel,
    copyMarkdown: copyMarkdown,
    clearAll: clearAll,
    state: state
  };
})();
