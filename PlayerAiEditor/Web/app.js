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
    gameRunning: false
  };

  // ---------------------------------------------------------------- 工具

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
    badge.textContent = dirty ? '已修改' : '未修改';
    badge.className = 'badge' + (dirty ? ' dirty' : '');
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
    return found;
  }

  function findParent(id) {
    var found = null;
    walk(state.tree, function (node, parent) { if (node.id === id) found = parent; });
    return found;
  }

  function nextId(prefix) {
    var used = {};
    walk(state.tree, function (node) { used[node.id] = true; });
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
    if (state.actions.length) {
      groups.push(PlayerAiLowcode.actionsToMaterials(state.actions));
    }
    return groups;
  }

  function setIssues(list) {
    var box = $('issues');
    box.innerHTML = '';
    (list || []).forEach(function (line) {
      var text = String(line);
      var kind = /^ERROR/.test(text) ? 'error' : (/^WARN/.test(text) ? 'warning' : 'info');
      var div = document.createElement('div');
      div.className = 'issue ' + kind;
      div.textContent = text;
      box.appendChild(div);
    });
  }

  // ---------------------------------------------------------------- 画布

  function renderTree() {
    var root = $('treeRoot');
    root.innerHTML = '';
    if (!state.tree) {
      root.innerHTML = '<p class="hint">打开一个包开始编辑。</p>';
      return;
    }
    root.appendChild(renderNode(state.tree, 0));
    $('canvasHint').textContent = state.path ? state.path : '';
  }

  function renderNode(node, depth) {
    var wrap = document.createElement('div');
    wrap.className = 'node' + (node.id === state.selectedId ? ' selected' : '');

    var row = document.createElement('div');
    row.className = 'node-row';

    var tag = document.createElement('span');
    var material = materialFor(node.type);
    tag.className = 'tag ' + (material && material.allowsChildren ? 'composite'
      : (node.type === 'Root' ? 'root' : 'task'));
    tag.textContent = node.type || '?';
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
    if ((node.decorators || []).length) counts.push('装饰器×' + node.decorators.length);
    if ((node.services || []).length) counts.push('服务×' + node.services.length);
    if (counts.length) {
      var extra = document.createElement('span');
      extra.className = 'hint';
      extra.textContent = counts.join(' ');
      row.appendChild(extra);
    }

    row.addEventListener('click', function (event) {
      event.stopPropagation();
      state.selectedId = node.id;
      renderTree();
      renderInspector();
    });
    wrap.appendChild(row);

    if ((node.children || []).length) {
      var children = document.createElement('div');
      children.className = 'node-children';
      node.children.forEach(function (child) { children.appendChild(renderNode(child, depth + 1)); });
      wrap.appendChild(children);
    }
    return wrap;
  }

  // ---------------------------------------------------------------- 属性区

  function renderInspector() {
    var body = $('inspectorBody');
    body.innerHTML = '';
    var node = state.selectedId ? findNode(state.selectedId) : null;
    if (!node) {
      body.innerHTML = '<p class="hint">左侧选中一个节点。</p>';
      return;
    }

    var head = document.createElement('div');
    head.className = 'prop-row';
    head.innerHTML = '<label>类型</label><span class="tag">' + (node.type || '?') + '</span>';
    body.appendChild(head);

    body.appendChild(textField('id', node.id, function (value) {
      if (!value) return;
      var duplicate = value !== node.id && !!findNode(value);
      if (duplicate) { setStatus('id 已被占用：' + value); return; }
      node.id = value;
      state.selectedId = value;
      markDirty(true);
      renderTree();
    }));

    body.appendChild(textField('name（显示名）', node.name || '', function (value) {
      if (value) node.name = value; else delete node.name;
      markDirty(true);
      renderTree();
    }));

    var material = materialFor(node.type);
    var specs = material ? material.properties : [];
    if (specs.length) {
      var title = document.createElement('h2');
      title.textContent = '参数';
      title.style.marginTop = '10px';
      body.appendChild(title);
      specs.forEach(function (spec) {
        body.appendChild(propertyField(node, spec));
      });
    }

    var actions = document.createElement('div');
    actions.className = 'actions';

    if (material && material.allowsChildren) {
      actions.appendChild(button('＋ 子节点（选中物料后点这里）', function () {
        var type = prompt('子节点类型（形如 Task.Wait / Sequence）', 'Task.Wait');
        if (!type) return;
        addChild(node, type);
      }));
    }
    if (material && material.allowsServices) {
      actions.appendChild(button('＋ 服务', function () {
        var type = prompt('服务类型', 'Service.UpdateNearestPlayer');
        if (!type) return;
        node.services = node.services || [];
        node.services.push({ id: nextId('svc'), type: type, interval: 0.25, properties: {} });
        markDirty(true);
        renderTree();
        renderInspector();
      }));
    }
    actions.appendChild(button('＋ 装饰器', function () {
      var type = prompt('装饰器类型（Blackboard/Cooldown/TimeLimit/Loop/ForceSuccess/Inverter）',
        'Blackboard');
      if (!type) return;
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
      actions.appendChild(button('上移', function () { move(parent, node, -1); }));
      actions.appendChild(button('下移', function () { move(parent, node, +1); }));
      actions.appendChild(button('删除', function () {
        if (!confirm('删除节点 ' + node.id + ' 及其子树？')) return;
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
      dTitle.textContent = '装饰器';
      dTitle.style.marginTop = '10px';
      body.appendChild(dTitle);
      node.decorators.forEach(function (decorator, index) {
        body.appendChild(helperRow(decorator.type + ' #' + decorator.id, function () {
          node.decorators.splice(index, 1);
          markDirty(true);
          renderTree();
          renderInspector();
        }));
        var spec = state.materials.byType['decorator:' + decorator.type];
        (spec ? spec.properties : []).forEach(function (p) {
          body.appendChild(propertyField(decorator, p, 'properties'));
        });
        body.appendChild(enumField('观察者中断', decorator.observerAborts || 'None',
          state.schema.abortModes, function (value) {
            if (value === 'None') delete decorator.observerAborts;
            else decorator.observerAborts = value;
            markDirty(true);
          }));
      });
    }

    if ((node.services || []).length) {
      var sTitle = document.createElement('h2');
      sTitle.textContent = '服务';
      sTitle.style.marginTop = '10px';
      body.appendChild(sTitle);
      node.services.forEach(function (service, index) {
        body.appendChild(helperRow(service.type + ' #' + service.id, function () {
          node.services.splice(index, 1);
          markDirty(true);
          renderTree();
          renderInspector();
        }));
        body.appendChild(numberField('interval（秒）', service.interval, function (value) {
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
    if (!material) { setStatus('未知类型：' + type + '（物料区里没有）'); return; }
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
  }

  function shortId(type) {
    var tail = String(type || 'node').split('.').pop();
    return tail.charAt(0).toLowerCase() + tail.slice(1) + '_';
  }

  function move(parent, node, delta) {
    var index = parent.children.indexOf(node);
    var target = index + delta;
    if (target < 0 || target >= parent.children.length) return;
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
    row.appendChild(button('移除', onRemove));
    return row;
  }

  function textField(label, value, onChange) {
    var row = document.createElement('div');
    row.className = 'prop-row';
    var text = document.createElement('label');
    text.textContent = label;
    var input = document.createElement('input');
    input.type = 'text';
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
      row = textField(label + '（逗号分隔）', (current || []).join(','), function (value) {
        commit(value);
      });
      if (spec.description) row.title = spec.description;
      return row;
    }

    row = textField(label, current === undefined || current === null ? '' : current, commit);
    if (spec.description) row.title = spec.description;
    return row;
  }

  /**
   * `packages` 属性（Task.PlayActionPackage）：勾选现成的动作包，而不是让人手打文件名。
   * 不在目录里的名字也列出来（黄色"手动"），否则一保存就被悄悄清掉了。
   */
  function packagesField(owner, container, spec, current, commit) {
    var wrap = document.createElement('div');
    wrap.className = 'prop-row packages';
    var text = document.createElement('label');
    text.textContent = spec.name + (spec.required ? ' *' : '') + '（可多选）';
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
        ? '（' + (action.duration || 0) + 's/' + (action.frames || 0) + '帧）'
        : '（仅结构：不能回放）');
      item.appendChild(span);
      box.appendChild(item);
    });

    selected.forEach(function (name) {
      if (findAction(name)) return;
      var item = document.createElement('label');
      item.className = 'package-item manual';
      var span = document.createElement('span');
      span.textContent = name + '（不在动作包目录里）';
      item.appendChild(span);
      item.appendChild(button('移除', function () {
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
      title.textContent = group.label + '（' + items.length + '）';
      wrap.appendChild(title);

      items.forEach(function (item) {
        var element = button(item.kind === 'action'
          ? actionPaletteLabel(item)
          : item.type, function () {
          if (item.kind === 'action') { useAction(item); return; }
          var selected = state.selectedId ? findNode(state.selectedId) : null;
          if (item.kind === 'decorator') {
            if (!selected) { setStatus('先选中一个节点，再挂装饰器'); return; }
            selected.decorators = selected.decorators || [];
            selected.decorators.push({ id: nextId('d'), type: item.type, properties: {} });
            markDirty(true);
            renderTree();
            renderInspector();
            return;
          }
          if (item.kind === 'service') {
            if (!selected) { setStatus('先选中一个节点，再挂服务'); return; }
            selected.services = selected.services || [];
            selected.services.push({ id: nextId('svc'), type: item.type, interval: 0.25, properties: {} });
            markDirty(true);
            renderTree();
            renderInspector();
            return;
          }
          if (!state.tree) { setStatus('先打开一个包'); return; }
          var target = selected;
          if (!target) target = state.tree;
          var material = materialFor(target.type);
          if (!material || !material.allowsChildren) {
            setStatus('「' + target.type + '」不能有子节点；先选一个组合节点');
            return;
          }
          addChild(target, item.type);
        });
        element.className = 'palette-item' + (item.kind === 'action' ? ' action'
          : (item.kind === 'node' ? '' : ' helper'));
        element.title = item.kind === 'action'
          ? ('动作包：' + item.file + (item.replayable ? '' : '（仅结构，不能回放）'))
          : (item.kind + '：' + item.type);
        wrap.appendChild(element);
      });
      box.appendChild(wrap);
    });
  }

  function actionPaletteLabel(item) {
    return item.file + (item.replayable ? '' : '  ⚠不可回放');
  }

  /**
   * 点一个动作包 = 两种用法（看当前选中的是什么）：
   *   · 选中 `Task.PlayActionPackage` → 把它加进/移出这个节点的 `packages` 列表；
   *   · 选中别的、且它能挂子节点 → 挂一个播放节点出来；
   *   · 什么都没选 → 挂到根节点下。
   */
  function useAction(item) {
    if (!state.tree) { setStatus('先打开一个包'); return; }
    var selected = state.selectedId ? findNode(state.selectedId) : null;

    if (selected && selected.type === 'Task.PlayActionPackage') {
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
      setStatus((at >= 0 ? '从播放列表移除 ' : '加入播放列表 ') + item.file);
      return;
    }

    var target = selected || state.tree;
    var material = materialFor(target.type);
    if (!material || !material.allowsChildren) {
      setStatus('「' + target.type + '」不能有子节点；先选一个组合节点，或选中一个 Task.PlayActionPackage');
      return;
    }
    addChild(target, 'Task.PlayActionPackage');
    var node = state.selectedId ? findNode(state.selectedId) : null;
    if (node) {
      node.properties = node.properties || {};
      node.properties.packages = [item.file];
      markDirty(true);
      renderInspector();
    }
    setStatus('已挂上播放节点：' + item.file);
  }

  // ---------------------------------------------------------------- 动作

  function loadMeta() {
    return api('/api/meta').then(function (meta) {
      state.gameRunning = !!meta.gameRunning;
      var badge = $('gameBadge');
      badge.textContent = state.gameRunning
        ? '游戏：在线' + (meta.gameInfo && meta.gameInfo.instanceId ? '（' + meta.gameInfo.instanceId + '）' : '')
        : '游戏：未运行';
      badge.className = 'badge' + (state.gameRunning ? ' live' : '');
      $('rootInfo').textContent = '实例根：' + meta.instanceRoot + '　包目录：' + meta.folders;
    });
  }

  function loadSchema() {
    return api('/api/schema').then(function (schema) {
      state.schema = schema;
      state.materials = PlayerAiLowcode.toMaterials(schema);
      renderPalette($('paletteFilter').value.toLowerCase());
    });
  }

  function loadPackages() {
    return api('/api/packages').then(function (data) {
      state.packages = data.packages || [];
      var select = $('packageSelect');
      select.innerHTML = '';
      state.packages.forEach(function (item) {
        var option = document.createElement('option');
        option.value = item.path;
        option.textContent = item.file + (item.id ? '  [' + item.id + ']' : '')
          + '  ' + (item.nodes || 0) + '节点'
          + (item.writable ? '' : '  (只读)');
        select.appendChild(option);
      });
      if (state.path) select.value = state.path;
      setStatus('包 ' + state.packages.length + ' 个');
    });
  }

  function openPackage(path) {
    if (!path) return Promise.resolve();
    return api('/api/package?path=' + encodeURIComponent(path)).then(function (data) {
      if (!data || data.ok === false && !data.manifest) {
        setIssues([data && data.reason ? 'ERROR ' + data.reason : 'ERROR 打不开']);
        return;
      }
      state.path = data.path;
      state.manifest = data.manifest;
      state.tree = data.tree;
      state.writable = !!data.writable;
      state.selectedId = state.tree ? state.tree.id : null;
      markDirty(false);
      setIssues((data.issues || []).filter(function (line) { return /^ERROR|^WARN/.test(line); }));
      renderTree();
      renderInspector();
      setStatus('已打开 ' + data.file + (data.writable ? '' : '（只读：Mod 分发目录）'));
      $('btnSave').disabled = !data.writable;
    });
  }

  function validateNow() {
    if (!state.tree) return;
    setStatus('校验中…');
    fetch('/api/validate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manifest: state.manifest, tree: state.tree })
    }).then(function (r) { return r.json(); }).then(function (data) {
      setIssues((data.issues || []).filter(function (line) { return /^ERROR|^WARN/.test(line); }));
      setStatus(data.ok ? '校验通过' : ('校验失败：' + data.errors + ' 个错误'));
    });
  }

  function saveNow(forcePath) {
    if (!state.tree) return;
    var path = forcePath || state.path;
    if (!path) { saveAs(); return; }
    setStatus('保存中…');
    fetch('/api/package?path=' + encodeURIComponent(path) + '&overwrite=true', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manifest: state.manifest, tree: state.tree, overwrite: true })
    }).then(function (r) { return r.json(); }).then(function (data) {
      if (!data.ok) {
        setIssues((data.issues || []).concat(data.reason ? ['ERROR ' + data.reason] : []));
        setStatus('保存失败：' + (data.reason || '见下方问题'));
        return;
      }
      markDirty(false);
      state.path = data.path;
      setStatus('已保存 ' + data.path + '（' + data.bytes + ' 字节，' + (data.nodes || 0)
        + ' 节点）—— 可以点"推送热重载"让游戏立刻用上');
      loadPackages();
    });
  }

  function saveAs() {
    var suggestion = (state.manifest && state.manifest.id ? state.manifest.id : 'my_tree') + '_copy';
    var name = prompt('另存为（写进实例目录的新文件，不动原包）', suggestion);
    if (!name) return;
    var clean = name.replace(/[^A-Za-z0-9._-]/g, '_');
    if (!/\.scbtpak$/.test(clean)) clean += '.scbtpak';
    state.manifest.id = clean.replace(/\.scbtpak$/, '');
    var folder = ($('rootInfo').textContent.match(/实例根：([^\s　]+)/) || [])[1];
    var target = (folder || '.') + '/PlayerAi/BehaviorTrees/' + clean;
    saveNow(target);
  }

  function notifyGame() {
    if (!state.path) return;
    setStatus('通知游戏热重载…');
    api('/api/notify?path=' + encodeURIComponent(state.path)).then(function (data) {
      if (data.ok) setStatus('已通知游戏：' + JSON.stringify(data.game));
      else setStatus('通知失败：' + (data.reason || '?'));
    });
  }

  function showGameStatus() {
    api('/api/game/status').then(function (data) {
      if (!data.ok) { setStatus('游戏没在跑：' + data.reason); return; }
      var status = data.status || {};
      var tree = status.tree || {};
      setStatus('游戏内：mode=' + status.mode + ' 树=' + (tree.id || '-')
        + ' 节点=' + (tree.liveNodes || 0) + ' 脏=' + (tree.dirty ? '是' : '否')
        + ' 活动路径=' + (tree.activePath || '-'));
    });
  }

  // ---------------------------------------------------------------- 动作包

  function actionLabel(item) {
    return item.file
      + (item.replayable ? '' : '（仅结构：不能回放）')
      + (item.id && item.id !== item.file.replace(/\.scatpak$/, '') ? '  [' + item.id + ']' : '')
      + '  ' + (item.duration || 0) + 's/' + (item.frames || 0) + '帧'
      + (item.source === 'mod' ? '  (只读)' : '')
      + (item.shadowed ? '  (被实例同名包遮住)' : '');
  }

  function loadActions() {
    return api('/api/actions').then(function (data) {
      state.actions = data.actions || [];
      var select = $('actionSelect');
      select.innerHTML = '';
      state.actions.forEach(function (item) {
        var option = document.createElement('option');
        option.value = item.file;
        option.textContent = actionLabel(item);
        select.appendChild(option);
      });

      var replayable = state.actions.filter(function (a) { return a.replayable; }).length;
      setStatus('动作包 ' + state.actions.length + ' 个（可回放 ' + replayable + '）');
      var badge = $('actionBadge');
      if (badge) {
        badge.textContent = '动作包 ' + state.actions.length + '/可回放 ' + replayable;
        badge.className = 'badge' + (state.actions.length ? ' live' : '');
      }
      renderPalette($('paletteFilter').value.toLowerCase());
      renderInspector();
    });
  }

  function selectedActionFile() {
    var select = $('actionSelect');
    return select && select.value ? select.value : null;
  }

  function validateAction() {
    var file = selectedActionFile();
    if (!file) { setStatus('没有动作包可选（先录一个，或点"自造"）'); return; }
    setStatus('校验动作包 ' + file + '…');
    api('/api/action/validate?name=' + encodeURIComponent(file)).then(function (data) {
      var issues = (data.issues || []).map(function (line) { return String(line); });
      if (data.ok) {
        issues.unshift('INFO 动作包 ' + data.file + '：可回放=' + (data.replayable ? '是' : '否')
          + '  时长=' + data.duration + 's  帧=' + data.frames
          + '  目录=' + (data.source === 'mod' ? 'Mod 分发（只读）' : '实例')
          + '  按键=' + ((data.keys || []).join('/') || '无'));
      } else if (data.reason) {
        issues.unshift('ERROR ' + data.reason);
      }
      setIssues(issues.filter(function (line) { return /^ERROR|^WARN|^INFO/.test(line); }));
      setStatus(data.ok
        ? ('动作包可用：' + data.file + '（' + (data.replayable ? '可回放' : '不能回放') + '）')
        : ('动作包有问题：' + (data.reason || (data.errors + ' 个错误'))));
    });
  }

  function playAction() {
    var file = selectedActionFile();
    if (!file) { setStatus('没有动作包可选'); return; }
    setStatus('让游戏回放 ' + file + '…');
    fetch('/api/action/play', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: file, repeat: 1 })
    }).then(function (r) { return r.json(); }).then(function (data) {
      if (!data.ok) { setStatus('回放失败：' + (data.reason || '?')); return; }
      setStatus('游戏在回放：' + file + '  ' + JSON.stringify(data.game || {}));
    });
  }

  function stopAction() {
    fetch('/api/action/stop', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{}'
    }).then(function (r) { return r.json(); }).then(function (data) {
      setStatus(data.ok ? ('已停止回放：' + JSON.stringify(data.game || {}))
        : ('停止失败：' + (data.reason || '?')));
    });
  }

  /** 自造动作包：写一个确定内容的示例轨道进实例目录（不依赖真机录制）。 */
  function createAction() {
    var name = prompt('新动作包名字（写进实例目录的 .scatpak）', 'my_action');
    if (!name) return;
    fetch('/api/action/create', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: name })
    }).then(function (r) { return r.json(); }).then(function (data) {
      if (!data.ok) { setStatus('自造失败：' + (data.reason || '?')); return; }
      setStatus('已造出 ' + data.file + '（' + data.frames + ' 帧 / ' + data.duration
        + 's，可回放=' + (data.replayable ? '是' : '否') + '）');
      loadActions();
    });
  }

  // ---------------------------------------------------------------- 启动

  function boot() {
    $('btnOpen').addEventListener('click', function () { openPackage($('packageSelect').value); });
    $('btnReload').addEventListener('click', function () { openPackage(state.path); });
    $('btnValidate').addEventListener('click', validateNow);
    $('btnSave').addEventListener('click', function () { saveNow(null); });
    $('btnSaveAs').addEventListener('click', saveAs);
    $('btnNotify').addEventListener('click', notifyGame);
    $('btnGame').addEventListener('click', showGameStatus);
    $('btnActionValidate').addEventListener('click', validateAction);
    $('btnActionPlay').addEventListener('click', playAction);
    $('btnActionStop').addEventListener('click', stopAction);
    $('btnActionCreate').addEventListener('click', createAction);
    $('actionSelect').addEventListener('change', function () {
      var action = findAction(selectedActionFile());
      if (action) setStatus('动作包 ' + actionLabel(action));
    });
    $('paletteFilter').addEventListener('input', function () {
      renderPalette($('paletteFilter').value.toLowerCase());
    });
    window.addEventListener('beforeunload', function (event) {
      if (!state.dirty) return;
      event.preventDefault();
      event.returnValue = '';
    });

    Promise.all([loadMeta(), loadSchema()])
      .then(loadPackages)
      .then(loadActions)
      .then(function () {
        if (state.packages.length) openPackage(state.packages[0].path);
        else setStatus('没有找到任何 .scbtpak —— 先启动游戏让它安装出厂示例。');
      })
      .catch(function (error) { setStatus('初始化失败：' + error.message); });
  }

  document.addEventListener('DOMContentLoaded', boot);
})();
