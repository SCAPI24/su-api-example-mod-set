/*
 * lowcode-engine 适配层（可选）——D15 spike 的落点。
 *
 * 结论（有据可查，见 doc/player-ai-references.md §2.2 的更正）：
 *   · 真实包名是 @alilc/lowcode-engine（1.3.4），npm 上 **不叫** @alibaba/lowcode-engine；
 *   · 它发布的是 CJS/ESM（lib/engine-core.js、es/engine-core.js），**没有现成 UMD**；
 *   · 想真正用起来还需要 @alilc/lowcode-engine-ext（1.0.6），而它 peer 依赖
 *     **react ^16.3.0 + @alifd/next 1.x**，并且要一个打包器 → 离线内嵌的成本是
 *     "React16 + Fusion + 打包链 + 十几 MB 资源"，还要把我们的 tree.json 映射成
 *     低代码搭建协议（我们的包格式与它并不同构）。
 *   · 所以本期**默认用自研渲染**（app.js：物料/画布/属性三分栏，零依赖、随 exe 内嵌、离线可用），
 *     但把渲染层做成了可替换的一层：本文件就是挂载点 —— 如果以后确实想上 lowcode-engine，
 *     只需要在这里把方案对象（schema + 树）喂给它，UI 其余部分（打开/校验/保存/推送）不用动。
 *
 * 现在它做一件小事：把 schema 归一成"物料描述列表"，无论谁来渲染都用这一份。
 */
(function (global) {
  'use strict';

  function toMaterials(schema) {
    var materials = { groups: [], byType: {} };
    if (!schema) return materials;

    var shapes = [
      { key: 'root', label: '根' },
      { key: 'composite', label: '组合' },
      { key: 'task', label: '任务' }
    ];

    (schema.nodes || []).forEach(function (node) {
      var groupKey = node.shape === 'root' ? 'root'
        : (node.allowsChildren ? 'composite' : 'task');
      var group = materials.groups.filter(function (g) { return g.key === groupKey; })[0];
      if (!group) {
        group = { key: groupKey, label: groupKey === 'root' ? '根' : (groupKey === 'composite' ? '组合' : '任务'), items: [] };
        materials.groups.push(group);
      }
      var material = {
        kind: 'node',
        type: node.type,
        shape: node.shape,
        allowsChildren: !!node.allowsChildren,
        allowsServices: !!node.allowsServices,
        properties: node.properties || []
      };
      group.items.push(material);
      materials.byType[node.type] = material;
    });

    materials.groups.push({
      key: 'decorator',
      label: '装饰器',
      items: (schema.decorators || []).map(function (d) {
        var m = { kind: 'decorator', type: d.type, properties: d.properties || [] };
        materials.byType['decorator:' + d.type] = m;
        return m;
      })
    });

    materials.groups.push({
      key: 'service',
      label: '服务',
      items: (schema.services || []).map(function (s) {
        var m = { kind: 'service', type: s.type, properties: s.properties || [] };
        materials.byType['service:' + s.type] = m;
        return m;
      })
    });

    return materials;
  }

  /* 渲染层接口：只要能 setTree/render/onSelect 就能替换。
     自研渲染在 app.js 里实现；这里保留一个注册点。 */
  var renderer = null;
  function registerRenderer(factory) {
    renderer = factory;
  }
  function getRenderer() {
    return renderer;
  }

  /*
   * 动作包（.scatpak）也是**物料**：它不是节点类型，而是"录好的输入轨道"。
   * 拖/点它 = 造一个 Task.PlayActionPackage 节点（或用它填 packages 属性）。
   * 单独一个分组，让"行为树决定做什么 / 动作包提供怎么做"在 UI 上就看得见。
   */
  function actionsToMaterials(actions) {
    var items = (actions || []).map(function (action) {
      return {
        kind: 'action',
        type: 'Task.PlayActionPackage',
        file: action.file,
        id: action.id,
        replayable: !!action.replayable,
        duration: action.duration,
        frames: action.frames,
        source: action.source,
        writable: !!action.writable,
        properties: []
      };
    });
    return {
      key: 'action',
      label: '动作包（录好的输入轨道）',
      items: items
    };
  }

  global.PlayerAiLowcode = {
    toMaterials: toMaterials,
    actionsToMaterials: actionsToMaterials,
    registerRenderer: registerRenderer,
    getRenderer: getRenderer,
    /** 供调试：当前是否挂了 lowcode-engine 外壳 */
    engineMounted: function () { return false; }
  };
})(window);
