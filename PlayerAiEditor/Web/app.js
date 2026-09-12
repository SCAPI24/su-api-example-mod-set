/*
 * PlayerAi 行为树编辑器前端（低代码三分栏：物料区 / 画布 / 属性区）。
 * 零依赖、无构建：随 exe 内嵌，离线可用。
 *
 * 数据就是**包格式本身**（manifest.json + tree.json）：
 *   · 画布改的是 tree 对象（节点的 children/decorators/services/properties）；
 *   · 保存时整份 POST 给编辑器后端，由**游戏内同一份校验器**判定能不能写（计划 §5.2）；
 *   · 写盘后可以一键"推送热重载"，游戏不重启就换上新树。
 */
(function () {
  'use strict';

  var state = {
    schema: null,
    materials: null,
    packages: [],
    actions: [],
    path: null,
    manifest: null,
    tree: null,
    selectedId: null,
    dirty: false,
    writable: false,
    gameRunning: false,
    instanceRoot: null,
    /** 校验问题按节点 id 归集（画布上标出来）：id → 'error' | 'warning' */
    issueIds: {},
    /** 多选：选中的节点 id 列表（selectedId 仍是"最后点的那个"，属性区看它） */
    selection: [],
    /** Shift 连选的锚点 */
    anchorId: null,
    /** 节点图缩放是否来自"自动适应"（手动缩放后置 false，窗口再变也不覆盖用户选择） */
    graphAutoFit: true,
    /** 两步移动：待搬走的节点 id（点目标节点完成移动；Esc 取消） */
    pendingMove: null,
    /** 节点图里被选中的连线：{from, to}（Delete 断开 = 把子节点提升为父节点的兄弟） */
    selectedWire: null,
    /** 被选中的节点组（注释框）id；选中它时属性区换成组的编辑面板 */
    selectedGroupId: null,
    /** 当前视图：默认就是**节点图**（UE 那种画布），缩进树按需切 */
    view: 'graph',
    /** 手动摆过的节点位置：id → {x, y}（画布坐标，未乘缩放）。没有条目就走自动布局 */
    nodePos: {},
    /** 节点组/注释框：[{id, title, color, x, y, w, h, members:[nodeId]}] */
    groups: [],
    /** 游离节点：还没接进行为树的节点（物料拖到空白画布上新建的就是这种） */
    detached: [],
    /** 当前画布的平移余量（内容坐标 0 在画布里的偏移，见 syncCanvasViewportSize） */
    originPad: 0,
    /** 框选中的矩形：{x, y, w, h}（画布坐标）或 null */
    marquee: null,
    /** 当前主题：'light'（默认） | 'dark'（真正生效的是 <html> 上的 light 类） */
    theme: 'light',
    /** 最近一次 `ai.status` 的原文（跑的是哪棵树 / 暂停没暂停 / 接管没接管）——播放按钮靠它决定文案 */
    gameStatus: null,
    /** 最近一次读 `ai.status` 为什么失败（没连上 / 没准备好 / 被拒绝） */
    gameStatusError: null,
    /** 「试跑」时是不是我们顺手把树暂停的（是的话提醒用户"试跑完点继续"） */
    pausedForTrial: false,
    /** 试跑结束的自动恢复只发一次（轮询会让状态回包到达很多次） */
    trialResumeSent: false
  };

  /** 窗口 resize 的防抖定时器（拖窗口会连续触发） */
  var resizeTimer = null;

  /** 画布容器的尺寸观察者（比只听 window.resize 更可靠，见 observeCanvasResize） */
  var canvasObserver = null;

  /** 观察者当前盯着的容器（每次渲染都是新元素，重复 observe 同一个没意义） */
  var canvasObserved = null;
  var scrollerObserved = null;

  /** 全局输入是否已经接好（bindGlobalInput 只允许接一次，见那里的说明） */
  var inputBound = false;

  /** 当前渲染出来的连线层（拖线时要往里插一条临时线） */
  var wireLayer = null;

  /** 正在从事的"拉线"操作（真节点编辑器里的核心手势），null = 没在拉 */
  var wireDrag = null;

  // ---------------------------------------------------------------- 撤销/重做
  //
  // 快照整份 {manifest, tree}：包就是一个对象图，深拷贝最省心也不会漏字段
  // （比"记录逆操作"可靠得多 —— 逆操作写错一次就会把树改坏，而这里永远不会）。
  // 连续同类操作（例如拖数字输入框）在 800ms 内合并成一步，避免撤销栈被灌满。
  var history = { past: [], future: [], limit: 80, lastLabel: null, lastAt: 0 };

  function snapshot() {
    return {
      manifest: clone(state.manifest),
      tree: clone(state.tree),
      path: state.path,
      selectedId: state.selectedId
    };
  }

  /** 改树之前调用：把"改之前"压进撤销栈。label 用于合并连续同类操作。 */
  function pushHistory(label) {
    if (!state.tree) return;
    var now = Date.now();
    if (label && label === history.lastLabel && now - history.lastAt < 800) {
      history.lastAt = now;
      return;
    }
    history.lastLabel = label || null;
    history.lastAt = now;
    history.past.push(snapshot());
    if (history.past.length > history.limit) history.past.shift();
    history.future.length = 0;
    updateHistoryUi();
  }

  function restoreSnapshot(entry) {
    state.manifest = clone(entry.manifest);
    state.tree = clone(entry.tree);
    state.path = entry.path;
    state.selectedId = entry.selectedId && findNode(entry.selectedId) ? entry.selectedId
      : (state.tree ? state.tree.id : null);
    markDirty(true);
    renderTree();
    renderInspector();
    updateHistoryUi();
  }

  function undo() {
    if (!history.past.length) { setStatus(L('st.059', '没有可撤销的操作')); return; }
    history.future.push(snapshot());
    restoreSnapshot(history.past.pop());
    setStatus(I18n.format("st.c001", '已撤销（还可撤销 {0} 步，可重做 {1} 步）', history.past.length, history.future.length));
  }

  function redo() {
    if (!history.future.length) { setStatus(L('st.060', '没有可重做的操作')); return; }
    history.past.push(snapshot());
    restoreSnapshot(history.future.pop());
    setStatus(I18n.format("st.c002", '已重做（还可重做 {0} 步）', history.future.length));
  }

  function resetHistory() {
    history.past.length = 0;
    history.future.length = 0;
    history.lastLabel = null;
    updateHistoryUi();
  }

  function updateHistoryUi() {
    var undoButton = $('btnUndo');
    var redoButton = $('btnRedo');
    if (undoButton) {
      undoButton.disabled = !history.past.length;
      undoButton.title = I18n.format("ui.059", '撤销（Ctrl+Z）　可撤销 {0} 步', history.past.length);
    }
    if (redoButton) {
      redoButton.disabled = !history.future.length;
      redoButton.title = I18n.format("ui.060", '重做（Ctrl+Y / Ctrl+Shift+Z）　可重做 {0} 步', history.future.length);
    }
    var badge = $('historyBadge');
    if (badge) badge.textContent = I18n.format("ui.001", '撤销 {0} / 重做 {1}', history.past.length, history.future.length);
  }

  // ---------------------------------------------------------------- 工具

  // ---------------------------------------------------------------- 语言 / 显示翻译（i18n.js）
  //
  // 用户要求：① 物料区加一层**只影响显示**的翻译（`Task.Log` 显示成"任务·日志"，包里仍是 Task.Log）；
  //          ② 界面在主题旁边能切中文 / English。
  // 文案与节点显示名都在 i18n.js；这里只做"取词 + 回填 + 重画"。
  // 缺 i18n.js（旧 exe / 直接打开文件）时退化成"原样返回"，界面照样能用。

  var I18n = window.PlayerAiI18n || {
    t: function (key, fallback) { return fallback !== undefined ? fallback : key; },
    nodeLabel: function (type) { return type; },
    nodeRawHint: function () { return ''; },
    getLanguage: function () { return 'zh'; },
    setLanguage: function () { return 'zh'; },
    detect: function () { return 'zh'; },
    normalize: function (lang) { return String(lang || '') === 'en' ? 'en' : 'zh'; }
  };

  /** 取一条界面文案。 */
  function L(key, fallback) {
    return I18n.t(key, fallback);
  }

  /** 节点显示名（**只用于显示**；写进包的永远是 node.type 原文）。 */
  function nodeLabel(type) {
    return I18n.nodeLabel(type);
  }

  /** 在容器里放"显示名 + 原始类型小字"：翻译看得懂，真类型也不丢。 */
  function setNodeLabel(element, type) {
    var text = type || '?';
    element.textContent = '';
    // 用 <span> 而不是裸文本节点：DOM 自检（editor_web_selftest.js）与真浏览器自检都是
    // 按"元素子节点"遍历画布的，塞文本节点会让它们把文本当成控件读（实测踩过）。
    var label = document.createElement('span');
    label.className = 'node-label';
    label.textContent = nodeLabel(text);
    element.appendChild(label);
    var raw = I18n.nodeRawHint(text);
    if (raw) {
      var span = document.createElement('span');
      span.className = 'node-raw';
      span.textContent = raw;
      element.appendChild(span);
    }
    return element;
  }

  /** 把 index.html 里带 data-i18n* 的静态文案按当前语言回填。 */
  function applyLanguage() {
    var nodes = document.querySelectorAll('[data-i18n]');
    for (var i = 0; i < nodes.length; i++) {
      var key = nodes[i].getAttribute('data-i18n');
      var text = L(key, nodes[i].textContent);
      if (text !== undefined && text !== null) nodes[i].textContent = text;
    }
    var htmlNodes = document.querySelectorAll('[data-i18n-html]');
    for (var j = 0; j < htmlNodes.length; j++) {
      var htmlKey = htmlNodes[j].getAttribute('data-i18n-html');
      var html = L(htmlKey, htmlNodes[j].innerHTML);
      if (html !== undefined && html !== null) htmlNodes[j].innerHTML = html;
    }
    var placeholders = document.querySelectorAll('[data-i18n-placeholder]');
    for (var k = 0; k < placeholders.length; k++) {
      var phKey = placeholders[k].getAttribute('data-i18n-placeholder');
      placeholders[k].setAttribute('placeholder', L(phKey, placeholders[k].getAttribute('placeholder')));
    }

    var titled = document.querySelectorAll('[data-i18n-title]');
    for (var t = 0; t < titled.length; t++) {
      var titleKey = titled[t].getAttribute('data-i18n-title');
      var titleText = L(titleKey, titled[t].getAttribute('title'));
      if (titleText !== undefined && titleText !== null) titled[t].setAttribute('title', titleText);
    }

    var langButton = $('btnLang');
    if (langButton) {
      langButton.textContent = L('lang.switchTo');
      langButton.title = L('lang.switchTo');
    }
    applyTheme(themePreference());   // 主题按钮的文案也跟着语言走
    // 徽标是**动态写的**（markDirty / updateTreeRunUi 每次刷新都会重写），
    // 只回填 data-i18n 的静态文案不够 —— 自检实测：切回中文后改动徽标还停在 "Modified"。
    if ($('dirtyBadge')) markDirty(state.dirty);
    updateTreeRunUi();
    var liveButton = $('btnLive');
    if (liveButton) liveButton.textContent = live.timer ? L('btn.live.stop') : L('btn.live');
    document.documentElement.setAttribute('lang',
      I18n.getLanguage() === 'en' ? 'en' : 'zh-CN');

    // 重画依赖语言的部分。
    //
    // 这条清单是被用户**三次实测反馈**逼出来的（底部"实例根/包目录"、顶部"5节点"、
    // 动作包下拉的"（仅结构：不能回放）/1043 帧"）：那些文字各自由"只在启动或加载时
    // 跑一次"的函数写上去，语言切换时没人重画它们。所以凡是**带文字的渲染**都要在这里
    // 重跑一遍 —— 只回填 data-i18n 的静态文案远远不够。
    renderMeta();                 // 游戏徽标 + 底部"实例根/包目录"（缓存了 meta，不发请求）
    renderPackageOptions();       // 打开包下拉（选项里带"5节点"）
    renderActionSelect();         // 动作包下拉 + 徽标（选项里带"（仅结构：不能回放）1043帧"）
    if (state.materials) renderPalette($('paletteFilter') ? $('paletteFilter').value : '');
    renderTree();
    renderInspector();
    renderLivePanel();
    renderLocatedLine();          // 「落点 x,y」那一行是按上一次回包拼的
    if (uiPick.data) renderUiPick();
  }

  function languagePreference() {
    return I18n.detect();
  }

  function toggleLanguage() {
    var next = I18n.getLanguage() === 'en' ? 'zh' : 'en';
    I18n.setLanguage(next);
    applyLanguage();
    setStatus(next === 'en'
    ? L('lang.switchedEn', 'Language: English')
    : L('lang.switchedZh', '界面语言：中文（节点类型写进包时仍是原文）'));
  }

  function $(id) { return document.getElementById(id); }

  function api(path, options) {
    return fetch(path, options).then(function (response) {
      return response.json().then(function (json) {
        if (!response.ok && !json) throw new Error('HTTP ' + response.status);
        return json;
      });
    });
  }

  function setStatus(text) { $('statusText').textContent = text; }

  function markDirty(dirty) {
    state.dirty = dirty;
    var badge = $('dirtyBadge');
    badge.textContent = dirty
              ? L("ui.002", '已修改')
              : L("ui.003", '未修改');
    badge.className = 'badge' + (dirty ? ' dirty' : '');
    // 脏了之后播放按钮的含义会变（"暂停" → "推送改动"），所以要跟着刷新
    updateTreeRunUi();
  }

  function clone(value) { return JSON.parse(JSON.stringify(value)); }

  function walk(node, visit, parent) {
    if (!node) return;
    visit(node, parent || null);
    (node.children || []).forEach(function (child) { walk(child, visit, node); });
  }

  function findNode(id) {
    var found = null;
    walk(state.tree, function (node) { if (node.id === id) found = node; });
    if (found) return found;
    // 游离节点也"能被选中、能改属性"，所以查找要带上它们
    for (var i = 0; i < state.detached.length; i++) {
      if (state.detached[i].id === id) return state.detached[i];
    }
    return null;
  }

  /** 这个节点是不是"游离"（没接进行为树）。 */
  function isDetached(id) {
    if (!id) return false;
    for (var i = 0; i < state.detached.length; i++) {
      if (state.detached[i].id === id) return true;
    }
    return false;
  }

  /** 把一个已经在树里的节点摘下来变成游离节点（属性区的「摘下来」用）。 */
  function detachNode(node) {
    if (!node || node === state.tree) { setStatus(L('st.049', '根节点不能摘下来')); return false; }
    if (isDetached(node.id)) { setStatus(L('st.017', '它已经是游离节点了')); return false; }
    var parent = findParent(node.id);
    if (!parent) { setStatus(L('st.038', '找不到它的父节点')); return false; }
    pushHistory();
    parent.children = parent.children.filter(function (item) { return item !== node; });
    state.detached.push(node);
    state.selectedId = node.id;
    persistLayout();
    markDirty(true);
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c003", '已把 {0} 摘成游离节点（它还在画布上，保存时不会写进包）', node.id));
    return true;
  }

  /** 建一个游离节点（物料拖到空白画布时用）。 */
  function createDetachedNode(type, properties) {
    var material = materialFor(type);
    if (!material) { setStatus(I18n.format("st.c004", '未知类型：{0}（物料区里没有）', type)); return null; }
    var node = { id: nextId(shortId(type)), type: type };
    var props = properties || {};
    var names = Object.keys(props);
    if (names.length) {
      node.properties = {};
      names.forEach(function (name) { node.properties[name] = props[name]; });
    }
    state.detached.push(node);
    return node;
  }

  function findParent(id) {
    var found = null;
    walk(state.tree, function (node, parent) { if (node.id === id) found = parent; });
    return found;
  }

  function nextId(prefix) {
    var used = {};
    walk(state.tree, function (node) { used[node.id] = true; });
    // 游离节点的 id 也要避开，否则会出现两个同 id 节点
    state.detached.forEach(function (node) { used[node.id] = true; });
    for (var i = 1; i < 10000; i++) {
      var candidate = prefix + i;
      if (!used[candidate]) return candidate;
    }
    return prefix + Date.now();
  }

  function materialFor(type) {
    if (!state.materials) return null;
    return state.materials.byType[type] || null;
  }

  function findAction(file) {
    for (var i = 0; i < state.actions.length; i++) {
      if (state.actions[i].file === file) return state.actions[i];
    }
    return null;
  }

  /** 物料分组 = schema 派生出来的那些 + 动态的「动作包」组（不改动 state.materials）。 */
  function paletteGroups() {
    var groups = (state.materials ? state.materials.groups : []).slice();
    // 「UI 操作」组：**语义目标**是「控件名 / 路径 / 列表里的某一行」三合一的东西，
    // 光一个 Task.UiClick 类型说不清 target 该怎么填 —— 所以给一个「从游戏里现取」的入口：
    // 点它就把当前屏幕上的控件（含列表的每一行，例如 WorldsList 里的某一张地图）列出来，
    // 选一个直接生成节点。用户原话：「用于解析 list 或多种结构的」。
    groups.push({
      key: 'ui',
      label: L('palette.group.ui', 'UI 操作（界面/列表）'),
      items: [{ kind: 'ui-target', type: 'Task.UiClick' }]
    });
    if (state.actions.length) {
      groups.push(PlayerAiLowcode.actionsToMaterials(state.actions));
    }
    return groups;
  }

  /** 物料分组标题（走翻译，缺 key 就用适配层给的中文标签）。 */
  function paletteGroupLabel(group) {
    return L('palette.group.' + group.key, group.label);
  }

  /** 物料按钮：节点/装饰器/服务显示**翻译名 + 原始类型**，动作包显示文件名。 */
  function paletteMaterialButton(item, handler) {
    if (item.kind === 'action') return button(actionPaletteLabel(item), handler);
    var element = document.createElement('button');
    element.addEventListener('click', handler);
    if (item.kind === 'ui-target') {
      element.textContent = L('palette.uiPick', '🎯 拾取界面目标…');
      return element;
    }
    setNodeLabel(element, item.type);
    return element;
  }

  /**
   * 从一行校验信息里找出"出问题的节点 id"。
   *
   * 校验器的落点长这样：`ERROR shape.children @tree.json#root.children[0]#sel.children[1]#idle: …`
   * —— `#` 后面是节点 id（`children[0]` 这种下标段要跳过）。取**最后一个能在树里找到**的 id，
   * 那才是真正出问题的那个节点。
   */
  function issueNodeId(text) {
    var matches = String(text).match(/#([A-Za-z0-9_.\-]+)/g) || [];
    for (var i = matches.length - 1; i >= 0; i--) {
      var id = matches[i].slice(1);
      if (findNode(id)) return id;
    }
    return null;
  }

  function setIssues(list) {
    var box = $('issues');
    box.innerHTML = '';
    state.issueIds = {};

    (list || []).forEach(function (line) {
      var text = String(line);
      var kind = /^ERROR/.test(text) ? 'error' : (/^WARN/.test(text) ? 'warning' : 'info');
      var div = document.createElement('div');
      div.className = 'issue ' + kind;
      div.textContent = text;

      var id = kind === 'info' ? null : issueNodeId(text);
      if (id) {
        // 点了就跳到那个节点：选中 + 滚到可见（不然长树里根本找不到）
        div.classList.add('jumpable');
        div.title = I18n.format("ui.061", '点一下跳到节点 {0}', id);
        div.addEventListener('click', function () { selectNode(id, true); });
        if (state.issueIds[id] !== 'error') state.issueIds[id] = kind;
      }
      box.appendChild(div);
    });

    if (state.tree) renderTree();
  }

  /** 选中一个节点（可选滚到可见）。 */
  function selectNode(id, scroll) {
    if (!findNode(id)) { setStatus(I18n.format("st.c005", '节点不在了：{0}', id)); return; }
    state.selectedId = id;
    state.selection = [id];
    state.anchorId = id;
    renderTree();
    renderInspector();
    if (scroll) {
      var row = document.querySelector('.node-row[data-node-id="' + id + '"]');
      if (row && row.scrollIntoView) row.scrollIntoView({ block: 'center' });
    }
  }

  // ---------------------------------------------------------------- 多选

  /**
   * 点击的选中语义（与文件管理器一致，别自创一套）：
   *   · 普通点击：只选它；
   *   · Ctrl/Cmd+点击：加入/移出选区（不改变"最后点的那个"以外的语义）；
   *   · Shift+点击：从锚点到它**按画布顺序**整段选中。
   */
  function handleSelectClick(id, event) {
    if (!findNode(id)) return;
    var additive = !!(event && (event.ctrlKey || event.metaKey));
    var range = !!(event && event.shiftKey);

    if (range && state.anchorId && findNode(state.anchorId)) {
      var order = layoutTree(state.tree).nodes.map(function (entry) { return entry.id; });
      var from = order.indexOf(state.anchorId);
      var to = order.indexOf(id);
      if (from >= 0 && to >= 0) {
        state.selection = from <= to ? order.slice(from, to + 1) : order.slice(to, from + 1);
      }
    } else if (additive) {
      var at = state.selection.indexOf(id);
      if (at >= 0) state.selection.splice(at, 1);
      else state.selection.push(id);
      state.anchorId = id;
    } else {
      state.selection = [id];
      state.anchorId = id;
    }

    state.selectedId = id;
    renderTree();
    renderInspector();
    if (state.selection.length > 1)
      setStatus(I18n.format("st.c006", '已选中 {0} 个节点（属性区有批量操作）', state.selection.length));
  }

  /** 选中集合对应的节点（按画布顺序，跳过已经不存在的）。 */
  function selectedNodes() {
    var order = layoutTree(state.tree).nodes.map(function (entry) { return entry.id; });
    return state.selection
      .filter(function (id) { return order.indexOf(id) >= 0; })
      .sort(function (left, right) { return order.indexOf(left) - order.indexOf(right); })
      .map(function (id) { return findNode(id); })
      .filter(function (node) { return !!node; });
  }

  /**
   * 批量删除：**祖先也被选中时跳过它**（否则删父再删子，第二步会扑空/报错）。
   * 整批算**一步撤销** —— 人按一次"批量删除"就该一次撤回。
   */
  function deleteSelection() {
    var nodes = selectedNodes();
    if (!nodes.length) { setStatus(L('st.063', '没有选中的节点')); return; }
    var doomed = {};
    nodes.forEach(function (node) { doomed[node.id] = true; });

    var removed = 0;
    nodes.forEach(function (node) {
      if (node === state.tree) return;                 // 根节点不动
      for (var parent = findParent(node.id); parent; parent = findParent(parent.id)) {
        if (doomed[parent.id]) return;                 // 祖先也在选区里 → 交给祖先那次删除
      }
      var parent = findParent(node.id);
      if (!parent) return;
      pushHistory('batch-delete');
      parent.children = parent.children.filter(function (child) { return child !== node; });
      removed++;
    });

    if (!removed) { setStatus(L('st.079', '选中的节点不能删除（根节点/已被祖先覆盖）')); return; }
    state.selection = [state.tree.id];
    state.selectedId = state.tree.id;
    state.anchorId = state.tree.id;
    markDirty(true);
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c007", '批量删除 {0} 个节点（Ctrl+Z 一次撤回）', removed));
  }

  /**
   * 把选中的兄弟节点**包进一个 Sequence**（行为树里最常见的整理动作：
   * "这几步要按顺序做"）。要求它们同属一个父节点 —— 不同父节点的没法包成一个顺序。
   */
  function wrapSelection(parentType) {
    var nodes = selectedNodes();
    if (nodes.length < 2) { setStatus(L('st.068', '至少选两个节点才能包起来')); return; }
    var type = parentType || 'Sequence';
    var parent = findParent(nodes[0].id);
    if (!parent) { setStatus(L('st.002', '不能把根节点包起来')); return; }
    var sameParent = nodes.every(function (node) { return findParent(node.id) === parent; });
    if (!sameParent) { setStatus(I18n.format("st.c008", '这些节点不在同一个父节点下，包不成一个 {0}', type)); return; }

    var material = materialFor(type);
    if (!material || !material.allowsChildren) { setStatus(I18n.format("st.c009", '{0} 不是组合节点', type)); return; }

    pushHistory();
    var index = parent.children.indexOf(nodes[0]);
    var wrapper = { id: nextId(shortId(type)), type: type, children: [] };
    if (material.properties.length) {
      wrapper.properties = {};
      material.properties.forEach(function (spec) {
        if (spec.default !== null && spec.default !== undefined)
          wrapper.properties[spec.name] = coerce(spec, spec.default);
      });
      if (!Object.keys(wrapper.properties).length) delete wrapper.properties;
    }
    parent.children = parent.children.filter(function (child) { return nodes.indexOf(child) < 0; });
    var at = Math.min(index, parent.children.length);
    parent.children.splice(at, 0, wrapper);
    nodes.forEach(function (node) { wrapper.children.push(node); });

    state.selection = [wrapper.id];
    state.selectedId = wrapper.id;
    state.anchorId = wrapper.id;
    markDirty(true);
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c010", '已把 {0} 个节点包进 {1}（Ctrl+Z 可撤回）', nodes.length, type));
  }

  // ---------------------------------------------------------------- 画布

  /**
   * 从活动节点路径里取出沿途的节点 id。
   *
   * `ai.tree.snapshot` 给的是 `Root#root > Sequence#seq > Task.PlayActionPackage#play`
   * 这样的字符串 —— `#` 后面就是节点 id，正好和包里的 id 对得上，于是能在画布上高亮。
   * 没有 `#` 的段落（例如 `<none>`）直接忽略。
   */
  function activeNodeIds(activePath) {
    var ids = [];
    String(activePath || '').split('>').forEach(function (segment) {
      var match = /#([A-Za-z0-9_.\-]+)/.exec(segment);
      if (match && ids.indexOf(match[1]) < 0) ids.push(match[1]);
    });
    return ids;
  }

  /**
   * 该点亮哪一个节点：**只点亮"正在执行"的那一个**（用户明确要求：已执行的不亮）。
   *
   * `ai.tree.snapshot` 的 `path` 是逐节点的明细（含 `active` / `result`），顺序是前序遍历，
   * 于是"最深且 IsActive 的那个"就是最后一个 `active === true` 的条目。
   * 拿不到明细（老版本 Mod / 只有字符串路径）时退回路径字符串的最后一个 id。
   */
  function liveActiveChain(pathDetails, fallbackIds) {
    var leaf = null;
    if (pathDetails && pathDetails.length) {
      for (var i = 0; i < pathDetails.length; i++) {
        var entry = pathDetails[i];
        if (entry && entry.active && entry.id) leaf = entry.id;
      }
    }
    if (!leaf && fallbackIds && fallbackIds.length)
      leaf = fallbackIds[fallbackIds.length - 1];
    return leaf ? [leaf] : [];
  }

  /**
   * 拆 `Task.Subtree` 的引用写法：`id` 或 `id#节点id`。
   * 与游戏侧 `TreeCompiler.ResolveSubtree` 同一套约定（那边也是按第一个 `#` 切）。
   */
  function parseSubtreeRef(reference) {
    var text = String(reference || '').trim();
    if (!text) return null;
    var separator = text.indexOf('#');
    if (separator < 0) return { package: text, node: null };
    return {
      package: text.substring(0, separator),
      node: separator + 1 < text.length ? text.substring(separator + 1) : null
    };
  }

  /** 从一棵 tree.json 里按 id 找节点（展开嵌套包时用来定位入口）。 */
  function findNodeIn(tree, id) {
    var found = null;
    (function walkIn(node) {
      if (!node || found) return;
      if (node.id === id) { found = node; return; }
      (node.children || []).forEach(walkIn);
    })(tree);
    return found;
  }

  /**
   * 把一个 `Task.Subtree` 引用的包**就地展开**在画布上（只读）。
   *
   * 为什么要"只读地展开"：嵌套包的内容属于另一个包，在这里改会让人以为改的是当前包
   * （实际保存下去的是当前包，改动会丢）。所以展开只给看，要改就双击那个节点去打开它。
   */
  function toggleSubtree(node) {
    if (!state.tree || !node) return;
    var reference = node.properties ? node.properties.package : null;
    var parsed = parseSubtreeRef(reference);
    if (!parsed) {
      setStatus(L('st.074', '这个 Subtree 节点还没填 properties.package（引用哪个包）'));
      return;
    }

    state.folded = state.folded || {};
    if (state.folded[node.id] && !state.folded[node.id].loading) {
      delete state.folded[node.id];
      renderTree();
      setStatus(I18n.format("st.c011", '已折叠 {0}', node.id));
      return;
    }

    state.folded[node.id] = { loading: true, reference: reference };
    renderTree();
    setStatus(I18n.format("st.c012", '展开引用 {0} …', parsed.package));

    api('/api/subtree?path=' + encodeURIComponent(state.path)
      + '&node=' + encodeURIComponent(node.id)).then(function (data) {
      if (!data.ok) {
        state.folded[node.id] = { error: data.reason || L('ui.89', '展开失败'), reference: reference };
      } else {
        var entryId = data.entry || (data.tree ? data.tree.id : null);
        var entry = data.tree ? (findNodeIn(data.tree, entryId) || data.tree) : null;
        state.folded[node.id] = {
          reference: reference,
          resolvedFile: data.resolvedFile,
          resolvedId: data.resolvedId,
          entry: entryId,
          nodes: data.nodes,
          tree: entry,
          availableEntries: data.availableEntries || []
        };
      }
      renderTree();
      var info = state.folded[node.id];
      setStatus(info.error
            ? I18n.format("st.t002", '展开失败：{0}', info.error)
            : I18n.format("st.t003", '已展开 {0}#{1}（{2} 节点，只读）', info.resolvedId, (info.entry || '?'), info.nodes));
    });
  }

  /** 双击 `Task.Subtree`：打开它引用的那个包（而不是就地改它）。 */
  function openSubtreePackage(node) {
    if (!state.tree || !node) return;
    var parsed = parseSubtreeRef(node.properties ? node.properties.package : null);
    if (!parsed) { setStatus(L('st.073', '这个 Subtree 节点还没填 properties.package')); return; }

    api('/api/subtree?path=' + encodeURIComponent(state.path)
      + '&node=' + encodeURIComponent(node.id)).then(function (data) {
      if (!data.ok || !data.resolvedFile) {
        setStatus(I18n.format("st.c013", '打开不了引用：{0}', (data.reason || L('ui.288', '解析失败'))));
        return;
      }
      setStatus(I18n.format("st.c014", '打开被引用的包：{0}', data.resolvedFile));
      return loadPackages().then(function () { return openPackage(data.resolvedFile); });
    });
  }

  /** 渲染展开出来的引用子树（递归，带"只读"标记）。 */
  function renderFoldedTree(box, tree, depth) {
    (tree.children || []).forEach(function (child) {
      var row = document.createElement('div');
      row.className = 'node-row folded-node';
      var tag = document.createElement('span');
      var material = materialFor(child.type);
      tag.className = 'tag ' + (material && material.allowsChildren ? 'composite'
        : (child.type === 'Root' ? 'root' : 'task'));
      tag.textContent = child.type || '?';
      row.appendChild(tag);
      var idSpan = document.createElement('span');
      idSpan.className = 'node-id';
      idSpan.textContent = child.id || '(no id)';
      row.appendChild(idSpan);
      if (child.properties && child.properties.package) {
        var ref = document.createElement('span');
        ref.className = 'hint';
        ref.textContent = '↳ ' + child.properties.package;
        row.appendChild(ref);
      }
      box.appendChild(row);
      if ((child.children || []).length)
        renderFoldedTree(box, child, depth + 1);
    });
  }
  function blackboardLines(blackboard) {
    var lines = [];
    if (!blackboard || typeof blackboard !== 'object') return lines;
    var keys = blackboard.keys;
    if (Array.isArray(keys)) {
      keys.forEach(function (entry) {
        if (entry && typeof entry === 'object') {
          lines.push((entry.name || entry.key || '?') + ' = '
            + (entry.value === undefined || entry.value === null ? '—' : String(entry.value))
            + (entry.type ? '  (' + entry.type + ')' : ''));
        } else {
          lines.push(String(entry));
        }
      });
      return lines;
    }
    Object.keys(blackboard).forEach(function (name) {
      var value = blackboard[name];
      if (value && typeof value === 'object' && 'value' in value) {
        lines.push(name + ' = ' + String(value.value) + (value.type ? '  (' + value.type + ')' : ''));
      } else {
        lines.push(name + ' = ' + (value === null || value === undefined ? '—' : String(value)));
      }
    });
    return lines;
  }

  /**
   * 画布尺寸诊断行（纯拼字符串，便于自检）。
   *
   * 为什么要把它显示出来：用户反馈"窗口缩小后没有自适应、只有全屏才对"，而"看到的现象"
   * 与"程序里的数字"之间隔着一层 —— 把**画布栏实际像素、内容尺寸、当前缩放、是否自动适应**
   * 直接摆在界面上，一眼就能判断到底是没重算、还是重算了但容器本身就没变。
   */
  function diagText(info) {
    if (!info) return L('ui.90', '尺寸诊断…');
    var parts = [];
    parts.push(L('ui.91', '视图=') + (info.view === 'graph' ? L('ui.92', '节点图') : L('ui.93', '缩进树')));
    parts.push(L('ui.94', '画布栏=') + Math.round(info.paneWidth || 0) + '×' + Math.round(info.paneHeight || 0));
    if (info.view === 'graph') {
      parts.push(L('ui.95', '内容=') + Math.round(info.contentWidth || 0) + '×'
        + Math.round(info.contentHeight || 0));
      parts.push(L('ui.96', '缩放=') + Math.round((info.scale || 1) * 100) + '%'
        + (info.autoFit ? L('ui.97', '（自动）') : L('ui.98', '（手动）')));
    }
    parts.push(L('ui.99', '节点=') + (info.nodes || 0));
    if (info.scrollWidth && info.scrollWidth > (info.paneWidth || 0))
      parts.push(L('ui.100', '横向需滚动 ') + Math.round(info.scrollWidth - info.paneWidth) + 'px');
    return parts.join(L('ui.101', '　'));
  }

  function updateDiag() {
    var line = $('diagLine');
    if (!line) return;
    if (state.view === 'graph') updateMinimap();
    var pane = $('canvas');
    var scroller = document.querySelector('.graph-scroller');
    var geometry = state.tree && state.view === 'graph'
      ? graphGeometry(layoutTree(state.tree), state.graphScale || 1, state.nodePos) : null;
    line.textContent = diagText({
      view: state.view || 'tree',
      paneWidth: pane && pane.clientWidth ? pane.clientWidth : 0,
      paneHeight: pane && pane.clientHeight ? pane.clientHeight : 0,
      contentWidth: geometry ? geometry.width : 0,
      contentHeight: geometry ? geometry.height : 0,
      scale: state.graphScale || 1,
      autoFit: state.graphAutoFit !== false,
      nodes: state.tree ? layoutTree(state.tree).nodes.length : 0,
      // 只报「内容比画布栏宽」这件事（画布本身带了平移留白，scrollWidth 永远更大）
      scrollWidth: geometry ? geometry.width : 0
    });
  }

  /**
   * 记住当前节点图滚到哪儿了。
   *
   * 重画会把滚动区**换成新元素**（scrollLeft 从 0 开始），于是"平移到大图的另一头 → 点个节点改参数"
   * 会让视野啪地跳回原点。这份坐标就是为了重画后接回去（见 renderGraph 的第二个参数）。
   */
  function currentGraphScroll() {
    var scroller = document.querySelector('.graph-scroller');
    if (!scroller) return null;
    return { left: scroller.scrollLeft || 0, top: scroller.scrollTop || 0 };
  }

  function renderTree() {
    var root = $('treeRoot');
    var keepScroll = state.view === 'graph' ? currentGraphScroll() : null;
    root.innerHTML = '';
    if (!state.tree) {
      root.innerHTML = L("ui.052", '<p class="hint">打开一个包开始编辑。</p>');
      updateDiag();
      return;
    }
    if (state.view === 'graph') {
      renderGraph(root, keepScroll);
      $('canvasHint').textContent = I18n.format("ui.004", '{0}　[节点图]', (state.path ? state.path : ''));
      updateDiag();
      return;
    }
    root.appendChild(renderNode(state.tree, 0));
    // 游离节点单独列一节（用户要求：没挂上的节点在缩进树里就是单独的一个）
    (state.detached || []).forEach(function (node) {
      var row = renderNode(node, 0);
      if (row.classList) row.classList.add('detached-node');
      root.appendChild(row);
    });
    if (state.detached && state.detached.length) {
      var note = document.createElement('p');
      note.className = 'hint detached-note';
      note.textContent = I18n.format("ui.005", '以上 {0} 个是**游离节点**（还没接进行为树）：拖到某个节点上、或用引脚连线就会挂上；保存时不会写进包。', state.detached.length);
      root.appendChild(note);
    }
    $('canvasHint').textContent = I18n.format("ui.006", '{0}　[缩进树]', (state.path ? state.path : ''));
    updateDiag();
  }

  /**
   * **节点图**视图：同一份布局数据用绝对定位的方框 + SVG 连线画出来。
   *
   * 画法参照 UE / Shader Graph / Blender 那一类节点编辑器：
   *   · 深色网格背景；
   *   · 方框 = 彩色标题栏（按节点类别配色）+ 若干引脚行；
   *   · 左边一个**输入脚**（接父节点），右边每个子节点一个**输出脚**；
   *   · 连线是引脚之间的 S 形贝塞尔，线下面还压着一条透明粗线专门接鼠标（好点中）；
   *   · 方框上方一个小标签写着类型名。
   * 与缩进树共用全部交互语义：点选、拖方框重排、拖物料进来、双击 Subtree 进它的包。
   */
  function renderGraph(root, keepScroll) {
    var layout = layoutTree(state.tree);
    var geometry = graphGeometry(layout, state.graphScale || 1, state.nodePos);

    var scroller = document.createElement('div');
    scroller.className = 'graph-scroller';

    var canvas = document.createElement('div');
    canvas.className = 'graph-canvas';
    canvas.style.width = geometry.canvasWidth + 'px';
    canvas.style.height = geometry.canvasHeight + 'px';
    canvas.style.padding = geometry.originPad + 'px';

    // 网格层：铺满整块画布，background-position = 内容原点在画布里的偏移。
    // 必须**最先**创建（最底层），且要在内容层之前 —— 否则它会盖住节点。
    var gridLayer = document.createElement('div');
    gridLayer.className = 'graph-grid';
    canvas.appendChild(gridLayer);

    // 内容层：内容坐标系的原点在这里（画布留白就是这一层的 left/top）。
    // 必须**先**于连线 SVG / 注释框层 / 节点方框创建：它们都挂在它下面。
    var contentLayer = document.createElement('div');
    contentLayer.className = 'graph-content';
    contentLayer.style.left = geometry.originPad + 'px';
    contentLayer.style.top = geometry.originPad + 'px';
    canvas.appendChild(contentLayer);
    // 先按"内容"设一遍；挂进滚动区之后 syncCanvasViewportSize 会把它撑到铺满可视区

    // 连线：一张 SVG 铺在底下（指针事件默认关掉，但命中线自己会打开 —— 见 .graph-wire-hit）
    var svgNamespace = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS ? document.createElementNS(svgNamespace, 'svg') : null;
    if (svg) {
      svg.setAttribute('class', 'graph-wires');
      svg.setAttribute('width', geometry.width);
      svg.setAttribute('height', geometry.height);
      wireLayer = svg;
      geometry.wires.forEach(function (wire) {
        var selected = state.selectedWire
          && state.selectedWire.from === wire.from && state.selectedWire.to === wire.to;
        var path = document.createElementNS(svgNamespace, 'path');
        path.setAttribute('d', wire.d);
        path.setAttribute('class', 'graph-wire'
          + (state.selectedId === wire.to ? ' to-selected' : '')
          + (selected ? ' selected' : ''));
        path.setAttribute('data-wire', wire.from + '>' + wire.to);
        svg.appendChild(path);

        // 细线不好点中：底下再压一条透明的粗线专职接鼠标（UE 里也是这么处理的）
        var hit = document.createElementNS(svgNamespace, 'path');
        hit.setAttribute('d', wire.d);
        hit.setAttribute('class', 'graph-wire-hit');
        hit.setAttribute('data-wire', wire.from + '>' + wire.to);
        hit.addEventListener('mousedown', function (event) {
          if (event.stopPropagation) event.stopPropagation();
        });
        hit.addEventListener('click', function (event) {
          if (event.stopPropagation) event.stopPropagation();
          // Shift + 点线 = 直接断开（用户要求的手势；普通点击只是选中，Delete 才断）
          if (event.shiftKey) {
            disconnectWire(wire.from, wire.to);
            return;
          }
          selectWire(wire);
        });
        svg.appendChild(hit);

        // 子节点序号：Sequence/Selector 是**有序**的，看不出先后就没法读
        var label = document.createElementNS(svgNamespace, 'text');
        label.setAttribute('x', wire.labelX);
        label.setAttribute('y', wire.labelY);
        label.setAttribute('class', 'graph-order');
        // 拖动节点时要能就地更新序号位置，所以标签也带上"它属于哪条线"
        label.setAttribute('data-wire-order', wire.from + '>' + wire.to);
        label.textContent = String(wire.order + 1);
        svg.appendChild(label);
      });
      contentLayer.appendChild(svg);
    }

    // 注释框（节点组）：画在连线和节点**下面**，所以先 append（后 append 的在上层）
    var groupsLayer = document.createElement('div');
    groupsLayer.className = 'graph-groups';
    state.groups.forEach(function (group) { groupsLayer.appendChild(renderGroupBox(group)); });
    contentLayer.appendChild(groupsLayer);

    geometry.boxes.forEach(function (box) {
      var node = box.node;
      var material = materialFor(node.type);
      var category = node.type === 'Root' ? 'root'
        : (material && material.allowsChildren ? 'composite' : 'task');

      var element = document.createElement('div');
      element.className = 'gnode ' + category;
      if (box.detached || isDetached(box.id)) element.classList.add('detached');
      element.setAttribute('data-node-id', box.id);
      element.style.left = box.x + 'px';
      element.style.top = box.y + 'px';
      element.style.width = box.w + 'px';
      element.style.height = box.h + 'px';
      if (box.id === state.selectedId) element.classList.add('selected');
      // 多选要在画布上看得出来（缩进树有 .multi-selected，节点图以前只有"最后一个点中的"那个亮）
      else if (state.selection && state.selection.indexOf(box.id) >= 0)
        element.classList.add('multi-selected');
      if (state.issueIds[box.id]) element.classList.add('has-' + state.issueIds[box.id]);
      // 实时监视：**只亮正在执行的那一个**（亮绿色边框）。路径上的祖先、已经跑完的都不亮。
      if (state.liveTipId === box.id) element.classList.add('live-active');
      if (state.search && nodeMatches(node, state.search)) element.classList.add('search-hit');

      // 方框上方的小标签：写**节点 id**（参考图里每个节点上方都挂一个名字标签；
      // 类型名已经在标题栏里了，这里再写一遍就是重复）
      var caption = document.createElement('div');
      caption.className = 'gnode-caption';
      caption.textContent = (isDetached(box.id) ? L('ui.102', '游离 · ') : '') + (box.id || '(no id)');
      caption.title = (box.id || '') + L('ui.101', '　') + (node.type || '?');
      element.appendChild(caption);

      // 彩色标题栏
      var head = document.createElement('div');
      head.className = 'gnode-head';
      setNodeLabel(head, node.type || '?');
      head.title = (node.type || '?') + L('ui.103', '　id=') + box.id
        + (material && material.allowsChildren ? L('ui.104', '（可以挂子节点）') : L('ui.105', '（任务节点，没有子节点）'));
      element.appendChild(head);

      var body = document.createElement('div');
      body.className = 'gnode-body';
      element.appendChild(body);

      // 第 0 行：节点 id（左边就是输入脚的位置）
      var idRow = document.createElement('div');
      idRow.className = 'gnode-row first';
      idRow.style.top = (box.bodyPadding + 0.5 * box.rowHeight - 8) + 'px';
      idRow.textContent = box.id || '(no id)';
      idRow.title = box.id;
      body.appendChild(idRow);

      // 子节点行：每行一个输出脚（顺序就是 Sequence 的执行次序）
      box.childIds.forEach(function (childId, index) {
        var row = document.createElement('div');
        row.className = 'gnode-row out-row';
        row.style.top = (box.bodyPadding + (index + 1.5) * box.rowHeight - 8) + 'px';
        row.textContent = (index + 1) + ' · ' + childId;
        row.title = I18n.format("ui.062", '第 {0} 个子节点：{1}', (index + 1), childId);
        body.appendChild(row);
      });

      // ---- 预览区（节点底部）：行为树没有纹理可缩略，所以缩略的是"这个节点是什么、
      //      关键参数是多少、现在什么状态" —— 一眼能扫过去，不用点开属性区。
      var preview = document.createElement('div');
      preview.className = 'gnode-preview';
      preview.style.height = (box.previewHeight - 4) + 'px';
      var glyph = document.createElement('div');
      glyph.className = 'gnode-glyph ' + category;
      glyph.textContent = previewGlyph(node, material);
      preview.appendChild(glyph);
      var previewText = document.createElement('div');
      previewText.className = 'gnode-preview-text';
      previewText.textContent = previewSummary(node, material);
      previewText.title = previewText.textContent;
      preview.appendChild(previewText);
      var chip = previewChip(node);
      if (chip) {
        var chipEl = document.createElement('span');
        chipEl.className = 'gnode-chip ' + chip.kind;
        chipEl.textContent = chip.text;
        preview.appendChild(chipEl);
      }
      element.appendChild(preview);

      // 输入脚（左边缘）：拖它到别的节点上 = 让本节点成为那个节点的子节点
      var inPin = document.createElement('div');
      inPin.className = 'pin in';
      inPin.setAttribute('data-node-id', box.id);
      inPin.setAttribute('data-pin-role', 'in');
      inPin.style.left = (-6) + 'px';
      inPin.style.top = (box.inPin.y - box.y - 6) + 'px';
      inPin.title = L("ui.063", '输入：父节点接到这里（拖这个圈到别的节点上＝本节点改挂到它下面）');
      inPin.addEventListener('mousedown', function (event) {
        event.stopPropagation();     // 别让方框拖动/画布平移接手
        if (event.preventDefault) event.preventDefault();
        beginWireDrag(box, { x: box.inPin.x, y: box.inPin.y }, 'in', event);
      });
      element.appendChild(inPin);

      // 输出脚（右边缘）：拖它到别的节点上 = 把那个节点挂成本节点的子节点
      box.outPins.forEach(function (pin, index) {
        var outPin = document.createElement('div');
        outPin.className = 'pin out';
        outPin.setAttribute('data-node-id', box.id);
        outPin.setAttribute('data-pin-role', 'out');
        outPin.setAttribute('data-pin-index', String(index));
        outPin.setAttribute('data-child-id', pin.id);
        outPin.style.left = (box.w - 6) + 'px';
        outPin.style.top = (pin.y - box.y - 6) + 'px';
        outPin.title = I18n.format("ui.064", '输出 {0}（{1}）：拖到别的节点上＝把它挂成第 {2} 个子节点', (index + 1), pin.id, (index + 1));
        outPin.addEventListener('mousedown', function (event) {
          event.stopPropagation();
          if (event.preventDefault) event.preventDefault();
          beginWireDrag(box, { x: pin.x, y: pin.y }, 'out', event);
        });
        element.appendChild(outPin);
      });

      if (node.type === 'Task.Subtree' && node.properties && node.properties.package) {
        var refSpan = document.createElement('span');
        refSpan.className = 'node-ref';
        refSpan.textContent = '↳ ' + node.properties.package;
        refSpan.style.position = 'absolute';
        refSpan.style.right = '10px';
        refSpan.style.top = '5px';
        element.appendChild(refSpan);
      }
      if ((node.decorators || []).length || (node.services || []).length) {
        var extra = document.createElement('span');
        extra.className = 'hint gnode-extra';
        extra.textContent = ((node.decorators || []).length ? L('ui.106', '装饰器×') + node.decorators.length + ' ' : '')
          + ((node.services || []).length ? L('ui.107', '服务×') + node.services.length : '');
        element.appendChild(extra);
      }

      element.addEventListener('click', function (event) {
        event.stopPropagation();
        if (state.pendingMove && completeMove(node)) return;
        state.selectedWire = null;
        state.selectedId = box.id;
        state.selection = [box.id];
        state.anchorId = box.id;
        renderTree();
        renderInspector();
      });
      element.addEventListener('dblclick', function (event) {
        event.stopPropagation();
        if (node.type === 'Task.Subtree') openSubtreePackage(node);
        else if (material && material.allowsChildren) {
          var type = prompt(L('ui.108', '子节点类型（形如 Task.Wait / Sequence）'), 'Task.Wait');
          if (type) addChild(node, type);
        }
      });

      // 拖：把方框拖到别的方框上 = 移动节点；把物料拖进来 = 新建/挂载
      // 落点由 document 级的 mousemove/mouseup 统一处理（见"自定义拖拽"一节），
      // 这里只登记"这个方框能作为拖动源"，以及它代表哪个节点（data-node-id 在上面已设）。
      makeDraggable(element, { kind: 'node', id: box.id });

      contentLayer.appendChild(element);
    });

    scroller.appendChild(canvas);
    makePannable(scroller);
    makeWheelable(scroller);
    scroller.addEventListener('scroll', function () { updateMinimap(); });
    // 空白处左键拖 = 框选（中键/Shift+左键才是平移，见 panIntent）
    scroller.addEventListener('mousedown', function (event) {
      var onNode = !!nodeAncestorOf(event.target) || !!groupAncestorOf(event.target);
      if (!marqueeIntent(onNode, event.button, event.shiftKey)) return;
      beginMarquee(scroller, event);
    });
    // 点空白处 = 取消连线/注释框的选中（点节点、连线、组时它们自己会 stopPropagation）
    canvas.addEventListener('click', function () {
      if (!state.selectedWire && !state.selectedGroupId) return;
      state.selectedWire = null;
      state.selectedGroupId = null;
      setStatus(L('st.033', '已取消选中'));
      renderTree();
      renderInspector();
    });
    root.appendChild(scroller);
    // 每次渲染都是新元素：重新盯上它，"窗口/容器变小 → 重新适应"才有着落
    observeScroller(scroller);
    var canvasSize = syncCanvasViewportSize(geometry);
    syncWireLayerSize(canvasSize);
    // 把重画前的滚动位置接回来（否则每次编辑视野都会跳回原点）；
    // 首次渲染/换包时没有旧位置，就把内容摆到视口正中
    if (keepScroll) {
      // 内容层偏移可能因为这次重画变了（比如新建的节点在左上方 → 画布扩边）：
      // 把变化量补进滚动位置，画面才不会在重画的一瞬间平移。
      scroller.scrollLeft = keepScroll.left + (canvasSize && canvasSize.dx ? canvasSize.dx : 0);
      scroller.scrollTop = keepScroll.top + (canvasSize && canvasSize.dy ? canvasSize.dy : 0);
    } else {
      centerGraphScroll(geometry);
    }
    updateMinimap(geometry);
  }

  /** 画一个注释框（标题栏可拖、右下角可缩放、点它选中）。 */
  function renderGroupBox(group) {
    var factor = state.graphScale || 1;
    var element = document.createElement('div');
    element.className = 'graph-group ' + (group.color || 'blue')
      + (state.selectedGroupId === group.id ? ' selected' : '');
    element.setAttribute('data-group-id', group.id);
    element.style.left = (group.x * factor) + 'px';
    element.style.top = (group.y * factor) + 'px';
    element.style.width = (group.w * factor) + 'px';
    element.style.height = (group.h * factor) + 'px';

    var head = document.createElement('div');
    head.className = 'graph-group-head';
    head.textContent = groupTitle(group);
    head.title = L("ui.065", '拖动这里＝整体移动组内节点；双击＝改名；Delete＝解散');
    head.addEventListener('mousedown', function (event) {
      if (event.stopPropagation) event.stopPropagation();
      if (event.preventDefault) event.preventDefault();
      beginGroupDrag(group, event, 'move');
    });
    head.addEventListener('dblclick', function (event) {
      if (event.stopPropagation) event.stopPropagation();
      var name = prompt(L('ui.109', '注释框标题'), groupTitle(group));
      if (name !== null) renameGroup(group, name);
    });
    element.appendChild(head);

    var resize = document.createElement('div');
    resize.className = 'graph-group-resize';
    resize.title = L("ui.066", '拖动右下角＝调整注释框大小');
    resize.addEventListener('mousedown', function (event) {
      if (event.stopPropagation) event.stopPropagation();
      if (event.preventDefault) event.preventDefault();
      beginGroupDrag(group, event, 'resize');
    });
    element.appendChild(resize);

    element.addEventListener('click', function (event) {
      if (event.stopPropagation) event.stopPropagation();
      selectGroup(group);
    });
    return element;
  }

  /** 内容包围盒（几何坐标，可能为负）。没有方框时退回几何自身的 padding 范围。 */
  function contentBounds(geometry) {
    var boxes = (geometry && geometry.boxes) || [];
    if (!boxes.length) {
      return { minX: 0, minY: 0, maxX: (geometry && geometry.width) || 0,
        maxY: (geometry && geometry.height) || 0 };
    }
    var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    boxes.forEach(function (box) {
      minX = Math.min(minX, box.x);
      minY = Math.min(minY, box.y);
      maxX = Math.max(maxX, box.x + box.w);
      maxY = Math.max(maxY, box.y + box.h);
    });
    return { minX: minX, minY: minY, maxX: maxX, maxY: maxY };
  }

  /**
   * 画布尺寸 & 内容层偏移（"无限画布"的地基）。
   *
   * 画布 = 内容包围盒（左右上下各留 originPad）+ 保证至少一屏多一点（永远有得滚）。
   * 内容往左上长出去时（坐标为负），把它换算成**内容层的偏移**，而不是去改所有坐标 ——
   * 这样几何、连线、命中测试全都还在同一套"内容坐标"里。
   *
   * 返回值里的 dx/dy 是本次内容层偏移的变化量：调用方要把它补偿到滚动位置上，
   * 否则扩边的那一瞬间整个画面会平移一下（拖到边上的节点会"跟不上鼠标"）。
   */
  function syncCanvasViewportSize(geometry) {
    var canvas = document.querySelector('.graph-canvas');
    var scroller = document.querySelector('.graph-scroller');
    if (!canvas || !scroller) return null;
    var pad = geometry && geometry.originPad ? geometry.originPad : 0;
    var bounds = contentBounds(geometry);
    var previousLeft = state.layerLeft === undefined ? pad : state.layerLeft;
    var previousTop = state.layerTop === undefined ? pad : state.layerTop;
    // 内容往负方向长了多少，内容层就右/下挪多少
    var layerLeft = pad - Math.min(0, bounds.minX);
    var layerTop = pad - Math.min(0, bounds.minY);
    var layer = canvas.querySelector ? canvas.querySelector('.graph-content') : null;
    var grid = canvas.querySelector ? canvas.querySelector('.graph-grid') : null;
    state.layerLeft = layerLeft;
    state.layerTop = layerTop;
    state.originPad = pad;

    var viewWidth = scroller.clientWidth || 0;
    var viewHeight = scroller.clientHeight || 0;
    // 内容跨度（含往左上长出去的部分）+ 两侧留白；再和"一屏 + 两侧留白"取大，保证有得滚
    var contentWidth = Math.max(0, bounds.maxX) - Math.min(0, bounds.minX);
    var contentHeight = Math.max(0, bounds.maxY) - Math.min(0, bounds.minY);
    var width = Math.max(contentWidth + pad * 2, viewWidth + pad * 2);
    var height = Math.max(contentHeight + pad * 2, viewHeight + pad * 2);
    canvas.style.width = width + 'px';
    canvas.style.height = height + 'px';
    canvas.style.minWidth = viewWidth ? viewWidth + 'px' : '';
    canvas.style.minHeight = viewHeight ? viewHeight + 'px' : '';
    // 格子大小跟着缩放走：格子的"世界尺寸"恒定（放大时格子跟着变大）
    var gridUnit = Math.max(4, 20 * (state.graphScale || 1));
    var gridMajor = gridUnit * 5;
    var gridSize = gridUnit + 'px ' + gridUnit + 'px, '
      + gridUnit + 'px ' + gridUnit + 'px, '
      + gridMajor + 'px ' + gridMajor + 'px, '
      + gridMajor + 'px ' + gridMajor + 'px';
    // 网格层要**在算出 width/height 之后**再定尺寸：层一旦是 0×0 就没有可画的框
    // —— 网格会整个消失（这里踩过一次）。
    // 网格层铺满整块画布（含内容原点左上角那圈留白），所以尺寸就是画布尺寸，
    // 格子的相位靠 background-position = 内容原点偏移 来锚定。
    if (grid) {
      grid.style.left = '0px';
      grid.style.top = '0px';
      grid.style.width = Math.max(0, width) + 'px';
      grid.style.height = Math.max(0, height) + 'px';
      grid.style.backgroundSize = gridSize;
      grid.style.backgroundPosition = layerLeft + 'px ' + layerTop + 'px';
      grid.setAttribute('data-grid-anchor', layerLeft + ',' + layerTop);
    }
    // 内容层：只画"画布减去偏移"那部分，尺寸比画布大会把滚动区撑大
    // → 冒出滚动条 → 滚动容器尺寸一变 ResizeObserver 就会触发"自动适应"。
    if (layer) {
      layer.style.left = layerLeft + 'px';
      layer.style.top = layerTop + 'px';
      layer.style.width = Math.max(0, width - layerLeft) + 'px';
      layer.style.height = Math.max(0, height - layerTop) + 'px';
    }
    return {
      width: width, height: height,
      contentWidth: contentWidth, contentHeight: contentHeight,
      originPad: pad, layerLeft: layerLeft, layerTop: layerTop,
      dx: layerLeft - previousLeft, dy: layerTop - previousTop,
      bounds: bounds
    };
  }

  /** 内嵌 SVG 的尺寸要跟着画布走，否则超出旧范围的部分会被裁掉。 */
  function syncWireLayerSize(size) {
    var canvas = document.querySelector('.graph-canvas');
    if (!canvas || !size || !canvas.querySelector) return false;
    var svg = canvas.querySelector('.graph-wires');
    if (!svg || !svg.setAttribute) return false;
    svg.setAttribute('width', size.width);
    svg.setAttribute('height', size.height);
    svg.style.width = size.width + 'px';
    svg.style.height = size.height + 'px';
    return true;
  }

  /** 内容层当前偏移（几何坐标 → 画布内坐标要加上它）。 */
  function contentOrigin() {
    var pad = originPad();
    return { x: state.layerLeft === undefined ? pad : state.layerLeft,
      y: state.layerTop === undefined ? pad : state.layerTop };
  }

  /** 当前画布的平移余量（内容坐标 0 在画布里的屏幕偏移，未乘缩放）。 */
  function originPad() {
    return state.originPad || 0;
  }

  /**
   * 只挪位置、**不重建 DOM**：拖动节点时每一帧都要走这里。
   *
   * 为什么不能直接 renderTree()：那会重建全部方框和连线（几十上百个元素），
   * 拖动会卡，而且鼠标下面的元素会被换掉（拖到一半"掉了"）。这里按几何数据
   * 就地改 left/top 与 path 的 d，只有方框数量/结构变化时才需要整体重画。
   */
  function refreshGraphPositions() {
    if (state.view !== 'graph' || !state.tree) return false;
    var canvas = document.querySelector('.graph-canvas');
    if (!canvas || !canvas.querySelectorAll) return false;
    var geometry = graphGeometry(layoutTree(state.tree), state.graphScale || 1, state.nodePos);

    var boxById = {};
    geometry.boxes.forEach(function (box) { boxById[box.id] = box; });
    var boxes = canvas.querySelectorAll('.gnode[data-node-id]');
    for (var i = 0; i < boxes.length; i++) {
      var box = boxById[boxes[i].getAttribute('data-node-id')];
      if (!box) continue;
      boxes[i].style.left = box.x + 'px';
      boxes[i].style.top = box.y + 'px';
      boxes[i].style.width = box.w + 'px';
      boxes[i].style.height = box.h + 'px';
    }

    var wireByKey = {};
    geometry.wires.forEach(function (wire) { wireByKey[wire.from + '>' + wire.to] = wire; });
    var paths = canvas.querySelectorAll('.graph-wire[data-wire], .graph-wire-hit[data-wire]');
    for (var j = 0; j < paths.length; j++) {
      var wire = wireByKey[paths[j].getAttribute('data-wire')];
      if (wire) paths[j].setAttribute('d', wire.d);
    }
    var labels = canvas.querySelectorAll('.graph-order[data-wire-order]');
    for (var k = 0; k < labels.length; k++) {
      var labelWire = wireByKey[labels[k].getAttribute('data-wire-order')];
      if (!labelWire) continue;
      labels[k].setAttribute('x', labelWire.labelX);
      labels[k].setAttribute('y', labelWire.labelY);
    }
    // 画布要跟着内容一起长（拖到边界外时继续扩边），并把扩边量补偿到滚动位置 ——
    // 否则拖到左上角外时画面会整体平移一下（节点看着跟不上鼠标）。
    var size = syncCanvasViewportSize(geometry);
    syncWireLayerSize(size);
    if (size && (size.dx || size.dy)) {
      var scroller = document.querySelector('.graph-scroller');
      if (scroller) {
        scroller.scrollLeft = (scroller.scrollLeft || 0) + size.dx;
        scroller.scrollTop = (scroller.scrollTop || 0) + size.dy;
      }
    }
    // 注释框跟着一起动（拖动组时它自己和成员都变了）
    var factor = state.graphScale || 1;
    var groupElements = canvas.querySelectorAll('.graph-group[data-group-id]');
    for (var g = 0; g < groupElements.length; g++) {
      var group = findGroup(groupElements[g].getAttribute('data-group-id'));
      if (!group) continue;
      groupElements[g].style.left = (group.x * factor) + 'px';
      groupElements[g].style.top = (group.y * factor) + 'px';
      groupElements[g].style.width = (group.w * factor) + 'px';
      groupElements[g].style.height = (group.h * factor) + 'px';
    }
    if (typeof updateMinimap === 'function') updateMinimap(geometry);
    return true;
  }

  /**
   * 自由拖动时"跟着一起动"的节点集合：拖的是多选里的一员就把整组带走
   * （UE / Figma 里都是这个手感；单选时就是它自己）。
   */
  function movingIds(id) {
    if (state.selection && state.selection.length > 1
      && state.selection.indexOf(id) >= 0) return state.selection.slice();
    return [id];
  }

  /**
   * 拖动中把节点摆到鼠标位置（UE 式自由布局）。
   *
   * 位移按"按下点到当前位置"整段算，不逐帧累加 —— 累加会把误差和丢帧放大，
   * 松手后位置会和鼠标差一截。位置存进 state.nodePos（未缩放的画布坐标）。
   * 按下前的整份 nodePos 会先拍个快照（prePos），Shift 改挂父子时用它还原。
   */
  function freeMoveNodes(event) {
    if (!mouseDrag || !state.tree) return false;
    var ids = movingIds(mouseDrag.payload.id);
    if (!mouseDrag.startPos) {
      // 第一次移动：把起点坐标拍下来（自动布局的节点也要有起点）
      var geometry = graphGeometry(layoutTree(state.tree), 1, state.nodePos);
      mouseDrag.startPos = {};
      ids.forEach(function (id) {
        var box = null;
        for (var i = 0; i < geometry.boxes.length; i++) {
          if (geometry.boxes[i].id === id) box = geometry.boxes[i];
        }
        if (box) mouseDrag.startPos[id] = { x: box.x, y: box.y };
      });
      mouseDrag.movedIds = ids;
      mouseDrag.prePos = JSON.parse(JSON.stringify(state.nodePos || {}));
    }
    var scale = state.graphScale || 1;
    var dx = (event.clientX - mouseDrag.startX) / scale;
    var dy = (event.clientY - mouseDrag.startY) / scale;
    Object.keys(mouseDrag.startPos).forEach(function (id) {
      state.nodePos[id] = { x: mouseDrag.startPos[id].x + dx, y: mouseDrag.startPos[id].y + dy };
    });
    refreshGraphPositions();
    return true;
  }

  /** 还原这次拖动过程中写进去的位置（切换成 Shift 改结构、或取消拖动时用）。 */
  function restoreDragPositions(drag) {
    if (!drag || !drag.prePos) return false;
    state.nodePos = drag.prePos;
    refreshGraphPositions();
    return true;
  }

  /**
   * 预览区里那个小方块里的字：行为树没有纹理可缩略，用"类别符号"代替缩略图 ——
   * 根/组合/任务/引用各自一个一眼能分辨的记号。
   */
  function previewGlyph(node, material) {
    if (node.type === 'Task.Subtree') return '↳';
    if (node.type === 'Root') return '⌂';
    if (material && material.allowsChildren) return node.type === 'Selector' ? '?' : '»';
    return '·';
  }

  /** 预览区的一行摘要：组合节点看有几个子节点，任务看关键参数，引用看指向哪个包。 */
  function previewSummary(node, material) {
    if (node.type === 'Task.Subtree') {
      var ref = node.properties && node.properties.package;
      return ref ? (L('ui.110', '引用 ') + ref) : L('ui.111', '未填引用');
    }
    var properties = node.properties || {};
    var parts = [];
    Object.keys(properties).forEach(function (name) {
      if (parts.length >= 2) return;
      var value = properties[name];
      if (value === null || value === undefined || value === '') return;
      if (typeof value === 'object') value = JSON.stringify(value);
      parts.push(name + '=' + value);
    });
    if (parts.length) return parts.join(L('ui.101', '　'));
    if (material && material.allowsChildren) {
      var count = (node.children || []).length;
      return count ? I18n.format("ui.t01", '{0} 个子节点', count)
        : L("ui.t02", '空组合（还没有子节点）');
    }
    return node.type || '';
  }

  /** 预览区右端的状态标记：实时执行中 / 有问题（"路径上"不再标记 —— 用户只要正在执行的那个）。 */
  function previewChip(node) {
    if (state.liveTipId === node.id) return { kind: 'live', text: L('ui.112', '▶ 执行中') };
    if (state.issueIds[node.id] === 'error') return { kind: 'error', text: L('ui.113', '! 错误') };
    if (state.issueIds[node.id] === 'warning') return { kind: 'warn', text: L('ui.114', '! 警告') };
    return null;
  }

  /**
   * 节点图里的落点语义：**中间=挂进去，上/下边缘=插到前面/后面**（按方框高度分三档）。 */
  function graphDropMode(node, box, event, element) {
    var material = materialFor(node.type);
    var canChild = !!(material && material.allowsChildren);
    var rect = (element && element.getBoundingClientRect)
      ? element.getBoundingClientRect() : { top: box.y, height: box.h };
    var height = rect.height || box.h || 1;
    var ratio = (event.clientY - rect.top) / height;
    if (ratio <= 0.25) return canChild ? 'before' : 'before';
    if (ratio >= 0.75) return 'after';
    return canChild ? 'child' : 'after';
  }

  /**
   * 拖空白处平移节点图（纯算术部分抽出来，方便自检）。
   * 拖动方向与内容相反：鼠标往右拖 = 看到左边的内容 = `scrollLeft` 变小。
   */
  function scrollAfterDrag(startScroll, startClient, currentClient) {
    return startScroll - (currentClient - startClient);
  }


  /** 从事件目标往上找最近的 `.gnode`（点在方框里的文字上，等于点在节点上）。 */
  function nodeAncestorOf(target) {
    var current = target;
    while (current && current.classList) {
      if (current.classList.contains('gnode')) return current;
      current = current.parentNode;
    }
    return null;
  }

  /** 从事件目标往上找最近的注释框（点在组上时不能开始框选/平移）。 */
  function groupAncestorOf(target) {
    var current = target;
    while (current && current.classList) {
      if (current.classList.contains('graph-group')) return current;
      current = current.parentNode;
    }
    return null;
  }

  /**
   * 空白处按下该怎么办（纯判断，便于自检）：
   *   · **中键**（或 Shift+左键）→ 平移画布 —— UE / Figma / Blender 都是中键平移；
   *   · **左键** → 框选（橡皮筋多选）；
   *   · 点在节点方框上 → 交给"拖动节点"。
   * 之前是"左键拖空白 = 平移"，现在把左键让给框选：节点编辑器里框选是高频操作，
   * 平移还有滚轮和中键两条路。
   */
  function panIntent(isOnNode, button, shiftKey) {
    if (isOnNode) return false;
    if (button === 1) return true;
    return button === 0 && !!shiftKey;
  }

  /** 左键拖空白 = 框选（Shift 给平移了，所以这里排除 Shift）。 */
  function marqueeIntent(isOnNode, button, shiftKey) {
    return !isOnNode && button === 0 && !shiftKey;
  }

  // ---------------------------------------------------------------- 平移画布（中键拖动）
  //
  // 平移走**和节点拖动同一条 document 级鼠标通路**：以前监听挂在滚动容器自己身上，
  // 鼠标一旦拖出容器（拖大图时几乎必然发生）就收不到 mousemove，手感像"拖到一半断了"。
  // 中键还要 preventDefault，否则浏览器会弹出自动滚动的小圆圈把后续事件吃掉。

  var panDrag = null;

  function beginPan(scroller, event) {
    if (!scroller) return false;
    panDrag = {
      scroller: scroller,
      startX: event.clientX,
      startY: event.clientY,
      startLeft: scroller.scrollLeft || 0,
      startTop: scroller.scrollTop || 0
    };
    scroller.classList.add('panning');
    return true;
  }

  function handlePanMove(event) {
    if (!panDrag) return false;
    if (event.preventDefault) event.preventDefault();
    panDrag.scroller.scrollLeft = scrollAfterDrag(panDrag.startLeft, panDrag.startX, event.clientX);
    panDrag.scroller.scrollTop = scrollAfterDrag(panDrag.startTop, panDrag.startY, event.clientY);
    updateMinimap();
    return true;
  }

  function handlePanEnd() {
    var drag = panDrag;
    panDrag = null;
    if (!drag) return false;
    if (drag.scroller.classList) drag.scroller.classList.remove('panning');
    return true;
  }

  function cancelPan() {
    return handlePanEnd();
  }

  function makePannable(scroller) {
    if (!scroller || !scroller.addEventListener) return;
    scroller.addEventListener('mousedown', function (event) {
      var onNode = !!nodeAncestorOf(event.target) || !!groupAncestorOf(event.target);
      if (!panIntent(onNode, event.button, event.shiftKey)) return;
      beginPan(scroller, event);
      if (event.preventDefault) event.preventDefault();
    });
    // 中键的默认行为（Windows 上是自动滚动）必须掐掉，否则拖不动
    scroller.addEventListener('auxclick', function (event) {
      if (event.button === 1 && event.preventDefault) event.preventDefault();
    });
    scroller.addEventListener('contextmenu', function (event) {
      if (event.button === 1 && event.preventDefault) event.preventDefault();
    });
  }

  /**
   * 节点图的滚轮行为（纯判断，便于自检）：
   *   · **普通滚轮 → 缩放**（用户要求："滚轮上下滚动为 zoom"；缩放到光标位置）
   *   · Ctrl/⌘ + 滚轮 → 也缩放（保留常见习惯）
   *   · Shift + 滚轮 → 左右平移（少数还想滚动的场合）
   * 平移改由**按住滚轮（中键）拖动**完成，见 panIntent / beginPan。
   */
  function wheelIntent(event) {
    if (!event) return { mode: 'none' };
    var delta = event.deltaY || 0;
    if (!delta) return { mode: 'none' };     // 有些设备会发纯 deltaX/零值事件，别把它当成缩放
    if (event.shiftKey && !event.ctrlKey && !event.metaKey) {
      return { mode: 'scroll', deltaX: delta, deltaY: 0 };
    }
    // 一格滚轮固定 8%：不同的鼠标/trackpad 的 deltaY 差很多，不做比例换算更稳
    return { mode: 'zoom', zoom: delta < 0 ? 0.08 : -0.08 };
  }

  function makeWheelable(scroller) {
    if (!scroller || !scroller.addEventListener) return;
    scroller.addEventListener('wheel', function (event) {
      var intent = wheelIntent(event);
      if (intent.mode === 'none') return;
      if (event.preventDefault) event.preventDefault();
      if (intent.mode === 'zoom') {
        zoomGraphAt(intent.zoom, event.clientX, event.clientY);
        return;
      }
      if (!intent.deltaX && !intent.deltaY) return;
      scroller.scrollLeft = (scroller.scrollLeft || 0) + intent.deltaX;
      scroller.scrollTop = (scroller.scrollTop || 0) + intent.deltaY;
    }, { passive: false });
  }

  /**
   * 「两步移动」：先选中要搬的节点 → 点「移动」按钮 → 再点目标节点。
   *
   * 为什么要有它：拖拽在个别浏览器/输入设备上并不可靠（触控板、被扩展拦截、
   * 或者容器上有别的手势处理）。这是**不依赖拖拽**的等价路径 —— 拖不动也能重构树。
   */
  function beginMove(node) {
    if (!node) { setStatus(L('st.011', '先选中一个节点')); return; }
    if (node === state.tree) { setStatus(L('st.050', '根节点不能移动')); return; }
    state.pendingMove = node.id;
    setStatus(I18n.format("st.c015", '正在移动 {0}：点目标节点把它挂/插过去（Esc 取消）', node.id));
    renderInspector();
  }

  function cancelMove() {
    if (!state.pendingMove) return false;
    state.pendingMove = null;
    setStatus(L('st.030', '已取消移动'));
    renderInspector();
    return true;
  }

  /** 有"待移动的节点"时，点击目标 = 落点（能挂子节点就当子节点，否则插到它后面）。 */
  function completeMove(targetNode) {
    if (!state.pendingMove) return false;
    var sourceId = state.pendingMove;
    state.pendingMove = null;
    if (sourceId === targetNode.id) { setStatus(L('st.062', '没有移动（点的是同一个节点）')); renderInspector(); return true; }
    var material = materialFor(targetNode.type);
    applyDrop(targetNode, material && material.allowsChildren ? 'child' : 'sibling',
      { kind: 'node', id: sourceId });
    setStatus(I18n.format("st.c016", '已把 {0} 移到 {1} 下（Ctrl+Z 可撤回）', sourceId, targetNode.id));
    renderInspector();
    return true;
  }

  function setView(mode) {
    state.view = mode === 'graph' ? 'graph' : 'tree';
    var button = $('btnView');
    if (button) button.textContent = state.view === 'graph'
              ? L("ui.007", '视图：节点图')
              : L("ui.008", '视图：缩进树');
    if (!state.tree) return;

    // 切到节点图就**自动适应一次**：不然一进去看到的是一棵被裁掉一半的树，
    // 还得手动点"适应窗口"才知道自己看全了没有。
    if (state.view === 'graph') {
      state.graphAutoFit = true;
      fitGraphToWindow(true);   // force：切视图必须画出来，即使缩放值正好没变
      return;
    }
    renderTree();
  }

  /**
   * 算"适应窗口"该用多大缩放（纯函数）。
   *
   * 大树的节点图一屏装不下，靠滚动条翻很烦；这个函数把"可用区域 / 内容尺寸"取小值，
   * 并夹在 [最小, 最大] 之间 —— 太小看不清、太大一屏只放得下两三个节点，两头都不好。
   * 内容为 0（空树）时返回 1，别除零。
   */
  function fitScale(availableWidth, availableHeight, geometry) {
    var minimum = 0.35;
    var maximum = 1.2;
    // 没有内容（空树）就没有"适应"可言 —— 返回 100%，别拿 padding 的 36×36 去放大到上限。
    if (!geometry || !geometry.boxes || !geometry.boxes.length)
      return 1;
    if (!geometry.width || !geometry.height)
      return 1;
    if (!availableWidth || !availableHeight || availableWidth <= 0 || availableHeight <= 0)
      return 1;

    var byWidth = availableWidth / geometry.width;
    var byHeight = availableHeight / geometry.height;
    var scale = Math.min(byWidth, byHeight);
    return Math.max(minimum, Math.min(maximum, scale));
  }

  function zoomGraph(delta) {
    state.graphScale = Math.max(0.35, Math.min(1.6, (state.graphScale || 1) + delta));
    state.graphAutoFit = false; // 手动缩放过：窗口再变也不自动覆盖用户的选择
    if (state.view === 'graph') renderTree();
    setStatus(I18n.format("st.c017", '节点图缩放 {0}%（手动）', Math.round(state.graphScale * 100)));
  }

  /** 当前渲染出来的几何（含平移留白）——给"居中"这类操作复用。 */
  function geometryForCentering() {
    if (!state.tree) return null;
    return graphGeometry(layoutTree(state.tree), state.graphScale || 1, state.nodePos);
  }

  /** 屏幕坐标 → 画布坐标（未缩放的坐标系，和 state.nodePos 同一套）。 */
  function canvasPointFromClient(clientX, clientY) {
    var canvas = document.querySelector('.graph-canvas');
    if (!canvas || !canvas.getBoundingClientRect) return null;
    var rect = canvas.getBoundingClientRect();
    var scale = state.graphScale || 1;
    var origin = contentOrigin();   // 内容坐标 (0,0) 在画布内的位置（含留白与负坐标补偿）
    return {
      x: ((clientX - rect.left) - origin.x) / scale,
      y: ((clientY - rect.top) - origin.y) / scale
    };
  }

  /**
   * 缩放到**光标位置**（滚轮缩放的正确手感）：先记住光标下面是哪个内容点，
   * 缩放重画后再把滚动位置挪回去，让那个点仍然停在光标下。
   * 不这么做的话，缩放总是以左上角为锚点，图会"跑"到视野外。
   */
  function zoomGraphAt(delta, clientX, clientY) {
    var before = canvasPointFromClient(clientX, clientY);
    var scaleBefore = state.graphScale || 1;
    zoomGraph(delta);
    if (!before) return state.graphScale;
    var scaleAfter = state.graphScale || 1;
    if (Math.abs(scaleAfter - scaleBefore) < 0.0001) return scaleAfter;
    // 关键：renderTree 会把滚动区和画布都换成**新元素**，必须重新取一遍 ——
    // 拿旧元素设 scrollLeft 等于什么都没做（缩放锚点就是这么失效过一轮的）。
    var scroller = document.querySelector('.graph-scroller');
    var canvas = document.querySelector('.graph-canvas');
    if (!scroller || !canvas || !canvas.getBoundingClientRect) return scaleAfter;
    var rect = canvas.getBoundingClientRect();
    // 内容点缩放后的新位置（画布坐标 → 屏幕），把它对齐回光标处
    var origin = contentOrigin();
    var wantLeft = rect.left + origin.x + before.x * scaleAfter;
    var wantTop = rect.top + origin.y + before.y * scaleAfter;
    scroller.scrollLeft = (scroller.scrollLeft || 0) + (wantLeft - clientX);
    scroller.scrollTop = (scroller.scrollTop || 0) + (wantTop - clientY);
    updateMinimap();
    return scaleAfter;
  }

  /**
   * 把视野对准内容（首次渲染、换包、点「适应窗口」之后）。
   *
   * 有了平移留白之后，如果还按左上角对齐，内容会被推到画布的一角、视口里看着是空的 ——
   * 所以这些时机都要把内容摆到视口正中。
   */
  function centerGraphScroll(geometry) {
    var scroller = document.querySelector('.graph-scroller');
    if (!scroller || !geometry) return false;
    var origin = contentOrigin();
    var bounds = contentBounds(geometry);
    // 内容包围盒的中心（画布内坐标 = 内容层偏移 + 几何坐标）
    var centerX = origin.x + (bounds.minX + bounds.maxX) / 2;
    var centerY = origin.y + (bounds.minY + bounds.maxY) / 2;
    scroller.scrollLeft = centerX - (scroller.clientWidth || 0) / 2;
    scroller.scrollTop = centerY - (scroller.clientHeight || 0) / 2;
    updateMinimap(geometry);
    return true;
  }

  /** 「适应窗口」：按当前画布可视区域算一个缩放，让整棵树一屏装下。
   *  force=true 时无论如何都重画一次（切到节点图视图时必须这样：跟"缩放变没变"无关）；
   *  quiet=true 时不动状态栏（自动适应不该把用户动作的提示顶掉）。 */
  function fitGraphToWindow(force, quiet) {
    if (!state.tree) { setStatus(L('st.008', '先打开一个包')); return; }
    if (state.view !== 'graph') setView('graph');

    var scroller = document.querySelector('.graph-scroller');
    var availableWidth = scroller && scroller.clientWidth ? scroller.clientWidth - 4
      : ($('treeRoot') && $('treeRoot').clientWidth ? $('treeRoot').clientWidth - 4 : 900);
    var availableHeight = scroller && scroller.clientHeight ? scroller.clientHeight - 4
      : 520;

    var geometry = graphGeometry(layoutTree(state.tree), 1);
    var next = fitScale(availableWidth, availableHeight, geometry);
    var changed = Math.abs(next - (state.graphScale || 1)) > 0.001;
    state.graphScale = next;
    state.graphAutoFit = true; // 记下"这是自动适应来的"，窗口再变时跟着重算

    // 只有缩放真的变了（或是用户明确点了"适应窗口"）才重画 + 报状态。
    // 重画会换掉画布里的元素，而"盯着新元素"的 ResizeObserver 一上来就会回调一次 →
    // 又触发一次适应 → 又重画……这条自反馈环把状态栏一直刷成"已适应窗口"
    // （把"已移动节点 …"顶掉），还会在拖拽途中把鼠标下面的元素换掉。
    // 真浏览器自检里实测到了这个环，这里从源头掐断。
    if (changed || force) {
      renderTree();
      centerGraphScroll(geometryForCentering());
      // 自动适应（窗口/容器变了、重画后重新收敛）**不写状态栏**：
      // 状态栏留给用户动作（"已移动节点…""已连接…"），否则刚点完就被"已适应窗口"顶掉。
      // "当前缩放是多少、是不是自动"在画布上方的诊断行里一直能看到。
      if (!quiet) {
        setStatus(I18n.format("st.c018", '已适应窗口：{0}%（可视 {1}×{2}，内容 {3}×{4}）', Math.round(state.graphScale * 100), Math.round(availableWidth), Math.round(availableHeight), Math.round(geometry.width), Math.round(geometry.height)));
      }
    } else {
      updateDiag();
    }
  }

  /**
   * 可视区域变了就重算（`graphAutoFit` 为真时）。
   *
   * 为什么要**观察容器**而不只是听 `window.resize`：窗口没变、但画布区域变了的情况同样存在
   * —— 侧栏被折叠、浏览器缩放（Ctrl+滚轮那种页面缩放）也会让"可用像素"变化。
   * 用户反馈"窗口缩小后没有重新自适应、只有全屏才对"就是这类：光听窗口事件不够稳。
   */
  function observeCanvasResize() {
    if (typeof ResizeObserver !== 'function') return;
    var target = document.getElementById('canvas');
    if (!target) return;
    if (!canvasObserver) canvasObserver = new ResizeObserver(function () { handleWindowResize(); });
    if (canvasObserved !== target) {
      canvasObserver.observe(target);
      canvasObserved = target;
    }
    // 节点图的滚动容器才是"适应窗口"真正依赖的那块尺寸；它每次渲染都是新元素，
    // 所以这里和 renderGraph 里都要接一次（见 observeScroller）。
    observeScroller(document.querySelector('.graph-scroller'));
  }

  /** 盯住节点图的滚动容器（渲染出来的元素每次都是新的，所以要重新 observe）。 */
  function observeScroller(scroller) {
    if (!scroller) return;
    if (!canvasObserver) { observeCanvasResize(); return; }
    if (scroller === scrollerObserved) return;
    canvasObserver.observe(scroller);
    scrollerObserved = scroller;
  }

  /** 窗口尺寸变了 → 如果当前的缩放是"自动适应"来的，就跟着重算一次。 */
  function handleWindowResize() {
    if (!state.tree) { updateDiag(); return; }
    if (state.view !== 'graph' || !state.graphAutoFit) { updateDiag(); return; }
    if (resizeTimer) window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(function () {
      resizeTimer = null;
      // 拖拽过程中**绝不重画**：重画会把鼠标下面的元素换掉、也会盖掉"拖动中"的提示，
      // 用户看到的就是"拖到一半画布自己跳了一下"。等拖完再说（这里是重新排队）。
      if (mouseDrag) { handleWindowResize(); return; }
      if (state.view === 'graph' && state.graphAutoFit) fitGraphToWindow(false, true);
      else updateDiag();
    }, 150);
  }

  function renderNode(node, depth) {
    var wrap = document.createElement('div');
    wrap.className = 'node' + (node.id === state.selectedId ? ' selected' : '');

    var row = document.createElement('div');
    row.className = 'node-row';
    row.setAttribute('data-node-id', node.id);

    var material = materialFor(node.type);

    // 拖动源：按下即记录候选，位移超过阈值才算拖（见 makeDraggable / handleMouseDragMove）
    makeDraggable(row, { kind: 'node', id: node.id });

    var tag = document.createElement('span');
    tag.className = 'tag ' + (material && material.allowsChildren ? 'composite'
      : (node.type === 'Root' ? 'root' : 'task'));
    setNodeLabel(tag, node.type || '?');
    row.appendChild(tag);

    var idSpan = document.createElement('span');
    idSpan.className = 'node-id';
    idSpan.textContent = node.id || '(no id)';
    row.appendChild(idSpan);

    if (node.name) {
      var nameSpan = document.createElement('span');
      nameSpan.className = 'node-name';
      nameSpan.textContent = node.name;
      row.appendChild(nameSpan);
    }

    var counts = [];
    if ((node.decorators || []).length) counts.push(L('ui.106', '装饰器×') + node.decorators.length);
    if ((node.services || []).length) counts.push(L('ui.107', '服务×') + node.services.length);
    if (counts.length) {
      var extra = document.createElement('span');
      extra.className = 'hint';
      extra.textContent = counts.join(' ');
      row.appendChild(extra);
    }

    // 这个节点正被校验器点名 → 在画布上直接标出来（红=错、黄=警）
    if (state.issueIds[node.id]) {
      var flag = document.createElement('span');
      flag.className = 'issue-flag ' + state.issueIds[node.id];
      flag.textContent = state.issueIds[node.id] === 'error'
              ? L("ui.009", '! 有问题')
              : L("ui.010", '! 警告');
      row.appendChild(flag);
    }

    // 搜索命中：整行加个描边，跳转时一眼能找到
    if (state.search && nodeMatches(node, state.search)) row.classList.add('search-hit');
    // 多选：选中的都标出来（selectedId 那个另有 .selected 高亮）
    if ((state.selection || []).indexOf(node.id) >= 0 && state.selection.length > 1)
      row.classList.add('multi-selected');

    // 实时监视：游戏里**正在执行**的那个节点 → 整行高亮（已执行的、路径上的都不标）
    if (state.liveTipId === node.id) {
      row.classList.add('live-active');
      var liveFlag = document.createElement('span');
      liveFlag.className = 'live-flag tip';
      liveFlag.textContent = L("ui.011", '▶ 正在执行');
      row.appendChild(liveFlag);
    }

    // 落点：拖到这一行上 = 作为它的子节点（能挂子节点时）或它的兄弟（挂不下时）。
    // 高亮和落点判定都在 document 级的 mousemove/mouseup 里做（见"自定义拖拽"一节），
    // 靠的就是上面那个 data-node-id。

    // 嵌套包（Task.Subtree）：显示引用了谁，可展开看里面是什么、双击去打开那个包
    if (node.type === 'Task.Subtree') {
      var reference = node.properties ? node.properties.package : null;
      var refSpan = document.createElement('span');
      refSpan.className = 'node-ref';
      refSpan.textContent = reference ? ('↳ ' + reference) : L('ui.115', '↳ (未填引用)');
      row.appendChild(refSpan);

      var toggle = document.createElement('button');
      var folded = (state.folded || {})[node.id];
      toggle.textContent = folded && !folded.error && !folded.loading
              ? L("ui.013", '▾ 收起')
              : L("ui.014", '▸ 展开引用');
      toggle.className = 'mini';
      toggle.title = L("ui.067", '就地展开被引用的包（只读；要改就双击这个节点打开那个包）');
      toggle.addEventListener('click', function (event) {
        event.stopPropagation();
        toggleSubtree(node);
      });
      row.appendChild(toggle);

      row.addEventListener('dblclick', function (event) {
        event.stopPropagation();
        openSubtreePackage(node);
      });
    }

    row.addEventListener('click', function (event) {
      event.stopPropagation();
      // "两步移动"进行中：这次点击就是落点
      if (state.pendingMove && completeMove(node)) return;
      handleSelectClick(node.id, event);
    });
    wrap.appendChild(row);

    if ((node.children || []).length) {
      var children = document.createElement('div');
      children.className = 'node-children';
      node.children.forEach(function (child) { children.appendChild(renderNode(child, depth + 1)); });
      wrap.appendChild(children);
    }

    // 展开出来的嵌套包内容（只读视图，不属于当前树，不参与保存/撤销）
    var folded = (state.folded || {})[node.id];
    if (folded) {
      var box = document.createElement('div');
      box.className = 'node-children folded';
      var head = document.createElement('div');
      head.className = 'folded-head';
      if (folded.loading) {
        head.textContent = L("ui.015", '正在读引用…');
      } else if (folded.error) {
        head.textContent = I18n.format("ui.016", '读不了引用：{0}', folded.error);
      } else {
        head.textContent = I18n.format("ui.017", '引用 {0}#{1}（{2} 节点，只读；双击原节点可打开该包）', folded.resolvedId, (folded.entry || '?'), folded.nodes);
      }
      box.appendChild(head);
      if (folded.tree) renderFoldedTree(box, folded.tree, 0);
      wrap.appendChild(box);
    }
    return wrap;
  }

  // ---------------------------------------------------------------- 属性区

  /** 注释框的编辑面板：改名、换色、贴回内容、解散。 */
  function renderGroupInspector(body, group) {
    var head = document.createElement('div');
    head.className = 'prop-row';
    head.innerHTML = I18n.format("ui.053", '<label>注释框</label><span class="tag">{0}</span>', group.id);
    body.appendChild(head);

    body.appendChild(textField(L('ui.116', '标题'), groupTitle(group), function (value) {
      if (value === group.title) return;
      renameGroup(group, value);
    }, 'groupTitleInput'));

    var colorRow = document.createElement('div');
    colorRow.className = 'prop-row';
    var colorLabel = document.createElement('label');
    colorLabel.textContent = L("ui.018", '颜色');
    colorRow.appendChild(colorLabel);
    var colorSelect = document.createElement('select');
    GROUP_COLORS.forEach(function (color) {
      var option = document.createElement('option');
      option.value = color;
      option.textContent = { blue: L('ui.117', '蓝'), purple: L('ui.118', '紫'), green: L('ui.119', '绿'), orange: L('ui.120', '橙'), gray: L('ui.121', '灰') }[color] || color;
      if ((group.color || 'blue') === color) option.selected = true;
      colorSelect.appendChild(option);
    });
    colorSelect.addEventListener('change', function () { setGroupColor(group, colorSelect.value); });
    colorRow.appendChild(colorSelect);
    body.appendChild(colorRow);

    var members = groupMembers(group);
    var info = document.createElement('p');
    info.className = 'hint';
    info.textContent = I18n.format("ui.019", '组内 {0} 个节点：{1}　—— 拖动组头整体移动，右下角可缩放。', members.length, (members.join(L('ui.253', '、')) || L('ui.287', '(空)')));
    body.appendChild(info);

    var actions = document.createElement('div');
    actions.className = 'actions';
    var fitButton = document.createElement('button');
    fitButton.textContent = L("ui.020", '贴回内容');
    fitButton.title = L("ui.068", '把框重新贴合到组内节点（加过节点或挪过位置之后用）');
    fitButton.addEventListener('click', function () { fitGroupToMembers(group); });
    actions.appendChild(fitButton);
    var disbandButton = document.createElement('button');
    disbandButton.textContent = L("ui.021", '解散');
    disbandButton.title = L("ui.069", '解散注释框（里面的节点原地不动）');
    disbandButton.addEventListener('click', function () { disbandGroup(group); });
    actions.appendChild(disbandButton);
    body.appendChild(actions);

    var membership = document.createElement('p');
    membership.className = 'hint';
    membership.textContent = state.selectedId && groupsForNode(state.selectedId).length
              ? I18n.format("ui.022", '当前节点 {0} 属于：{1}', state.selectedId, groupsForNode(state.selectedId).map(function (item) { return item.title; }).join(L('ui.253', '、')))
              : L("ui.023", '提示：先在画布上框选几个节点，再点画布上方的「建组」。');
    body.appendChild(membership);
  }

  function renderInspector() {
    var body = $('inspectorBody');
    body.innerHTML = '';
    // 选中注释框时，属性区换成"组的编辑面板"（跟选中节点是互斥的两种状态）
    var selectedGroup = findGroup(state.selectedGroupId);
    if (selectedGroup) {
      renderGroupInspector(body, selectedGroup);
      return;
    }
    var node = state.selectedId ? findNode(state.selectedId) : null;
    if (!node) {
      body.innerHTML = L("ui.054", '<p class="hint">左侧选中一个节点。</p>');
      return;
    }

    var head = document.createElement('div');
    head.className = 'prop-row';
    // 用 createElement 拼而不是 innerHTML + querySelector：DOM 自检桩不解析 innerHTML 里的子元素，
    // 那样会在自检里拿到 null（实测踩过）。
    var typeLabel = document.createElement('label');
    typeLabel.textContent = L('inspector.type', '类型');
    head.appendChild(typeLabel);
    var typeTag = document.createElement('span');
    typeTag.className = 'tag';
    setNodeLabel(typeTag, node.type || '?');
    head.appendChild(typeTag);
    body.appendChild(head);

    body.appendChild(textField('id', node.id, function (value) {
      if (!value) return;
      var duplicate = value !== node.id && !!findNode(value);
      if (duplicate) { setStatus(I18n.format("st.c019", 'id 已被占用：{0}', value)); return; }
      pushHistory('node.id');
      node.id = value;
      state.selectedId = value;
      markDirty(true);
      renderTree();
    }, 'nodeIdInput'));

    body.appendChild(textField(L('ui.122', 'name（显示名）'), node.name || '', function (value) {
      pushHistory('node.name');
      if (value) node.name = value; else delete node.name;
      markDirty(true);
      renderTree();
    }));

    var material = materialFor(node.type);
    var specs = material ? material.properties : [];
    if (node.type === 'Task.UiClick') {
      // 点击类节点最麻烦的是「target 怎么填」：给一个从游戏里现取的入口，
      // 选中的东西（控件或列表里的某一行）直接写进 target 属性。
      var pickRow = document.createElement('div');
      pickRow.className = 'prop-row';
      var pickLabel = document.createElement('label');
      pickLabel.textContent = L('palette.uiPick', '🎯 拾取界面目标…');
      pickRow.appendChild(pickLabel);
      pickRow.appendChild(button(L('palette.uiPick', '🎯 拾取界面目标…'), function () {
        openUiTargetPicker(function (element) {
          var picked = uiElementTarget(element);
          if (!picked) return;
          pushHistory('node.target');
          node.properties = node.properties || {};
          node.properties.target = picked;
          if (!node.properties.mode) node.properties.mode = 'direct';
          if (node.properties.waitSeconds === undefined) node.properties.waitSeconds = 3;
          markDirty(true);
          renderTree();
          renderInspector();
          setStatus(node.id + '.target = ' + picked);
        });
      }));
      body.appendChild(pickRow);
    }
    if (specs.length) {
      var title = document.createElement('h2');
      title.textContent = L("ui.024", '参数');
      title.style.marginTop = '10px';
      body.appendChild(title);
      specs.forEach(function (spec) {
        body.appendChild(propertyField(node, spec));
      });
    }

    var actions = document.createElement('div');
    actions.className = 'actions';

    // 多选批量操作（只在选了多个时出现，免得平时占地方）
    if ((state.selection || []).length > 1) {
      var multi = document.createElement('div');
      multi.className = 'actions multi';
      var multiTitle = document.createElement('span');
      multiTitle.className = 'hint';
      multiTitle.textContent = I18n.format("ui.025", '已选 {0} 个：', state.selection.length);
      multi.appendChild(multiTitle);
      multi.appendChild(button(L('ui.123', '批量删除'), function () { deleteSelection(); }));
      multi.appendChild(button(L('ui.124', '包进 Sequence'), function () { wrapSelection('Sequence'); }));
      multi.appendChild(button(L('ui.125', '包进 Selector'), function () { wrapSelection('Selector'); }));
      multi.appendChild(button(L('ui.126', '清空选择'), function () {
        state.selection = [state.selectedId];
        renderTree();
        renderInspector();
      }));
      body.appendChild(multi);
    }

    if (material && material.allowsChildren) {
      actions.appendChild(button(L('ui.127', '＋ 子节点（选中物料后点这里）'), function () {
        var type = prompt(L('ui.108', '子节点类型（形如 Task.Wait / Sequence）'), 'Task.Wait');
        if (!type) return;
        addChild(node, type);
      }));
    }
    if (material && material.allowsServices) {
      actions.appendChild(button(L('ui.128', '＋ 服务'), function () {
        var type = prompt(L('ui.129', '服务类型'), 'Service.UpdateNearestPlayer');
        if (!type) return;
        pushHistory();
        node.services = node.services || [];
        node.services.push({ id: nextId('svc'), type: type, interval: 0.25, properties: {} });
        markDirty(true);
        renderTree();
        renderInspector();
      }));
    }
    actions.appendChild(button(L('ui.130', '＋ 装饰器'), function () {
      var type = prompt(L('ui.131', '装饰器类型（Blackboard/Cooldown/TimeLimit/Loop/ForceSuccess/Inverter）'),
        'Blackboard');
      if (!type) return;
      pushHistory();
      node.decorators = node.decorators || [];
      var decorator = { id: nextId('d'), type: type, properties: {} };
      var spec = state.materials.byType['decorator:' + type];
      (spec ? spec.properties : []).forEach(function (p) {
        if (p.default !== null && p.default !== undefined) decorator.properties[p.name] = coerce(p, p.default);
      });
      node.decorators.push(decorator);
      markDirty(true);
      renderTree();
      renderInspector();
    }));

    var parent = findParent(node.id);
    if (parent) {
      actions.appendChild(button(state.pendingMove === node.id ? L('ui.132', '▶ 移动中（点目标节点）') : L('ui.133', '移动…'),
        function () {
          if (state.pendingMove === node.id) { cancelMove(); return; }
          beginMove(node);
        }));
      actions.appendChild(button(L('ui.134', '移动到这里'), function () {
        if (!state.pendingMove) { setStatus(L('st.010', '先点「移动…」选一个要搬的节点')); return; }
        completeMove(node);
      }));
      actions.appendChild(button(L('ui.135', '复制'), function () { copyNode(node, false); }));
      actions.appendChild(button(L('ui.136', '剪切'), function () { copyNode(node, true); }));
      actions.appendChild(button(L('ui.137', '重复'), function () { duplicateNode(node); }));
      actions.appendChild(button(L('ui.138', '粘贴到此处'), function () {
        pasteSubtree(node, material && material.allowsChildren ? 'child' : 'sibling');
      }));
      actions.appendChild(button(L('ui.139', '上移'), function () { move(parent, node, -1); }));
      actions.appendChild(button(L('ui.140', '下移'), function () { move(parent, node, +1); }));
      actions.appendChild(button(L('ui.141', '删除'), function () {
        if (!confirm(L('ui.142', '删除节点 ') + node.id + L('ui.143', ' 及其子树？'))) return;
        pushHistory();
        parent.children = parent.children.filter(function (child) { return child !== node; });
        state.selectedId = parent.id;
        markDirty(true);
        renderTree();
        renderInspector();
      }));
    }

    body.appendChild(actions);

    if ((node.decorators || []).length) {
      var dTitle = document.createElement('h2');
      dTitle.textContent = L("ui.026", '装饰器');
      dTitle.style.marginTop = '10px';
      body.appendChild(dTitle);
      node.decorators.forEach(function (decorator, index) {
        body.appendChild(helperRow(decorator.type + ' #' + decorator.id, function () {
          pushHistory();
          node.decorators.splice(index, 1);
          markDirty(true);
          renderTree();
          renderInspector();
        }));
        var spec = state.materials.byType['decorator:' + decorator.type];
        (spec ? spec.properties : []).forEach(function (p) {
          body.appendChild(propertyField(decorator, p, 'properties'));
        });
        body.appendChild(enumField(L('ui.144', '观察者中断'), decorator.observerAborts || 'None',
          state.schema ? state.schema.abortModes : [], function (value) {
            if (value === 'None') delete decorator.observerAborts;
            else decorator.observerAborts = value;
            markDirty(true);
          }));
      });
    }

    if ((node.services || []).length) {
      var sTitle = document.createElement('h2');
      sTitle.textContent = L("ui.027", '服务');
      sTitle.style.marginTop = '10px';
      body.appendChild(sTitle);
      node.services.forEach(function (service, index) {
        body.appendChild(helperRow(service.type + ' #' + service.id, function () {
          pushHistory();
          node.services.splice(index, 1);
          markDirty(true);
          renderTree();
          renderInspector();
        }));
        body.appendChild(numberField(L('ui.145', 'interval（秒）'), service.interval, function (value) {
          pushHistory('service.interval');
          service.interval = Number(value);
          markDirty(true);
        }));
        var spec = state.materials.byType['service:' + service.type];
        (spec ? spec.properties : []).forEach(function (p) {
          body.appendChild(propertyField(service, p, 'properties'));
        });
      });
    }
  }

  function addChild(parent, type) {
    var material = materialFor(type);
    if (!material) { setStatus(I18n.format("st.c004", '未知类型：{0}（物料区里没有）', type)); return; }
    pushHistory();
    var node = { id: nextId(shortId(type)), type: type };
    if (material.properties.length) {
      node.properties = {};
      material.properties.forEach(function (p) {
        if (p.default !== null && p.default !== undefined) node.properties[p.name] = coerce(p, p.default);
      });
      if (!Object.keys(node.properties).length) delete node.properties;
    }
    parent.children = parent.children || [];
    parent.children.push(node);
    state.selectedId = node.id;
    markDirty(true);
    renderTree();
    renderInspector();
    return node;
  }

  function shortId(type) {
    var tail = String(type || 'node').split('.').pop();
    return tail.charAt(0).toLowerCase() + tail.slice(1) + '_';
  }

  // ---------------------------------------------------------------- 复制 / 剪切 / 粘贴 / 重复
  //
  // 剪贴板只在**内存**里（刷新页面就没了）：包是文件，"跨会话的剪贴板"该由用户用
  // 打开/另存为来管理，编辑器不偷偷往磁盘记东西。粘贴时**重新生成 id**（连同装饰器/服务的 id），
  // 否则同一个包里会出现两个同 id 节点 —— 那是校验器会挡、但写出来很难看的错误。

  function freshId(prefix, used) {
    for (var i = 1; i < 10000; i++) {
      var candidate = prefix + i;
      if (!used[candidate] && !findNode(candidate)) return candidate;
    }
    return prefix + Date.now();
  }

  /** 深拷贝一棵子树并重新分配所有 id（节点/装饰器/服务）。 */
  function cloneSubtreeForPaste(node, used) {
    var copyNode = {};
    Object.keys(node).forEach(function (key) {
      if (key === 'children' || key === 'decorators' || key === 'services') return;
      copyNode[key] = clone(node[key]);
    });
    copyNode.id = freshId(shortId(node.type), used);
    used[copyNode.id] = true;

    if (node.children) {
      copyNode.children = node.children.map(function (child) {
        return cloneSubtreeForPaste(child, used);
      });
    }
    if (node.decorators) {
      copyNode.decorators = node.decorators.map(function (decorator) {
        var copyDecorator = clone(decorator);
        copyDecorator.id = freshId('d', used);
        used[copyDecorator.id] = true;
        return copyDecorator;
      });
    }
    if (node.services) {
      copyNode.services = node.services.map(function (service) {
        var copyService = clone(service);
        copyService.id = freshId('svc', used);
        used[copyService.id] = true;
        return copyService;
      });
    }
    return copyNode;
  }

  /** 收集一棵子树里用到的节点类型（粘贴后给个"贴了点什么"的提示，顺便做引用检查）。 */
  function subtreeInfo(node) {
    var info = { count: 0, references: [] };
    (function walkIn(current) {
      if (!current) return;
      info.count++;
      if (current.type === 'Task.Subtree' && current.properties && current.properties.package) {
        var parsed = parseSubtreeRef(current.properties.package);
        if (parsed && info.references.indexOf(parsed.package) < 0)
          info.references.push(parsed.package);
      }
      (current.children || []).forEach(walkIn);
    })(node);
    return info;
  }

  function copyNode(node, cut) {
    if (!node) { setStatus(L('st.011', '先选中一个节点')); return; }
    if (cut && node === state.tree) { setStatus(L('st.048', '根节点不能剪切')); return; }
    state.clipboard = { node: clone(node), cut: !!cut, sourceId: node.id };
    setStatus(I18n.format("st.c020", '{0}{1} 及其子树（{2} 个节点）—— 选中目标节点后 Ctrl+V', (cut ? L('ui.286', '已剪切 ') : L('ui.285', '已复制 ')), node.id, subtreeInfo(node).count));
  }

  /** 粘贴：`mode` = 'child'（贴进目标节点）或 'sibling'（贴在目标后面）。 */
  function pasteSubtree(targetNode, mode) {
    if (!state.clipboard) { setStatus(I18n.format("st.c021", '剪贴板是空的（先 Ctrl+C 复制一个节点）')); return; }
    if (!state.tree) { setStatus(L('st.008', '先打开一个包')); return; }

    var target = targetNode || (state.selectedId ? findNode(state.selectedId) : state.tree);
    var material = materialFor(target.type);
    var asChild = mode !== 'sibling' && material && material.allowsChildren;

    pushHistory();
    if (state.clipboard.cut) {
      var source = state.clipboard.sourceId ? findNode(state.clipboard.sourceId) : null;
      if (source) {
        var from = findParent(source.id);
        if (from && from.children)
          from.children = from.children.filter(function (child) { return child !== source; });
      }
      state.clipboard.cut = false;
    }

    var used = {};
    var pasted = cloneSubtreeForPaste(state.clipboard.node, used);

    if (asChild) {
      target.children = target.children || [];
      target.children.push(pasted);
    } else {
      var parent = findParent(target.id);
      var list = parent ? parent.children : [state.tree];
      var index = list.indexOf(target);
      list.splice(index + 1, 0, pasted);
    }

    state.selectedId = pasted.id;
    markDirty(true);
    renderTree();
    renderInspector();

    // 粘贴过来的 Subtree 引用，如果当前包没声明，游戏会解析不到 —— 讲清楚，别等装包才炸
    var info = subtreeInfo(pasted);
    var declared = ((state.manifest && state.manifest.references) || []).map(function (reference) {
      return reference && reference.id;
    });
    var missing = info.references.filter(function (id) { return declared.indexOf(id) < 0; });
    setStatus(I18n.format("st.c022", '已粘贴 {0} 个节点（新 id {1}）{2}', info.count, pasted.id, (missing.length ? L('ui.284', '　注意：本包没声明引用 ') + missing.join(L('ui.253', '、')) + L('ui.283', '，游戏会解析不到')
        : '')));
  }

  function duplicateNode(node) {
    if (!node) { setStatus(L('st.011', '先选中一个节点')); return; }
    if (node === state.tree) { setStatus(L('st.051', '根节点不能重复')); return; }
    state.clipboard = { node: clone(node), cut: false, sourceId: node.id };
    // 注意：落点是**这个节点本身**（'sibling' = 插在它后面）。
    // 曾经写成"落点=它的父节点"，结果复制品跑到了父节点的后面（跑到外面去了）——测试抓到的。
    pasteSubtree(node, 'sibling');
  }

  function deleteNode(node) {
    if (!node) { setStatus(L('st.011', '先选中一个节点')); return; }
    if (isDetached(node.id)) {
      state.detached = state.detached.filter(function (item) { return item.id !== node.id; });
      delete state.nodePos[node.id];
      if (state.selectedId === node.id) state.selectedId = state.tree ? state.tree.id : null;
      persistLayout();
      renderTree();
      renderInspector();
      setStatus(I18n.format("st.c023", '已删掉游离节点 {0}', node.id));
      return;
    }
    var parent = findParent(node.id);
    if (!parent) { setStatus(L('st.052', '根节点删不了')); return; }
    pushHistory();
    parent.children = parent.children.filter(function (child) { return child !== node; });
    state.selectedId = parent.id;
    markDirty(true);
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c024", '已删除 {0} 及其子树（Ctrl+Z 可以撤回）', node.id));
  }

  // ------------------------------------------------ 键盘搬节点（整条路径不碰鼠标）
  //
  // 为什么一定要有：这个编辑器已经两次因为"拖拽在某台机器/某个 webview 上不生效"而被退回来。
  // 重排树是编辑器的核心操作，它**不应该**只依赖一种输入方式 —— 现在有三条等价路径：
  // ① 鼠标拖方框　② 属性区「移动」两步走　③ Alt+方向键（这一节）。
  // 三条路径最后都只改同一份数据（children 数组），所以效果与撤销步数完全一致。

  function afterNodeMove(node, label) {
    state.selectedId = node.id;
    state.selection = [node.id];
    state.anchorId = node.id;
    markDirty(true);
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c025", '{0}：{1}（Ctrl+Z 可以撤回）', label, node.id));
  }

  /** 在兄弟之间挪一位（delta=-1 上移 / +1 下移）。到边界就明说，别默默什么都不做。 */
  function moveSibling(node, delta) {
    if (!node) { setStatus(L('st.011', '先选中一个节点')); return false; }
    if (node === state.tree) { setStatus(L('st.050', '根节点不能移动')); return false; }
    var parent = findParent(node.id);
    var list = parent ? parent.children : [state.tree];
    var at = list.indexOf(node);
    var to = at + delta;
    if (at < 0) return false;
    if (to < 0) { setStatus(L('st.037', '已经是第一个了')); return false; }
    if (to >= list.length) { setStatus(L('st.035', '已经是最后一个了')); return false; }
    pushHistory();
    list.splice(at, 1);
    list.splice(to, 0, node);
    afterNodeMove(node, delta < 0 ? L('ui.146', '已上移') : L('ui.147', '已下移'));
    return true;
  }

  /** 降一级：变成**前一个兄弟**的子节点（前一个必须是组合节点）。 */
  function indentNode(node) {
    if (!node) { setStatus(L('st.011', '先选中一个节点')); return false; }
    if (node === state.tree) { setStatus(L('st.050', '根节点不能移动')); return false; }
    var parent = findParent(node.id);
    var list = parent ? parent.children : [state.tree];
    var at = list.indexOf(node);
    if (at <= 0) { setStatus(L('st.016', '前面没有兄弟，降不了级（先把它拖到别的组合节点下）')); return false; }
    var previous = list[at - 1];
    var material = materialFor(previous.type);
    if (!material || !material.allowsChildren) {
      setStatus(I18n.format("st.c026", '前一个节点是任务（{0}），挂不下子节点', previous.type));
      return false;
    }
    pushHistory();
    list.splice(at, 1);
    previous.children = previous.children || [];
    previous.children.push(node);
    afterNodeMove(node, I18n.format("ui.t03", '已降级到 {0} 下', previous.id));
    return true;
  }

  /** 升一级：挪到父节点的后面，成为父节点的兄弟。 */
  function outdentNode(node) {
    if (!node) { setStatus(L('st.011', '先选中一个节点')); return false; }
    if (node === state.tree) { setStatus(L('st.050', '根节点不能移动')); return false; }
    var parent = findParent(node.id);
    if (!parent) { setStatus(L('st.050', '根节点不能移动')); return false; }
    if (parent === state.tree) { setStatus(L('st.036', '已经是根节点的直接子节点，升不了了')); return false; }
    var grand = findParent(parent.id);
    var list = grand ? grand.children : [state.tree];
    pushHistory();
    parent.children = parent.children.filter(function (child) { return child !== node; });
    list.splice(list.indexOf(parent) + 1, 0, node);
    afterNodeMove(node, I18n.format("ui.t04", '已升级成 {0} 的兄弟', parent.id));
    return true;
  }

  /** Alt+方向键的语义（纯判断，便于自检）；返回 null 表示不是这套键。 */
  function altMoveIntent(event) {
    if (!event || !event.altKey || event.ctrlKey) return null;
    var key = String(event.key || '').toLowerCase();
    if (key === 'arrowup') return 'up';
    if (key === 'arrowdown') return 'down';
    if (key === 'arrowright') return 'indent';
    if (key === 'arrowleft') return 'outdent';
    return null;
  }

  /** 按 Alt 语义搬节点（键位处理只管"按了哪个键"，动作都在这里）。 */
  function applyAltMove(node, intent) {
    if (!node) { setStatus(I18n.format("st.c027", '先选中一个节点，再用 Alt+方向键搬它')); return false; }
    if (intent === 'up') return moveSibling(node, -1);
    if (intent === 'down') return moveSibling(node, 1);
    if (intent === 'indent') return indentNode(node);
    if (intent === 'outdent') return outdentNode(node);
    return false;
  }

  /**
   * 把一棵树摊成**布局**：每个节点一行（深度/行号/父节点/是否末子），外加父子连线。
   *
   * 这一步刻意做成**纯函数**（不碰 DOM）：它是"画布从缩进树升级成节点图"的地基 ——
   * 缩进树也能用（行号就是纵向位置），将来画节点图时同一份数据喂给 SVG 连线和绝对定位。
   * 纯函数还意味着它可以脱离浏览器自检（见 Mod/Packages/editor_web_selftest.js）。
   */
  function layoutTree(tree) {
    var nodes = [];
    var edges = [];
    var row = 0;

    // 游离节点：接在树后面（depth 0、没有父节点），几何层会给它们排到后面的槽位
    (state.detached || []).forEach(function (node) {
      nodes.push({
        id: node.id, type: node.type, name: node.name || null,
        depth: 0, row: row++, parentId: null, isLastChild: true,
        childCount: 0, detached: true, node: node
      });
    });

    (function place(node, depth, parentId, isLast) {
      if (!node) return;
      var entry = {
        id: node.id,
        type: node.type,
        name: node.name || null,
        depth: depth,
        row: row++,
        parentId: parentId || null,
        isLastChild: !!isLast,
        childCount: (node.children || []).length,
        node: node
      };
      nodes.push(entry);
      if (parentId) edges.push({ from: parentId, to: node.id });
      (node.children || []).forEach(function (child, index, all) {
        place(child, depth + 1, node.id, index === all.length - 1);
      });
    })(tree, 0, null, true);

    return { nodes: nodes, edges: edges };
  }

  /**
   * 布局 → **几何**（纯函数，不碰 DOM）：每个节点一个方框的坐标 + 每根引脚的位置 + 每条连线一段 SVG path。
   *
   * 坐标约定（与 UE / Shader Graph / Blender 那一类节点编辑器一致）：
   *   · x 随**深度**往右走，y 随槽位往下走（父节点摆在子节点中间，同 tidy tree）；
   *   · 方框 = 标题栏 + 若干"引脚行"：第 0 行是节点 id 与**输入脚**（接父节点），
   *     第 k+1 行是第 k 个子节点的**输出脚**；
   *   · 连线从左边的输出脚画到右边的输入脚，用**水平出入的三次贝塞尔**（就是 UE 那种 S 形）。
   *
   * 之所以把这些都算成数字再交给渲染层：引脚和连线必须**共用同一份坐标**，
   * 否则拖出来的线会和圆圈差几个像素（"看着就是没接上"）。这份数据能脱离浏览器自检。
   */
  function graphGeometry(layout, scale, positions) {
    var boxWidth = 200;
    var headerHeight = 26;
    var rowHeight = 22;
    var bodyPadding = 6;
    var previewHeight = 30;   // 节点底部的"预览区"（参数摘要 / 子节点数 / 引用 / 状态）
    var gapX = 96;
    var gapY = 30;
    var padding = 24;
    var factor = scale || 1;

    // ---- y 先算出来：叶子按先后占槽位，父节点摆在**子节点的中间**（tidy tree 的简化版）。
    // 为什么不能直接用"深度优先行号"当 y：那样一条深链会画成斜着的楼梯，
    // 而且父节点不在子节点上方 —— 节点图就没法看了（这一条是被自检逼出来的）。
    var childrenOf = {};
    (layout.nodes || []).forEach(function (entry) { childrenOf[entry.id] = []; });
    (layout.edges || []).forEach(function (edge) {
      if (childrenOf[edge.from]) childrenOf[edge.from].push(edge.to);
    });

    // 每个父节点下"第几个子"（给连线标序号用；边是按画布顺序来的，所以下标就是序号）
    var orderOf = {};
    Object.keys(childrenOf).forEach(function (parentId) {
      var map = {};
      childrenOf[parentId].forEach(function (childId, index) { map[childId] = index; });
      orderOf[parentId] = map;
    });

    var slotOf = {};
    var nextSlot = 0;
    function assignSlot(id) {
      var children = childrenOf[id] || [];
      if (!children.length) {
        slotOf[id] = nextSlot++;
        return slotOf[id];
      }
      // 注意：**每个**子节点都要先排好位（早先只递归了第一个，后面几个没槽位 → NaN）
      var slots = children.map(assignSlot);
      slotOf[id] = (slots[0] + slots[slots.length - 1]) / 2; // 第一个子与最后一个子的中点
      return slotOf[id];
    }
    if (layout.nodes && layout.nodes.length)
      assignSlot(layout.nodes[0].id);
    // 游离节点没有父子关系，排到树的下方
    (layout.nodes || []).forEach(function (entry) {
      if (entry.detached && slotOf[entry.id] === undefined) slotOf[entry.id] = nextSlot++;
    });

    // 总高度随"有多少个输出脚"增长：一个节点下面挂十个子节点，方框就必须有十行
    var rowCountOf = {};
    (layout.nodes || []).forEach(function (entry) {
      rowCountOf[entry.id] = 1 + (childrenOf[entry.id] || []).length;
    });

    var boxes = [];
    var byId = {};
    (layout.nodes || []).forEach(function (entry) {
      var slot = slotOf[entry.id] === undefined ? entry.row : slotOf[entry.id];
      // 同一槽位如果被多个"高方框"挤到，往下顺延一点，免得互相压住（简单避让，够用）
      var rows = rowCountOf[entry.id] || 1;
      var height = headerHeight + bodyPadding * 2 + rows * rowHeight + previewHeight;
      // 手动摆过的节点（UE 那样自由拖动）：位置优先于自动布局 —— 拖动过就听用户的
      var manual = positions && positions[entry.id];
      var box = {
        id: entry.id,
        type: entry.type,
        depth: entry.depth,
        row: entry.row,
        slot: slot,
        rows: rows,
        manual: !!manual,
        childIds: (childrenOf[entry.id] || []).slice(),
        x: manual ? manual.x * factor
          : padding + entry.depth * (boxWidth + gapX) * factor,
        // 方框以"槽位中心"对齐，而不是以顶边对齐 —— 否则高方框会整体偏下
        y: manual ? manual.y * factor
          : padding + slot * (boxHeightBase(headerHeight, bodyPadding, rowHeight) + gapY) * factor
            - (height - boxHeightBase(headerHeight, bodyPadding, rowHeight)) * factor / 2,
        w: boxWidth * factor,
        h: height * factor,
        headerHeight: headerHeight * factor,
        bodyPadding: bodyPadding * factor,
        rowHeight: rowHeight * factor,
        previewHeight: previewHeight * factor,
        node: entry.node
      };
      // 输入脚：第 0 行的左边缘（和第 0 行的文字同一水平线）
      box.inPin = {
        x: box.x,
        y: box.y + box.headerHeight + box.bodyPadding + box.rowHeight / 2
      };
      // 输出脚：第 k+1 行的右边缘
      box.outPins = box.childIds.map(function (childId, index) {
        return {
          id: childId,
          index: index,
          x: box.x + box.w,
          y: box.y + box.headerHeight + box.bodyPadding + (index + 1.5) * box.rowHeight
        };
      });
      boxes.push(box);
      byId[entry.id] = box;
    });

    var wires = [];
    (layout.edges || []).forEach(function (edge) {
      var from = byId[edge.from];
      var to = byId[edge.to];
      if (!from || !to) return;
      var order = orderOf[edge.from] ? orderOf[edge.from][edge.to] : 0;
      var outPin = from.outPins[order] || from.outPins[from.outPins.length - 1] || {
        x: from.x + from.w, y: from.y + from.h / 2
      };
      var x1 = outPin.x;
      var y1 = outPin.y;
      var x2 = to.inPin.x;
      var y2 = to.inPin.y;
      wires.push({
        from: edge.from,
        to: edge.to,
        // 第几个子节点（0 基）：行为树里顺序就是语义（Sequence 的执行次序），
        // 所以线要标出来，不然一排兄弟看不出谁先谁后。
        order: order,
        fromPin: { x: x1, y: y1 },
        toPin: { x: x2, y: y2 },
        labelX: (x1 + x2) / 2,
        labelY: (y1 + y2) / 2 - 4,
        d: wirePath(x1, y1, x2, y2)
      });
    });

    var width = padding * 2;
    var height = padding * 2;
    boxes.forEach(function (box) {
      width = Math.max(width, box.x + box.w + padding);
      height = Math.max(height, box.y + box.h + padding);
    });
    // ---- 平移余量（originPad）
    // 画布只有"内容那么大"时，内容装进一屏就没得滚 —— 中键拖动、小地图拖可视窗都**看不出效果**。
    // 所以给画布四周留一大圈空白（做成 canvas 元素的 padding，见 renderGraph）：
    // 内容坐标本身不动（几何、连线、适应窗口都还按内容算），只是整体往右下挪了这么多。
    // 220 屏像素左右：够「按住中键拖动看得出画布在动」，又不会在内容左上方留下巨大的
    // 空白区（那片区域里的落点会算出负坐标，看着像「没落在松手处」）
    var originPad = Math.round(220 * Math.max(0.6, factor));
    return {
      boxes: boxes, wires: wires,
      width: width, height: height, scale: factor,
      originPad: originPad,
      canvasWidth: width + originPad * 2,
      canvasHeight: height + originPad * 2
    };
  }

  /** 只有一个引脚行时方框的基础高度（用来算"槽位间距"，让不同高度的方框纵向对齐得好看） */
  function boxHeightBase(headerHeight, bodyPadding, rowHeight) {
    return headerHeight + bodyPadding * 2 + rowHeight;
  }

  /**
   * 节点编辑器那种 S 形连线：控制点水平外伸，所以线从引脚出发时是**水平**的
   * （UE / Blender / Shader Graph 都是这个手感，斜着直接连过去会显得很乱）。
   */
  function wirePath(x1, y1, x2, y2) {
    var reach = Math.max(36, Math.abs(x2 - x1) * 0.5);
    // 往左连（回连）时控制点方向要反过来，否则线会先往右绕一大圈
    if (x2 < x1) reach = Math.max(36, (x1 - x2) * 0.5 + 60);
    return 'M ' + x1 + ' ' + y1 + ' C ' + (x1 + reach) + ' ' + y1 + ', ' + (x2 - reach) + ' '
      + y2 + ', ' + x2 + ' ' + y2;
  }

  /**
   * 把一棵子树打包成**独立的包**（纯函数：只算数据，不落盘）。
   *
   * 为什么要它：把"一段常用的行为"从一棵大树里摘出来单独成包，是复用（`Task.Subtree` 引用它）
   * 的第一步。生成的 manifest 里：
   *   · `entry` = 这棵子树自己的根节点 id（不是原包的入口）；
   *   · `references` 只保留**这棵子树真的用到**的那些引用（`Task.Subtree` 节点里写的 package id）——
   *     全抄过来会带一堆用不上的引用，游戏虽不报错，但人看着乱、也容易漏看真正缺的那条。
   */
  function subtreeExportPayload(node, name) {
    var info = subtreeInfo(node);
    var used = {};
    var tree = cloneSubtreeForPaste(node, used);
    var id = String(name || node.id || 'subtree').replace(/[^A-Za-z0-9._-]/g, '_')
      .replace(/^[._]+|[._]+$/g, '') || ('subtree_' + Date.now());

    var all = (state.manifest && state.manifest.references) || [];
    var references = all.filter(function (reference) {
      return reference && info.references.indexOf(reference.id) >= 0;
    }).map(function (reference) {
      return { id: reference.id, path: reference.path };
    });

    var manifest = {
      format: 'scbt',
      version: (state.format && state.format.scbt) || 1,
      id: id,
      name: String(name || id),
      entry: tree.id,
      blackboard: clone((state.manifest && state.manifest.blackboard) || [])
    };
    if (references.length) manifest.references = references;
    return { manifest: manifest, tree: tree, id: id, nodes: info.count,
             references: references.map(function (r) { return r.id; }) };
  }

  /** 导出选中子树为单独包（写到包目录，然后把它打开）。 */
  function exportSubtree(node) {
    if (!state.tree || !node) { setStatus(L('st.011', '先选中一个节点')); return; }
    if (!state.instanceRoot) { setStatus(L('st.070', '还不知道包目录，稍后再试')); return; }
    var name = prompt(L('ui.148', '新包的名字（会写进包目录）'), node.id + '_pack');
    if (!name) return;

    var payload = subtreeExportPayload(node, name);
    var target = state.instanceRoot + '/PlayerAi/BehaviorTrees/'
      + String(name).replace(/[\\/:*?"<>|]/g, '_') + '.scbtpak';

    setStatus(L('st.021', '导出中…'));
    fetch('/api/package?path=' + encodeURIComponent(target), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manifest: payload.manifest, tree: payload.tree })
    }).then(function (response) { return response.json(); }).then(function (data) {
      if (!data.ok) {
        setIssues((data.issues || []).concat(data.reason ? ['ERROR ' + data.reason] : []));
        setStatus(I18n.format("st.c028", '导出失败：{0}', (data.reason || L('ui.279', '见下方问题'))));
        return;
      }
      setStatus(I18n.format("st.c029", '已导出 {0} 个节点到 {1}{2}', payload.nodes, data.path, (payload.references.length ? (L('ui.282', '（引用 ') + payload.references.join(L('ui.253', '、')) + L('ui.281', ' 一并带上）')) : '')));
      return loadPackages().then(function () { return openPackage(data.path); });
    });
  }

  /** 节点搜索：命中就高亮，回车跳到第一个。 */
  /**
   * 懒加载"被引用包有哪些节点"（给入口选择器用）。
   *
   * 走的还是 `/api/subtree`（它本来就会返回 `availableEntries`），只是换一个已声明的引用
   * 来找一个代表节点当跳板：拿被引用包自己的入口节点去问。读完缓存下来并重画属性区。
   */
  function ensureSubtreeEntries(referenceId) {
    if (state.subtreeEntries[referenceId] !== undefined) return;
    state.subtreeEntries[referenceId] = null; // 占位：正在读（避免重复请求）

    var representative = null;
    walk(state.tree, function (node) {
      if (representative || node.type !== 'Task.Subtree') return;
      var parsed = parseSubtreeRef(node.properties ? node.properties.package : null);
      if (parsed && parsed.package === referenceId) representative = node.id;
    });
    if (!representative) { state.subtreeEntries[referenceId] = { list: [] }; return; }

    api('/api/subtree?path=' + encodeURIComponent(state.path)
      + '&node=' + encodeURIComponent(representative)).then(function (data) {
      var list = [];
      if (data.ok && data.availableEntries) {
        data.availableEntries.forEach(function (text) {
          var match = /^(\S+)\s*\(([^)]+)\)$/.exec(String(text));
          if (match) list.push({ id: match[1], type: match[2] });
        });
      }
      state.subtreeEntries[referenceId] = { list: list, entry: data.entry || null };
      renderInspector();
    }).catch(function () {
      state.subtreeEntries[referenceId] = { list: [] };
    });
  }

  function searchNodes(term) {
    state.search = String(term || '').trim().toLowerCase();
    renderTree();
    if (!state.search) { setStatus(L('st.044', '搜索已清空')); return; }
    var hits = 0;
    walk(state.tree, function (node) {
      if (nodeMatches(node, state.search)) hits++;
    });
    setStatus(hits
            ? I18n.format("st.t004", '找到 {0} 个匹配（回车跳到第一个）', hits)
            : I18n.format("st.t005", '没有匹配：{0}', term));
  }

  function nodeMatches(node, term) {
    if (!term) return false;
    return String(node.id || '').toLowerCase().indexOf(term) >= 0
      || String(node.type || '').toLowerCase().indexOf(term) >= 0
      || String(node.name || '').toLowerCase().indexOf(term) >= 0;
  }

  function jumpToFirstMatch() {
    var found = null;
    walk(state.tree, function (node) {
      if (!found && nodeMatches(node, state.search)) found = node;
    });
    if (!found) { setStatus(I18n.format("st.c030", '没有匹配：{0}', (state.search || ''))); return; }
    selectNode(found.id, true);
    setStatus(I18n.format("st.c031", '跳到 {0}（{1}）', found.id, found.type));
  }

  function move(parent, node, delta) {
    var index = parent.children.indexOf(node);
    var target = index + delta;
    if (target < 0 || target >= parent.children.length) return;
    pushHistory();
    parent.children.splice(index, 1);
    parent.children.splice(target, 0, node);
    markDirty(true);
    renderTree();
  }

  function coerce(spec, value) {
    var kind = (spec.kind || '').toLowerCase();
    if (kind === 'bool') return value === true || value === 'true';
    if (kind === 'int' || kind === 'float') return Number(value);
    if (kind === 'stringlist') {
      if (Array.isArray(value)) {
        return value.map(function (s) { return String(s).trim(); })
          .filter(function (s) { return s; });
      }
      return String(value).split(',').map(function (s) { return s.trim(); })
        .filter(function (s) { return s; });
    }
    return value;
  }

  // ---------------------------------------------------------------- 拖拽

  function clearDropHighlight() {
    // 两个视图都要清：缩进树的行是 .node-row，节点图的方框是 .gnode。
    // 之前只查 .node-row —— 于是拖动经过好几个方框之后，那些方框的高亮**再也不会消失**，
    // 屏幕上同时亮着四五个"落点"，看上去就像拖拽坏了。
    var marked = document.querySelectorAll
      ? document.querySelectorAll('.node-row.drop-child, .node-row.drop-sibling, '
        + '.gnode.drop-child, .gnode.drop-sibling')
      : [];
    for (var i = 0; i < marked.length; i++) {
      marked[i].classList.remove('drop-child', 'drop-sibling');
    }
  }

  /**
   * 落点语义：靠一行的上/下 1/4 是"插到它前面/后面"（当兄弟），中间一半是"作为子节点"。
   * 组合节点才挂得下子节点，挂不下就退化成"贴到它后面" —— 免得拖半天什么都不发生。
   *
   * 保留 `event` 参数只是为了能被自检直接喂一个 `{clientY}`（真实调用来自 mousemove）。
   */
  function dropModeFor(node, row, event) {
    var material = materialFor(node.type);
    var canChild = !!(material && material.allowsChildren);

    var rect = row.getBoundingClientRect();
    var ratio = rect.height > 0 ? (event.clientY - rect.top) / rect.height : 0.5;
    if (ratio <= 0.25) return 'before';
    if (ratio >= 0.75) return 'after';
    if (canChild) return 'child';

    // 不是组合节点：上/下半区当兄弟，正中间就干脆"贴到它后面"
    return 'after';
  }

  function applyDrop(targetNode, mode, payload) {
    if (!payload || !state.tree) return;

    if (payload.kind === 'node') {
      if (payload.id === targetNode.id) return;
      var dragged = findNode(payload.id);
      if (!dragged) { setStatus(L('st.043', '拖动的节点已不在树里')); return; }
      // 不许把节点拖进自己的子树（那会把树断开/成环）
      var inside = false;
      walk(dragged, function (candidate) { if (candidate.id === targetNode.id) inside = true; });
      if (inside) { setStatus(L('st.003', '不能把节点拖进它自己的子树')); return; }

      pushHistory();
      var from = findParent(dragged.id);
      if (from && from.children) {
        from.children = from.children.filter(function (child) { return child !== dragged; });
      }
      if (mode === 'child') {
        targetNode.children = targetNode.children || [];
        targetNode.children.push(dragged);
      } else {
        var parent = findParent(targetNode.id);
        var list = parent ? parent.children : [state.tree];
        var index = list.indexOf(targetNode);
        list.splice(mode === 'before' ? index : index + 1, 0, dragged);
      }
      state.selectedId = dragged.id;
      markDirty(true);
      renderTree();
      renderInspector();
      setStatus(I18n.format("st.c032", '已移动节点 {0}', dragged.id));
      return;
    }

    if (payload.kind === 'action') {
      var action = findAction(payload.file);
      useAction(action || { kind: 'action', file: payload.file, type: 'Task.PlayActionPackage' },
        targetNode);
      return;
    }

    if (payload.kind === 'node-material') {
      var material = materialFor(payload.type);
      if (!material) { setStatus(I18n.format("st.c033", '未知类型：{0}', payload.type)); return; }
      if (mode === 'child') {
        addChild(targetNode, payload.type);
        return;
      }
      var siblingParent = findParent(targetNode.id);
      if (!siblingParent) { addChild(targetNode, payload.type); return; }
      var inserted = addChild(siblingParent, payload.type);
      // addChild 会追加到末尾；这里再挪到落点位置
      siblingParent.children = siblingParent.children.filter(function (child) { return child !== inserted; });
      var at = siblingParent.children.indexOf(targetNode);
      siblingParent.children.splice(mode === 'before' ? at : at + 1, 0, inserted);
      state.selectedId = inserted.id;
      renderTree();
      renderInspector();
      setStatus(I18n.format("st.c034", '已插入 {0}', payload.type));
      return;
    }

    if (payload.kind === 'decorator' || payload.kind === 'service') {
      pushHistory();
      if (payload.kind === 'decorator') {
        targetNode.decorators = targetNode.decorators || [];
        var decorator = { id: nextId('d'), type: payload.type, properties: {} };
        var spec = state.materials.byType['decorator:' + payload.type];
        (spec ? spec.properties : []).forEach(function (p) {
          if (p.default !== null && p.default !== undefined)
            decorator.properties[p.name] = coerce(p, p.default);
        });
        targetNode.decorators.push(decorator);
      } else {
        targetNode.services = targetNode.services || [];
        var service = { id: nextId('svc'), type: payload.type, interval: 0.25, properties: {} };
        var serviceSpec = state.materials.byType['service:' + payload.type];
        (serviceSpec ? serviceSpec.properties : []).forEach(function (p) {
          if (p.default !== null && p.default !== undefined)
            service.properties[p.name] = coerce(p, p.default);
        });
        targetNode.services.push(service);
      }
      state.selectedId = targetNode.id;
      markDirty(true);
      renderTree();
      renderInspector();
      setStatus(I18n.format("st.c035", '已挂到 {0}：{1}', targetNode.id, payload.type));
    }
  }

  /**
   * 自由摆放的位置 / 节点组存在**浏览器本地**（localStorage，按包路径分键）。
   *
   * 为什么不写进包：包格式是游戏要读的（`tree.json` 的节点属性受 schema 校验），
   * 往里塞编辑器专用的坐标会污染行为树数据、还可能被游戏校验拒绝。
   * 位置属于"编辑器怎么看"，不属于"游戏怎么跑"，所以分开存。
   * 代价：换浏览器/换机器不会跟着走（点「自动布局」即可复位）。
   */
  var LAYOUT_KEY_PREFIX = 'playerAiEditor.layout.';

  function layoutKey() {
    return state.path ? LAYOUT_KEY_PREFIX + state.path : null;
  }

  function persistLayout() {
    var key = layoutKey();
    if (!key || typeof localStorage === 'undefined' || !localStorage) return false;
    try {
      localStorage.setItem(key, JSON.stringify({
        nodePos: state.nodePos, groups: state.groups, detached: state.detached
      }));
      return true;
    } catch (error) {
      return false;   // 存不下（隐私模式/配额满）也不该影响编辑
    }
  }

  function restoreLayout() {
    state.nodePos = {};
    state.groups = [];
    if (!state.detached) state.detached = [];
    state.detached = [];
    var key = layoutKey();
    if (!key || typeof localStorage === 'undefined' || !localStorage) return false;
    try {
      var raw = localStorage.getItem(key);
      if (!raw) return false;
      var data = JSON.parse(raw);
      if (data && data.nodePos) state.nodePos = data.nodePos;
      if (data && data.groups) state.groups = data.groups;
      if (data && data.detached) state.detached = data.detached;
      return true;
    } catch (error) {
      return false;
    }
  }

  /** 「自动布局」：丢掉手动位置和节点组，回到 tidy tree 的自动排布。 */
  function resetLayout() {
    var had = Object.keys(state.nodePos).length > 0 || state.groups.length > 0;
    state.nodePos = {};
    state.groups = [];
    persistLayout();
    renderTree();
    renderInspector();
    setStatus(had ? L('st.034', '已恢复自动布局（手动摆放和节点组都清掉了）') : L('st.t006', '本来就是自动布局'));
    return had;
  }

  // ---------------------------------------------------------------- 自定义拖拽（不用 HTML5 DnD）
  //
  // 为什么把 HTML5 拖拽换掉：它在**嵌入式 webview**（Electron/WebView2/某些内嵌浏览器）里
  // 常常根本不启动 dragstart，或者在容器有手势处理时被吞掉；而且它**没法被自检覆盖**
  // （无头环境里合成不出真实拖拽）。自检只能看代码字符串，于是"拖不动"这类问题反复出现。
  //
  // 现在改成纯 mousedown/mousemove/mouseup + `document.elementFromPoint`：
  //   · 所有浏览器/webview 行为一致（就是普通鼠标事件）；
  //   · 目标节点用 `[data-node-id]` 认（缩进树的行和节点图的方框都带这个属性）；
  //   · 位移超过 5px 才算拖拽，否则仍当作普通点击（选中）；
  //   · 落点效果仍走同一个 `applyDrop`（拖拽只是"怎么选到目标"，效果只有一处实现）；
  //   · 能被自检覆盖：桩里塞一个假的 `elementFromPoint` 就能把整条链跑通。

  const DRAG_THRESHOLD_PX = 5;
  var mouseDrag = null;

  /** 从一个屏幕坐标找它落在哪个节点上（沿父链找 `data-node-id`）。 */
  function nodeIdFromPoint(x, y, doc) {
    var target = doc || document;
    if (!target || typeof target.elementFromPoint !== 'function') return null;
    var element = target.elementFromPoint(x, y);
    while (element && element.getAttribute) {
      var id = element.getAttribute('data-node-id');
      if (id) return id;
      element = element.parentNode;
    }
    return null;
  }

  /** 超过阈值才算真的开始拖（阈值内松手 = 点击）。 */
  function dragMoved(startX, startY, x, y) {
    return Math.abs(x - startX) + Math.abs(y - startY) > DRAG_THRESHOLD_PX;
  }

  function beginMouseDrag(payload, event, sourceElement) {
    if (!payload || event.button !== 0) return;
    mouseDrag = { payload: payload, startX: event.clientX, startY: event.clientY,
      active: false, targetId: null, source: sourceElement || null };
  }

  /** 拖动中给拖动源加个半透明标记 —— 不然点了半天看不出"到底有没有在拖"。 */
  function markDragging(element, on) {
    if (!element || !element.classList) return;
    if (on) element.classList.add('dragging');
    else element.classList.remove('dragging');
  }

  function handleMouseDragMove(event) {
    // 平移（中键 / Shift+左键）优先：它自己管滚动
    if (panDrag) {
      // 中键在窗口外松开时收不到 mouseup，只能靠 buttons===0 认出来
      if (typeof event.buttons === 'number' && event.buttons === 0) { handlePanEnd(); return; }
      handlePanMove(event);
      return;
    }
    // 拉线优先：引脚上按下之后走的是连线这条路径，不是搬节点
    if (wireDrag) { handleWireDragMove(event); return; }
    if (groupDrag) { handleGroupDragMove(event); return; }
    if (marqueeDrag) { handleMarqueeMove(event); return; }
    if (!mouseDrag) return;
    // 在窗口外松手时收不到 mouseup（浏览器只把 mouseup 发给文档）——
    // 靠 buttons===0 认出"键已经放开了"，否则会卡在"一直在拖"的状态里
    if (typeof event.buttons === 'number' && event.buttons === 0) {
      handleMouseDragEnd(event);
      return;
    }
    if (!mouseDrag.active) {
      if (!dragMoved(mouseDrag.startX, mouseDrag.startY, event.clientX, event.clientY)) return;
      mouseDrag.active = true;
      markDragging(mouseDrag.source, true);
      // 从物料区拖出来的东西：跟一个 ghost（节点自己的拖动不需要，方框就跟着鼠标）
      if (mouseDrag.payload && mouseDrag.payload.kind !== 'node') {
        showDragGhost(mouseDrag.payload, event);
      }
      setStatus(mouseDrag.payload && mouseDrag.payload.kind === 'node' && state.view === 'graph'
            ? I18n.format("st.t007", '拖动中：松手即摆到这里（按住 Shift 落到节点上＝挂成父子关系）')
            : I18n.format("st.t008", '拖动中：放到目标节点上松手（能挂子节点的挂进去，任务节点则插到它后面）'));
    }
    if (event.preventDefault) event.preventDefault();

    // 节点图里的节点：默认**自由摆放**（UE 那种）。按住 Shift 则切换成"改父子关系"：
    // 这时节点不再跟着鼠标走（否则它会盖住落点，命中测试永远只认它自己），
    // 而是回到按下前的位置、只高亮落点目标。
    if (mouseDrag.payload && mouseDrag.payload.kind === 'node' && state.view === 'graph') {
      if (event.shiftKey) {
        if (mouseDrag.mode !== 'reparent') {
          mouseDrag.mode = 'reparent';
          // 把这次拖动过程中已经写进去的位置还原（Shift 是"我要改结构，不是要挪位置"）
          restoreDragPositions(mouseDrag);
        }
        var shiftHit = nodeIdFromPoint(event.clientX, event.clientY);
        if (shiftHit === mouseDrag.payload.id) shiftHit = null;   // 落在自己身上不算
        if (shiftHit !== mouseDrag.targetId) {
          mouseDrag.targetId = shiftHit;
          clearDropHighlight();
          if (shiftHit) highlightDropTarget(shiftHit, mouseDrag.payload);
        }
        setStatus(shiftHit
            ? I18n.format("st.t009", 'Shift 松手：{0} 会挂到 {1}（{2}）', mouseDrag.payload.id, shiftHit, dropHintFor(shiftHit, mouseDrag.payload))
            : I18n.format("st.t010", 'Shift：落到某个节点上就改父子关系（松开 Shift 则是自由摆放）'));
      } else {
        if (mouseDrag.mode === 'reparent') mouseDrag.mode = null;
        freeMoveNodes(event);
        clearDropHighlight();
      }
      return;
    }

    moveDragGhost(event);
    var hit = nodeIdFromPoint(event.clientX, event.clientY) || mouseDrag.targetId;
    // 注意 overEmpty 要"第一次也判"：目标一直是 null 时，单看 hit !== targetId 会一次都不进这段，
    // 于是"松手就在这里新建"的提示永远不会出现（真实反馈会比操作慢半拍）。
    var overEmptyChanged = (!hit) !== (!mouseDrag.targetId)
      || mouseDrag.overEmpty === undefined;
    mouseDrag.overEmpty = !hit;
    if (hit !== mouseDrag.targetId || overEmptyChanged) {
      mouseDrag.targetId = hit;
      clearDropHighlight();
      if (hit) {
        highlightNewDropZone(false);
        highlightDropTarget(hit, mouseDrag.payload);
        setStatus(I18n.format("st.c036", '拖动中：松手后落到 {0}（{1}）', hit, dropHintFor(hit, mouseDrag.payload)));
      } else if (state.view === 'graph' && isMaterialPayload(mouseDrag.payload)) {
        // 空白画布也是合法落点：松手就在这里新建（见 dropMaterialOnCanvas）
        highlightNewDropZone(true);
        setStatus(I18n.format("st.c037", '拖动中：松手就在画布这里新建 {0}（挂到当前选中的节点下）', (mouseDrag.payload.type || L('ui.149', '节点'))));
      } else {
        highlightNewDropZone(false);
      }
    }
  }

  /** 空白画布的"可以在这里新建"高亮。 */
  function highlightNewDropZone(on) {
    var canvas = document.querySelector('.graph-canvas');
    if (!canvas || !canvas.classList) return false;
    if (on) canvas.classList.add('drop-new');
    else canvas.classList.remove('drop-new');
    return !!on;
  }

  /** 放弃拖动（Esc / 拖到一半不要了）：什么都不改，只清状态。 */
  function cancelMouseDrag() {
    hideDragGhost();
    if (!mouseDrag) return false;
    var drag = mouseDrag;
    mouseDrag = null;
    clearDropHighlight();
    markDragging(drag.source, false);
    setStatus(L('st.028', '已取消拖动'));
    return true;
  }

  // ---------------------------------------------------------------- 拖动影像（ghost）
  //
  // 从物料区往画布拖的时候，鼠标底下什么都没有 —— 用户看不出"东西到底抓住没有"。
  // 这里跟一个半透明的小标签（UE / Shader Graph 拖节点预览的等价物）。
  // 节点自身的拖动不需要它（方框本来就跟着鼠标走）。

  var dragGhost = null;

  function payloadLabel(payload) {
    if (!payload) return '?';
    if (payload.kind === 'node') return payload.id || L('ui.149', '节点');
    if (payload.kind === 'action') return payload.file || L('ui.150', '动作包');
    return payload.type || payload.kind || L('ui.149', '节点');
  }

  function showDragGhost(payload, event) {
    if (!document.createElement) return null;
    if (!dragGhost) {
      dragGhost = document.createElement('div');
      dragGhost.className = 'drag-ghost';
      if (document.body && document.body.appendChild) document.body.appendChild(dragGhost);
    }
    dragGhost.textContent = payloadLabel(payload);
    moveDragGhost(event);
    return dragGhost;
  }

  function moveDragGhost(event) {
    if (!dragGhost || !event) return false;
    dragGhost.style.left = (event.clientX + 14) + 'px';
    dragGhost.style.top = (event.clientY + 12) + 'px';
    return true;
  }

  function hideDragGhost() {
    if (!dragGhost) return false;
    if (dragGhost.parentNode && dragGhost.parentNode.removeChild) {
      dragGhost.parentNode.removeChild(dragGhost);
    }
    dragGhost = null;
    return true;
  }

  /** 给状态栏说清楚"松手会发生什么"（挂进去还是插到后面）。 */
  function dropHintFor(nodeId, payload) {
    var node = findNode(nodeId);
    var material = node ? materialFor(node.type) : null;
    if (payload && payload.kind !== 'node') {
      return material && material.allowsChildren ? L('ui.151', '作为它的子节点') : L('ui.152', '插到它后面');
    }
    return material && material.allowsChildren ? L('ui.153', '挂进去当子节点') : L('ui.154', '插到它后面当兄弟');
  }

  function handleMouseDragEnd(event) {
    hideDragGhost();
    if (panDrag) return handlePanEnd(event);
    if (wireDrag) return handleWireDrop(event);
    if (groupDrag) return handleGroupDragEnd(event);
    if (marqueeDrag) return handleMarqueeEnd(event);
    var drag = mouseDrag;
    mouseDrag = null;
    if (!drag) return true;
    clearDropHighlight();
    markDragging(drag.source, false);
    if (!drag.active) return true;   // 没超过阈值 → 当作点击，交给 click 处理器

    // 节点图里的节点：位置在拖动过程中已经写进 state.nodePos，这里只负责收尾。
    // 按住 Shift 落到某个节点上才改成"挂父子关系"（拖动本身是自由摆放，跟 UE 一致）。
    if (drag.payload && drag.payload.kind === 'node' && state.view === 'graph') {
      var moved = drag.movedIds || [drag.payload.id];
      if (drag.mode === 'reparent') {
        var shiftTargetId = drag.targetId || nodeIdFromPoint(event.clientX, event.clientY);
        var shiftTarget = shiftTargetId ? findNode(shiftTargetId) : null;
        if (shiftTarget && shiftTarget.id !== drag.payload.id) {
          var material = materialFor(shiftTarget.type);
          applyDrop(shiftTarget, material && material.allowsChildren ? 'child' : 'sibling',
            drag.payload);
          persistLayout();
          return true;
        }
        setStatus(L('st.001', 'Shift 拖动没落到别的节点上 —— 位置也没改'));
        return true;
      }
      persistLayout();
      setStatus(I18n.format("st.c038", '已摆放 {0}（位置存在这个浏览器里；点「自动布局」可复位）', (moved.length > 1 ? moved.length + L('ui.280', ' 个节点') : moved[0])));
      return true;
    }

    var targetId = drag.targetId || nodeIdFromPoint(event.clientX, event.clientY);
    var target = targetId ? findNode(targetId) : null;

    // 从左侧物料区拖到**空白画布**上：就在落点新建一个节点（UE / Shader Graph 里也是这样）。
    // 行为树要求每个节点都有归宿，所以父节点取"当前选中的那个"，没选中就挂到根上。
    if (!target && state.view === 'graph' && isMaterialPayload(drag.payload)) {
      dropMaterialOnCanvas(drag.payload, event);
      return true;
    }

    if (!target) { setStatus(L('st.042', '拖动取消：没放到任何节点上')); return true; }

    var material = materialFor(target.type);
    applyDrop(target, material && material.allowsChildren ? 'child' : 'sibling', drag.payload);
    return true;
  }

  /** 这个载荷是不是"从物料区拖出来的新东西"（区别于已有的节点）。 */
  function isMaterialPayload(payload) {
    if (!payload) return false;
    return payload.kind === 'node-material' || payload.kind === 'decorator'
      || payload.kind === 'service';
  }

  /**
   * 把物料"落"在画布的空白处：新建节点 → 挂到当前选中节点（没选中就根节点）下 →
   * 并把它的位置钉在鼠标松开的那一点上。
   */
  function dropMaterialOnCanvas(payload, event) {
    if (!state.tree) { setStatus(L('st.008', '先打开一个包')); return false; }
    if (payload.kind !== 'node-material') {
      // 装饰器/服务：没有"游离"可言，挂到当前选中的节点上
      var host = state.selectedId ? findNode(state.selectedId) : state.tree;
      applyDrop(host || state.tree, 'child', payload);
      return true;
    }
    var point = canvasPointFromClient(event.clientX, event.clientY);
    var material = materialFor(payload.type);
    var properties = {};
    if (material) {
      material.properties.forEach(function (spec) {
        if (spec.default !== null && spec.default !== undefined)
          properties[spec.name] = coerce(spec, spec.default);
      });
    }
    // 用户要求：落在空白画布上就是**一个游离节点**，不自动挂到当前选中节点上
    var node = createDetachedNode(payload.type, properties);
    if (!node) return false;
    if (point) {
      // 不夹取：画布是无限画布，落在哪儿就是哪儿（含负坐标，画布会跟着扩边）
      state.nodePos[node.id] = { x: point.x - 40, y: point.y - 14 };
    }
    state.selectedId = node.id;
    persistLayout();
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c039", '已在画布上新建**游离节点** {0}（还没接进行为树：拖到别的节点上或用引脚连线才挂上；保存时不会写进包）', node.id));
    return true;
  }

  /** 高亮落点（缩进树的行 / 节点图的方框都带 data-node-id）。 */
  function highlightDropTarget(nodeId, payload) {
    var elements = document.querySelectorAll
      ? document.querySelectorAll('[data-node-id="' + nodeId + '"]') : [];
    // 高亮必须和"松手后真的会怎么落"用同一条规则（见 handleMouseDragEnd）：
    // 否则落在组合节点上时高亮显示"插到后面"，实际却挂成了子节点 —— 高亮在骗人。
    var node = findNode(nodeId);
    var material = node ? materialFor(node.type) : null;
    var asChild = !!(material && material.allowsChildren);
    for (var i = 0; i < elements.length; i++) {
      elements[i].classList.add(asChild ? 'drop-child' : 'drop-sibling');
    }
  }

  // ---------------------------------------------------------------- 框选（橡皮筋多选）

  var marqueeDrag = null;

  /** 空白处按下：开始框选（画布坐标系，跟缩放无关）。 */
  function beginMarquee(scroller, event) {
    var canvas = scroller && scroller.querySelector ? scroller.querySelector('.graph-canvas') : null;
    if (!canvas || !canvas.getBoundingClientRect) return false;
    var rect = canvas.getBoundingClientRect();
    var origin = contentOrigin();
    marqueeDrag = {
      canvas: canvas,
      startX: event.clientX - rect.left - origin.x,
      startY: event.clientY - rect.top - origin.y,
      additive: !!(event.ctrlKey || event.metaKey),
      box: null
    };
    if (event.preventDefault) event.preventDefault();
    return true;
  }

  /** 把矩形画出来（宽高都要正数，所以先归一下）。 */
  function marqueeRect(startX, startY, x, y) {
    return {
      x: Math.min(startX, x),
      y: Math.min(startY, y),
      w: Math.abs(x - startX),
      h: Math.abs(y - startY)
    };
  }

  /** 哪些节点被框住了（纯函数：矩形与方框相交即算选中）。 */
  function nodesInMarquee(rect, geometry) {
    if (!rect || !geometry) return [];
    return geometry.boxes.filter(function (box) {
      return box.x < rect.x + rect.w && box.x + box.w > rect.x
        && box.y < rect.y + rect.h && box.y + box.h > rect.y;
    }).map(function (box) { return box.id; });
  }

  function handleMarqueeMove(event) {
    if (!marqueeDrag) return false;
    var rect = marqueeDrag.canvas.getBoundingClientRect();
    var origin = contentOrigin();
    marqueeDrag.box = marqueeRect(marqueeDrag.startX, marqueeDrag.startY,
      event.clientX - rect.left - origin.x, event.clientY - rect.top - origin.y);
    if (!marqueeDrag.element) {
      var element = document.createElement('div');
      element.className = 'graph-marquee';
      // 放进内容层：框选矩形的坐标和方框是同一套（内容空间），不能挂在画布上
      var layer = marqueeDrag.canvas.querySelector
        ? marqueeDrag.canvas.querySelector('.graph-content') : null;
      (layer || marqueeDrag.canvas).appendChild(element);
      marqueeDrag.element = element;
    }
    marqueeDrag.element.style.left = marqueeDrag.box.x + 'px';
    marqueeDrag.element.style.top = marqueeDrag.box.y + 'px';
    marqueeDrag.element.style.width = marqueeDrag.box.w + 'px';
    marqueeDrag.element.style.height = marqueeDrag.box.h + 'px';
    if (event.preventDefault) event.preventDefault();
    return true;
  }

  function handleMarqueeEnd(event) {
    var drag = marqueeDrag;
    if (!drag) return false;
    marqueeDrag = null;
    if (drag.element && drag.element.parentNode && drag.element.parentNode.removeChild) {
      drag.element.parentNode.removeChild(drag.element);
    }
    if (!drag.box) return true;               // 只是点了一下空白 → 交给 click 处理
    // 用**当前缩放**下的几何判断相交（方框画在哪就按哪算）
    var geometry = graphGeometry(layoutTree(state.tree), state.graphScale || 1, state.nodePos);
    var hit = nodesInMarquee(drag.box, geometry);
    if (!hit.length) {
      if (!drag.additive) { state.selection = []; renderTree(); renderInspector(); }
      setStatus(I18n.format("st.c040", '框选里没有节点（{0}×{1}）', Math.round(drag.box.w), Math.round(drag.box.h)));
      return true;
    }
    var next = drag.additive ? state.selection.slice() : [];
    hit.forEach(function (id) { if (next.indexOf(id) < 0) next.push(id); });
    state.selection = next;
    state.selectedId = hit[hit.length - 1];
    state.anchorId = hit[0];
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c041", '框选了 {0} 个节点：{1}', hit.length, hit.join(L('ui.253', '、'))));
    return true;
  }

  function cancelMarquee() {
    if (!marqueeDrag) return false;
    var drag = marqueeDrag;
    marqueeDrag = null;
    if (drag.element && drag.element.parentNode && drag.element.parentNode.removeChild) {
      drag.element.parentNode.removeChild(drag.element);
    }
    return true;
  }

  // ---------------------------------------------------------------- 小地图
  //
  // 节点图一旦有几十个节点、又缩放过，找路全靠滚动条会很痛苦。小地图把"整张图 +
  // 当前视口在哪"画在一张小图上，点一下就跳过去（UE / Blender / Shader Graph 都有）。

  /** 一堆方框的包围盒（小地图取景用）。 */
  function boxesBounds(geometry, margin) {
    var boxes = (geometry && geometry.boxes) || [];
    var pad = margin === undefined ? 40 : margin;
    if (!boxes.length) return { x: 0, y: 0, w: 1, h: 1 };
    var minX = Infinity;
    var minY = Infinity;
    var maxX = -Infinity;
    var maxY = -Infinity;
    boxes.forEach(function (box) {
      minX = Math.min(minX, box.x);
      minY = Math.min(minY, box.y);
      maxX = Math.max(maxX, box.x + box.w);
      maxY = Math.max(maxY, box.y + box.h);
    });
    return { x: minX - pad, y: minY - pad, w: (maxX - minX) + pad * 2, h: (maxY - minY) + pad * 2 };
  }

  var minimapElement = null;
  var minimapFit = null;   // {scale, offsetX, offsetY}：画布坐标 → 小地图坐标

  function ensureMinimap() {
    var pane = document.getElementById('canvas');
    if (!pane || !document.createElementNS) return null;
    if (minimapElement && minimapElement.parentNode === pane) {
      // 每次进节点图都顺手把提示文案按当前语言刷新一遍：
      // 这个 title 只在**创建时**写过一次，语言切换时没人重画它（用户实测残留中文）。
      minimapElement.title = L("ui.070", '小地图：点/拖这里可以跳到图上任意位置（缩到看不清时尤其有用）');
      return minimapElement;
    }
    var element = document.createElement('div');
    element.id = 'minimap';
    element.title = L("ui.070", '小地图：点/拖这里可以跳到图上任意位置（缩到看不清时尤其有用）');
    element.addEventListener('mousedown', function (event) {
      if (event.stopPropagation) event.stopPropagation();
      if (event.preventDefault) event.preventDefault();
      minimapJumpTo(event);
      minimapPanning = true;
    });
    doc_addEventListener(document, 'mousemove', function (event) {
      if (minimapPanning) minimapJumpTo(event);
    });
    doc_addEventListener(document, 'mouseup', function () { minimapPanning = false; });
    pane.appendChild(element);
    minimapElement = element;
    return element;
  }

  var minimapPanning = false;

  /** 真的往 document 上挂监听（抽出来是为了在自检里能看出"挂了几个"）。 */
  function doc_addEventListener(target, type, handler) {
    if (target && target.addEventListener) target.addEventListener(type, handler);
  }

  /** 小地图上的点 → 画布坐标 → 把滚动区居中到那里。 */
  function minimapJumpTo(event) {
    if (!minimapFit || !minimapElement) return false;
    var rect = minimapElement.getBoundingClientRect();
    var localX = event.clientX - rect.left;
    var localY = event.clientY - rect.top;
    var contentX = (localX - minimapFit.offsetX) / minimapFit.scale;
    var contentY = (localY - minimapFit.offsetY) / minimapFit.scale;
    var scroller = document.querySelector('.graph-scroller');
    if (!scroller) return false;
    // 几何空间坐标 + 内容层偏移 = 滚动像素（几何坐标已经含缩放，别再乘）
    var px = contentX + (minimapFit.originX || 0);
    var py = contentY + (minimapFit.originY || 0);
    scroller.scrollLeft = px - (scroller.clientWidth || 0) / 2;
    scroller.scrollTop = py - (scroller.clientHeight || 0) / 2;
    updateMinimap();
    return true;
  }

  /**
   * 重画小地图（几何变化、滚动、缩放、窗口变化时都要叫它）。
   * 传入 geometry 可以省一次重算（拖动时每帧都会传）。
   */
  function updateMinimap(geometry) {
    var element = ensureMinimap();
    if (!element) return false;
    if (state.view !== 'graph' || !state.tree) {
      element.classList.add('hidden');
      return false;
    }
    element.classList.remove('hidden');
    var geo = geometry || graphGeometry(layoutTree(state.tree), state.graphScale || 1, state.nodePos);
    var width = element.clientWidth || 190;
    var height = element.clientHeight || 120;
    var margin = 4;
    // 取景按**节点包围盒**（画布现在带一大圈留白，按画布尺寸取景的话节点会缩成一小撮）
    var bounds = boxesBounds(geo, 40);
    var fit = Math.min((width - margin * 2) / Math.max(1, bounds.w),
      (height - margin * 2) / Math.max(1, bounds.h));
    minimapFit = {
      scale: fit,
      offsetX: margin + ((width - margin * 2) - bounds.w * fit) / 2 - bounds.x * fit,
      offsetY: margin + ((height - margin * 2) - bounds.h * fit) / 2 - bounds.y * fit,
      pad: geo.originPad || 0
    };
    var origin = contentOrigin();
    minimapFit.originX = origin.x;
    minimapFit.originY = origin.y;
    var namespace = 'http://www.w3.org/2000/svg';
    var parts = ['<svg xmlns="' + namespace + '" width="' + width + '" height="' + height + '">'];
    var selected = state.selection && state.selection.length ? state.selection : [state.selectedId];
    geo.boxes.forEach(function (box) {
      var cls = 'mm-box ' + (box.type === 'Root' ? 'root'
        : (materialFor(box.type) && materialFor(box.type).allowsChildren ? 'composite' : 'task'));
      if (selected && selected.indexOf(box.id) >= 0) cls += ' selected';
      else if (box.manual) cls += ' member';
      parts.push('<rect class="' + cls + '" x="' + (minimapFit.offsetX + box.x * fit)
        + '" y="' + (minimapFit.offsetY + box.y * fit)
        + '" width="' + Math.max(2, box.w * fit) + '" height="' + Math.max(2, box.h * fit)
        + '" rx="1"></rect>');
    });
    var scroller = document.querySelector('.graph-scroller');
    if (scroller) {
      // 几何空间坐标 = 滚动像素 - 内容层偏移（几何坐标本身就是缩放后的 CSS px）
      var sx = (scroller.scrollLeft || 0) - (minimapFit.originX || 0);
      var sy = (scroller.scrollTop || 0) - (minimapFit.originY || 0);
      var sw = (scroller.clientWidth || 0) / (state.graphScale || 1);
      var sh = (scroller.clientHeight || 0) / (state.graphScale || 1);
      parts.push('<rect class="mm-view" x="' + (minimapFit.offsetX + sx * fit)
        + '" y="' + (minimapFit.offsetY + sy * fit)
        + '" width="' + Math.max(4, sw * fit) + '" height="' + Math.max(4, sh * fit)
        + '" rx="1"></rect>');
    }
    parts.push('</svg>');
    element.innerHTML = parts.join('');
    return true;
  }

  // ---------------------------------------------------------------- 主题（深色 / 浅色）
  //
  // 主题就是 <html> 上有没有 `light` 这个类：两套调色板都在 app.css 里按 token 定义，
  // JS 只负责切类名 + 记住选择。这样"浅色"不需要在任何组件里写第二遍样式。
  // index.html 里还有一小段内联脚本在第一帧之前就把类加上（避免浅色用户看到一闪的深色）。

  var THEME_KEY = 'playerAiEditor.theme';

  /** ?theme=light / ?theme=dark 可以强制指定（截图、核对两种模式时用）。 */
  function themeFromQuery() {
    try {
      var match = /[?&]theme=(light|dark)\b/.exec(String(location.search || ''));
      return match ? match[1] : null;
    } catch (error) {
      return null;
    }
  }

  function themePreference() {
    var forced = themeFromQuery();
    if (forced) return forced;
    // 默认**浅色**（用户要求）；只有明确选过深色才用深色
    if (typeof localStorage === 'undefined' || !localStorage) return 'light';
    try {
      return localStorage.getItem(THEME_KEY) === 'dark' ? 'dark' : 'light';
    } catch (error) {
      return 'light';
    }
  }

  /** 真正切换主题（改 <html> 的类 + 按钮文案），返回当前主题名。 */
  function applyTheme(name) {
    state.theme = name === 'light' ? 'light' : 'dark';
    var root = document.documentElement;
    if (root && root.classList) {
      if (state.theme === 'light') root.classList.add('light');
      else root.classList.remove('light');
    }
    var button = document.getElementById('btnTheme');
    if (button) {
      button.textContent = state.theme === 'light'
              ? L("ui.028", '主题：浅色')
              : L("ui.029", '主题：深色');
      button.title = state.theme === 'light'
              ? L("ui.071", '当前是浅色模式，点一下切回深色（记在这个浏览器里）')
              : L("ui.072", '当前是深色模式，点一下切到浅色（记在这个浏览器里）');
    }
    return state.theme;
  }

  function persistTheme() {
    if (typeof localStorage === 'undefined' || !localStorage) return false;
    try {
      localStorage.setItem(THEME_KEY, state.theme);
      return true;
    } catch (error) {
      return false;
    }
  }

  /** 切换开关（按钮走这里）：切完立刻记住。 */
  function toggleTheme() {
    applyTheme(state.theme === 'light' ? 'dark' : 'light');
    persistTheme();
    setStatus(state.theme === 'light'
            ? I18n.format("st.t011", '已切到浅色模式')
            : I18n.format("st.t012", '已切到深色模式'));
    return state.theme;
  }

  // ---------------------------------------------------------------- 节点组 / 注释框
  //
  // UE 的 Comment Box、Blender 的 Frame 都是这个：一块带标题的底色区域，把相关节点圈在一起，
  // 拖它的标题栏整体移动组内节点。它**只影响摆放，不影响行为树结构** ——
  // 所以它跟位置一起存在浏览器本地，解散之后节点原地不动。

  var GROUP_COLORS = ['blue', 'purple', 'green', 'orange', 'gray'];
  var GROUP_PADDING = 26;     // 组边框内留白（节点不要贴着框线）
  var GROUP_TITLE_HEIGHT = 26;

  function nextGroupId() {
    var index = 1;
    var used = {};
    state.groups.forEach(function (group) { used[group.id] = true; });
    while (used['g' + index]) index++;
    return 'g' + index;
  }

  function findGroup(id) {
    if (!id) return null;
    for (var i = 0; i < state.groups.length; i++) {
      if (state.groups[i].id === id) return state.groups[i];
    }
    return null;
  }

  /** 组内成员（按 id 过滤掉已经不在树里的节点，避免残留成员把框撑坏）。 */
  function groupMembers(group) {
    if (!group) return [];
    return (group.members || []).filter(function (id) { return !!findNode(id); });
  }

  /** 包住这些节点的矩形（含标题栏高度与留白）。纯函数，便于自检。 */
  function groupRectFor(ids, geometry) {
    var boxes = (geometry && geometry.boxes ? geometry.boxes : []).filter(function (box) {
      return ids.indexOf(box.id) >= 0;
    });
    if (!boxes.length) return null;
    var minX = Infinity;
    var minY = Infinity;
    var maxX = -Infinity;
    var maxY = -Infinity;
    boxes.forEach(function (box) {
      minX = Math.min(minX, box.x);
      minY = Math.min(minY, box.y);
      maxX = Math.max(maxX, box.x + box.w);
      maxY = Math.max(maxY, box.y + box.h);
    });
    return {
      x: minX - GROUP_PADDING,
      y: minY - GROUP_PADDING - GROUP_TITLE_HEIGHT,
      w: (maxX - minX) + GROUP_PADDING * 2,
      h: (maxY - minY) + GROUP_PADDING * 2 + GROUP_TITLE_HEIGHT
    };
  }

  /**
   * 组的显示名：用户改过名就用名字，没改过就按**当前语言**现算「注释 N」。
   *
   * 为什么要现算：默认名以前在建组时就算好并**存进 localStorage**，
   * 于是中文下建的组切到英文之后，组头上一直挂着中文（整页扫描实测抓到的就是它）。
   */
  function groupTitle(group) {
    if (!group) return '';
    if (group.title) return group.title;
    var index = state.groups.indexOf(group);
    return L('ui.155', '注释 ') + (index >= 0 ? index + 1 : 1);
  }

  /** 用当前选中（或指定的）节点建一个组。 */
  function createGroupFromSelection(memberIds, title) {
    if (!state.tree) { setStatus(L('st.008', '先打开一个包')); return null; }
    var ids = (memberIds || state.selection || []).slice();
    if (!ids.length && state.selectedId) ids = [state.selectedId];
    ids = ids.filter(function (id) { return !!findNode(id); });
    if (!ids.length) { setStatus(L('st.015', '先选中节点（空白处拖框选，或 Shift 点选），再建组')); return null; }
    var geometry = graphGeometry(layoutTree(state.tree), 1, state.nodePos);
    var rect = groupRectFor(ids, geometry);
    if (!rect) { setStatus(L('st.076', '这些节点算不出范围，建组失败')); return null; }
    var group = {
      id: nextGroupId(),
      // 不给默认标题：默认名按当前语言**现算**（`groupTitle`），否则中文下建好、
      // 切到英文之后组头上会一直挂着「注释 1」—— 而且这个默认名会被存进 localStorage，
      // 存下来之后就再也翻不过去了。用户自己改过名才写进 title。
      title: title || null,
      color: GROUP_COLORS[state.groups.length % GROUP_COLORS.length],
      x: rect.x, y: rect.y, w: rect.w, h: rect.h,
      members: ids
    };
    state.groups.push(group);
    state.selectedGroupId = group.id;
    persistLayout();
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c042", '已建组「{0}」包住 {1} 个节点（拖组头整体移动、双击标题改名、Delete 解散）', groupTitle(group), ids.length));
    return group;
  }

  /** 把组的框重新贴回成员节点（加了节点、挪过节点之后用）。 */
  function fitGroupToMembers(group) {
    if (!group) return false;
    var geometry = graphGeometry(layoutTree(state.tree), 1, state.nodePos);
    var rect = groupRectFor(groupMembers(group), geometry);
    if (!rect) { setStatus(L('st.067', '组里没有节点了，可以直接解散')); return false; }
    group.x = rect.x;
    group.y = rect.y;
    group.w = rect.w;
    group.h = rect.h;
    persistLayout();
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c043", '已把「{0}」的框贴回组内节点', groupTitle(group)));
    return true;
  }

  function renameGroup(group, title) {
    if (!group) return false;
    var name = String(title === undefined || title === null ? '' : title).trim();
    if (!name) { setStatus(L('st.066', '组名不能是空的')); return false; }
    group.title = name;
    persistLayout();
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c044", '组名改成「{0}」', name));
    return true;
  }

  function setGroupColor(group, color) {
    if (!group || GROUP_COLORS.indexOf(color) < 0) return false;
    group.color = color;
    persistLayout();
    renderTree();
    return true;
  }

  /**
   * 整体移动组：组里**还没有手动位置**的节点要先按当前自动位置落一个基准，
   * 否则一移动它们就被自动布局拽回去了。
   */
  function moveGroupBy(group, dx, dy) {
    if (!group) return false;
    group.x += dx;
    group.y += dy;
    var geometry = graphGeometry(layoutTree(state.tree), 1, state.nodePos);
    groupMembers(group).forEach(function (id) {
      var box = null;
      for (var i = 0; i < geometry.boxes.length; i++) {
        if (geometry.boxes[i].id === id) box = geometry.boxes[i];
      }
      if (!box) return;
      state.nodePos[id] = { x: box.x + dx, y: box.y + dy };
    });
    return true;
  }

  function disbandGroup(group) {
    if (!group) return false;
    state.groups = state.groups.filter(function (item) { return item !== group; });
    if (state.selectedGroupId === group.id) state.selectedGroupId = null;
    persistLayout();
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c045", '已解散「{0}」（里面的节点保持原位，只是不再成组）', groupTitle(group)));
    return true;
  }

  function selectGroup(group) {
    state.selectedGroupId = group ? group.id : null;
    if (group) {
      state.selectedWire = null;
      setStatus(I18n.format("st.c046", '已选中注释框「{0}」：{1} 个节点（拖组头移动、双击标题改名、Delete 解散）', groupTitle(group), groupMembers(group).length));
      markGroupSelection();
      renderInspector();
      return true;
    }
    renderTree();
    renderInspector();
    return false;
  }

  /**
   * 只更新选中样式，**不重画画布**。
   * 拖动注释框时会先选中它：如果这里走 renderTree，鼠标下面的元素会被换掉，
   * 拖动就只能靠 document 上的监听勉强续命（而且要重新查元素）。直接改类名最省事也最稳。
   */
  function markGroupSelection() {
    var elements = document.querySelectorAll
      ? document.querySelectorAll('.graph-group[data-group-id]') : [];
    for (var i = 0; i < elements.length; i++) {
      var id = elements[i].getAttribute('data-group-id');
      if (id === state.selectedGroupId) elements[i].classList.add('selected');
      else elements[i].classList.remove('selected');
    }
    return true;
  }

  /** 某个节点属于哪些组（属性区显示用）。 */
  function groupsForNode(id) {
    return state.groups.filter(function (group) {
      return (group.members || []).indexOf(id) >= 0;
    });
  }

  // ---- 拖动/缩放组（走和节点拖动同一条 document 级鼠标链路）

  var groupDrag = null;

  function beginGroupDrag(group, event, mode) {
    if (!group || !state.tree) return false;
    var geometry = graphGeometry(layoutTree(state.tree), 1, state.nodePos);
    var startPos = {};
    groupMembers(group).forEach(function (id) {
      for (var i = 0; i < geometry.boxes.length; i++) {
        if (geometry.boxes[i].id === id) startPos[id] = { x: geometry.boxes[i].x, y: geometry.boxes[i].y };
      }
    });
    groupDrag = {
      group: group,
      mode: mode || 'move',
      startX: event.clientX,
      startY: event.clientY,
      startRect: { x: group.x, y: group.y, w: group.w, h: group.h },
      startPos: startPos,
      moved: false
    };
    // 选中但不重画画布（见 markGroupSelection 的说明）
    state.selectedGroupId = group.id;
    state.selectedWire = null;
    markGroupSelection();
    renderInspector();
    return true;
  }

  function handleGroupDragMove(event) {
    if (!groupDrag) return false;
    var scale = state.graphScale || 1;
    var dx = (event.clientX - groupDrag.startX) / scale;
    var dy = (event.clientY - groupDrag.startY) / scale;
    if (!groupDrag.moved && Math.abs(dx) + Math.abs(dy) < 1) return true;
    groupDrag.moved = true;
    var group = groupDrag.group;
    if (groupDrag.mode === 'resize') {
      group.w = Math.max(80, groupDrag.startRect.w + dx);
      group.h = Math.max(60, groupDrag.startRect.h + dy);
    } else {
      group.x = groupDrag.startRect.x + dx;
      group.y = groupDrag.startRect.y + dy;
      Object.keys(groupDrag.startPos).forEach(function (id) {
        state.nodePos[id] = { x: groupDrag.startPos[id].x + dx, y: groupDrag.startPos[id].y + dy };
      });
    }
    refreshGraphPositions();
    return true;
  }

  function handleGroupDragEnd() {
    var drag = groupDrag;
    groupDrag = null;
    if (!drag) return false;
    if (drag.moved) {
      persistLayout();
      setStatus(drag.mode === 'resize'
            ? I18n.format("st.t013", '注释框已调整大小')
            : I18n.format("st.t014", '已整体移动「{0}」（{1} 个节点）', groupTitle(drag.group), groupMembers(drag.group).length));
    }
    return true;
  }

  function cancelGroupDrag() {
    if (!groupDrag) return false;
    groupDrag = null;
    setStatus(L('st.031', '已取消组的拖动'));
    return true;
  }

  /** 把一个元素变成"按鼠标拖动"的拖动源（payload 决定放下后做什么）。 */  function makeDraggable(element, payload) {
    element.draggable = false; // 明确关掉 HTML5 拖拽：它和 mousemove 互斥，会互相打架
    element.addEventListener('mousedown', function (event) {
      // 挡住浏览器的默认行为（否则按住拖动会变成"选一段文字"，物料看着就像拖不动）
      if (event.preventDefault) event.preventDefault();
      beginMouseDrag(payload, event, element);
    });
  }

  // ---------------------------------------------------------------- 引脚连线（UE 式节点图的核心）
  //
  // 语义很直白：**从输出脚拖到某个节点上 = 那个节点成为本节点的子节点**；
  // 从**输入脚**拖到某个节点上 = 本节点改挂到那个节点下面。
  // 断线（选中连线按 Delete）不删除节点，而是把子节点**提升为父节点的兄弟** ——
  // 行为树里每个节点都必须有归宿，直接删节点太危险，提升是可逆的（Ctrl+Z）。

  /**
   * 连线的合法性（纯判断，便于自检）：返回 null 表示可以连，否则返回拒绝原因。
   * 这些规则和游戏里的形状校验是同一套（组合节点才能有子节点、不能成环）。
   */
  function connectBlocker(parentId, childId, tree) {
    var root = tree || state.tree;
    if (!root) return L('ui.156', '还没有打开任何包');
    if (!parentId || !childId) return L('ui.157', '连线的两端都没认出来');
    if (parentId === childId) return L('ui.158', '不能连到自己');
    if (childId === root.id) return L('ui.159', '根节点不能当别人的子节点');
    var parent = findNode(parentId);
    var child = findNode(childId);
    if (!parent || !child) return L('ui.160', '有一端已经不在树里了');
    var material = materialFor(parent.type);
    if (!material || !material.allowsChildren)
      return L('ui.161', '「') + parent.type + L('ui.162', '」是任务节点，挂不下子节点（先包一层 Sequence）');
    // 我是不是它的祖先？（把它挂到自己后代下面会成环，游戏会拒绝加载）
    var inside = false;
    walk(child, function (candidate) { if (candidate.id === parentId) inside = true; });
    if (inside) return L('ui.163', '不能把它挂到它自己的后代下面（会成环）');
    var currentParent = findParent(childId);
    if (currentParent && currentParent.id === parentId) return L('ui.164', '它已经是你的子节点了');
    return null;
  }

  /** 真正接线（父 ← 子）：从原父节点摘下来，追加到新父节点末尾，一步撤销。 */
  function connectNodes(parentId, childId) {
    var reason = connectBlocker(parentId, childId, state.tree);
    if (reason) { setStatus(I18n.format("st.c047", '接不上：{0}', reason)); return false; }
    var parent = findNode(parentId);
    var child = findNode(childId);
    pushHistory();
    // 游离节点一挂上就不再游离
    if (isDetached(childId)) {
      state.detached = state.detached.filter(function (item) { return item.id !== childId; });
      persistLayout();
    }
    var from = findParent(childId);
    if (from && from.children) {
      from.children = from.children.filter(function (item) { return item !== child; });
    }
    parent.children = parent.children || [];
    parent.children.push(child);
    state.selectedWire = null;
    afterNodeMove(child, I18n.format("ui.t05", '已连接：{0} → {1} 的第 {2} 个子节点',
      childId, parentId, parent.children.length));
    return true;
  }

  /** 选中一条连线（Delete 断开、Esc 取消选中）。 */
  function selectWire(wire) {
    if (!wire) return false;
    state.selectedWire = { from: wire.from, to: wire.to };
    setStatus(I18n.format("st.c048", '已选中连线 {0} → {1}（Delete 断开连线：子节点提升为父节点的兄弟；Esc 取消选中）', wire.from, wire.to));
    renderTree();
    return true;
  }

  /**
   * 断开一条连线：把子节点提升为**父节点的兄弟**（不删节点）。
   * 父节点就是根节点时无处可提 —— 那时直接说清楚，让用户用 Delete 删节点。
   */
  function disconnectWire(fromId, toId) {
    var parent = findNode(fromId);
    var child = findNode(toId);
    if (!parent || !child) { setStatus(L('st.078', '这条连线已经不在树里了')); return false; }
    if (parent === state.tree) {
      setStatus(I18n.format("st.c049", '「{0}」是根节点，断线后子节点无处可去；要删节点请选中它按 Delete', fromId));
      return false;
    }
    var grand = findParent(parent.id);
    var list = grand ? grand.children : [state.tree];
    pushHistory();
    parent.children = parent.children.filter(function (item) { return item !== child; });
    list.splice(list.indexOf(parent) + 1, 0, child);
    state.selectedWire = null;
    afterNodeMove(child, I18n.format("ui.t06", '已断开连线：{0} 提升为 {1} 的兄弟', toId, parent.id));
    return true;
  }

  /** 断开当前选中的连线（Delete 键走这里）。 */
  function disconnectSelectedWire() {
    if (!state.selectedWire) return false;
    return disconnectWire(state.selectedWire.from, state.selectedWire.to);
  }

  /**
   * 开始拉线：role='out' 表示从输出脚出发（目标节点成为我的子节点），
   * role='in' 表示从输入脚出发（我改挂到目标节点下面）。
   */
  function beginWireDrag(box, pin, role, event) {
    if (!state.tree) return;
    wireDrag = {
      anchorId: box.id,
      role: role,
      x1: pin.x,
      y1: pin.y,
      targetId: null,
      path: null,
      startX: event && event.clientX !== undefined ? event.clientX : 0,
      startY: event && event.clientY !== undefined ? event.clientY : 0
    };
    setStatus(role === 'out'
            ? I18n.format("st.t015", '拉线中：松手落在哪个节点上，它就成为「{0}」的子节点', box.id)
            : I18n.format("st.t016", '拉线中：松手落在哪个节点上，「{0}」就挂到它下面', box.id));
  }

  /** 拉线过程中的实时反馈：临时线跟着鼠标走，目标节点标成"可接/不可接"。 */
  function handleWireDragMove(event) {
    if (!wireDrag) return false;
    if (event.preventDefault) event.preventDefault();
    var hitId = nodeIdFromPoint(event.clientX, event.clientY);
    wireDrag.targetId = hitId;
    paintTempWire(event.clientX, event.clientY);
    highlightWireTarget(hitId);
    return true;
  }

  /** 把临时线画到鼠标位置（坐标系要换算成画布内部的坐标）。 */
  function paintTempWire(clientX, clientY) {
    if (!wireDrag) return;
    var canvas = document.querySelector('.graph-canvas');
    if (!canvas || !canvas.getBoundingClientRect) return;
    var rect = canvas.getBoundingClientRect();
    var x2 = clientX - rect.left;
    var y2 = clientY - rect.top;
    if (!wireDrag.path) {
      if (!wireLayer) return;
      var path = document.createElementNS
        ? document.createElementNS('http://www.w3.org/2000/svg', 'path') : null;
      if (!path) return;
      path.setAttribute('class', 'graph-wire temp');
      wireLayer.appendChild(path);
      wireDrag.path = path;
    }
    wireDrag.path.setAttribute('d', wirePath(wireDrag.x1, wireDrag.y1, x2, y2));
  }

  /** 目标高亮：能接的亮绿，不能接的亮红（并说明为什么，别让人猜）。 */
  function highlightWireTarget(nodeId) {
    clearWireTargetHighlight();
    if (!nodeId) return;
    var elements = document.querySelectorAll
      ? document.querySelectorAll('[data-node-id="' + nodeId + '"]') : [];
    var role = wireDrag ? wireDrag.role : 'out';
    var parentId = role === 'out' ? wireDrag.anchorId : nodeId;
    var childId = role === 'out' ? nodeId : wireDrag.anchorId;
    var reason = connectBlocker(parentId, childId, state.tree);
    for (var i = 0; i < elements.length; i++) {
      elements[i].classList.add(reason ? 'wire-invalid' : 'wire-target');
    }
    if (reason) setStatus(I18n.format("st.c050", '落在「{0}」上接不了：{1}', nodeId, reason));
    else setStatus(I18n.format("st.c051", '松手即可连接：{0} → {1}', parentId, childId));
  }

  function clearWireTargetHighlight() {
    var marked = document.querySelectorAll
      ? document.querySelectorAll('.wire-target, .wire-invalid') : [];
    for (var i = 0; i < marked.length; i++) {
      marked[i].classList.remove('wire-target', 'wire-invalid');
    }
  }

  /** 松开鼠标：按落点完成连接（落空 = 取消，什么都不改）。 */
  function handleWireDrop(event) {
    var drag = wireDrag;
    if (!drag) return false;
    wireDrag = null;
    clearWireTargetHighlight();
    if (drag.path && drag.path.parentNode && drag.path.parentNode.removeChild) {
      drag.path.parentNode.removeChild(drag.path);
    }
    var hitId = nodeIdFromPoint(event.clientX, event.clientY) || drag.targetId;
    if (!hitId) { setStatus(L('st.039', '拉线取消：没落到任何节点上')); renderTree(); return true; }
    var parentId = drag.role === 'out' ? drag.anchorId : hitId;
    var childId = drag.role === 'out' ? hitId : drag.anchorId;
    connectNodes(parentId, childId);
    return true;
  }

  /** 放弃拉线（Esc / 拖到一半不要了）。 */
  function cancelWireDrag() {
    if (!wireDrag) return false;
    var drag = wireDrag;
    wireDrag = null;
    clearWireTargetHighlight();
    if (drag.path && drag.path.parentNode && drag.path.parentNode.removeChild) {
      drag.path.parentNode.removeChild(drag.path);
    }
    setStatus(L('st.027', '已取消拉线'));
    return true;
  }

  // ---------------------------------------------------------------- 控件

  function button(label, handler) {
    var element = document.createElement('button');
    element.textContent = label;
    element.addEventListener('click', handler);
    return element;
  }

  function helperRow(label, onRemove) {
    var row = document.createElement('div');
    row.className = 'prop-row';
    var text = document.createElement('label');
    text.textContent = label;
    row.appendChild(text);
    row.appendChild(button(L('ui.165', '移除'), onRemove));
    return row;
  }

  function textField(label, value, onChange, inputId) {
    var row = document.createElement('div');
    row.className = 'prop-row';
    var text = document.createElement('label');
    text.textContent = label;
    var input = document.createElement('input');
    input.type = 'text';
    if (inputId) input.id = inputId;
    input.value = value || '';
    input.addEventListener('change', function () { onChange(input.value); });
    row.appendChild(text);
    row.appendChild(input);
    return row;
  }

  function numberField(label, value, onChange) {
    var row = document.createElement('div');
    row.className = 'prop-row';
    var text = document.createElement('label');
    text.textContent = label;
    var input = document.createElement('input');
    input.type = 'number';
    input.step = 'any';
    input.value = value === undefined || value === null ? '' : value;
    input.addEventListener('change', function () { onChange(input.value); });
    row.appendChild(text);
    row.appendChild(input);
    return row;
  }

  function enumField(label, value, allowed, onChange) {
    var row = document.createElement('div');
    row.className = 'prop-row';
    var text = document.createElement('label');
    text.textContent = label;
    var select = document.createElement('select');
    (allowed || []).forEach(function (option) {
      var item = document.createElement('option');
      item.value = option;
      item.textContent = option;
      if (option === value) item.selected = true;
      select.appendChild(item);
    });
    select.addEventListener('change', function () { onChange(select.value); });
    row.appendChild(text);
    row.appendChild(select);
    return row;
  }

  /** 按 schema 生成一个属性输入控件（写进 node.properties）。 */
  function propertyField(owner, spec, containerName) {
    var container = containerName || 'properties';
    if (!owner[container]) owner[container] = {};
    var kind = (spec.kind || '').toLowerCase();
    var current = owner[container][spec.name];
    var label = spec.name + (spec.required ? ' *' : '');
    var row;

    function commit(value) {
      owner[container][spec.name] = coerce(spec, value);
      markDirty(true);
    }

    if (kind === 'bool') {
      row = document.createElement('div');
      row.className = 'prop-row';
      var text = document.createElement('label');
      text.textContent = label;
      var input = document.createElement('input');
      input.type = 'checkbox';
      input.checked = current === true;
      input.addEventListener('change', function () { commit(input.checked); });
      row.appendChild(text);
      row.appendChild(input);
      return row;
    }

    if (kind === 'enum') {
      return labelEnum(label, current, spec.allowed || [], commit, spec);
    }

    if (kind === 'int' || kind === 'float') {
      row = numberField(label, current, commit);
      if (spec.description) row.title = spec.description;
      return row;
    }

    if (kind === 'stringlist') {
      // 动作包列表：勾选现成的包（也能手打文件名，见下面的脚注输入框）
      if (spec.name === 'packages' && state.actions.length) {
        return packagesField(owner, container, spec, current, commit);
      }
      row = textField(label + L('ui.166', '（逗号分隔）'), (current || []).join(','), function (value) {
        commit(value);
      });
      if (spec.description) row.title = spec.description;
      return row;
    }

    // 嵌套包引用：给下拉，别让人手打一个不存在的 id
    if (spec.name === 'package' && owner.type === 'Task.Subtree') {
      return packageRefField(label, owner, container, spec, current, commit);
    }

    row = textField(label, current === undefined || current === null ? '' : current, commit);
    if (spec.description) row.title = spec.description;
    return row;
  }

  /**
   * `Task.Subtree` 的 `package` 属性：给一个"从本包引用表里选"的下拉。
   *
   * `manifest.references` 才是游戏认的引用清单（`LoadedPackage.FindReference` 查的就是它），
   * 而人容易手打一个不存在的 id —— 于是选择器只列**已声明**的引用；当前值不在清单里时
   * 额外显示一行"(未声明)"，让人一眼看出这棵树装不上。
   * `#节点id` 那段仍然手填（它是被引用包内部的节点名，编辑器这里不猜）。
   */
  function packageRefField(label, owner, container, spec, current, commit) {
    var wrap = document.createElement('div');
    wrap.className = 'prop-row packages';
    var text = document.createElement('label');
    text.textContent = label;
    if (spec && spec.required) text.textContent += ' *';
    wrap.appendChild(text);

    var box = document.createElement('div');
    box.className = 'packages-box';

    var selected = parseSubtreeRef(current);
    var references = (state.manifest && state.manifest.references) || [];
    var select = document.createElement('select');
    var declared = references.map(function (reference) { return reference && reference.id; })
      .filter(function (id) { return !!id; });
    if (declared.length === 0 && !selected) {
      var none = document.createElement('option');
      none.value = '';
      none.textContent = L("ui.030", '(本包没声明任何引用)');
      select.appendChild(none);
    }
    declared.forEach(function (id) {
      var option = document.createElement('option');
      option.value = id;
      option.textContent = id;
      if (selected && selected.package === id) option.selected = true;
      select.appendChild(option);
    });
    if (selected && declared.indexOf(selected.package) < 0) {
      var undeclared = document.createElement('option');
      undeclared.value = selected.package;
      undeclared.textContent = I18n.format("ui.031", '{0}（未声明！游戏解析不到）', selected.package);
      undeclared.selected = true;
      select.appendChild(undeclared);
    }
    select.addEventListener('change', function () {
      var next = select.value + (selected && selected.node ? '#' + selected.node : '');
      commit(next);
    });
    box.appendChild(select);

    // 入口节点（`#节点id` 那段）：引用解析出来之后就能列出来选，不用手打。
    // 解析结果缓存在 state.subtreeEntries[引用id] 里 —— 同一次会话里同一个引用只问一次。
    var entries = selected ? state.subtreeEntries[selected.package] : null;
    if (selected) {
      var entrySelect = document.createElement('select');
      var freeOption = document.createElement('option');
      freeOption.value = '';
      freeOption.textContent = entries && entries.length
        ? L('ui.167', '（入口：') + (entries.entry || '?') + L('ui.168', '）')
        : (entries === undefined ? L('ui.169', '（正在读入口…）') : L('ui.170', '（读不到入口，可手填 #节点id）'));
      entrySelect.appendChild(freeOption);
      (entries && entries.list ? entries.list : []).forEach(function (item) {
        var option = document.createElement('option');
        option.value = item.id;
        option.textContent = item.id + L('ui.171', '（') + item.type + L('ui.168', '）');
        if (selected.node === item.id) option.selected = true;
        entrySelect.appendChild(option);
      });
      entrySelect.addEventListener('change', function () {
        if (!entrySelect.value) return;
        commit(selected.package + '#' + entrySelect.value);
      });
      box.appendChild(entrySelect);

      if (entries === undefined) ensureSubtreeEntries(selected.package);
    }

    var hint = document.createElement('span');
    hint.className = 'hint';
    hint.textContent = declared.length
              ? I18n.format("ui.033", '本包引用：{0}（要加引用得改 manifest.references）', declared.join(L('ui.253', '、')))
              : L("ui.034", '本包没声明任何引用 —— 在 manifest.references 里加一条，游戏才解析得到');
    box.appendChild(hint);

    wrap.appendChild(box);
    return wrap;
  }

  /**
   * `packages` 属性（Task.PlayActionPackage）：勾选现成的动作包，而不是让人手打文件名。
   * 不在目录里的名字也列出来（黄色"手动"），否则一保存就被悄悄清掉了。
   */
  function packagesField(owner, container, spec, current, commit) {
    var wrap = document.createElement('div');
    wrap.className = 'prop-row packages';
    var text = document.createElement('label');
    text.textContent = I18n.format("ui.035", '{0}{1}（可多选）', spec.name, (spec.required ? ' *' : ''));
    if (spec.description) wrap.title = spec.description;

    var box = document.createElement('div');
    box.className = 'packages-box';
    var selected = Array.isArray(current) ? current.slice() : (current ? [String(current)] : []);

    state.actions.forEach(function (action) {
      var item = document.createElement('label');
      item.className = 'package-item' + (action.replayable ? '' : ' dead')
        + (action.writable ? '' : ' readonly');
      var input = document.createElement('input');
      input.type = 'checkbox';
      input.checked = selected.indexOf(action.file) >= 0;
      input.addEventListener('change', function () {
        var at = selected.indexOf(action.file);
        if (input.checked && at < 0) selected.push(action.file);
        if (!input.checked && at >= 0) selected.splice(at, 1);
        commit(selected.slice());
      });
      item.appendChild(input);
      var span = document.createElement('span');
      span.textContent = action.file + (action.replayable
        ? L('ui.171', '（') + (action.duration || 0) + 's/' + (action.frames || 0) + L('ui.172', '帧）')
        : L('ui.173', '（仅结构：不能回放）'));
      item.appendChild(span);
      box.appendChild(item);
    });

    selected.forEach(function (name) {
      if (findAction(name)) return;
      var item = document.createElement('label');
      item.className = 'package-item manual';
      var span = document.createElement('span');
      span.textContent = I18n.format("ui.036", '{0}（不在动作包目录里）', name);
      item.appendChild(span);
      item.appendChild(button(L('ui.165', '移除'), function () {
        var at = selected.indexOf(name);
        if (at >= 0) selected.splice(at, 1);
        commit(selected.slice());
        renderInspector();
      }));
      box.appendChild(item);
    });

    wrap.appendChild(text);
    wrap.appendChild(box);
    return wrap;
  }

  function labelEnum(label, current, allowed, commit, spec) {
    var row = document.createElement('div');
    row.className = 'prop-row';
    var text = document.createElement('label');
    text.textContent = label + (spec && spec.required ? ' *' : '');
    var select = document.createElement('select');
    var options = allowed.slice();
    if (current !== undefined && current !== null && options.indexOf(String(current)) < 0)
      options.unshift(String(current));
    options.forEach(function (option) {
      var item = document.createElement('option');
      item.value = option;
      item.textContent = option;
      if (String(current) === option) item.selected = true;
      select.appendChild(item);
    });
    select.addEventListener('change', function () { commit(select.value); });
    row.appendChild(text);
    row.appendChild(select);
    return row;
  }

  // ---------------------------------------------------------------- 物料区

  function renderPalette(filter) {
    var box = $('paletteList');
    box.innerHTML = '';
    if (!state.materials) return;

    paletteGroups().forEach(function (group) {
      var items = group.items.filter(function (item) {
        if (!filter) return true;
        var haystack = (item.type + ' ' + (item.file || '') + ' ' + (item.id || '')).toLowerCase();
        return haystack.indexOf(filter) >= 0;
      });
      if (!items.length) return;

      var wrap = document.createElement('div');
      wrap.className = 'palette-group';
      var title = document.createElement('h3');
      title.textContent = paletteGroupLabel(group) + L('ui.171', '（') + items.length + L('ui.168', '）');
      wrap.appendChild(title);

      items.forEach(function (item) {
        var element = paletteMaterialButton(item, function () {
          if (item.kind === 'action') { useAction(item); return; }
          var selected = state.selectedId ? findNode(state.selectedId) : null;
          if (item.kind === 'decorator') {
            if (!selected) { setStatus(L('st.014', '先选中一个节点，再挂装饰器')); return; }
            pushHistory();
            selected.decorators = selected.decorators || [];
            selected.decorators.push({ id: nextId('d'), type: item.type, properties: {} });
            markDirty(true);
            renderTree();
            renderInspector();
            return;
          }
          if (item.kind === 'service') {
            if (!selected) { setStatus(L('st.013', '先选中一个节点，再挂服务')); return; }
            pushHistory();
            selected.services = selected.services || [];
            selected.services.push({ id: nextId('svc'), type: item.type, interval: 0.25, properties: {} });
            markDirty(true);
            renderTree();
            renderInspector();
            return;
          }
          if (item.kind === 'ui-target') {
            pickUiTargetIntoTree();
            return;
          }
          if (!state.tree) { setStatus(L('st.008', '先打开一个包')); return; }
          var target = selected;
          if (!target) target = state.tree;
          var material = materialFor(target.type);
          if (!material || !material.allowsChildren) {
            setStatus(I18n.format("st.c052", '「{0}」不能有子节点；先选一个组合节点', target.type));
            return;
          }
          addChild(target, item.type);
        });
        element.className = 'palette-item' + (item.kind === 'action' ? ' action'
          : (item.kind === 'node' ? '' : ' helper'))
          + (item.kind === 'ui-target' ? ' ui-target' : '');
        // 显示名是翻译、会随语言变；**原始类型**单独放在 data-type 上：
        // 自检与脚本按它取物料，就不会被翻译/语言切换影响。
        if (item.type) element.setAttribute('data-type', item.type);
        element.setAttribute('data-kind', item.kind);
        if (item.file) element.setAttribute('data-file', item.file);
        element.title = item.kind === 'action'
          ? (L('ui.174', '动作包：') + item.file + (item.replayable ? '' : L('ui.175', '（仅结构，不能回放）'))
            + '\n' + L('palette.clickAction', '右键：编辑 / 删除 / 重命名'))
          : (item.kind === 'ui-target'
            ? L('palette.uiPick.title', '从游戏里现取一个界面目标')
            : (item.type + L('ui.171', '（') + nodeLabel(item.type) + L('ui.168', '）') + L('ui.176', '　—— 也可以直接拖到画布上')));
        // 物料也能拖：拖到哪个节点就用哪个节点（比"先选中再点"少一次点击）
        makeDraggable(element, item.kind === 'action'
          ? { kind: 'action', file: item.file }
          : { kind: item.kind === 'node' || item.kind === 'ui-target' ? 'node-material' : item.kind,
              type: item.type });
        if (item.kind === 'action' && item.file) {
          element.addEventListener('contextmenu', function (event) {
            showActionMenu(event, item.file);
          });
        }
        wrap.appendChild(element);
      });
      box.appendChild(wrap);
    });
  }

  function actionPaletteLabel(item) {
    return item.file + (item.replayable ? '' : L('ui.177', '  ⚠不可回放'));
  }

  /**
   * 用一个动作包：默认看"当前选中的是什么"，拖拽时可以指定落点节点。
   *   · 落点是（或选中了）`Task.PlayActionPackage` → 加进/移出它的 `packages` 列表；
   *   · 落点能挂子节点 → 挂一个播放节点出来；
   *   · 什么都没给 → 挂到根节点下。
   */
  function useAction(item, dropTarget) {
    if (!state.tree) { setStatus(L('st.008', '先打开一个包')); return; }
    if (!item) { setStatus(L('st.075', '这个动作包已经不在目录里了')); return; }
    var selected = dropTarget || (state.selectedId ? findNode(state.selectedId) : null);

    if (selected && selected.type === 'Task.PlayActionPackage') {
      pushHistory();
      selected.properties = selected.properties || {};
      var list = selected.properties.packages;
      if (!Array.isArray(list)) list = list ? [String(list)] : [];
      var at = list.indexOf(item.file);
      if (at >= 0) list.splice(at, 1); else list.push(item.file);
      if (list.length) selected.properties.packages = list;
      else delete selected.properties.packages;
      markDirty(true);
      renderTree();
      renderInspector();
      setStatus(at >= 0
            ? I18n.format("st.t030", '从播放列表移除 {0}', item.file)
            : I18n.format("st.t031", '加入播放列表 {0}', item.file));
      return;
    }

    var target = selected || state.tree;
    var material = materialFor(target.type);
    if (!material || !material.allowsChildren) {
      setStatus(I18n.format("st.c053", '「{0}」不能有子节点；先选一个组合节点，或选中一个 Task.PlayActionPackage', target.type));
      return;
    }
    var created = addChild(target, 'Task.PlayActionPackage');
    if (created) {
      created.properties = created.properties || {};
      created.properties.packages = [item.file];
      markDirty(true);
      renderInspector();
    }
    setStatus(I18n.format("st.c054", '已挂上播放节点：{0}', item.file));
  }

  // ---------------------------------------------------------------- 动作

  function loadMeta() {
    return api('/api/meta').then(function (meta) {
      state.meta = meta;                    // 缓存：语言切换时要照着重画下面两处文字
      state.gameRunning = !!meta.gameRunning;
      state.instanceRoot = meta.writableFolder || meta.instanceRoot || null;
      state.format = meta.format || {};
      renderMeta();
    });
  }

  /**
   * 画"游戏徽标 + 实例根/包目录"这两处（纯渲染，不发请求）。
   *
   * 为什么要拆出来：它们原先只在 `loadMeta()` 里写一次，而 `loadMeta()` 只在启动时跑 ——
   * 用户在界面里切到英文之后，底部那行仍然是「实例根：…　包目录：…」（实测反馈）。
   * 现在 `applyLanguage()` 会调它一次，用缓存的 meta 重画。
   */
  function renderMeta() {
    var meta = state.meta;
    if (!meta) return;
    var badge = $('gameBadge');
    if (badge) {
      badge.textContent = state.gameRunning
        ? I18n.format("ui.037", '游戏：在线{0}', (meta.gameInfo && meta.gameInfo.instanceId ? L('ui.171', '（') + meta.gameInfo.instanceId + L('ui.168', '）') : ''))
        : L("ui.038", '游戏：未运行');
      badge.className = 'badge' + (state.gameRunning ? ' live' : '');
    }
    var info = $('rootInfo');
    if (info) {
      info.textContent = I18n.format("ui.039", '实例根：{0}　包目录：{1}', meta.instanceRoot, meta.folders);
      // 机器可读的实例根单独放 data-* 上：saveAs 以前是对着上面这行**界面文字**
      // 正则抠 `实例根：`，切到英文就抠不到了（会存到相对目录里去）。
      info.setAttribute('data-instance-root', meta.instanceRoot || '');
    }
  }

  /**
   * 新建一棵空白树：`Root → Sequence → Task.Wait`。
   *
   * 为什么是这三个节点而不是"光一个 Root"：校验器要求**组合节点至少有一个子节点**
   * （`PackageValidator.cs` 的 `ShapeChildren`），空 Sequence 会被判错；
   * 而 `Task.Wait` 的属性 `seconds` 有默认值、不填也能过校验 —— 于是这棵最小骨架
   * 开箱即合法，用户在上面直接加东西就行。
   */
  function newTree() {
    if (!state.instanceRoot) { setStatus(L('st.070', '还不知道包目录，稍后再试')); return; }
    var name = prompt(L('ui.178', '新行为树的名字（字母/数字/中文都可以，会写进包目录）'), 'my_tree');
    if (!name) return;

    var id = String(name).replace(/[^A-Za-z0-9._-]/g, '_').replace(/^[._]+|[._]+$/g, '');
    if (!id) id = 'tree_' + Date.now();

    var manifest = {
      format: 'scbt',
      version: (state.format && state.format.scbt) || 1,
      id: id,
      name: name,
      entry: 'root',
      blackboard: []
    };
    var tree = {
      id: 'root',
      type: 'Root',
      children: [{
        id: 'seq',
        type: 'Sequence',
        children: [{ id: 'wait', type: 'Task.Wait', properties: { seconds: 1 } }]
      }]
    };

    var target = state.instanceRoot + '/PlayerAi/BehaviorTrees/' + name.replace(/[\\/:*?"<>|]/g, '_')
      + '.scbtpak';

    setStatus(L('st.045', '新建中…'));
    fetch('/api/package?path=' + encodeURIComponent(target), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manifest: manifest, tree: tree, writable: true })
    }).then(function (r) { return r.json(); }).then(function (data) {
      if (!data.ok) {
        setIssues((data.issues || []).concat(data.reason ? ['ERROR ' + data.reason] : []));
        setStatus(I18n.format("st.c055", '新建失败：{0}', (data.reason || L('ui.279', '见下方问题'))));
        return;
      }
      setStatus(I18n.format("st.c056", '已新建 {0}（{1} 节点），可以直接开始编', data.path, (data.nodes || 0)));
      return loadPackages().then(function () { return openPackage(data.path); });
    });
  }

  function loadSchema() {
    return api('/api/schema').then(function (schema) {
      state.schema = schema;
      state.materials = PlayerAiLowcode.toMaterials(schema);
      renderPalette($('paletteFilter').value.toLowerCase());
    });
  }

  /** quiet=true 时不写状态栏（保存后的"已保存…"提示不该被这次刷新顶掉）。 */
  function loadPackages(quiet) {
    return api('/api/packages').then(function (data) {
      state.packages = data.packages || [];
      renderPackageOptions();
      if (!quiet) setStatus(I18n.format("st.c057", '包 {0} 个', state.packages.length));
    });
  }

  /**
   * 画"打开包"下拉的每一行（纯渲染）。
   *
   * 为什么要拆出来：选项文字里带节点数（`{2}节点`），而它原先只在 `loadPackages()`
   * 里拼一次 —— 切到英文之后那一行仍然写着「common.scbtpak [common] 5节点」（用户实测反馈）。
   */
  function renderPackageOptions() {
    var select = $('packageSelect');
    if (!select) return;
    select.innerHTML = '';
    (state.packages || []).forEach(function (item) {
      var option = document.createElement('option');
      option.value = item.path;
      option.textContent = I18n.format("ui.040", '{0}{1}  {2}节点', item.file, (item.id ? '  [' + item.id + ']' : ''), (item.nodes || 0));
      select.appendChild(option);
    });
    if (state.path) select.value = state.path;
  }

  function openPackage(path) {
    if (!path) return Promise.resolve();
    return api('/api/package?path=' + encodeURIComponent(path)).then(function (data) {
      if (!data || data.ok === false && !data.manifest) {
        setIssues([data && data.reason ? 'ERROR ' + data.reason : L('ui.179', 'ERROR 打不开')]);
        return;
      }
      state.path = data.path;
      state.file = data.file || null;   // 播放按钮要用它判断"游戏跑的是不是这棵树"
      state.manifest = data.manifest;
      state.tree = data.tree;
      state.writable = !!data.writable;
      state.selectedId = state.tree ? state.tree.id : null;
      markDirty(false);
      resetHistory(); // 换了一份文档：撤销栈必须从零开始，否则会撤回到"上一棵树"
      state.folded = {}; // 展开的嵌套包视图也一起清掉（它属于上一份文档）
      restoreLayout();   // 手动摆的位置/节点组跟包走（存在浏览器本地，按路径分键）
      setIssues((data.issues || []).filter(function (line) { return /^ERROR|^WARN/.test(line); }));
      renderTree();
      renderInspector();
      setStatus(I18n.format("st.c058", '已打开 {0}（包目录：PlayerAi\BehaviorTrees，可以直接改并保存）', data.file));
      $('btnSave').disabled = !data.writable;
      refreshGameStatusQuiet();   // 换了包 → "跑的是不是这棵树"要重新算
    });
  }

  function validateNow() {
    if (!state.tree) return;
    setStatus(L('st.047', '校验中…'));
    fetch('/api/validate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manifest: state.manifest, tree: state.tree })
    }).then(function (r) { return r.json(); }).then(function (data) {
      setIssues((data.issues || []).filter(function (line) { return /^ERROR|^WARN/.test(line); }));
      setStatus(data.ok
            ? I18n.format("st.t017", '校验通过')
            : I18n.format("st.t018", '校验失败：{0} 个错误', data.errors));
    });
  }

  /**
   * 保存（原子写）。**返回一个 promise，成功 true / 失败 false** ——
   * "播放这棵树"必须先确认保存成功才敢让游戏切过去（拿一棵没保存的树去切是骗人）。
   */
  function saveNow(forcePath) {
    if (!state.tree) return Promise.resolve(false);
    var path = forcePath || state.path;
    if (!path) { saveAs(); return Promise.resolve(false); }
    setStatus(L('st.004', '保存中…'));
    return fetch('/api/package?path=' + encodeURIComponent(path) + '&overwrite=true', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manifest: state.manifest, tree: state.tree, overwrite: true })
    }).then(function (r) { return r.json(); }).then(function (data) {
      if (!data.ok) {
        setIssues((data.issues || []).concat(data.reason ? ['ERROR ' + data.reason] : []));
        setStatus(I18n.format("st.c059", '保存失败：{0}', (data.reason || L('ui.279', '见下方问题'))));
        return false;
      }
      markDirty(false);
      state.path = data.path;
      state.file = data.file || state.file;
      setStatus(I18n.format("st.c060", '已保存 {0}（{1} 字节，{2} 节点）', data.path, data.bytes, (data.nodes || 0)));
      loadPackages(true);   // 安静刷新列表，别把上面的保存结果顶掉
      updateTreeRunUi();    // 脏标记没了 → 按钮从"推送改动"变回"暂停"
      return true;
    }).catch(function (error) {
      setStatus(I18n.format("st.c059", '保存失败：{0}', error.message));
      return false;
    });
  }

  function saveAs() {
    var suggestion = (state.manifest && state.manifest.id ? state.manifest.id : 'my_tree') + '_copy';
    var name = prompt(L('ui.180', '另存为（写进包目录的新文件，不动原包）'), suggestion);
    if (!name) return;
    var clean = name.replace(/[^A-Za-z0-9._-]/g, '_');
    if (!/\.scbtpak$/.test(clean)) clean += '.scbtpak';
    state.manifest.id = clean.replace(/\.scbtpak$/, '');
    var folder = $('rootInfo').getAttribute('data-instance-root')
      || ($('rootInfo').textContent.match(/实例根：([^\s　]+)/) || [])[1];
    var target = (folder || '.') + '/PlayerAi/BehaviorTrees/' + clean;
    saveNow(target);
  }

  /**
   * 只推送热重载（`/api/notify`）：游戏里跑的就是这棵树、只想让改动生效时走这条。
   * 工具栏上**没有单独按钮**了 —— 「▶ 播放这棵树」在"正在跑这棵树且有改动"时
   * 就是它（`pushChangesToRunningTree`）；留着两个入口只会让人分不清该点哪个。
   */
  function notifyGame(path) {
    var target = path || state.path;
    if (!target) return;
    setStatus(L('st.080', '通知游戏热重载…'));
    return api('/api/notify?path=' + encodeURIComponent(target), { method: 'POST' })
      .then(function (data) {
        if (data.ok) setStatus(I18n.format("st.c061", '已通知游戏：{0}', JSON.stringify(data.game)));
        else setStatus(I18n.format("st.c062", '通知失败：{0}', (data.reason || '?')));
        return data;
      });
  }

  function showGameStatus() {
    api('/api/game/status').then(function (data) {
      if (!data.ok) { setStatus(I18n.format("st.c063", '游戏没在跑：{0}', data.reason)); return; }
      var status = data.status || {};
      var tree = status.tree || {};
      setStatus(I18n.format("st.c064", '游戏内：mode={0} 树={1} 节点={2} 脏={3} 活动路径={4}', status.mode, (tree.id || '-'), (tree.liveNodes || 0), (tree.dirty ? L('ui.242', '是') : L('ui.243', '否')), (tree.activePath || '-')));
    });
  }

  // ---------------------------------------------------------------- 启动 / 结束游戏

  /**
   * 三态徽标：**没启动 / 启动了但控制通道还没开 / 已连上**。
   *
   * 为什么要分三态：游戏从"进程起来"到"CmdBridgeMod 写好 runtime 文件"之间有几十秒
   * （加载世界），这段时间所有命令都必然是"连不上"，以前只显示一句"游戏没在跑"，
   * 看着像点错了。现在把"在启动、通道还没开"和"根本没起"分开说。
   */
  var gameWait = { timer: null, startedAt: 0, limitMs: 180000 };

  function refreshGameProcess() {
    return api('/api/game/process').then(function (data) {
      var badge = $('gameBadge');
      state.gameProcess = data;
      var label;
      if (data.channelConnected) {
        label = L("ui.077", '游戏：已连上');
        badge.className = 'badge live';
      } else if (data.running) {
        label = L("ui.078", '游戏：启动中（等控制通道）');
        badge.className = 'badge warn';
      } else {
        label = data.exeExists
              ? L("ui.079", '游戏：未启动')
              : L("ui.080", '游戏：找不到 Survivalcraft.exe');
        badge.className = 'badge' + (data.exeExists ? '' : ' warn');
      }
      badge.textContent = label + (data.pid ? '#' + data.pid : '');
      $('btnLaunchGame').disabled = !data.exeExists || !!data.running;
      $('btnQuitGame').disabled = !data.running;
      return data;
    });
  }

  function stopGameWait() {
    if (gameWait.timer) {
      window.clearInterval(gameWait.timer);
      gameWait.timer = null;
    }
  }

  /** 启动后轮询：等到控制通道能连上（或等够 3 分钟）为止。 */
  function waitForGameChannel() {
    stopGameWait();
    gameWait.startedAt = Date.now();
    gameWait.timer = window.setInterval(function () {
      var waited = Math.round((Date.now() - gameWait.startedAt) / 1000);
      refreshGameProcess().then(function (data) {
        if (data.channelConnected) {
          stopGameWait();
          setStatus(I18n.format("st.c065", '游戏已连上控制通道（等了 {0}s）—— 现在可以用「游戏状态 / 实时监视 / 试跑 / 推送热重载」', waited));
          loadMeta();
          // ⚠️ 必须**同时**刷新树状态：`state.gameStatus` 才是"▶ 播放"按钮和树徽标的依据，
          //    `loadMeta()` 只更新游戏徽标/实例根。少了这一句就会出现用户报的那个现象：
          //    页面是在游戏没跑的时候打开的 → 缓存里是 game_unreachable → 游戏起来之后
          //    播放按钮一直是灰的、徽标一直写"树：游戏没在跑"（只有手动点「游戏状态」才会恢复）。
          refreshGameStatusQuiet();
          return;
        }
        if (!data.running) {
          stopGameWait();
          setStatus(I18n.format("st.c066", '游戏进程没了（启动失败或提前退出）；看看 {0}', (data.exePath || 'Survivalcraft.exe')));
          return;
        }
        if (Date.now() - gameWait.startedAt > gameWait.limitMs) {
          stopGameWait();
          setStatus(I18n.format("st.c067", '等了 {0}s 还没连上控制通道：{1}', waited, (data.channelError || L('ui.278', '通道未就绪'))));
          return;
        }
        setStatus(I18n.format("st.c068", '游戏在启动，等控制通道…（已等 {0}s，加载世界期间通道是关着的）', waited));
      });
    }, 1000);
  }

  function launchGame() {
    setStatus(L('st.054', '正在启动游戏…'));
    api('/api/game/launch', { method: 'POST' }).then(function (data) {
      if (!data.ok) {
        setStatus(I18n.format("st.c069", '启动失败：{0}', (data.reason || data.code || '?')));
        refreshGameProcess();
        return;
      }
      setStatus(I18n.format("st.c070", '游戏已启动（pid {0}）：{1}', (data.pid || '?'), (data.hint || '')));
      waitForGameChannel();
    });
  }

  function quitGame() {
    if (!window.confirm(L('ui.181', '结束游戏？会先请它正常退出（释放 AI 输入、flush 日志），超时才强杀。'))) return;
    setStatus(L('st.055', '正在结束游戏…'));
    stopGameWait();
    api('/api/game/quit', { method: 'POST' }).then(function (data) {
      setStatus(data.ok
            ? I18n.format("st.t019", '游戏已结束（正常退出 {0} 个，强杀 {1} 个）', (data.closed || 0), (data.killed || 0))
            : I18n.format("st.t020", '结束游戏失败：{0}', (data.reason || '?')));
      refreshGameProcess();
    });
  }

  // ---------------------------------------------------------------- 行为树：播放 / 暂停
  //
  // 用户的原话："当前不知道行为树包是否在跑，应该需要一个播放按钮，点击可以播放或切换暂停按钮。
  // 播放的时候也就是推送热重载到游戏中。"
  //
  // 所以这里的模型是**一个按钮 + 一个状态徽标**，按钮的语义由"游戏里现在跑的是什么"决定：
  //
  //   游戏没接管 / 跑的是别的包   → 「▶ 播放这棵树」= 保存(若脏) → ai.tree.switch（切过去开始跑）
  //   正在跑的就是这棵树（干净）  → 「⏸ 暂停」    = ai.pause（保留运行态、释放输入）
  //   正在跑的就是这棵树（有改动）→ 「⟳ 推送改动」= 保存 → ai.tree.notify（热重载，不重启世界）
  //   跑的是这棵树但已暂停        → 「▶ 继续」    = ai.resume
  //
  // 为什么"播放"必须能切换而不只是 notify：`ai.tree.notify` 只对**活动树**生效
  // （游戏按 path+hash 匹配），如果游戏里跑的是 demo.greet、编辑器打开的是别的包，
  // notify 只会重载 demo.greet —— 用户看到的就是"点了没用"。

  var treeRun = { busy: false };

  /** 徽标里只写文件名：游戏侧的 `tree.source` 是绝对路径，整条塞进徽标会撑爆工具条。 */
  function shortFile(path) {
    if (!path) return '?';
    var text = String(path);
    var slash = Math.max(text.lastIndexOf('/'), text.lastIndexOf('\\'));
    return slash >= 0 ? text.slice(slash + 1) : text;
  }

  /** 游戏状态速览：跑的是哪棵树、暂停没暂停、接管没接管、通道通不通。 */
  function treeRunState() {
    if (state.gameStatus) {
      var status = state.gameStatus;
      var host = status.host || {};
      var tree = status.tree || {};
      var file = tree.source ? String(tree.source) : null;
      var name = file ? shortFile(file) : null;
      // 比**文件名**而不是整条路径：游戏侧报的是绝对路径，编辑器手上是文件名，
      // 直接比会永远不相等（于是"暂停"永远变不成，"切过去"反而每次都发）。
      var mine = !!name && !!state.file
        && name.toLowerCase() === String(state.file).toLowerCase();
      return {
        known: true, connected: true,
        // ready：有宿主可选（控制器宿主一直存在，所以"就绪"看它的 ready 与 name）。
        // 老版本 Mod 没有控制器宿主时 host.name 是 null → 这里就是"未接管"。
        ready: !!host.name && host.ready !== false,
        // 控制器现在在哪一层操作：world = 世界里（玩家输入），menu = 主菜单 / 刚退出世界（只点 UI）
        menu: host.situation === 'menu' || host.kind === 'menu',
        player: host.player || null,
        enabled: host.enabled !== false,
        running: !!tree.running, paused: !!status.paused, file: name, path: file, mine: mine,
        ticks: tree.ticks || 0, mode: status.mode || '-'
      };
    }
    var error = state.gameStatusError || {};
    return {
      known: !!error.code, connected: error.code !== 'game_unreachable',
      ready: false, menu: false, running: false, paused: false, file: null, mine: false,
      code: error.code || null, reason: error.reason || null
    };
  }

  function refreshGameStatusQuiet() {
    return api('/api/game/status').then(function (data) {
      if (data.ok) {
        state.gameStatus = data.status || {};
        state.gameStatusError = null;
      } else {
        state.gameStatus = null;
        state.gameStatusError = data;
      }
      updateTreeRunUi();
      maybeResumeAfterTrial();
      return data;
    }).catch(function (error) {
      state.gameStatus = null;
      state.gameStatusError = { code: 'editor_error', reason: error.message };
      updateTreeRunUi();
    });
  }

  /**
   * 低频"游戏状态哨兵"（默认 3s 一次）。
   *
   * 为什么必须有它（用户实测报的："游戏已经启动了，但树无法点击播放，提示游戏没在跑"）：
   * 树徽标与「▶ 播放」的判断全看 `state.gameStatus`，而它**只在**页面加载、换包、
   * 点播放/暂停/停止之后才刷新 —— 游戏是**在页面之外**起来的（手动双击 exe、或先开页面
   * 再开游戏）时，那份缓存永远停在 `game_unreachable`，于是：游戏徽标显示"已连上"，
   * 树徽标却一直写"游戏没在跑"，播放按钮还是灰的。
   *
   * 打开「实时监视」时它会停掉：那条 700ms 的轮询已经每次都在刷新状态，别重复打。
   * 标签页隐藏时不打（没人在看），重新可见时立刻补一次。
   */
  var statusWatch = { timer: null, intervalMs: 3000 };

  function pollStatusQuiet() {
    if (typeof document !== 'undefined' && document.hidden) return;
    refreshGameProcess();
    refreshGameStatusQuiet();
  }

  function startStatusWatch() {
    if (!statusWatch.timer) {
      statusWatch.timer = window.setInterval(pollStatusQuiet, statusWatch.intervalMs);
    }
  }

  function stopStatusWatch() {
    if (statusWatch.timer) {
      window.clearInterval(statusWatch.timer);
      statusWatch.timer = null;
    }
  }

  /**
   * 试跑时我们顺手暂停了树，**回放一结束就自动恢复**。
   *
   * 为什么必须自动：暂停是全局的，忘了恢复的话后面所有"播放"都只是把树装回去而不执行
   * （用户实测踩过："回主菜单再播放这棵树，第二次没有被执行"）。
   */
  function maybeResumeAfterTrial() {
    if (!state.pausedForTrial || state.trialResumeSent) return;
    var status = state.gameStatus;
    var action = status && status.action;
    if (!status || !action || action.playing !== false) return;
    state.trialResumeSent = true;
    api('/api/game/resume', { method: 'POST' }).then(function (data) {
      state.pausedForTrial = false;
      state.trialResumeSent = false;
      if (!data.ok) return;
      setStatus(L('st.069', '试跑结束，已自动恢复行为树（之前是为了试跑把它暂停的）'));
      refreshGameStatusQuiet();
    });
  }

  /** 播放按钮与"树：…"徽标的唯一刷新点（状态变了就调它，别在别处拼文案）。 */
  function updateTreeRunUi() {
    var button = $('btnTreeRun');
    var badge = $('treeRunBadge');
    if (!button || !badge) return;
    var st = treeRunState();
    var label;
    var title;

    if (treeRun.busy) {
      label = '…';
      title = L('ui.182', '正在和游戏通信');
    } else if (!st.connected) {
      label = L("ui.081", '▶ 播放这棵树');
      title = L('ui.183', '缓存里游戏没在跑（点它会先重新确认一次）。真没跑就点「启动游戏」');
    } else if (!st.ready) {
      label = L("ui.081", '▶ 播放这棵树');
      title = L('ui.184', '游戏在跑但还没进世界 / AI 没接管：进世界并在游戏里 ai enable 之后就能播');
    } else if (st.mine && st.running && !st.paused) {
      label = (state.dirty ? L('ui.185', '⟳ 推送改动') : L('ui.186', '⏸ 暂停'));
      title = state.dirty
        ? L('ui.187', '把编辑器里的改动保存并热重载到游戏（不重启世界、保留运行态）')
        : L('ui.188', '暂停行为树（保留运行态、立刻释放 AI 注入的输入）');
    } else if (st.mine && st.running && st.paused) {
      label = L("ui.082", '▶ 继续');
      title = L('ui.189', '从暂停处继续跑（不清空运行态）');
    } else {
      label = L("ui.081", '▶ 播放这棵树');
      title = st.file
        ? (L('ui.190', '游戏现在跑的是 ') + st.file + L('ui.191', '；点它切到编辑器打开的这棵树并开始跑'))
        : L('ui.192', '让游戏切到编辑器打开的这棵树并开始跑（毫秒级切换，不重载世界）');
    }
    button.textContent = label;
    button.title = title;
    // ⚠️ **不要**因为"缓存说游戏没在跑"就把按钮灰掉：那份缓存可能是页面刚打开时（游戏还没起）
    //    留下的，而且没人会去刷新它 —— 用户就会遇到"游戏明明开着，播放按钮点不动"。
    //    少了包名才是真的不能播（没有树可切）。点了会先重新确认状态，见 treeRunPrimary。
    button.disabled = treeRun.busy || (!state.path && !(st.mine && st.running));
    button.className = 'primary'
      + (st.mine && st.running && !st.paused && !state.dirty ? ' running' : '');

    // 「停止」：只有真的有树在跑（或暂停）时才可点 —— 它的语义是"卸下、回到没跑的状态"，
    // 之后再点播放就是从根开始（用户要的"重置"）。
    var stopButton = $('btnTreeStop');
    if (stopButton) {
      stopButton.disabled = treeRun.busy || !st.connected || !(st.running || st.paused);
      stopButton.title = st.running || st.paused
              ? I18n.format("ui.074", '停止并卸下 {0}（释放输入）——之后再点播放从头开始', (st.file || L('ui.277', '当前树')))
              : L("ui.075", '现在没有正在跑的树');
    }

    var text;
    var css = 'badge';
    if (!st.connected) {
      text = L("ui.083", '树：游戏没在跑');
    } else if (!st.ready) {
      text = L("ui.084", '树：未接管（进世界 + ai enable 后可播）');
      css += ' warn';
    } else if (st.mine && st.running) {
      // 控制器可以在主菜单跑树（「进入游戏」这种包就发生在这儿），也要能进世界接着跑 ——
      // 所以把"现在在哪一层操作"写出来，免得看到"运行中"却以为角色已经被接管了。
      text = I18n.format("ui.085", '树：{0}{1} {2}（tick {3}）', (st.paused ? L('ui.276', '已暂停') : L('ui.202', '运行中')), (st.menu ? L('ui.272', '（控制器·主菜单）') : (st.player ? L('ui.271', '（控制器·') + st.player + L('ui.168', '）') : L('ui.275', '（控制器）'))), shortFile(st.file), st.ticks);
      css += st.paused ? ' warn' : ' live';
    } else if (st.running && st.file) {
      text = I18n.format("ui.086", '树：跑的是 {0}（编辑器打开的是 {1}）', shortFile(st.file), (state.file || L('ui.274', '未打开')));
      css += ' warn';
    } else {
      text = I18n.format("ui.087", '树：未运行{0}{1}{2}', (st.paused ? L('ui.273', '（AI 已暂停，播放时会自动取消）') : ''), (st.menu ? L('ui.272', '（控制器·主菜单）') : (st.player ? L('ui.271', '（控制器·') + st.player + L('ui.168', '）') : '')), (state.file ? L('ui.270', '（打开的是 ') + shortFile(state.file) + L('ui.168', '）') : ''));
    }
    badge.textContent = text;
    badge.className = css;
  }

  /** 保存（脏的话）+ 让游戏切到这棵树 → 抽出来给"播放"和自检共用。 */
  function playThisTree() {
    if (!state.path) { setStatus(L('st.009', '先打开一个包，或「另存为」一个有名字的包再播放')); return; }
    var file = state.file || state.path;
    treeRun.busy = true;
    updateTreeRunUi();
    setStatus(I18n.format("st.c071", '让游戏切到 {0} …', file));

    var save = state.dirty ? saveNow(null) : Promise.resolve(true);
    save.then(function (saved) {
      if (!saved) {           // 保存失败（校验没过 / 写盘失败）：绝不拿没保存的树去切
        treeRun.busy = false;
        updateTreeRunUi();
        return null;
      }
      return api('/api/game/tree/switch?path=' + encodeURIComponent(state.path),
        { method: 'POST' });
    }).then(function (data) {
      if (!data) return;
      treeRun.busy = false;
      if (!data.ok) {
        setStatus(data.code === 'game_not_ready'
            ? I18n.format("st.t032", '还不能播放：{0}', (data.reason || '?'))
            : I18n.format("st.t033", '播放失败：{0}', (data.reason || '?')));
        refreshGameStatusQuiet();
        return;
      }
      setStatus(I18n.format("st.c072", '游戏开始跑这棵树：{0}（切换 {1}ms，{2} 节点{3}）', data.file, (data.switchMs || 0), (data.nodes || 0), (data.recompiled ? L('ui.269', '，重新编译过') : L('ui.268', '，用的常驻副本'))));
      // 暂停是**全局**的（帧首先看它）：留着它，树装进去了也不会被 tick。
      // 用户实测踩过这个坑（播放→暂停→停止→再播放，界面还写着"已暂停"），所以"播放"
      // 的语义就是"让它跑起来" —— 发现暂停就顺手取消，并把这件事说出来。
      refreshGameStatusQuiet().then(function () {
        var st = treeRunState();
        if (!st.paused) return;
        api('/api/game/resume', { method: 'POST' }).then(function (done) {
          setStatus(done.ok
            ? I18n.format("st.t021", '游戏开始跑这棵树：{0}（顺手取消了 AI 暂停 —— 暂停是全局的，留着它树不会跑）', data.file)
            : I18n.format("st.t022", '树已切过去，但取消暂停失败：{0}', (done.reason || '?')));
          refreshGameStatusQuiet();
          if (live.timer) pollLive();
        });
      });
      if (live.timer) pollLive();
    });
  }

  /** 停止并卸下当前树 —— 用户要的"重置"入口：停完再播放 = 从根重新跑。 */
  function stopThisTree() {
    treeRun.busy = true;
    updateTreeRunUi();
    setStatus(L('st.053', '正在停止行为树…'));
    api('/api/game/tree/stop', { method: 'POST' }).then(function (data) {
      treeRun.busy = false;
      state.pausedForTrial = false;
      if (!data.ok) {
        setStatus(data.code === 'game_not_ready'
            ? I18n.format("st.t034", '还不能停止：{0}', (data.reason || '?'))
            : I18n.format("st.t028", '停止失败：{0}', (data.reason || '?')));
      } else if (data.stopped) {
        setStatus(L('st.023', '已停止：树已卸下、输入已释放 —— 再点「▶ 播放这棵树」就是从头开始跑'));
      } else {
        setStatus(I18n.format("st.c073", '当前没有正在跑的树（{0}）', (data.reason || '')));
      }
      refreshGameStatusQuiet();
      if (live.timer) pollLive();
    });
  }

  /** 保存 + 只推送热重载（游戏里跑的就是这棵树时走这条）。 */
  function pushChangesToRunningTree() {
    treeRun.busy = true;
    updateTreeRunUi();
    setStatus(L('st.005', '保存并推送热重载…'));
    saveNow(null).then(function (saved) {
      if (!saved) { treeRun.busy = false; updateTreeRunUi(); return null; }
      return api('/api/notify?path=' + encodeURIComponent(state.path), { method: 'POST' });
    }).then(function (data) {
      if (!data) return;
      treeRun.busy = false;
      if (!data.ok) {
        setStatus(data.code === 'game_not_ready'
            ? I18n.format("st.t035", '还不能热重载：{0}', (data.reason || '?'))
            : I18n.format("st.t036", '热重载失败：{0}', (data.reason || '?')));
      } else {
        setStatus(I18n.format("st.c074", '已推送热重载：{0}', JSON.stringify(data.game || {})));
        if (live.timer) pollLive();
      }
      refreshGameStatusQuiet();
    });
  }

  /**
   * 播放按钮的唯一入口 —— 语义按当前状态分流（见上面那段注释）。
   * 这就是用户要的"一个播放 / 暂停按钮"。
   */
  function treeRunPrimary() {
    if (treeRun.busy) return;
    var st = treeRunState();

    // 缓存里写着"游戏没在跑"时**先重新确认一次**再决定：
    // 页面可能是游戏还没起来的时候打开的，或者用户是在游戏外面把游戏启动起来的
    // （手动双击 exe / 从别处启动），这时缓存永远没人更新 —— 用户看到的就是
    // "游戏明明开着，播放按钮却是灰的、还提示游戏没在跑"。
    if (!st.connected) {
      setStatus(L('st.056', '正在重新确认游戏是否在跑…'));
      refreshGameStatusQuiet().then(function () {
        treeRunPrimaryAfterRefresh();
      });
      return;
    }
    treeRunPrimaryAfterRefresh();
  }

  /** 重新确认过状态之后，再按"播放 / 暂停 / 继续 / 推送"的真实语义走。 */
  function treeRunPrimaryAfterRefresh() {
    if (treeRun.busy) return;
    var st = treeRunState();
    if (!st.connected) {
      setStatus(I18n.format("st.c075", '游戏没在跑：{0}　—— 点「启动游戏」，或确认游戏是本实例根里的那个 Survivalcraft.exe', (st.reason || L('ui.267', '控制通道没开'))));
      return;
    }
    if (st.mine && st.running && st.paused) {
      setPaused(false);
      return;
    }
    if (st.mine && st.running && !st.paused) {
      if (state.dirty) pushChangesToRunningTree();
      else setPaused(true);
      return;
    }
    playThisTree();
  }

  /** 暂停 / 继续行为树（工具栏按钮与实时监视面板共用）。 */
  function setPaused(paused) {
    // 动作词按 paused 取（英文里 "Now pausing the tree…" 才顺；之前是把中文词从调用点传进来，切英文就成了 "Now Pause the tree…"）
    setStatus(I18n.format("st.c076", '正在{0}行为树…', L(paused ? 'ui.w01' : 'ui.w02')));
    api('/api/game/' + (paused ? 'pause' : 'resume'), { method: 'POST' }).then(function (data) {
      if (!data.ok) {
        setStatus(data.code === 'game_not_ready'
            ? I18n.format("st.t037", '还不能操作：{0}', (data.reason || '?'))
            : I18n.format("st.t038", '操作失败：{0}', (data.reason || '?')));
      } else {
        setStatus(I18n.format("st.c077", '行为树已{0}（运行态{1}）',
          L(paused ? 'ui.w03' : 'ui.w04'), L(paused ? 'ui.w05' : 'ui.w06')));
        if (paused) state.pausedForTrial = false;
      }
      refreshGameStatusQuiet();
      if (live.timer) pollLive();
    });
  }

  // ---------------------------------------------------------------- 实时监视（P3）

  var live = { timer: null, intervalMs: 700, failures: 0 };

  function setLiveOn(on) {
    if (on && !live.timer) {
      live.timer = window.setInterval(pollLive, live.intervalMs);
      stopStatusWatch();     // 700ms 那条已经在刷状态，哨兵先停，别重复打
      pollLive();
      setStatus(I18n.format("st.c078", '实时监视已打开（每 {0}ms 拉一次 ai.status / snapshot / blackboard）', live.intervalMs));
    } else if (!on && live.timer) {
      window.clearInterval(live.timer);
      live.timer = null;
      state.livePathIds = [];
      state.liveIds = [];
      state.liveTipId = null;
      state.live = null;
      renderTree();
      renderLivePanel();
      startStatusWatch();    // 关掉监视 → 低频哨兵接手
      setStatus(L('st.020', '实时监视已关闭'));
    }
    $('btnLive').textContent = live.timer
              ? L("ui.041", '■ 停止监视')
              : L("ui.042", '▶ 实时监视');
  }

  function pollLive() {
    api('/api/game/live').then(function (data) {
      if (!data.ok) {
        live.failures++;
        state.live = null;
        state.livePathIds = [];
        state.liveIds = [];
        state.liveTipId = null;
        state.gameStatus = null;
        state.gameStatusError = data;
        updateTreeRunUi();
        renderTree();
        renderLivePanel(data.reason || L('ui.195', '读不到游戏状态'), data.code);
        // 只报一次，而且别把"还没准备好"说成故障 —— 世界加载完/ai enable 之后
        // 这里会自动开始刷新（用户实测就是这么恢复的，界面得说清楚）。
        if (live.failures === 1) {
          setStatus(data.code === 'game_not_ready' || data.code === 'game_unreachable'
            ? I18n.format("st.t023", '实时监视：等游戏准备好（{0}）', (data.reason || ''))
            : I18n.format("st.t024", '实时监视：{0}', (data.reason || L('ui.195', '读不到游戏状态'))));
        }
        return;
      }
      live.failures = 0;
      state.live = data;
      if (data.status) {
        state.gameStatus = data.status;
        state.gameStatusError = null;
        updateTreeRunUi();
        maybeResumeAfterTrial();   // 试跑回放结束 → 自动把树恢复回去
      }
      var snapshot = data.tree || {};
      // 高亮规则（用户要求）：**只亮"正在执行"的那一个节点**，亮绿色边框；
      // 已经执行完的、以及只是"路径上"的祖先节点都不亮。
      // 游戏侧 `ai.tree.snapshot` 的 `path` 数组就是"当前 IsActive 的节点"（前序遍历），
      // 所以**最后一个**就是最深、也就是真正在跑的那个；父组合节点即使 IsActive 也不亮。
      var ids = activeNodeIds(snapshot.activePath);          // 面板里显示"活动节点路径"用
      state.livePathIds = ids;
      state.liveIds = liveActiveChain(snapshot.path, ids);
      state.liveTipId = state.liveIds.length ? state.liveIds[state.liveIds.length - 1] : null;
      renderTree();
      renderLivePanel();
    }).catch(function (error) {
      renderLivePanel(L('ui.196', '请求失败：') + error.message);
    });
  }

  function renderLivePanel(problem, problemCode) {
    var box = $('liveBox');
    if (!box) return;
    box.innerHTML = '';

    if (problem) {
      // "还没准备好"不是故障：用中性样式 + 说清怎么让它开始刷新，
      // 别用红色 ERROR（用户第一眼看到红框会以为坏了，其实等世界加载完就好）。
      var waiting = problemCode === 'game_not_ready' || problemCode === 'game_unreachable';
      var info = document.createElement('div');
      info.className = 'issue' + (waiting ? '' : ' error');
      info.textContent = waiting
        ? (L('ui.197', '正在等游戏准备好：') + problem
          + L('ui.198', '　—— 进世界 + 游戏里 ai enable 之后，这里会自动开始刷新（不用重新点监视）。'))
        : problem;
      box.appendChild(info);
      return;
    }
    var data = state.live;
    if (!data) {
      box.innerHTML = L("ui.055", '<p class="hint">打开"实时监视"就能看到游戏里正在跑哪个节点。</p>');
      return;
    }

    var status = data.status || {};
    var tree = data.tree || {};
    var lines = [
      [L('ui.199', '模式'), String(status.mode || '-') + (status.paused ? L('ui.200', '（已暂停）') : '')],
      [L('ui.201', '活动树'), (tree.treeId || '-') + L('ui.101', '　') + (tree.running ? L('ui.202', '运行中') : L('ui.203', '未运行'))
        + L('ui.204', '　上次=') + (tree.lastResult || '-')],
      [L('ui.205', '活动节点'), tree.activePath || '-'],
      [L('ui.206', 'tick / 时间'), (tree.ticks || 0) + ' / ' + Number(tree.time || 0).toFixed(1) + 's'],
      [L('ui.207', '内存改动'), tree.dirty ? L('ui.208', '有（原包字节未变）') : L('ui.209', '无')]
    ];
    if (tree.lastError) lines.push([L('ui.210', '上次错误'), tree.lastError]);

    lines.forEach(function (pair) {
      var row = document.createElement('div');
      row.className = 'prop-row';
      var label = document.createElement('label');
      label.textContent = pair[0];
      var value = document.createElement('span');
      value.className = 'live-value';
      value.textContent = pair[1];
      row.appendChild(label);
      row.appendChild(value);
      box.appendChild(row);
    });

    var blackboard = blackboardLines(data.blackboard);
    var title = document.createElement('h3');
    title.textContent = I18n.format("ui.044", '黑板（{0}）', blackboard.length);
    box.appendChild(title);
    if (!blackboard.length) {
      var empty = document.createElement('p');
      empty.className = 'hint';
      empty.textContent = L("ui.045", '（空）');
      box.appendChild(empty);
    }
    blackboard.forEach(function (line) {
      var row = document.createElement('div');
      row.className = 'bb-row';
      row.textContent = line;
      box.appendChild(row);
    });

    // 画布上没找到高亮节点时提示一句：多半是"编辑器里打开的不是游戏里那棵树"
    var pathIds = state.livePathIds || state.liveIds || [];
    if (pathIds.length && !pathIds.some(function (id) { return !!findNode(id); })) {
      var mismatch = document.createElement('p');
      mismatch.className = 'hint';
      mismatch.textContent = I18n.format("ui.046", '游戏里跑的是 {0}，编辑器打开的这棵树里没有对应节点 —— 打开同名包就能看到高亮。', (tree.treeId || '?'));
      box.appendChild(mismatch);
    }
  }

  /** 实时监视面板里的暂停/继续按钮（与工具栏那个播放按钮共用 setPaused）。 */
  function pauseTree(paused) {
    setPaused(paused);
  }

  // ---------------------------------------------------------------- UI 拾取（UI-1 服务）
  //
  // 用户要求（原话）："把相应的方法做成 CmdBridgeMod 能提供的服务，在行为树编辑器中，要能够使用
  // 来获取坐标或点击对象。避免硬编码由于分辨率变化或窗口尺寸变化导致无法使用。"
  //
  // 所以这里做三件事，全走游戏侧的 `ui.locate` / `ui.clickElement`（与行为树/回放**同一份实现**）：
  //   ① 列当前屏幕上可交互的元素（真实坐标 + 能不能点）；
  //   ② 点一个元素 → 得到**语义目标**（控件路径，或列表行的 `list:列表@文字`）→ 写进树；
  //   ③ "点一下"在游戏里真的点它（可以顺便验证这个目标对不对）。
  // 绝不把此刻的像素写进树 —— 那正是"改窗口就点空"的来源。

  var uiPick = { data: null, busy: false };

  function uiClickMode() {
    return ($('uiClickMode') && $('uiClickMode').value) || 'direct';
  }

  /** 「标记落点」开关：定位/点击时在游戏里那个像素上亮 2 秒红点（默认开）。 */
  function uiMarkEnabled() {
    var box = $('uiMarkToggle');
    return !box || box.checked !== false;
  }

  /** 开关的持久化（记在这个浏览器里；读不到 localStorage 就用默认"开"）。 */
  function markPreference() {
    try {
      var saved = window.localStorage.getItem('playerAiEditor.uiMark');
      if (saved === '0') return false;
      if (saved === '1') return true;
    } catch (error) { /* 隐私模式 */ }
    return true;
  }

  function showLocatedLine(text) {
    var line = $('uiLocatedLine');
    if (line) line.textContent = text || '';
  }

  /** 客户区坐标 → `x,y`（拿不到就是 `-`）。 */
  function coordText(point) {
    point = point || {};
    return (typeof point.x === 'number')
      ? (Math.round(point.x) + ',' + Math.round(point.y)) : '-';
  }

  /**
   * 记下"上一次定位/点击"，并按**当前语言**重画那一行。
   *
   * 为什么不能只缓存拼好的字符串：那一行是 `落点 x,y（游戏里已亮红点）` 这种拼出来的句子，
   * 缓存字符串就翻不成英文了 —— 用户实测：切到英文后这行还留着「落点 -」。
   * 所以缓存**原始回包**，语言切换时照着重拼（`applyLanguage()` 会调它）。
   */
  function rememberLocated(kind, data, mode) {
    uiPick.located = { kind: kind, data: data || {}, mode: mode || '' };
    renderLocatedLine();
  }

  function renderLocatedLine() {
    var last = uiPick.located;
    if (!last) { showLocatedLine(''); return; }
    var data = last.data || {};
    if (last.kind === 'locate') {
      if (data.ok === false) { showLocatedLine(''); return; }
      var note = data.clickable === false
        ? L('ui.228', '（现在点不到：') + (data.blockedBy || data.clickReason || L('ui.229', '被挡住')) + L('ui.168', '）')
        : '';
      showLocatedLine(L('ui.230', '落点 ') + coordText(data.clientPoint)
        + (data.marked ? L('ui.231', '（游戏里已亮红点）') : '') + note);
      return;
    }
    if (data.ok === false) {
      showLocatedLine(L('ui.232', '点不动：') + describeUiError(data, L('ui.233', '点击')));
      return;
    }
    var hit = data.clickTarget ? (L('ui.234', '　命中 ') + data.clickTarget) : '';
    var missed = data.clickTarget && data.name && data.clickTarget !== data.name
      ? L('ui.235', '（注意：命中的是 ') + data.clickTarget + L('ui.236', '，不是你选中的 ') + data.name + L('ui.168', '）') : '';
    showLocatedLine(L('ui.237', '点过了：') + (data.mode || last.mode || '') + ' @ '
      + coordText(data.clickPoint || data.clientPoint) + hit + missed
      + (data.marked ? L('ui.238', '（已亮红点）') : ''));
  }

  function uiTargetValue() {
    return ($('uiTargetInput') && $('uiTargetInput').value || '').trim();
  }

  function describeUiError(data, what) {
    if (!data) return what + L('ui.211', '失败');
    if (data.code === 'game_unreachable') return L('ui.212', '游戏没在跑（或控制通道没开）');
    if (data.code === 'game_not_ready') return L('ui.213', '游戏还没准备好：') + (data.reason || '');
    return (data.reason || data.message || what + L('ui.211', '失败'));
  }

  function uiClickDescription(action) {
    return action.charAt(0).toUpperCase() + action.slice(1);
  }

  /** ① 拾取：问游戏要一份可交互元素清单。 */
  function pickUiElements() {
    if (uiPick.busy) return;
    uiPick.busy = true;
    $('btnUiPick').textContent = L("ui.047", '⟳ 拾取中…');
    api('/api/game/ui/elements?max=200').then(function (data) {
      uiPick.busy = false;
      $('btnUiPick').textContent = L("ui.048", '⟳ 拾取界面元素');
      if (!data.ok) {
        uiPick.data = null;
        state.uiElements = null;
        renderUiPick(describeUiError(data, L('ui.214', '拾取界面元素')), data.code);
        return;
      }
      uiPick.data = data;
      state.uiElements = data;
      renderUiPick();
      var count = asArray(data.elements).length;
      setStatus(I18n.format("st.c079", '拾取到 {0} 个可交互元素（screen={1}）', count, (data.screen || '?')));
    }).catch(function (error) {
      uiPick.busy = false;
      $('btnUiPick').textContent = L("ui.048", '⟳ 拾取界面元素');
      renderUiPick(L('ui.196', '请求失败：') + error.message);
    });
  }

  /**
   * 面板标题里的"当前是哪个界面"。
   *
   * 游戏侧的 `ui.elements` **没有** screen 字段（实测字段只有 elements / ready /
   * shown / totalCount / truncated），所以照着写会显示 `screen=?` —— 等于什么都没说。
   * 这里退一步用元素的路径根当界面名（`[SuPlayScreen#0]/…` → `SuPlayScreen`），
   * 一眼就能确认"我拾的是哪个界面"。
   */
  function screenLabel(data, elements) {
    if (data && data.screen) return data.screen;
    for (var i = 0; i < elements.length; i++) {
      var path = elements[i] && elements[i].path;
      if (!path) continue;
      var head = String(path).split('/')[0].replace(/^\[/, '').replace(/#.*$/, '');
      if (head) return head;
    }
    return '?';
  }

  function renderUiPick(problem, problemCode) {
    var box = $('uiPickBox');
    if (!box) return;
    box.innerHTML = '';

    if (problem) {
      var waiting = problemCode === 'game_not_ready' || problemCode === 'game_unreachable';
      var info = document.createElement('div');
      info.className = 'issue' + (waiting ? '' : ' error');
      info.textContent = problem;
      box.appendChild(info);
      return;
    }
    var data = uiPick.data;
    if (!data) {
      box.innerHTML = L("ui.056", '<p class="hint">点「拾取界面元素」看看现在屏幕上有什么可点的。</p>');
      return;
    }

    // 同 `renderPickerRows`：`elements` 万一不是数组也得能降级（`asArray` 注释里有实测经过）
    var elements = asArray(data.elements);
    if (!elements.length) {
      box.innerHTML = I18n.format("ui.057", '<p class="hint">现在屏幕上没有可交互的元素（screen={0}）。游戏里换个界面再拾取一次。</p>', screenLabel(data, elements));
      return;
    }

    var head = document.createElement('p');
    head.className = 'hint';
    head.textContent = I18n.format("ui.049", 'screen={0}　共 {1} 个 —— 点一个填进目标框；「点一下」会在游戏里真的点它。', screenLabel(data, elements), elements.length);
    box.appendChild(head);
    if (data.truncated) {
      // 游戏侧只给前 N 个（默认 200）；截断了就说出来，免得以为"界面上就这么点东西"
      var more = document.createElement('p');
      more.className = 'hint';
      more.textContent = I18n.format('picker.truncated', '（这里只列了前 {0} 个，界面上共 {1} 个）', elements.length, (data.totalCount || '?'));
      box.appendChild(more);
    }

    elements.forEach(function (element) {
      var row = document.createElement('div');
      row.className = 'prop-row ui-pick-row';
      var label = document.createElement('label');
      label.title = element.path || '';
      label.textContent = (element.name || element.type || '?') + (element.text ? L('ui.215', '  「') + element.text + L('ui.216', '」') : '');
      var value = document.createElement('span');
      value.className = 'live-value';
      var point = element.clientPoint || {};
      var coords = (typeof point.x === 'number')
        ? (Math.round(point.x) + ',' + Math.round(point.y)) : '-';
      // 点不到的要说清**为什么**（用户实测踩过：世界内 HUD 那条是触屏专用控件，
      // 在 Windows 上被平移到屏幕外，点了当然没反应；以前只给一个 ⛔，看不出原因）。
      var why = element.clickable ? '' : (L('ui.217', '　⛔ ') + shortReason(element));
      value.textContent = coords + why;
      row.title = I18n.format("ui.076", '{0}\n真实坐标（客户区像素，窗口一变就变）: {1}\n语义目标（写进树的是这个）: {2}{3}', (element.path || ''), coords, (element.target || element.name || ''), (element.clickable ? '' : (L('ui.266', '\n点不到的原因: ') + (element.clickReason || L('ui.265', '未知')))));
      row.className += element.clickable ? '' : ' ui-pick-blocked';
      row.addEventListener('click', function () {
        $('uiTargetInput').value = element.target || element.name || '';
        setStatus(I18n.format("st.c080", '已选中目标：{0}{1}', $('uiTargetInput').value, (element.clickable ? '' : (L('ui.264', '　—— 注意：现在点不到（') + shortReason(element) + L('ui.168', '）')))));
        locateUiTarget($('uiTargetInput').value, true);
      });
      row.appendChild(label);
      row.appendChild(value);
      box.appendChild(row);

      // 列表容器：把每一行也列出来 —— 用户要点的往往不是容器，而是里面那张地图。
      // `asArray`：`items` 万一不是数组（旧版 Mod 会把它压成 JSON 文本），
      // 这里也必须能优雅降级，而不是抛 `forEach is not a function` 把面板弄崩。
      var listInfo = element.list;
      var listItems = listInfo ? asArray(listInfo.items) : [];
      if (listItems.length) {
        var rowsBox = document.createElement('div');
        rowsBox.className = 'ui-pick-rows';
        listItems.forEach(function (item, index) {
          var rowLine = document.createElement('div');
          rowLine.className = 'prop-row ui-pick-row';
          var rowLabel = document.createElement('label');
          rowLabel.textContent = L('ui.218', '　#') + index + L('ui.101', '　') + (item.text || L('ui.219', '(空行)'));
          var rowTarget = 'list:' + element.name + '@' + (item.text || '');
          rowLabel.title = rowTarget;
          var rowValue = document.createElement('span');
          rowValue.className = 'live-value';
          var rowPoint = item.clientPoint || {};
          rowValue.textContent = (typeof rowPoint.x === 'number')
            ? (Math.round(rowPoint.x) + ',' + Math.round(rowPoint.y)) : '-';
          rowLine.title = rowTarget + L('ui.171', '（') + L('picker.rows', '列表里的行') + L('ui.168', '）');
          rowLine.addEventListener('click', function (event) {
            if (event && event.stopPropagation) event.stopPropagation();
            $('uiTargetInput').value = rowTarget;
            setStatus(I18n.format("st.c081", '已选中列表行目标：{0}', rowTarget));
            locateUiTarget(rowTarget, true);
          });
          rowLine.appendChild(rowLabel);
          rowLine.appendChild(rowValue);
          rowsBox.appendChild(rowLine);
        });
        box.appendChild(rowsBox);
      }
    });
  }

  /** 把"点不到的原因"压成一小段，够在列表里显示。 */
  function shortReason(element) {
    var reason = String(element.clickReason || '');
    if (!reason) return element.hittable === false ? L('ui.220', '屏幕外/被遮挡') : L('ui.221', '不可点');
    if (reason.indexOf('off-screen') >= 0) return L('ui.222', '屏幕外（面板被折叠）');
    if (reason.indexOf('zero size') >= 0) return L('ui.223', '还没被布局');
    if (reason.indexOf('mouse cursor is captured') >= 0) return L('ui.224', '这个世界界面不吃鼠标（用按键）');
    if (reason.indexOf('blocked by') === 0) return reason.replace('blocked by ', L('ui.225', '被 ')) + L('ui.226', ' 挡住');
    if (reason.indexOf('nothing is hit') >= 0) return L('ui.227', '中心没有控件');
    return reason.length > 18 ? reason.slice(0, 18) + '…' : reason;
  }

  /** ② 定位：解析目标 + 显示"现在在哪、能不能点"，并在游戏里亮一个红点（可关）。 */
  function locateUiTarget(target, quiet) {
    var want = target || uiTargetValue();
    if (!want) { if (!quiet) setStatus(L('st.007', '先填一个目标（或从拾取列表里点一个）')); return; }
    var mark = uiMarkEnabled();
    api('/api/game/ui/locate?mark=' + (mark ? '1' : '0') + '&target=' + encodeURIComponent(want))
      .then(function (data) {
        if (!data.ok) {
          rememberLocated('locate', data);
          if (!quiet) setStatus(describeUiError(data, L('st.018', '定位')));
          return;
        }
        var point = data.clientPoint || {};
        var where = coordText(point);
        var kind = data.kind || '?';
        var note = data.clickable === false ? L('ui.228', '（现在点不到：') + (data.blockedBy || data.clickReason || L('ui.229', '被挡住')) + L('ui.168', '）') : '';
        rememberLocated('locate', data);
        setStatus(I18n.format("st.c082", '目标 {0}　{1}　真实坐标 {2}{3}{4}', data.target, kind, where, (data.path ? L('ui.263', '　path=') + data.path : ''), note));
      }).catch(function (error) {
        if (!quiet) setStatus(I18n.format("st.c083", '定位请求失败：{0}', error.message));
      });
  }

  /** ③ 真的点一下（走游戏侧 UI 服务；只用输入层，不写游戏状态）。 */
  function clickUiTarget() {
    var want = uiTargetValue();
    if (!want) { setStatus(L('st.007', '先填一个目标（或从拾取列表里点一个）')); return; }
    var mode = uiClickMode();
    var mark = uiMarkEnabled();
    $('btnUiClick').disabled = true;
    setStatus(I18n.format("st.c084", '正在点 {0}（{1}）…', want, mode));
    api('/api/game/ui/click', {
      method: 'POST',
      body: JSON.stringify({ target: want, mode: mode, mark: mark })
    }).then(function (data) {
        $('btnUiClick').disabled = false;
        if (!data.ok) {
          rememberLocated('click', data, mode);
          setStatus(I18n.format("st.c085", '点不动：{0}', describeUiError(data, L('ui.233', '点击'))));
          return;
        }
        var point = data.clickPoint || data.clientPoint || {};
        var where = coordText(point);
        // 如实报"点到了哪个控件、用哪一层输入面"：世界内 HUD 按钮以前点不动，
        // 就是因为按下状态被写到了根输入面（`clickTarget` 是命中的控件名）。
        var hit = data.clickTarget ? (L('ui.234', '　命中 ') + data.clickTarget) : '';
        var missed = data.clickTarget && data.name && data.clickTarget !== data.name
          ? L('ui.235', '（注意：命中的是 ') + data.clickTarget + L('ui.236', '，不是你选中的 ') + data.name + L('ui.168', '）') : '';
        rememberLocated('click', data, mode);
        setStatus(I18n.format("st.c086", '点过了：{0}（{1}，坐标 {2}）{3}{4}', want, (data.mode || mode), where, hit, missed));
        pickUiElements();   // 界面多半已经变了，顺手刷新拾取列表
      }).catch(function (error) {
        $('btnUiClick').disabled = false;
        setStatus(I18n.format("st.c087", '点击请求失败：{0}', error.message));
      });
  }

  /** ④ 填进树：写进选中的 Task.UiClick；没有就新建一个挂上去（不写像素！）。 */
  function fillUiTargetIntoTree() {
    var target = uiTargetValue();
    if (!target) { setStatus(L('st.006', '先在拾取列表里点一个元素，或手填目标')); return; }
    var node = state.selectedId ? findNode(state.selectedId) : null;

    if (node && node.type === 'Task.UiClick') {
      pushHistory();
      node.properties = node.properties || {};
      node.properties.target = target;
      if (!node.properties.mode) node.properties.mode = 'direct';
      if (node.properties.waitSeconds === undefined) node.properties.waitSeconds = 3;
      markDirty(true);
      renderTree();
      renderInspector();
      setStatus(I18n.format("st.c088", '已写进 {0}.target = {1}', node.id, target));
      return;
    }

    var parent = node || state.tree || state.root;
    if (!parent) { setStatus(L('st.064', '画布里还没有树；先新建一棵')); return; }
    var created = addChild(parent, 'Task.UiClick');
    if (!created) return;
    created.properties = created.properties || {};
    created.properties.target = target;
    created.properties.mode = 'direct';
    created.properties.waitSeconds = 3;
    markDirty(true);
    renderTree();
    renderInspector();
    setStatus(I18n.format("st.c089", '已新建 {0}（Task.UiClick, target={1}）并挂到 {2} 下', created.id, target, (parent.id || L('palette.group.root', '根'))));
  }

  // ---------------------------------------------------------------- 动作包

  function actionLabel(item) {
    return item.file
      + (item.replayable ? '' : L('ui.173', '（仅结构：不能回放）'))
      + (item.id && item.id !== item.file.replace(/\.scatpak$/, '') ? '  [' + item.id + ']' : '')
      + '  ' + (item.duration || 0) + 's/' + (item.frames || 0) + L('ui.239', '帧');
  }

  function loadActions() {
    return api('/api/actions').then(function (data) {
      state.actions = data.actions || [];
      renderActionSelect();
      var replayable = (state.actions || []).filter(function (a) { return a.replayable; }).length;
      setStatus(I18n.format("st.c090", '动作包 {0} 个（可回放 {1}）', state.actions.length, replayable));
      renderPalette($('paletteFilter').value.toLowerCase());
      renderInspector();
    });
  }

  /**
   * 画动作包下拉与徽标（纯渲染，不发请求）。
   *
   * 为什么要拆出来：选项文字是 `文件名（仅结构：不能回放）2.231s/723帧` 这种拼出来的句子，
   * 原先只在 `loadActions()` 里写一次 —— 切英文之后整条下拉还是中文（用户实测反馈：
   * "actions 的（仅结构：不能回放）和 1043 帧也需要调整"）。
   */
  function renderActionSelect() {
    var select = $('actionSelect');
    if (select) {
      select.innerHTML = '';
      (state.actions || []).forEach(function (item) {
        var option = document.createElement('option');
        option.value = item.file;
        option.textContent = actionLabel(item);
        select.appendChild(option);
      });
    }
    var replayable = (state.actions || []).filter(function (a) { return a.replayable; }).length;
    var badge = $('actionBadge');
    if (badge) {
      badge.textContent = I18n.format("ui.050", '动作包 {0}/可回放 {1}', (state.actions || []).length, replayable);
      badge.className = 'badge' + ((state.actions || []).length ? ' live' : '');
    }
  }

  function selectedActionFile() {
    var select = $('actionSelect');
    return select && select.value ? select.value : null;
  }

  function validateAction() {
    var file = selectedActionFile();
    if (!file) { setStatus(L('st.058', '没有动作包可选（先录一个，或点"自造"）')); return; }
    setStatus(I18n.format("st.c091", '校验动作包 {0}…', file));
    api('/api/action/validate?name=' + encodeURIComponent(file)).then(function (data) {
      var issues = (data.issues || []).map(function (line) { return String(line); });
      if (data.ok) {
        issues.unshift(L('ui.240', 'INFO 动作包 ') + data.file + L('ui.241', '：可回放=') + (data.replayable ? L('ui.242', '是') : L('ui.243', '否'))
          + L('ui.244', '  时长=') + data.duration + L('ui.245', 's  帧=') + data.frames
          + L('ui.246', '  目录=') + (data.folder || 'PlayerAi\\BehaviorTrees')
          + L('ui.247', '  按键=') + ((data.keys || []).join('/') || L('ui.209', '无')));
      } else if (data.reason) {
        issues.unshift('ERROR ' + data.reason);
      }
      setIssues(issues.filter(function (line) { return /^ERROR|^WARN|^INFO/.test(line); }));
      setStatus(data.ok
            ? I18n.format("st.t025", '动作包可用：{0}（{1}）', data.file, (data.replayable ? L('editor.replayable', '可回放') : L('ui.262', '不能回放')))
            : I18n.format("st.t026", '动作包有问题：{0}', (data.reason || (data.errors + L('ui.261', ' 个错误')))));
    });
  }

  /**
   * 试跑：**只回放下拉框里选中的那一个包**（不经行为树）。
   *
   * 为什么不只发一条命令：树在跑的时候，树自己也在往同一套输入通道里写
   * （`PlayerAiRuntime.TickFrameStart` 里动作包先推进，之后没暂停才 tick 树），
   * 两边同时写 = 互相打架、看得出"动作不像录的那条"。所以试跑前先把树暂停，
   * 并记下来是我们暂停的，提示用户试跑完点「继续」恢复。
   */
  function playAction() {
    var file = selectedActionFile();
    if (!file) { setStatus(L('st.057', '没有动作包可选')); return; }
    setStatus(I18n.format("st.c092", '让游戏回放 {0} …', file));

    refreshGameStatusQuiet().then(function () {
      var st = treeRunState();
      var needPause = st.connected && st.running && !st.paused;
      var pause = needPause
        ? api('/api/game/pause', { method: 'POST' }).then(function (data) {
            state.pausedForTrial = !!data.ok;
            return data;
          })
        : Promise.resolve(null);

      return pause.then(function (pausedResult) {
        return api('/api/action/play', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ path: file, repeat: 1 })
        }).then(function (data) {
          if (!data.ok) {
            setStatus(data.code === 'game_not_ready'
            ? I18n.format("st.t039", '游戏还没准备好：{0}', (data.reason || '?'))
            : I18n.format("st.t040", '回放失败：{0}', (data.reason || '?')));
            refreshGameStatusQuiet();
            return;
          }
          setStatus(I18n.format("st.c093", '游戏在回放选中的这 1 个包：{0}{1}', file, (pausedResult && pausedResult.ok
              ? L('ui.260', '　（已先把行为树暂停，免得两边抢输入；试跑完点「▶ 继续」恢复树）')
              : '')));
          if (live.timer) pollLive();
          refreshGameStatusQuiet();
        });
      });
    });
  }

  function stopAction() {
    fetch('/api/action/stop', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{}'
    }).then(function (r) { return r.json(); }).then(function (data) {
      setStatus(data.ok
            ? I18n.format("st.t027", '已停止回放：{0}', JSON.stringify(data.game || {}))
            : I18n.format("st.t028", '停止失败：{0}', (data.reason || '?')));
      // 试跑时是我们把树暂停的：停完提醒一句，别让人以为树自己坏了
      if (data.ok && state.pausedForTrial) {
        setStatus(L('st.022', '已停止回放 —— 行为树还是暂停状态，点「▶ 继续」恢复它'));
        state.pausedForTrial = false;
      }
      refreshGameStatusQuiet();
    });
  }

  /** 自造动作包：写一个确定内容的示例轨道进包目录（不依赖真机录制）。 */
  function createAction() {
    var name = prompt(L('ui.248', '新动作包名字（写进包目录的 .scatpak）'), 'my_action');
    if (!name) return;
    fetch('/api/action/create', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: name })
    }).then(function (r) { return r.json(); }).then(function (data) {
      if (!data.ok) { setStatus(I18n.format("st.c094", '自造失败：{0}', (data.reason || '?'))); return; }
      setStatus(I18n.format("st.c095", '已造出 {0}（{1} 帧 / {2}s，可回放={3}）', data.file, data.frames, data.duration, (data.replayable ? L('ui.242', '是') : L('ui.243', '否'))));
      loadActions();
    });
  }

  // ---------------------------------------------------------------- 模态框

  /**
   * 极简模态框：标题 + 内容 + 底部按钮。返回 {close, body}。
   *
   * 为什么自己写而不是用 window.confirm/prompt：动作包编辑器与"界面目标选择器"都要放表格与
   * 实时数据，prompt 完全做不了；而这个文件一直是零依赖的，引一个组件库不划算。
   */
  function openModal(titleText, actions) {
    var host = $('modalHost');
    host.innerHTML = '';
    document.removeEventListener('keydown', handleModalKey, true);

    var backdrop = document.createElement('div');
    backdrop.className = 'modal-backdrop';
    var box = document.createElement('div');
    box.className = 'modal';
    var title = document.createElement('h2');
    title.textContent = titleText;
    var body = document.createElement('div');
    body.className = 'modal-body';
    var footer = document.createElement('div');
    footer.className = 'modal-actions';

    function close() {
      host.hidden = true;
      host.innerHTML = '';
      document.removeEventListener('keydown', handleModalKey, true);
    }
    function handleModalKey(event) {
      if (event.key === 'Escape') {
        event.stopPropagation();
        close();
      }
    }
    backdrop.addEventListener('click', close);
    document.addEventListener('keydown', handleModalKey, true);
    (actions || []).forEach(function (action) {
      var element = document.createElement('button');
      element.textContent = action.label;
      if (action.primary) element.className = 'primary';
      if (action.danger) element.className = 'danger';
      element.addEventListener('click', function () { action.run(close); });
      footer.appendChild(element);
    });

    box.appendChild(title);
    box.appendChild(body);
    box.appendChild(footer);
    host.appendChild(backdrop);
    host.appendChild(box);
    host.hidden = false;
    return { close: close, body: body, title: title, footer: footer };
  }

  // ---------------------------------------------------------------- 界面目标选择器
  //
  // 用户要求（原话）："左侧的物料区我希望增加一个，用于解析 list 或多种结构的，
  // 现在 worldslist 能够识别，但是我无法点击这个容器内的具体的地图。"
  //
  // 关键就是**把列表容器展开到每一行**：`WorldsList` 只是一个容器，真正要选的是里面那张地图。
  // 选出来的东西写进节点的是**语义目标**（`list:WorldsList@世界名`），不是此刻的像素。

  var uiPicker = { data: null, onPick: null, busy: false };

  function uiElementTarget(element) {
    if (element && element.rowTarget) return element.rowTarget;
    return (element && (element.target || element.name)) || '';
  }

  /**
   * 把"可能不是数组"的字段安全地变成数组。
   *
   * 为什么需要它：编辑器与游戏之间是 JSON 文本往返，**嵌套数组曾经被压成字符串**
   * （`list.items` 的值是 `[{"index":0,...}]` 这段文字）。而字符串也有 `.length`，
   * 所以 `if (list.items && list.items.length)` 这种守卫拦不住 —— 用户实测：
   * 在地图选择界面点「拾取界面元素」，整个面板报
   * `Request failed: listInfo.items.forEach is not a function`，一个元素都拾不到。
   * 现在：真数组直接用；长得像 JSON 文本就解析回来（兼容旧版 Mod 的返回）；
   * 其余一律当空数组 —— 宁可不展开列表行，也不能把整个面板弄崩。
   */
  function asArray(value) {
    if (Object.prototype.toString.call(value) === '[object Array]') return value;
    if (typeof value === 'string' && value.charAt(0) === '[') {
      try {
        var parsed = JSON.parse(value);
        if (Object.prototype.toString.call(parsed) === '[object Array]') return parsed;
      } catch (error) { /* 解析不了就落回空数组 */ }
    }
    return [];
  }

  /** 把一行元素渲染成列表行（`list` 元素会把每一行展开成子行）。 */
  function renderPickerRows(box, elements) {
    elements = asArray(elements);
    if (!elements.length) {
      var empty = document.createElement('p');
      empty.className = 'hint';
      empty.textContent = L('picker.none', '现在屏幕上没有可交互元素（换个界面再刷新）');
      box.appendChild(empty);
      return;
    }
    elements.forEach(function (element) {
      box.appendChild(pickerRow(element));
      var list = element.list;
      var listItems = list ? asArray(list.items) : [];
      if (!listItems.length) return;
      var rows = document.createElement('div');
      rows.className = 'ui-pick-rows';
      listItems.forEach(function (item, index) {
        rows.appendChild(pickerRow({
          name: L('ui.218', '　#') + index + ' ' + (item.text || L('ui.219', '(空行)')),
          type: element.name + L('ui.249', ' 行'),
          text: item.text,
          clientPoint: item.clientPoint,
          clickable: item.clickable !== false,
          clickReason: item.clickReason,
          target: 'list:' + element.name + '@' + (item.text || ''),
          rowTarget: 'list:' + element.name + '#' + index,
          parent: element.name,
          rowText: item.text
        }, true));
      });
      box.appendChild(rows);
    });
  }

  /** 选择器里的一行：左名、右坐标，两个动作按钮。 */
  function pickerRow(element, isRow) {
    var row = document.createElement('div');
    row.className = 'prop-row ui-pick-row' + (element.clickable === false ? ' ui-pick-blocked' : '');
    // 整行的 title = 语义目标：鼠标停在哪都能看到"这一行会被写成什么"
    // （列表行也靠它把 `list:列表@文字` 暴露出来，自检与脚本都读这个）。
    row.title = element.target || element.name || '';

    var label = document.createElement('label');
    label.title = (element.type ? element.type + L('ui.101', '　') : '') + (element.target || '');
    label.textContent = (element.name || element.type || '?') + (element.text ? L('ui.215', '  「') + element.text + L('ui.216', '」') : '');
    row.appendChild(label);

    var value = document.createElement('span');
    value.className = 'live-value';
    var point = element.clientPoint || {};
    value.textContent = (typeof point.x === 'number')
      ? (Math.round(point.x) + ',' + Math.round(point.y)) : '-';
    if (element.clickable === false) {
      value.textContent += L('ui.217', '　⛔ ') + shortReason(element);
    }
    row.appendChild(value);

    var tools = document.createElement('span');
    tools.className = 'tools';
    var useButton = document.createElement('button');
    useButton.textContent = L('picker.use', '用作节点目标');
    useButton.title = element.target || '';
    useButton.addEventListener('click', function () {
      if (uiPicker.onPick) uiPicker.onPick(element);
    });
    tools.appendChild(useButton);

    var tryButton = document.createElement('button');
    tryButton.textContent = L('picker.try', '在游戏里点一下');
    tryButton.addEventListener('click', function () {
      var target = element.rowTarget || element.target || element.name;
      if (!target) { setStatus(L('st.071', '这一行没有可用目标')); return; }
      $('uiTargetInput').value = target;
      setStatus(I18n.format("st.c096", '正在点 {0} …', target));
      api('/api/game/ui/click', {
        method: 'POST',
        body: JSON.stringify({ target: target, mode: uiClickMode(), mark: uiMarkEnabled() })
      }).then(function (data) {
        if (!data.ok) { setStatus(I18n.format("st.c085", '点不动：{0}', describeUiError(data, L('ui.233', '点击')))); return; }
        var at = data.clickPoint || {};
        setStatus(I18n.format("st.c097", '点过了：{0}（{1} @ {2},{3}）{4}', target, (data.mode || ''), Math.round(at.x), Math.round(at.y), (data.clickTargetOnTarget === false ? L('ui.259', '　注意：命中的不是目标本身') : '')));
        loadPickerElements();
      }).catch(function (error) { setStatus(I18n.format("st.c087", '点击请求失败：{0}', error.message)); });
    });
    tools.appendChild(tryButton);
    row.appendChild(tools);
    return row;
  }

  function loadPickerElements() {
    if (uiPicker.busy) return;
    uiPicker.busy = true;
    var box = uiPicker.box;
    if (box) box.innerHTML = '<p class="hint">' + L('ui.pick.busy', '⟳ 拾取中…') + '</p>';
    api('/api/game/ui/elements?max=200').then(function (data) {
      uiPicker.busy = false;
      if (!box) return;
      box.innerHTML = '';
      if (!data.ok) {
        var problem = document.createElement('div');
        problem.className = 'issue';
        problem.textContent = describeUiError(data, L('ui.214', '拾取界面元素'));
        box.appendChild(problem);
        return;
      }
      uiPicker.data = data;
      var head = document.createElement('p');
      head.className = 'hint';
      var picked = asArray(data.elements);
      head.textContent = I18n.format("ui.051", 'screen={0}　{1} 个可交互元素　{2}', screenLabel(data, picked), picked.length, L('picker.hint', L('ui.258', '点一行 → 填进目标的 target')));
      box.appendChild(head);
      if (data.truncated) {
        // 游戏侧只会给前 N 个（默认 200）；截断了就说出来，免得以为"界面上就这么点东西"
        var more = document.createElement('p');
        more.className = 'hint';
        more.textContent = I18n.format('picker.truncated', '（这里只列了前 {0} 个，界面上共 {1} 个）', picked.length, (data.totalCount || '?'));
        box.appendChild(more);
      }
      renderPickerRows(box, picked);
    }).catch(function (error) {
      uiPicker.busy = false;
      if (box) box.innerHTML = I18n.format("ui.058", '<div class="issue error">请求失败：{0}</div>', error.message);
    });
  }

  /** 打开"拾取界面目标"：选中的目标交给 onPick。 */
  function openUiTargetPicker(onPick) {
    uiPicker.onPick = onPick;
    var modal = openModal(L('picker.title', '拾取界面目标'), [
      { label: L('picker.refresh', '⟳ 刷新'), run: function () { loadPickerElements(); } },
      { label: L('picker.close', '关闭'), run: function (close) { close(); } }
    ]);
    uiPicker.box = modal.body;
    loadPickerElements();
    return modal;
  }

  /** 物料区的「🎯 拾取界面目标…」：选中的目标直接变成/更新一个 Task.UiClick 节点。 */
  function pickUiTargetIntoTree() {
    openUiTargetPicker(function (element) {
      var target = uiElementTarget(element);
      if (!target) { setStatus(L('st.072', '这一项没有可用的语义目标')); return; }
      $('uiTargetInput').value = target;
      fillUiTargetIntoTree();
    });
  }

  // ---------------------------------------------------------------- 动作包：右键菜单 / 编辑 / 重命名 / 删除

  var actionMenu = null;

  function closeActionMenu() {
    if (!actionMenu) return;
    if (actionMenu.parentNode) actionMenu.parentNode.removeChild(actionMenu);
    actionMenu = null;
    document.removeEventListener('mousedown', closeActionMenu, true);
    document.removeEventListener('keydown', closeActionMenuOnKey, true);
  }

  function closeActionMenuOnKey(event) {
    if (event.key === 'Escape') closeActionMenu();
  }

  /** 在鼠标位置弹出动作包菜单：编辑 / 删除 / 重命名（顺序按用户要求）。 */
  function showActionMenu(event, file) {
    if (event && event.preventDefault) event.preventDefault();
    if (event && event.stopPropagation) event.stopPropagation();
    closeActionMenu();

    var menu = document.createElement('div');
    menu.className = 'context-menu';
    var item = findAction(file);
    [['menu.edit', function () { openActionEditor(file); }, false],
     ['menu.delete', function () { deleteActionFile(file); }, true],
     ['menu.rename', function () { renameActionFile(file); }, false]
    ].forEach(function (entry) {
      var element = document.createElement('button');
      element.textContent = L(entry[0], entry[0]);
      element.title = L(entry[0] + '.title', '');
      if (entry[2]) element.className = 'danger';
      element.addEventListener('click', function () {
        closeActionMenu();
        entry[1]();
      });
      menu.appendChild(element);
    });

    var info = document.createElement('div');
    info.className = 'hint';
    info.style.padding = '4px 10px';
    info.textContent = (item && item.file ? item.file : file)
      + (item && item.replayable ? '' : L('ui.250', '　（仅结构，不能回放）'));
    menu.insertBefore(info, menu.firstChild);

    document.body.appendChild(menu);
    var left = (event ? event.clientX : 40) + 2;
    var top = (event ? event.clientY : 40) + 2;
    if (left + menu.offsetWidth > window.innerWidth - 8) left = window.innerWidth - menu.offsetWidth - 8;
    if (top + menu.offsetHeight > window.innerHeight - 8) top = window.innerHeight - menu.offsetHeight - 8;
    menu.style.left = Math.max(4, left) + 'px';
    menu.style.top = Math.max(4, top) + 'px';
    actionMenu = menu;
    document.addEventListener('mousedown', closeActionMenu, true);
    document.addEventListener('keydown', closeActionMenuOnKey, true);
  }

  /** 重命名动作包（manifest 里的名字跟着改；引用它的树会被点名提醒）。 */
  function renameActionFile(file) {
    var bare = String(file || '').replace(/\.scatpak$/i, '');
    var wanted = window.prompt(L('menu.rename', '重命名') + L('ui.251', '：') + file, bare);
    if (wanted === null || wanted === bare) return;
    api('/api/action/rename', {
      method: 'POST',
      body: JSON.stringify({ file: file, to: wanted })
    }).then(function (data) {
      if (!data.ok) { setStatus(I18n.format("st.c098", '重命名失败：{0}', (data.reason || data.code))); return; }
      var extra = (data.referencedByTrees || []).length
        ? L('ui.252', '　注意：这些树还在引用旧名字 —— ') + data.referencedByTrees.join(L('ui.253', '、')) : '';
      setStatus(I18n.format("st.c099", '已重命名为 {0}{1}', data.file, extra));
      loadActions();
    }).catch(function (error) { setStatus(I18n.format("st.c100", '重命名请求失败：{0}', error.message)); });
  }

  /** 删除动作包（不可撤销，先确认；还会列出引用它的树）。 */
  function deleteActionFile(file) {
    if (!window.confirm(L('menu.delete', '删除') + L('ui.251', '：') + file + L('ui.254', '？此操作不可撤销。'))) return;
    api('/api/action/delete', {
      method: 'POST',
      body: JSON.stringify({ file: file })
    }).then(function (data) {
      if (!data.ok) { setStatus(I18n.format("st.c101", '删除失败：{0}', (data.reason || data.code))); return; }
      var extra = (data.referencedByTrees || []).length
        ? L('ui.255', '　注意：这些树还在引用它 —— ') + data.referencedByTrees.join(L('ui.253', '、')) : '';
      setStatus(I18n.format("st.c102", '已删除 {0}{1}', data.file, extra));
      loadActions();
    }).catch(function (error) { setStatus(I18n.format("st.c103", '删除请求失败：{0}', error.message)); });
  }

  /**
   * 动作包编辑器：改**语义事件轨**（时间 / 类型 / 目标），其它轨道一字不动。
   *
   * 这是最该能手工改的地方 —— 录下来的是 `click:1010.6,64.83` 这种死像素，
   * 改成 `click:list:WorldsList@世界名` 之后再也不会因为窗口尺寸失效。
   */
  function openActionEditor(file) {
    api('/api/action/events?file=' + encodeURIComponent(file)).then(function (data) {
      if (!data.ok) { setStatus(I18n.format("st.c104", '打不开动作包：{0}', (data.reason || data.code))); return; }
      var events = (data.events || []).map(function (entry) {
        return { t: entry.t, kind: entry.kind, detail: entry.detail };
      });

      var modal = openModal(L('editor.title', '动作包编辑') + L('ui.101', '　') + data.file, [
        { label: L('editor.addEvent', '＋ 加一条'), run: function () {
          var last = events.length ? events[events.length - 1].t + 0.4 : 0;
          events.push({ t: Math.round(last * 1000) / 1000, kind: 'ui.click', detail: '' });
          renderRows();
        } },
        { label: L('editor.save', '保存事件'), primary: true, run: function () { save(); } },
        { label: L('editor.close', '关闭'), run: function (close) { close(); } }
      ]);

      var meta = document.createElement('p');
      meta.className = 'hint';
      meta.textContent = L('editor.file', '文件') + L('ui.251', '：') + data.file
        + L('ui.101', '　') + L('editor.meta', '时长 / 帧数') + L('ui.251', '：') + (data.duration || 0) + 's / ' + (data.frames || 0)
        + L('ui.101', '　') + L('editor.replayable', '可回放') + L('ui.251', '：') + (data.replayable ? '✔' : '✘')
        + (data.writable ? '' : L('ui.256', '　（只读目录，不能保存）'));
      modal.body.appendChild(meta);

      var hint = document.createElement('p');
      hint.className = 'hint';
      hint.innerHTML = L('editor.hint', '');
      modal.body.appendChild(hint);

      var head = document.createElement('div');
      head.className = 'event-row event-head';
      head.innerHTML = '<span class="t">' + L('editor.t', '时间(s)') + '</span>'
        + '<span class="kind">' + L('editor.kind', '类型') + '</span>'
        + '<span class="detail">' + L('editor.detail', '目标 / 说明') + '</span>'
        + '<span class="tools"></span>';
      modal.body.appendChild(head);

      var rows = document.createElement('div');
      modal.body.appendChild(rows);

      var issues = document.createElement('div');
      issues.className = 'issues';
      modal.body.appendChild(issues);

      function renderRows() {
        rows.innerHTML = '';
        events.forEach(function (entry, index) {
          rows.appendChild(eventRow(entry, index));
        });
      }

      function eventRow(entry, index) {
        var row = document.createElement('div');
        row.className = 'event-row';

        var time = document.createElement('input');
        time.className = 't';
        time.type = 'number';
        time.step = '0.001';
        time.min = '0';
        time.value = entry.t;
        time.addEventListener('change', function () {
          var value = parseFloat(time.value);
          entry.t = isNaN(value) || value < 0 ? 0 : value;
        });
        row.appendChild(time);

        var kind = document.createElement('input');
        kind.className = 'kind';
        kind.type = 'text';
        kind.value = entry.kind;
        kind.addEventListener('change', function () { entry.kind = kind.value.trim() || 'ui.click'; });
        row.appendChild(kind);

        var detail = document.createElement('input');
        detail.className = 'detail';
        detail.type = 'text';
        detail.value = entry.detail;
        detail.placeholder = L('ui.257', 'Play / [路径]/Play / list:WorldsList@世界名');
        detail.addEventListener('change', function () { entry.detail = detail.value; });
        row.appendChild(detail);

        var tools = document.createElement('span');
        tools.className = 'tools';
        // 拾取按钮：直接把这个动作包的某个点击改成"语义目标"
        var pick = document.createElement('button');
        pick.textContent = '🎯';
        pick.title = L('ui.pick', '拾取界面元素');
        pick.addEventListener('click', function () {
          openUiTargetPicker(function (element) {
            var target = uiElementTarget(element);
            if (!target) return;
            entry.detail = 'click:' + target;
            entry.kind = 'ui.click';
            renderRows();
            setStatus(I18n.format("st.c105", '这条事件改成：click:{0}', target));
          });
        });
        tools.appendChild(pick);

        var up = document.createElement('button');
        up.textContent = L('editor.up', '↑');
        up.disabled = index === 0;
        up.addEventListener('click', function () {
          if (index === 0) return;
          var swap = events[index - 1];
          events[index - 1] = events[index];
          events[index] = swap;
          renderRows();
        });
        tools.appendChild(up);

        var down = document.createElement('button');
        down.textContent = L('editor.down', '↓');
        down.disabled = index === events.length - 1;
        down.addEventListener('click', function () {
          if (index >= events.length - 1) return;
          var swap = events[index + 1];
          events[index + 1] = events[index];
          events[index] = swap;
          renderRows();
        });
        tools.appendChild(down);

        var remove = document.createElement('button');
        remove.textContent = L('editor.remove', '删');
        remove.className = 'danger';
        remove.addEventListener('click', function () {
          events.splice(index, 1);
          renderRows();
        });
        tools.appendChild(remove);

        row.appendChild(tools);
        return row;
      }

      function showIssues(list) {
        issues.innerHTML = '';
        (list || []).forEach(function (line) {
          var div = document.createElement('div');
          var text = String(line);
          div.className = 'issue ' + (/^ERROR/.test(text) ? 'error'
            : (/^WARN/.test(text) ? 'warning' : 'info'));
          div.textContent = text;
          issues.appendChild(div);
        });
      }

      function save() {
        if (!data.writable) { setStatus(L('st.077', '这是只读目录里的包，不能保存')); return; }
        api('/api/action/events', {
          method: 'POST',
          body: JSON.stringify({ file: data.file, events: events })
        }).then(function (saved) {
          if (!saved.ok) { setStatus(I18n.format("st.c059", '保存失败：{0}', (saved.reason || saved.code))); return; }
          setStatus(I18n.format('st.t041', '已保存并重新校验：{0}（{1} 条事件）',
            saved.file, saved.eventCount));
          showIssues(saved.issues);
          loadActions();
        }).catch(function (error) { setStatus(I18n.format("st.c106", '保存请求失败：{0}', error.message)); });
      }

      renderRows();
      showIssues(data.issues);
    }).catch(function (error) { setStatus(I18n.format("st.c107", '读取动作包失败：{0}', error.message)); });
  }

  // ---------------------------------------------------------------- 启动

  function boot() {
    applyTheme(themePreference());     // 先定主题，再画任何东西
    I18n.setLanguage(languagePreference());   // 语言也先定下来，免得先闪一下中文
    applyLanguage();
    $('btnTheme').addEventListener('click', toggleTheme);
    $('btnLang').addEventListener('click', toggleLanguage);
    // 动作包下拉的右键菜单（用户要求：编辑 / 删除 / 重命名）
    $('actionSelect').addEventListener('contextmenu', function (event) {
      var file = selectedActionFile();
      if (!file) return;
      showActionMenu(event, file);
    });
    $('actionSelect').title = L('action.select.title',
      '包目录 PlayerAi\\BehaviorTrees 里的 .scatpak（右键：编辑 / 删除 / 重命名）');
    $('btnNew').addEventListener('click', newTree);
    $('btnUndo').addEventListener('click', undo);
    $('btnRedo').addEventListener('click', redo);
    $('btnOpen').addEventListener('click', function () { openPackage($('packageSelect').value); });
    // 下拉里选一个包 = 直接打开它（用户反馈：以前选了没反应，还得再点「打开」）
    $('packageSelect').addEventListener('change', function () {
      openPackage($('packageSelect').value);
    });
    $('btnReload').addEventListener('click', function () { openPackage(state.path); });
    $('btnValidate').addEventListener('click', validateNow);
    $('btnSave').addEventListener('click', function () { saveNow(null); });
    $('btnSaveAs').addEventListener('click', saveAs);
    $('btnTreeRun').addEventListener('click', treeRunPrimary);
    $('btnTreeStop').addEventListener('click', stopThisTree);
    $('btnGame').addEventListener('click', showGameStatus);
    $('btnLaunchGame').addEventListener('click', launchGame);
    $('btnQuitGame').addEventListener('click', quitGame);
    $('btnLive').addEventListener('click', function () { setLiveOn(!live.timer); });
    $('btnPauseTree').addEventListener('click', function () { pauseTree(true); });
    $('btnResumeTree').addEventListener('click', function () { pauseTree(false); });
    // UI 拾取（UI-1 服务）：取真实位置 / 直接点它 / 把语义目标写进树
    $('btnUiPick').addEventListener('click', pickUiElements);
    $('btnUiLocate').addEventListener('click', function () { locateUiTarget(); });
    $('btnUiClick').addEventListener('click', clickUiTarget);
    $('btnUiFill').addEventListener('click', fillUiTargetIntoTree);
    var markToggle = $('uiMarkToggle');
    if (markToggle) {
      markToggle.checked = markPreference();
      markToggle.addEventListener('change', function () {
        try { window.localStorage.setItem('playerAiEditor.uiMark', markToggle.checked ? '1' : '0'); }
        catch (error) { /* 隐私模式存不了：无所谓，只影响下次默认值 */ }
        setStatus(markToggle.checked
          ? L('st.019', '定位/点击时会在游戏里亮 2 秒红点')
          : L('st.t029', '已关掉落点标记'));
      });
    }
    $('uiTargetInput').addEventListener('keydown', function (event) {
      if (event.key === 'Enter') { event.preventDefault(); locateUiTarget(); }
    });
    $('btnActionValidate').addEventListener('click', validateAction);
    $('btnActionPlay').addEventListener('click', playAction);
    $('btnActionStop').addEventListener('click', stopAction);
    $('btnActionCreate').addEventListener('click', createAction);
    $('actionSelect').addEventListener('change', function () {
      var action = findAction(selectedActionFile());
      if (action) setStatus(I18n.format("st.c108", '动作包 {0}', actionLabel(action)));
    });
    $('paletteFilter').addEventListener('input', function () {
      renderPalette($('paletteFilter').value.toLowerCase());
    });
    $('nodeSearch').addEventListener('input', function () { searchNodes($('nodeSearch').value); });
    $('btnView').addEventListener('click', function () {
      setView(state.view === 'graph' ? 'tree' : 'graph');
    });
    $('btnZoomIn').addEventListener('click', function () { zoomGraph(0.1); });
    $('btnZoomOut').addEventListener('click', function () { zoomGraph(-0.1); });
    $('btnFit').addEventListener('click', function () { fitGraphToWindow(true); });
    $('btnAutoLayout').addEventListener('click', resetLayout);
    $('btnGroup').addEventListener('click', function () { createGroupFromSelection(); });
    $('nodeSearch').addEventListener('keydown', function (event) {
      if (event.key === 'Enter') { event.preventDefault(); jumpToFirstMatch(); }
      if (event.key === 'Escape') { $('nodeSearch').value = ''; searchNodes(''); }
    });
    bindGlobalInput();

    window.addEventListener('beforeunload', function (event) {
      if (!state.dirty) return;
      event.preventDefault();
      event.returnValue = '';
    });

    // 切回这个标签页 / 窗口重新获得焦点时**立刻补一次**状态：
    // 用户最典型的动作就是"在游戏那边点完、切回编辑器点播放"，这一刻状态必须是最新的。
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) pollStatusQuiet();
    });
    window.addEventListener('focus', function () { pollStatusQuiet(); });

    updateHistoryUi();
    Promise.all([loadMeta(), loadSchema()])
      .then(loadPackages)
      .then(loadActions)
      .then(function () {
        if (state.packages.length) openPackage(state.packages[0].path);
        else setStatus(L('st.061', '没有找到任何 .scbtpak —— 先点"新建"造一棵，或启动游戏让它安装出厂示例。'));
        // 顺带把"游戏进程 / 控制通道"状态摸一遍：徽标要显示三态，
        // 而且如果游戏本来就在跑，能立刻反映成"已连上"
        return refreshGameProcess();
      })
      .then(function () {
        // 树徽标 / 播放按钮看的是**游戏状态**，不只是"进程在不在"：两样都要拉一次，
        // 否则页面打开时游戏刚起来，会出现"游戏徽标已连上、树徽标却说游戏没在跑"。
        refreshGameStatusQuiet();
        startStatusWatch();    // 之后低频盯着（游戏在页面之外起停也能跟上）
      })
      .catch(function (error) { setStatus(I18n.format("st.c109", '初始化失败：{0}', error.message)); });
  }

  /**
   * 全局输入接线：拖拽（document 上的 mousemove/mouseup）、窗口尺寸（resize + ResizeObserver）、
   * 编辑器快捷键。
   *
   * 为什么单独抽成一个函数：这些是"挂上去才有用"的东西，而**函数写好了没人调用**这类事故
   * 光读源码看不出来（本项目已经出过一次：采样函数写好了但没接上主循环，录出来的包根本不能回放）。
   * 抽出来之后，Mod/Packages/editor_web_selftest.js 可以用一个会记录的 DOM 桩把
   * `bindGlobalInput()` 真调一遍，再顺着桩里记下的监听器走完"按下 → 移动 → 松手"，
   * 从而证明**接线本身**是通的，而不只是"函数存在"。
   */
  function bindGlobalInput() {
    // 只接一次：重复接线会让一次 mousemove 被处理两遍，拖拽会"跳过一格"
    if (inputBound) return;
    inputBound = true;
    document.addEventListener('mousemove', handleMouseDragMove);
    document.addEventListener('mouseup', handleMouseDragEnd);
    window.addEventListener('resize', handleWindowResize);
    observeCanvasResize();
    document.addEventListener('keydown', handleEditorKey);
  }

  /** 编辑器快捷键（Ctrl+Z/Y/C/X/V/D、Delete、Esc、F2/F3、Alt+方向键）。输入框里不抢键。 */
  function handleEditorKey(event) {
    var tag = (event.target && event.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
    var key = String(event.key || '').toLowerCase();
    var selected = state.selectedId ? findNode(state.selectedId) : null;

    // Alt+方向键：完全不碰鼠标也能重排树（拖拽失灵时的兜底，见"键盘搬节点"）
    var intent = altMoveIntent(event);
    if (intent) {
      event.preventDefault();
      applyAltMove(selected, intent);
      return;
    }
    if (event.altKey) return;

    if (event.ctrlKey) {
      if (key === 'z' && !event.shiftKey) { event.preventDefault(); undo(); }
      else if (key === 'y' || (key === 'z' && event.shiftKey)) { event.preventDefault(); redo(); }
      else if (key === 'c') { event.preventDefault(); copyNode(selected, false); }
      else if (key === 'x') { event.preventDefault(); copyNode(selected, true); }
      else if (key === 'v') { event.preventDefault(); pasteSubtree(selected, null); }
      else if (key === 'd') { event.preventDefault(); duplicateNode(selected); }
      return;
    }
    if (key === 'delete' || key === 'backspace') {
      // 选中注释框时，Delete 解散组（里面的节点原地不动）
      if (state.selectedGroupId) {
        event.preventDefault();
        disbandGroup(findGroup(state.selectedGroupId));
        return;
      }
      // 选中连线时，Delete 断的是线（把子节点提升为父节点的兄弟），不是删节点
      if (state.selectedWire) {
        event.preventDefault();
        disconnectSelectedWire();
        return;
      }
      if (!selected) return;
      event.preventDefault();
      deleteNode(selected);
      return;
    }
    if (key === 'escape' && state.selectedGroupId) {
      event.preventDefault();
      state.selectedGroupId = null;
      setStatus(L('st.029', '已取消注释框选中'));
      renderTree();
      renderInspector();
      return;
    }
    if (key === 'escape' && wireDrag) {
      // 拉到一半按 Esc = 放弃这次连线（此时还没改任何数据）
      event.preventDefault();
      cancelWireDrag();
      return;
    }
    if (key === 'escape' && state.selectedWire) {
      event.preventDefault();
      state.selectedWire = null;
      setStatus(L('st.032', '已取消连线选中'));
      renderTree();
      return;
    }
    if (key === 'escape' && mouseDrag) {
      // 拖到一半按 Esc = 放弃这次拖动（此时还没改任何数据）
      event.preventDefault();
      cancelMouseDrag();
      return;
    }
    if (key === 'escape' && state.pendingMove) {
      event.preventDefault();
      cancelMove();
      return;
    }
    if (key === 'f2') {
      // 改 id：把焦点丢进属性区那个输入框并全选，省得用鼠标点
      event.preventDefault();
      var input = document.getElementById('nodeIdInput');
      if (input && input.focus) { input.focus(); if (input.select) input.select(); }
      else setStatus(L('st.012', '先选中一个节点再按 F2'));
    }
    if (key === 'f3' || (key === 'f' && event.ctrlKey)) {
      event.preventDefault();
      var box = document.getElementById('nodeSearch');
      if (box && box.focus) box.focus();
    }
  }

  // 给**无头自检**用的一小块内部接口（浏览器里没人用它，只有置了
  // `window.__PLAYER_AI_EDITOR_TEST__` 时才会挂出来）。
  //
  // 为什么需要：撤销栈、拖拽落点判定、校验问题定位、新建树的 payload —— 这些逻辑
  // "看源码"是验证不了的。暴露出来之后，`Mod/Packages/editor_web_selftest.js`
  // 可以用 node + 一个 DOM 桩把她们真跑一遍（见 README §9.5）。
  if (window.__PLAYER_AI_EDITOR_TEST__) {
    window.PlayerAiEditorTest = {
      state: state,
      history: history,
      pushHistory: pushHistory,
      undo: undo,
      redo: redo,
      resetHistory: resetHistory,
      applyDrop: applyDrop,
      dropModeFor: dropModeFor,
      issueNodeId: issueNodeId,
      activeNodeIds: activeNodeIds,
      blackboardLines: blackboardLines,
      parseSubtreeRef: parseSubtreeRef,
      findNodeIn: findNodeIn,
      toggleSubtree: toggleSubtree,
      copyNode: copyNode,
      pasteSubtree: pasteSubtree,
      duplicateNode: duplicateNode,
      deleteNode: deleteNode,
      moveSibling: moveSibling,
      indentNode: indentNode,
      outdentNode: outdentNode,
      altMoveIntent: altMoveIntent,
      applyAltMove: applyAltMove,
      cloneSubtreeForPaste: cloneSubtreeForPaste,
      subtreeInfo: subtreeInfo,
      searchNodes: searchNodes,
      nodeMatches: nodeMatches,
      jumpToFirstMatch: jumpToFirstMatch,
      layoutTree: layoutTree,
      handleSelectClick: handleSelectClick,
      selectedNodes: selectedNodes,
      deleteSelection: deleteSelection,
      wrapSelection: wrapSelection,
      subtreeExportPayload: subtreeExportPayload,
      exportSubtree: exportSubtree,
      ensureSubtreeEntries: ensureSubtreeEntries,
      graphGeometry: graphGeometry,
      graphDropMode: graphDropMode,
      renderGraph: renderGraph,
      setView: setView,
      zoomGraph: zoomGraph,
      fitScale: fitScale,
      fitGraphToWindow: fitGraphToWindow,
      scrollAfterDrag: scrollAfterDrag,
      makePannable: makePannable,
      panIntent: panIntent,
      beginPan: beginPan,
      handlePanMove: handlePanMove,
      handlePanEnd: handlePanEnd,
      cancelPan: cancelPan,
      panDragState: function () { return panDrag; },
      zoomGraphAt: zoomGraphAt,
      canvasPointFromClient: canvasPointFromClient,
      syncCanvasViewportSize: syncCanvasViewportSize,
      centerGraphScroll: centerGraphScroll,
      originPad: originPad,
      contentOrigin: contentOrigin,
      contentBounds: contentBounds,
      syncWireLayerSize: syncWireLayerSize,
      boxesBounds: boxesBounds,
      geometryForCentering: geometryForCentering,
      showDragGhost: showDragGhost,
      moveDragGhost: moveDragGhost,
      hideDragGhost: hideDragGhost,
      payloadLabel: payloadLabel,
      dropMaterialOnCanvas: dropMaterialOnCanvas,
      isMaterialPayload: isMaterialPayload,
      highlightNewDropZone: highlightNewDropZone,
      renderTree: renderTree,
      renderInspector: renderInspector,
      renderPalette: renderPalette,
      nodeAncestorOf: nodeAncestorOf,
      handleWindowResize: handleWindowResize,
      wheelIntent: wheelIntent,
      beginMove: beginMove,
      cancelMove: cancelMove,
      completeMove: completeMove,
      observeCanvasResize: observeCanvasResize,
      observeScroller: observeScroller,
      diagText: diagText,
      updateDiag: updateDiag,
      beginMouseDrag: beginMouseDrag,
      handleMouseDragMove: handleMouseDragMove,
      handleMouseDragEnd: handleMouseDragEnd,
      nodeIdFromPoint: nodeIdFromPoint,
      dragMoved: dragMoved,
      makeDraggable: makeDraggable,
      mouseDragState: function () { return mouseDrag; },
      wireDragState: function () { return wireDrag; },
      cancelMouseDrag: cancelMouseDrag,
      beginWireDrag: beginWireDrag,
      handleWireDragMove: handleWireDragMove,
      handleWireDrop: handleWireDrop,
      cancelWireDrag: cancelWireDrag,
      selectWire: selectWire,
      disconnectWire: disconnectWire,
      disconnectSelectedWire: disconnectSelectedWire,
      connectNodes: connectNodes,
      connectBlocker: connectBlocker,
      wirePath: wirePath,
      highlightWireTarget: highlightWireTarget,
      clearWireTargetHighlight: clearWireTargetHighlight,
      refreshGraphPositions: refreshGraphPositions,
      freeMoveNodes: freeMoveNodes,
      restoreDragPositions: restoreDragPositions,
      movingIds: movingIds,
      persistLayout: persistLayout,
      restoreLayout: restoreLayout,
      resetLayout: resetLayout,
      layoutKey: layoutKey,
      panIntent: panIntent,
      marqueeIntent: marqueeIntent,
      beginMarquee: beginMarquee,
      handleMarqueeMove: handleMarqueeMove,
      handleMarqueeEnd: handleMarqueeEnd,
      cancelMarquee: cancelMarquee,
      marqueeRect: marqueeRect,
      nodesInMarquee: nodesInMarquee,
      marqueeState: function () { return marqueeDrag; },
      previewSummary: previewSummary,
      previewGlyph: previewGlyph,
      previewChip: previewChip,
      updateMinimap: updateMinimap,
      minimapJumpTo: minimapJumpTo,
      minimapFit: function () { return minimapFit; },
      minimapElement: function () { return minimapElement; },
      themePreference: themePreference,
      themeFromQuery: themeFromQuery,
      createDetachedNode: createDetachedNode,
      isDetached: isDetached,
      detachNode: detachNode,
      createGroupFromSelection: createGroupFromSelection,
      groupRectFor: groupRectFor,
      groupMembers: groupMembers,
      fitGroupToMembers: fitGroupToMembers,
      renameGroup: renameGroup,
      setGroupColor: setGroupColor,
      moveGroupBy: moveGroupBy,
      disbandGroup: disbandGroup,
      selectGroup: selectGroup,
      markGroupSelection: markGroupSelection,
      findGroup: findGroup,
      groupsForNode: groupsForNode,
      beginGroupDrag: beginGroupDrag,
      handleGroupDragMove: handleGroupDragMove,
      handleGroupDragEnd: handleGroupDragEnd,
      cancelGroupDrag: cancelGroupDrag,
      groupDragState: function () { return groupDrag; },
      groupAncestorOf: groupAncestorOf,
      groupColors: function () { return GROUP_COLORS.slice(); },
      renderGroupBox: renderGroupBox,
      applyTheme: applyTheme,
      toggleTheme: toggleTheme,
      persistTheme: persistTheme,
      themeKey: function () { return THEME_KEY; },
      bindGlobalInput: bindGlobalInput,
      handleEditorKey: handleEditorKey,
      clearDropHighlight: clearDropHighlight,
      renderLivePanel: renderLivePanel,
      pollLive: pollLive,
      setLiveOn: setLiveOn,
      // 游戏状态哨兵（"游戏明明开着却说没在跑"的修复）：自检要点它、要能读回状态
      pollStatusQuiet: pollStatusQuiet,
      startStatusWatch: startStatusWatch,
      stopStatusWatch: stopStatusWatch,
      statusWatchMs: function () { return statusWatch.intervalMs; },
      treeRunPrimaryAfterRefresh: treeRunPrimaryAfterRefresh,
      // UI 拾取（UI-1 服务）：真浏览器自检要点它、要能读回拾取结果
      pickUiElements: pickUiElements,
      renderUiPick: renderUiPick,
      locateUiTarget: locateUiTarget,
      clickUiTarget: clickUiTarget,
      fillUiTargetIntoTree: fillUiTargetIntoTree,
      uiPickState: function () { return uiPick; },
      uiClickMode: uiClickMode,
      uiMarkEnabled: uiMarkEnabled,
      showLocatedLine: showLocatedLine,
      shortReason: shortReason,
      openUiTargetPicker: openUiTargetPicker,
      pickUiTargetIntoTree: pickUiTargetIntoTree,
      showActionMenu: showActionMenu,
      openActionEditor: openActionEditor,
      renameActionFile: renameActionFile,
      deleteActionFile: deleteActionFile,
      closeActionMenu: closeActionMenu,
      i18n: I18n,
      L: L,
      nodeLabel: nodeLabel,
      applyLanguage: applyLanguage,
      toggleLanguage: toggleLanguage,
      languagePreference: languagePreference,
      paletteGroups: paletteGroups,
      paletteGroupLabel: paletteGroupLabel,
      treeRunPrimary: treeRunPrimary,
      playThisTree: playThisTree,
      pushChangesToRunningTree: pushChangesToRunningTree,
      stopThisTree: stopThisTree,
      liveActiveChain: liveActiveChain,
      treeRunState: treeRunState,
      updateTreeRunUi: updateTreeRunUi,
      refreshGameStatusQuiet: refreshGameStatusQuiet,
      setPaused: setPaused,
      playAction: playAction,
      launchGame: launchGame,
      quitGame: quitGame,
      refreshGameProcess: refreshGameProcess,
      waitForGameChannel: waitForGameChannel,
      stopGameWait: stopGameWait,
      gameWaitState: function () {
        return { polling: !!gameWait.timer, startedAt: gameWait.startedAt,
          limitMs: gameWait.limitMs };
      },
      setIssues: setIssues,
      addChild: addChild,
      findNode: findNode,
      findParent: findParent,
      materialFor: materialFor,
      newTree: newTree,
      setTree: function (manifest, tree, path) {
        state.manifest = manifest;
        state.tree = tree;
        state.path = path || null;
        state.selectedId = tree ? tree.id : null;
      }
    };
  }

  document.addEventListener('DOMContentLoaded', boot);
})();
