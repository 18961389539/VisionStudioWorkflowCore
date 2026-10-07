/**
 * UI message dictionary for the VisionStudio shell.
 *
 * Every user-visible string that used to be hard-coded in a component lives here with
 * both Chinese and English variants. Keys are typed, so a typo or a missing key is a
 * compile error instead of a silent fallback.
 *
 * Placeholders use `{name}` and are filled by `translate()` in ./index.ts.
 *
 * Note: catalog labels (node names, parameter labels, options, descriptions) and runtime
 * status words are NOT here — they are backend-provided English strings translated by
 * ./catalog.ts, where English mode passes the backend text through unchanged.
 */
export const messages = {
  // ── 通用 / common ─────────────────────────────────────────────────────────
  'common.none': { zh: '无', en: 'none' },
  'common.operationFailed': { zh: '操作失败', en: 'Operation failed' },

  // ── App 顶部工具栏 / top bar ───────────────────────────────────────────────
  'app.catalogUnavailable': { zh: '节点目录不可用', en: 'Node catalog unavailable' },
  'app.catalogFallbackWarning': {
    zh: '后端 Catalog 不可用；保留当前 Catalog（首次启动时使用自动生成 fallback）',
    en: 'Backend catalog unavailable; keeping the current catalog (the generated fallback is used on first launch)'
  },
  'app.pluginsRescanDone': {
    zh: '插件重新扫描完成 · 已加载 {loaded}{restart}',
    en: 'Plugin rescan complete · {loaded} loaded{restart}'
  },
  'app.pluginsRescanRestartSuffix': { zh: ' · 需要重启 {count}', en: ' · {count} require restart' },
  'app.pluginsRescanFailed': { zh: '插件重新扫描失败', en: 'Plugin rescan failed' },
  'app.catalogTag': { zh: '节点目录 · {source} {count}', en: 'Node catalog · {source} {count}' },
  'app.catalogSourceRuntime': { zh: '运行时', en: 'runtime' },
  'app.catalogSourceLocal': { zh: '本地', en: 'local' },
  'app.pluginsTag': { zh: '插件 {count}', en: 'Plugins {count}' },
  'app.moduleDisplayName': { zh: '可复用模块 · {name} · V{version}', en: 'Reusable module · {name} · V{version}' },
  'app.moduleCategory': { zh: '模块', en: 'Module' },
  'app.moduleDescription': {
    zh: '不可变的可复用工作流模块调用。固定版本会在编译 / 执行前展开。',
    en: 'Immutable reusable Workflow Module call. The pinned module version is expanded before compile/execution.'
  },
  'app.previewTitle': { zh: '图像查看器 · 感兴趣区域 / 覆盖层', en: 'Image viewer · ROI / overlays' },
  'app.dslModalTitle': { zh: '已编译的工作流 DSL', en: 'Compiled workflow DSL' },

  // ── 顶栏特性标签 / feature tags ───────────────────────────────────────────
  'tag.workflowEngine': { zh: '工作流引擎', en: 'Workflow engine' },
  'tag.recursiveIr': { zh: '递归中间表示', en: 'Recursive IR' },
  'tag.typedVisionData': { zh: '强类型视觉数据', en: 'Typed vision data' },
  'tag.imageViewer': { zh: '图像查看器', en: 'Image viewer' },
  'tag.acquisitionPipeline': { zh: '采集流水线', en: 'Acquisition pipeline' },
  'tag.jobVersions': { zh: '作业版本', en: 'Job versions' },
  'tag.traceability': { zh: '追溯', en: 'Traceability' },
  'tag.visionToolSdk': { zh: '视觉工具 SDK', en: 'Vision tool SDK' },
  'tag.typedGeometry': { zh: '强类型几何', en: 'Typed geometry' },
  'tag.calibrationFrames': { zh: '标定 / 坐标系', en: 'Calibration / frames' },
  'tag.robotGuidance2d': { zh: '二维机器人引导', en: '2D robot guidance' },
  'tag.deviceTags': { zh: '设备标签', en: 'Device tags' },
  'tag.liveDiagnostics': { zh: '实时诊断', en: 'Live diagnostics' },

  // ── 顶栏按钮 / toolbar buttons ────────────────────────────────────────────
  'button.validate': { zh: '校验', en: 'Validate' },
  'button.coreDsl': { zh: '核心 DSL', en: 'Core DSL' },
  'button.plugins': { zh: '插件', en: 'Plugins' },
  'button.cameras': { zh: '相机', en: 'Cameras' },
  'button.robots': { zh: '机器人', en: 'Robots' },
  'button.devices': { zh: '设备', en: 'Devices' },
  'button.calibration': { zh: '标定', en: 'Calibration' },
  'button.frames': { zh: '坐标系', en: 'Frames' },
  'button.jobs': { zh: '作业', en: 'Jobs' },
  'button.traces': { zh: '追溯', en: 'Traces' },
  'button.datasetValidation': { zh: '数据集验证', en: 'Dataset validation' },
  'button.parameterTuning': { zh: '参数调优', en: 'Parameter tuning' },
  'button.modules': { zh: '可复用模块', en: 'Reusable modules' },
  'button.diagnostics': { zh: '诊断', en: 'Diagnostics' },
  'button.hardware': { zh: '硬件信息', en: 'Hardware' },
  'button.production': { zh: '生产', en: 'Production' },
  'button.breakpoint': { zh: '断点', en: 'Breakpoint' },
  'button.runNode': { zh: '运行节点', en: 'Run node' },
  'button.runFromHere': { zh: '从此处运行', en: 'Run from here' },
  'button.debug': { zh: '调试', en: 'Debug' },
  'button.load': { zh: '加载', en: 'Load' },
  'button.save': { zh: '保存', en: 'Save' },
  'button.run': { zh: '运行', en: 'Run' },

  // ── 示例选择器 / demo selector ────────────────────────────────────────────
  'demo.camera': { zh: '相机示例', en: 'Camera demo' },
  'demo.cameraTrigger': { zh: '触发式相机', en: 'Triggered camera' },
  'demo.frameSet': { zh: '同步帧组', en: 'Synchronized frame set' },
  'demo.measurement': { zh: '测量示例', en: 'Measurement demo' },
  'demo.calibration': { zh: '标定示例', en: 'Calibration demo' },
  'demo.robotEyeToHand': { zh: '眼在手外示例', en: 'Eye-to-hand demo' },
  'demo.robotEyeInHand': { zh: '眼在手上示例', en: 'Eye-in-hand demo' },
  'demo.robotRuntime': { zh: '机器人运行时', en: 'Robot runtime' },
  'demo.robotPlc': { zh: '机器人 PLC 握手', en: 'Robot PLC handshake' },
  'demo.robotTcp': { zh: '机器人 TCP 模拟器', en: 'Robot TCP simulator' },
  'demo.devicePlc': { zh: '设备 / PLC 运行时', en: 'Device / PLC runtime' },
  'demo.linear': { zh: '线性流程示例', en: 'Linear flow demo' },
  'demo.roi': { zh: '感兴趣区域示例', en: 'ROI demo' },
  'demo.if': { zh: '条件分支示例', en: 'If-branch demo' },
  'demo.parallel': { zh: '并行流程示例', en: 'Parallel flow demo' },
  'demo.nested': { zh: '嵌套流程示例', en: 'Nested flow demo' },

  // ── 编辑器交互消息 / editor messages ─────────────────────────────────────
  'editor.portTypeMismatch': { zh: '端口类型不匹配: {from} → {to}', en: 'Port type mismatch: {from} → {to}' },
  'editor.runFailed': { zh: '运行失败', en: 'Run failed' },
  'editor.selectNodeFirst': { zh: '先选择一个节点', en: 'Select a node first' },
  'editor.breakpointRequired': { zh: '请先为至少一个节点设置断点', en: 'Set a breakpoint on at least one node first' },
  'editor.validationPassed': {
    zh: '流程校验通过 · {nodes} 个节点 · {pipelines} 个流水线 · {regions} 个结构化区域',
    en: 'Validation passed · {nodes} nodes · {pipelines} pipelines · {regions} structured regions'
  },
  'editor.validationFailed': { zh: '校验失败', en: 'Validation failed' },
  'editor.compileFailed': { zh: '编译失败', en: 'Compilation failed' },
  'editor.moduleInserted': { zh: '已插入模块 {id} V{version}', en: 'Inserted module {id} V{version}' },
  'editor.moduleUpgraded': { zh: '已将选中的模块调用升级到 {id} V{version}', en: 'Upgraded the selected module call to {id} V{version}' },
  'editor.workflowSaved': { zh: '工作流已保存为后端 JSON', en: 'Workflow saved as backend JSON' },
  'editor.saveFailed': { zh: '保存失败', en: 'Save failed' },
  'editor.workflowNotSavedYet': { zh: '后端尚未保存当前示例工作流', en: 'The backend has not saved the current demo workflow yet' },
  'editor.workflowLoaded': { zh: '工作流已加载', en: 'Workflow loaded' },
  'editor.selectPlanarCalibrationNode': { zh: '请先选择“平面标定”节点', en: 'Select a "Planar calibration" node first' },
  'editor.tuningApplied': { zh: '已将调优后的候选方案应用到设计器', en: 'Applied the tuned candidate to the designer' },
  'editor.moduleReplaced': { zh: '已将所选节点替换为固定版本的可复用模块', en: 'Replaced the selected node with a pinned reusable module' },
  'editor.moduleInternalsLoaded': { zh: '已将模块内部节点加载到设计器', en: 'Loaded the module internals into the designer' },

  // ── 面板通用标题 / panel titles ──────────────────────────────────────────
  'panel.toolbox': { zh: '节点工具箱', en: 'Node toolbox' },
  'panel.properties': { zh: '属性', en: 'Properties' },
  'panel.selectNode': { zh: '选择一个节点', en: 'Select a node' },

  // ── 属性面板 / property panel ────────────────────────────────────────────
  'property.overlay': { zh: '覆盖层', en: 'Overlay' },
  'property.deterministic': { zh: '确定性执行', en: 'Deterministic' },
  'property.roiHint': { zh: '感兴趣区域 · 在图像查看器中编辑', en: 'ROI · edit in the image viewer' },
  'property.noParameters': { zh: '无可配置参数', en: 'No configurable parameters' },

  // ── 节点状态 / node status ───────────────────────────────────────────────
  'node.status.ok': { zh: '成功', en: 'Success' },
  'node.status.warmup': { zh: '预热', en: 'Warmup' },
  'node.status.paused': { zh: '暂停', en: 'Paused' },
  'node.status.error': { zh: '错误', en: 'Error' },
  'node.status.running': { zh: '运行中', en: 'Running' },
  'node.status.idle': { zh: '空闲', en: 'Idle' },
  'node.breakpoint': { zh: '断点', en: 'Breakpoint' },

  // ── 图像查看器 / image viewer ────────────────────────────────────────────
  'viewer.tool.pan': { zh: '平移', en: 'Pan' },
  'viewer.tool.rect': { zh: '矩形', en: 'Rect' },
  'viewer.tool.rectTitle': { zh: '矩形选区', en: 'Rectangle ROI' },
  'viewer.tool.circle': { zh: '圆形', en: 'Circle' },
  'viewer.tool.circleTitle': { zh: '圆形选区', en: 'Circle ROI' },
  'viewer.tool.polygon': { zh: '多边形', en: 'Polygon' },
  'viewer.tool.polygonTitle': { zh: '多边形选区 · 双击完成', en: 'Polygon ROI · double-click to finish' },
  'viewer.fit': { zh: '适应窗口', en: 'Fit' },
  'viewer.oneToOne': { zh: '原始比例', en: '1:1' },
  'viewer.clearRoi': { zh: '清除选区', en: 'Clear ROI' },
  'viewer.showOverlay': { zh: '显示覆盖层', en: 'Show overlays' },
  'viewer.roiTarget': { zh: '选区 → {target}', en: 'ROI → {target}' },
  'viewer.selectImageNode': { zh: '请选择图像节点', en: 'select an image node' },
  'viewer.placeholder': { zh: '运行工作流以显示图像和矢量覆盖层', en: 'Run the workflow to show the image and vector overlays' },
  'viewer.running': { zh: '运行中…', en: 'Running…' },

  // ── 运行结果面板 / run panel ─────────────────────────────────────────────
  'run.title': { zh: '运行 / 调试结果', en: 'Run / debug results' },
  'run.empty': { zh: '点击“运行”“运行节点”或“调试”来执行工作流', en: 'Click "Run", "Run node" or "Debug" to execute the workflow' },
  'run.summary': { zh: '{state} · {quality} · {duration} 毫秒', en: '{state} · {quality} · {duration} ms' },
  'run.succeeded': { zh: '执行成功', en: 'Succeeded' },
  'run.failed': { zh: '执行失败', en: 'Execution failed' },
  'run.quality': { zh: '检测判定：{quality}', en: 'Quality disposition: {quality}' },
  'run.column.phase': { zh: '阶段', en: 'Phase' },
  'run.column.node': { zh: '节点', en: 'Node' },
  'run.column.duration': { zh: '耗时', en: 'Duration' },
  'run.column.summary': { zh: '摘要', en: 'Summary' },
  'run.phase.warmup': { zh: '预热', en: 'Warmup' },
  'run.phase.execute': { zh: '运行', en: 'Run' },

  // ── 作业 / 配方面板 / job panel ──────────────────────────────────────────
  'job.title': { zh: '产品 / 配方参数 · V0.51', en: 'Products / recipe parameters · V0.51' },
  'job.intro.message': { zh: '一套标准工作流 + 产品 / 配方参数集', en: 'One standard workflow + product / recipe parameter sets' },
  'job.intro.description': {
    zh: '参数草稿可随时编辑。保存草稿版本时会将绑定映射和参数快照固化到不可变的有效工作流中。验证和生产运行只使用该固化版本。',
    en: 'Parameter drafts can be edited at any time. Saving a draft version freezes the binding map and parameter snapshot into an immutable effective workflow. Validation and production runs only ever use that frozen version.'
  },
  'job.error.jsonObject': { zh: '参数必须是 JSON 对象。', en: 'Parameters must be a JSON object.' },
  'job.error.selectProduct': { zh: '请先选择产品。', en: 'Select a product first.' },
  'job.error.selectOrCreateProduct': { zh: '请先选择或创建产品。', en: 'Select or create a product first.' },
  'job.error.saveProductParameters': { zh: '保存产品参数失败', en: 'Failed to save product parameters' },
  'job.error.saveRecipeParameters': { zh: '保存配方参数失败', en: 'Failed to save recipe parameters' },
  'job.error.resolveBindings': { zh: '解析参数绑定失败', en: 'Failed to resolve parameter bindings' },
  'job.error.createProduct': { zh: '创建产品失败', en: 'Failed to create product' },
  'job.error.createRecipe': { zh: '创建配方失败', en: 'Failed to create recipe' },
  'job.error.saveVersion': { zh: '保存版本失败', en: 'Failed to save version' },
  'job.error.load': { zh: '加载失败', en: 'Load failed' },
  'job.error.linkValidation': { zh: '关联验证记录失败', en: 'Failed to link the validation record' },
  'job.error.publish': { zh: '发布失败', en: 'Publish failed' },
  'job.error.clone': { zh: '复制配方失败', en: 'Failed to clone the recipe' },
  'job.error.diff': { zh: '版本差异比较失败', en: 'Version diff failed' },
  'job.error.dependencyValidation': { zh: '依赖校验失败', en: 'Dependency validation failed' },
  'job.error.runPublished': { zh: '已发布版本运行失败', en: 'Failed to run the published version' },
  'job.error.legacyNeedsProduct': { zh: '旧版作业需要先在产品下重新创建。', en: 'Legacy jobs must be recreated under a product first.' },
  'job.error.noValidationCandidate': {
    zh: '没有与此不可变工作流哈希匹配的已完成数据集验证记录。请先运行数据集验证。',
    en: 'No completed dataset validation matches this immutable workflow hash. Run dataset validation first.'
  },
  'job.error.validationRejected': { zh: '验证策略未通过：{reason}', en: 'Validation policy not satisfied: {reason}' },
  'job.error.dependencyDrift': { zh: '运行时依赖发生变化', en: 'Runtime dependencies have changed' },
  'job.productCreated': { zh: '产品已创建', en: 'Product created' },
  'job.recipeCreated': { zh: '配方已创建为不可变的 V1 版本', en: 'Recipe created as an immutable V1 version' },
  'job.recipeCloned': { zh: '配方已复制', en: 'Recipe cloned' },
  'job.bindingsResolved': { zh: '已解析 {count} 个绑定', en: 'Resolved {count} bindings' },
  'job.draftCreated': { zh: '草稿 V{version} 已创建', en: 'Draft V{version} created' },
  'job.validationAccepted': { zh: 'V{version} 已通过验证 · {runId}', en: 'V{version} accepted · {runId}' },
  'job.published': { zh: '已发布 → V{version}', en: 'Published → V{version}' },
  'job.rolledBack': { zh: '已回滚 → V{version}', en: 'Rolled back → V{version}' },
  'job.effectiveLoaded': {
    zh: '已加载 {id} V{version} 的有效工作流，可用于验证和调试',
    en: 'Loaded the effective workflow of {id} V{version} for validation and debugging'
  },
  'job.baseLoaded': {
    zh: '已加载 {id} V{version} 的基础工作流，可编辑参数',
    en: 'Loaded the base workflow of {id} V{version} for parameter editing'
  },
  'job.dependenciesMatch': { zh: '运行时依赖一致', en: 'Runtime dependencies match' },
  'job.publishedRunDone': { zh: '当前生效配方已运行', en: 'The active recipe has been executed' },
  'job.prompt.newRecipeId': { zh: '新配方 ID', en: 'New recipe ID' },
  'job.prompt.newRecipeCode': { zh: '新配方编码', en: 'New recipe code' },
  'job.diff.noChanges': { zh: '没有影响执行语义的变更。', en: 'No changes affecting execution semantics.' },
  'job.section.products': { zh: '产品 / 配方', en: 'Products / recipes' },
  'job.field.productId': { zh: '产品 ID', en: 'Product ID' },
  'job.field.name': { zh: '名称', en: 'Name' },
  'job.field.recipeId': { zh: '配方 ID', en: 'Recipe ID' },
  'job.field.code': { zh: '编码', en: 'Code' },
  'job.field.description': { zh: '说明', en: 'Description' },
  'job.field.changeNote': { zh: '变更说明', en: 'Change note' },
  'job.action.createProduct': { zh: '创建产品', en: 'Create product' },
  'job.action.createRecipe': { zh: '创建配方', en: 'Create recipe' },
  'job.action.clone': { zh: '复制', en: 'Clone' },
  'job.action.validateDependencies': { zh: '校验依赖', en: 'Validate dependencies' },
  'job.action.runActive': { zh: '运行当前版本', en: 'Run active version' },
  'job.action.saveDraftVersion': { zh: '保存草稿版本', en: 'Save draft version' },
  'job.action.base': { zh: '基础版', en: 'Base' },
  'job.action.effective': { zh: '有效版', en: 'Effective' },
  'job.action.validate': { zh: '验证', en: 'Validate' },
  'job.action.publish': { zh: '发布', en: 'Publish' },
  'job.action.rollback': { zh: '回滚', en: 'Roll back' },
  'job.action.compare': { zh: '对比', en: 'Compare' },
  'job.filter.all': { zh: '全部 / 旧版作业', en: 'All / legacy jobs' },
  'job.recipeCount': { zh: '{name} · {count} 个配方', en: '{name} · {count} recipes' },
  'job.column.recipe': { zh: '配方', en: 'Recipe' },
  'job.column.status': { zh: '状态', en: 'Status' },
  'job.column.version': { zh: '版本', en: 'Version' },
  'job.column.lifecycle': { zh: '生命周期', en: 'Lifecycle' },
  'job.column.validation': { zh: '验证', en: 'Validation' },
  'job.column.createdAt': { zh: '创建时间', en: 'Created' },
  'job.column.bindings': { zh: '绑定数', en: 'Bindings' },
  'job.column.actions': { zh: '操作', en: 'Actions' },
  'job.legacy': { zh: '旧版', en: 'Legacy' },
  'job.legacyJob': { zh: '旧版作业', en: 'Legacy job' },
  'job.tag.active': { zh: '生效 V{version}', en: 'Active V{version}' },
  'job.tag.validated': { zh: '已验证', en: 'Validated' },
  'job.tag.draft': { zh: '草稿', en: 'Draft' },
  'job.tag.published': { zh: '已发布', en: 'Published' },
  'job.tag.rejected': { zh: '未通过', en: 'Rejected' },
  'job.validation.passed': { zh: '通过', en: 'Passed' },
  'job.validation.failed': { zh: '失败', en: 'Failed' },
  'job.validation.notLinked': { zh: '未关联', en: 'Not linked' },
  'job.detail.activeVersion': { zh: '当前生效 {version}', en: 'Active {version}' },
  'job.section.createFromDesigner': { zh: '从设计器创建配方', en: 'Create recipe from designer' },
  'job.empty.selectRecipe': { zh: '请选择配方', en: 'Select a recipe' },
  'job.section.bindings': { zh: '配方参数绑定', en: 'Recipe parameter bindings' },
  'job.binding.warningMessage': { zh: '草稿参数不会直接修改已发布版本', en: 'Draft parameters never modify published versions directly' },
  'job.binding.warningDescription': {
    zh: '先保存参数草稿，再将节点参数映射到 product.* / recipe.*，解析预览后保存草稿版本。不可变版本会固化具体参数值和有效工作流哈希。',
    en: 'Save parameter drafts first, then map node parameters to product.* / recipe.*, resolve a preview, and save a draft version. Immutable versions freeze the concrete parameter values and the effective workflow hash.'
  },
  'job.binding.productDraft': { zh: '产品参数草稿', en: 'Product parameter draft' },
  'job.binding.recipeDraft': { zh: '配方参数草稿', en: 'Recipe parameter draft' },
  'job.binding.saveProduct': { zh: '保存产品参数', en: 'Save product parameters' },
  'job.binding.saveRecipe': { zh: '保存配方参数', en: 'Save recipe parameters' },
  'job.binding.nodeParameterPlaceholder': { zh: '节点参数', en: 'Node parameter' },
  'job.binding.scope.product': { zh: '产品', en: 'Product' },
  'job.binding.scope.recipe': { zh: '配方', en: 'Recipe' },
  'job.binding.keyPlaceholder': { zh: '参数键', en: 'Parameter key' },
  'job.binding.add': { zh: '添加绑定', en: 'Add binding' },
  'job.binding.resolve': { zh: '解析预览', en: 'Resolve preview' },
  'job.binding.loadEffective': { zh: '加载有效版本到设计器', en: 'Load effective version into designer' },
  'job.binding.column.target': { zh: '工作流参数', en: 'Workflow parameter' },
  'job.binding.column.source': { zh: '参数来源', en: 'Source' },
  'job.binding.remove': { zh: '移除', en: 'Remove' },
  'job.section.history': { zh: '发布历史', en: 'Publication history' },

  // ── 诊断中心 / diagnostics ───────────────────────────────────────────────
  'diagnostics.title': { zh: '诊断中心 · 资产健康 / 实时事件', en: 'Diagnostics · Asset health / live events' },
  'diagnostics.filter.all': { zh: '全部', en: 'All' },
  'diagnostics.filter.camera': { zh: '相机', en: 'Cameras' },
  'diagnostics.filter.device': { zh: '设备', en: 'Devices' },
  'diagnostics.filter.robot': { zh: '机器人', en: 'Robots' },
  'diagnostics.liveLabel': { zh: '实时连接 {state}', en: 'Live connection {state}' },
  'diagnostics.live.connected': { zh: '已连接', en: 'connected' },
  'diagnostics.live.reconnecting': { zh: '正在重连', en: 'reconnecting' },
  'diagnostics.live.connecting': { zh: '连接中', en: 'connecting' },
  'diagnostics.live.offline': { zh: '已断开', en: 'disconnected' },
  'diagnostics.refresh': { zh: '刷新状态', en: 'Refresh' },
  'diagnostics.note': {
    zh: '仅当状态、健康度或错误发生变化时才会发送事件，不会在每次轮询时重复发送。',
    en: 'Events are emitted only when state, health or error changes — not on every poll.'
  },
  'diagnostics.stats.total': { zh: '资产总数', en: 'Total assets' },
  'diagnostics.stats.healthy': { zh: '正常', en: 'Healthy' },
  'diagnostics.stats.degraded': { zh: '降级', en: 'Degraded' },
  'diagnostics.stats.faulted': { zh: '故障', en: 'Faulted' },
  'diagnostics.stats.offline': { zh: '离线', en: 'Offline' },
  'diagnostics.section.health': { zh: '资产健康状况', en: 'Asset health' },
  'diagnostics.section.events': { zh: '实时事件流', en: 'Live event stream' },
  'diagnostics.column.kind': { zh: '类别', en: 'Category' },
  'diagnostics.column.asset': { zh: '资产', en: 'Asset' },
  'diagnostics.column.level': { zh: '健康度', en: 'Health' },
  'diagnostics.column.state': { zh: '状态', en: 'State' },
  'diagnostics.column.activity': { zh: '活动', en: 'Activity' },
  'diagnostics.column.driver': { zh: '驱动', en: 'Driver' },
  'diagnostics.column.error': { zh: '错误', en: 'Error' },
  'diagnostics.column.time': { zh: '时间', en: 'Time' },
  'diagnostics.column.severity': { zh: '级别', en: 'Severity' },
  'diagnostics.column.event': { zh: '事件', en: 'Event' },
  'diagnostics.column.message': { zh: '消息', en: 'Message' },

  // ── 坐标系树 / frame tree ────────────────────────────────────────────────
  'frames.title': { zh: '坐标系树', en: 'Frame tree' },
  'frames.error.resolve': { zh: '坐标路径解析失败', en: 'Frame path resolution failed' },
  'frames.tag.static': { zh: '静态坐标系图', en: 'Static frame graph' },
  'frames.source': { zh: '源坐标系', en: 'Source frame' },
  'frames.target': { zh: '目标坐标系', en: 'Target frame' },
  'frames.resolve': { zh: '解析路径', en: 'Resolve path' },
  'frames.addTransform': { zh: '添加变换', en: 'Add transform' },
  'frames.column.unit': { zh: '单位', en: 'Unit' },
  'frames.column.angle': { zh: '角度 °', en: 'Angle °' },
  'frames.delete': { zh: '删除', en: 'Delete' },
  'frames.resolved': { zh: '解析结果：', en: 'Resolved:' },
  'frames.note': {
    zh: '眼在手上等动态坐标变换（例如 Tool → RobotBase）会在工作流中根据机器人当前位姿生成，不应添加到此静态列表中。',
    en: 'Dynamic transforms such as eye-in-hand (e.g. Tool → RobotBase) are generated by the workflow from the current robot pose and must not be added to this static list.'
  },

  // ── 存储维护 / storage maintenance ───────────────────────────────────────
  'storage.title': { zh: '存储维护 · Schema / 容量 / 备份 / 还原', en: 'Storage maintenance · Schema / capacity / backup / restore' },
  'storage.error.adminRequired': { zh: '备份与还原操作需要管理员角色。', en: 'Administrator role is required for backup and restore operations.' },
  'storage.backupCreated': { zh: '备份 {id} 已创建', en: 'Backup {id} created' },
  'storage.backupFailed': { zh: '备份失败', en: 'Backup failed' },
  'storage.backupDeleted': { zh: '备份已删除', en: 'Backup deleted' },
  'storage.deleteFailed': { zh: '删除失败', en: 'Delete failed' },
  'storage.restore.title': { zh: '还原 {id}？', en: 'Restore {id}?' },
  'storage.restore.content': {
    zh: '生产运行时必须处于停止状态。API 会进入维护模式，使用 SQLite 在线备份 API 还原数据库，并可选还原预览文件。还原成功后请重启宿主进程。',
    en: 'The Production Runtime must be stopped. The API enters maintenance mode, restores SQLite with the Online Backup API, and optionally restores preview artifacts. Restart the host after a successful restore.'
  },
  'storage.restore.ok': { zh: '还原备份', en: 'Restore backup' },
  'storage.restoreDone': { zh: '已还原 {id}。建议重启。', en: 'Restored {id}. Restart recommended.' },
  'storage.restoreFailed': { zh: '还原失败', en: 'Restore failed' },
  'storage.capacity.title': { zh: '存储容量', en: 'Storage capacity' },
  'storage.capacity.description': {
    zh: '生产启动 {production} · 预览写入 {artifacts}',
    en: 'Production start {production} · preview writes {artifacts}'
  },
  'storage.capacity.allowed': { zh: '允许', en: 'allowed' },
  'storage.capacity.blocked': { zh: '阻止', en: 'blocked' },
  'storage.stats.free': { zh: '可用空间', en: 'Free' },
  'storage.stats.database': { zh: '数据库', en: 'Database' },
  'storage.stats.artifacts': { zh: '预览产物', en: 'Artifacts' },
  'storage.stats.backups': { zh: '备份', en: 'Backups' },
  'storage.schema.message': { zh: 'SQLite schema v{current} / 目标 v{target}', en: 'SQLite schema v{current} / target v{target}' },
  'storage.schema.description': {
    zh: '{total} 条迁移记录 · {baselined} 条来自 V0.28 之前安装的基线',
    en: '{total} migration records · {baselined} baselined from pre-V0.28 installs'
  },
  'storage.section.backups': { zh: '本地备份', en: 'Local backups' },
  'storage.includeArtifacts': { zh: '包含预览文件', en: 'Include preview artifacts' },
  'storage.action.createBackup': { zh: '创建备份', en: 'Create backup' },
  'storage.action.refresh': { zh: '刷新', en: 'Refresh' },
  'storage.column.createdAt': { zh: '创建时间', en: 'Created' },
  'storage.column.backupId': { zh: '备份 ID', en: 'Backup ID' },
  'storage.column.archive': { zh: '归档大小', en: 'Archive' },
  'storage.column.artifacts': { zh: '预览产物', en: 'Artifacts' },
  'storage.column.actions': { zh: '操作', en: 'Actions' },
  'storage.artifacts.metadataOnly': { zh: '仅元数据', en: 'Metadata only' },
  'storage.action.restore': { zh: '还原', en: 'Restore' },
  'storage.action.delete': { zh: '删除', en: 'Delete' },
  'storage.confirmDelete': { zh: '删除此备份？', en: 'Delete this backup?' },
  'storage.note': {
    zh: '备份包含 SQLite 元数据和可选的追溯预览文件。插件二进制文件和外部控制器程序通过运行时依赖清单进行漂移校验，不会复制到备份中。',
    en: 'Backups contain SQLite metadata and optional trace preview artifacts. Plugin binaries and external controller programs are drift-verified through the runtime dependency manifest and are not copied into backups.'
  },

  // ── 运行观测 / run observability ─────────────────────────────────────────
  'observability.loading': { zh: '正在加载运行时间线…', en: 'Loading run timeline…' },
  'observability.error.title': { zh: '运行观测不可用', en: 'Run observability unavailable' },
  'observability.noTimeline.title': { zh: '该运行没有精确的节点时间线', en: 'No exact node timeline for this run' },
  'observability.noTimeline.description': {
    zh: 'V0.60 之前的追溯记录仍可读取，但 VisionStudio 不会根据完成顺序推测起始偏移。',
    en: 'Pre-V0.60 traces remain readable, but VisionStudio does not guess start offsets from completion order.'
  },
  'observability.title': { zh: '运行观测', en: 'Run observability' },
  'observability.subtitle': { zh: '节点执行时间线与历史耗时基线', en: 'Node execution timeline and historical duration baselines' },
  'observability.stat.timelineSpan': { zh: '时间线跨度', en: 'Timeline span' },
  'observability.stat.overhead': { zh: '主机 / 编排开销', en: 'Host / orchestration' },
  'observability.stat.concurrency': { zh: '峰值并发', en: 'Peak concurrency' },
  'observability.stat.baselineRuns': { zh: '基线样本数', en: 'Baseline runs' },
  'observability.stat.slowNodes': { zh: '慢节点数', en: 'Slow nodes' },
  'observability.noBaseline': { zh: '无基线', en: 'No baseline' },
  'observability.column.node': { zh: '节点', en: 'Node' },
  'observability.column.current': { zh: '当前', en: 'Current' },
  'observability.column.status': { zh: '状态', en: 'Status' },

  // ── 安全门禁 / security gate ─────────────────────────────────────────────
  'security.error.unavailable': { zh: '安全服务不可用', en: 'Security service unavailable' },
  'security.error.login': { zh: '登录失败', en: 'Sign-in failed' },
  'security.error.bootstrap': { zh: '初始化管理员账户失败', en: 'Failed to initialize the administrator account' },
  'security.loading': { zh: '正在加载 VisionStudio 安全设置…', en: 'Loading VisionStudio security settings…' },
  'security.title.firstRun': { zh: 'VisionStudio · 首次安全设置', en: 'VisionStudio · First-time security setup' },
  'security.title.login': { zh: 'VisionStudio · 登录', en: 'VisionStudio · Sign in' },
  'security.bootstrap.message': { zh: '创建首个管理员账户', en: 'Create the first administrator account' },
  'security.bootstrap.description': {
    zh: '创建首个账户后，此初始化入口会自动关闭。',
    en: 'This setup entry closes automatically after the first account is created.'
  },
  'security.field.username': { zh: '用户名', en: 'Username' },
  'security.field.displayName': { zh: '显示名称', en: 'Display name' },
  'security.field.password': { zh: '密码（至少 10 个字符）', en: 'Password (at least 10 characters)' },
  'security.action.bootstrap': { zh: '创建管理员并登录', en: 'Create administrator and sign in' },
  'security.action.login': { zh: '登录', en: 'Sign in' },
  'security.note': {
    zh: '本地凭据以 PBKDF2 哈希形式保存。浏览器会话使用 HttpOnly Cookie。',
    en: 'Local credentials are stored as PBKDF2 hashes. Browser sessions use HttpOnly cookies.'
  },
  'security.action.logout': { zh: '退出登录', en: 'Sign out' },
  'security.disabled': { zh: '安全功能已关闭', en: 'Security disabled' },
  'security.defaultDisplayName': { zh: '管理员', en: 'Administrator' }
} as const;

export type MessageKey = keyof typeof messages;
export type MessageParams = Record<string, string | number>;
