# 当前设计决策

> 最后核验：2026-09-18。

- **保持模块化单体。** 后端仍部署为一个 API，但源码按 Replay、Semantics、Maps、WinPrediction 和 Situation 划分；目录先表达职责，现有命名空间保持兼容。
- **运行入口与开发入口分离。** `apps/api/Program.cs` 只负责 HTTP 宿主；数据导出、诊断和验收工作流由 `apps/cli` 提供。
- **验证代码不进入产品程序集。** 所有 Verifier 位于 `tests/CsDemoMap.Api.Tests`；测试项目只引用 API，API 不引用测试。需要复用的样例目录校验属于产品工作流，放在 Situation Contracts 中。
- **公开契约与构建算法分别版本化。** 四套契约保持 v1；输出变化通过 `sceneBuilderVersion` 标识，当前为 v1.3。
- **旧回放窗口协议保持稳定。** 局势语义使用独立 `situation-window-sidecar-v1`；不向 `DemoWindow` 或 manifest 添加局势字段。
- **旧 sidecar 只做显式独立目录补建。** 调用方必须提供源 Demo，补建逐窗口核对旧数据并写入 `situation-sidecar-rebuild-v1` 清单；运行时只采用显式登记且仍完整有效的目录，撤销登记即可回退。
- **窗口切分只有一个实现。** 正常导入和旧 sidecar 补建都调用 `DemoWindowSliceBuilder`，避免边界逻辑随两条路径分叉；补建路径不运行胜率推理。
- **同一个构建核心服务离线与运行时。** Timeline 和窗口只负责形成同语义的 `SceneInputPrefix`，匿名化、坐标和汇总只在 `SituationSceneBuilder` 实现。
- **只缓存可内容寻址的文件来源结果。** 运行时导入和窗口入口用当前/上一窗口及 sidecar 的内容 SHA-256、补建清单修订、请求上下文及版本组成键；离线 Timeline 直接构建。缓存只持有规范化 JSON/哈希，返回时生成独立场景对象。
- **局势并发独立限流。** 相同键共享底层任务，不同键使用局势服务自己的 2 个执行槽和 32 个排队名额；不借用回放或胜率锁。调用方各自取消，全部等待者离开或应用停止时才取消共享任务。
- **未知优先于推断。** 未验证楼层、缺失时钟/阵容/装备/库存保持 null 或 unknown；默认零通过质量码暴露。
- **严格 as-of。** 目标帧、状态、轨迹和效果只读取实际 tick 以前的观察；重复窗口记录内容冲突时失败。
- **匿名排序只使用允许输出的观察。** 不使用姓名、Steam ID 或其哈希；槽位只在单场景有效。
- **固定 79/8 切分。** 保留比赛只做冻结后的契约和数据正确性验证，不用于阈值、规则、提示词或模型选择；阶段三已按预承诺请求正式观察一次保留输出。
- **阶段四显式冻结 71/8/8。** 原 8 场 validation 不重抽并直接成为 test；dev 只从父 79 场按 `SHA-256("situation-stage4-dev-v1|42|" + matchId)` 的 ordinal 顺序取 8 场，其余 71 场为 train。冻结文件保存三组完整成员与父 split/manifest 哈希，后续不得从 seed 或文件枚举重新拆分。
- **阶段四切分不信任路径提示。** CLI 必须显式接收 Demo 目录和父 split，忽略父文件中的绝对 `sourceDirectory`，对真实 87 个 Demo 重算 SHA-256；输出只保留仓库相对引用、文件名与内容哈希。
- **阶段四训练记录与复核工件分离。** 基础 JSONL 永远保持 `template-prelabel/unreviewed`；批准、修改和拒绝写入独立、带自身哈希的 review decision，冻结标签另建版本，禁止原地改写基础记录。
- **模型边界采用显式白名单。** `SituationModelInputProjector` 移除来源、Demo、窗口、requested tick 和真实 round ID；prompt 投影只包含 `input/output`。分组引用、split、选择标签、权重、哈希及 provenance 只留在模型不可见的门禁层。
- **阶段四版本逐项隔离。** split、记录、选择、manifest、标签统计和各 review 工件分别版本化；manifest 逐项记录 Scene/Builder/Geometry/Facts/Rules/Narrative/eligibility/selection/input/review 版本，并绑定 12 个依赖/阶段四 Schema 的文件 SHA-256。步骤二已获批并固定在 checkpoint `34fa03d`。
- **v4.2 与阶段四共用单一 outcome-free eligibility。** `RoundSampleEligibility` 集中判断 Mirage、完成回合、半开 live 区间、live/post-plant、回合号、时钟、阵容和存活快照；返回值不含赢家、结束原因或训练标签。v4.2 继续保留既有拒绝原因，阶段四只在合格后通过 `SituationSceneService.BuildFromTimeline` 形成 as-of scene。
- **候选选择是配置驱动且 outcome-free。** 选择核心只接收同回合结构化快照、冻结 Facts 与事件类型/tick；部署、tail、1vN/2vN、映射容差、类别优先级、post-plant 偏好、上限和 tie-breaker 全部由 `situation-training-selection-v1` 绑定。事件标题/详情、赢家和结束原因不进入接口或结果。
- **先覆盖锚点，再做确定性远点填充。** 核心、事件、稀有类别按配置顺序加入，同 tick 合并全部 ordinal 标签；空位最大化与已选 tick 的最小距离，完全并列取更早 tick 后再用域分隔候选摘要。最终 tick 排序，每回合唯一且不超过 16 条，每条权重固定为 1/n。
- **覆盖靠替换而非放大。** 每回合最多 16 个唯一 tick，稀有场景替换低优先级普通样本；最终每条写 1/n 有理权重。人工 train 标签只携带 `recommendedSftRepeat=5`，dev/test 明确禁止 SFT 重复。
- **性能请求只来自训练侧。** 阶段二固定请求包含真实拆除和跨窗口读取；保留比赛仅运行冻结契约回归。首次文件读取定义为新服务、无结果缓存且无显式预读，操作系统页缓存状态另行说明。
- **阶段边界保持清晰。** 阶段二负责服务化与性能，阶段三负责 Facts 规则和模板叙述。
- **阶段三规则只从嵌入资源加载。** 运行时不接受任意路径或热切换；candidate-1/2 历史保留，用户批准后的不可变 `situation-analysis-rules-v1` SHA-256 为 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。
- **质量影响按规则实际字段收敛。** candidate-2 不让装备、速度或金钱的 `legacy-default-ambiguous` 降低 Facts 置信度；楼层、装备和道具缺失不机械污染未使用这些输入的结论。
- **保留请求与规则输出分离。** 冻结前只按阶段、当前数据质量和稳定哈希固定 8 场各 2 个 tick；请求承诺 SHA-256 写入训练清单，验收入口在冻结资源缺失时先失败。获批后仅正式生成一次 16 条输出，后续规则修订不得把同一保留集重新描述为盲测。
- **Narrative 只复述绑定 evidence。** 模板重点使用 evidence 原文，孤立结论只公开阵营级信息，不在摘要中输出槽位；内部 margin 可使用匿名 slot。
