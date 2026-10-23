# 前端交互约定

范围：`apps/web` 中的本地 CS2 Demo 导入与复盘。中文为主；原生文件选择器和 select 弹出层采用平台语言及行为。游戏经济的 `$` 是游戏内金额，不涉及支付。日期、账号、权限管理、删除、计费与法律确认均不在此页面范围内。

## 业务来源

| 领域 | 权威来源 | UI 后果 |
| --- | --- | --- |
| 回放与模型边界 | [项目索引](PROJECT_INDEX.md)、[运行手册](docs/context/RUNBOOK.md) | 模型不可用时雷达与事件仍可使用 |
| 导入及窗口协议 | `apps/web/src/services/api.ts`、`domain/timeline.ts` | 使用原有 API；显示上传、排队、解析、预测、分窗、加载阶段 |
| 时间和事件补点 | `domain/timeline.ts`、`domain/winReview.ts` 及其测试 | 当前胜率仅用已到达且有效的点；不跨回合或数据断点补值 |
| 异步生命周期 | `composables/useReplay.ts` 及其测试 | 切回示例使旧请求失效；窗口未就绪时不显示上一窗口概率 |
| 视觉优先级 | 用户确认的 B 展示页、[DESIGN.md](DESIGN.md) | 真实雷达与走势并排，模型来源及限制保留 |

## Canonical UI Map

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
| --- | --- | --- | --- | --- |
| Select/Listbox | Native HTML select | App.vue、WinReviewChart.vue | 播放速度、复盘回合；平台原生 popup | 类型检查、前端测试、浏览器键盘 |
| Form | Native input + useReplay | App.vue、useReplay.ts、services/api.ts | `.dem` 文件、五个图层 checkbox、整场与回合 range | 导入生命周期测试、浏览器控件检查 |
| Scrollbar | Global application stylesheet | styles.css、DESIGN.md | 文档、经济表水平、事件列表垂直 | 浏览器计算样式与窄屏检查 |

不引入 Toast、CRUD、Date 或 Table Selection：当前工作台没有这些交互需求。反馈由持久的内联状态负责，没有第二套通知系统。

## 操作结果与失败恢复

| 操作 | 成功结果 | 等待／失败／未知 | 来源 |
| --- | --- | --- | --- |
| 导入 .DEM | 原有导入流程加载首窗口及整回合复盘 | 禁用重复导入，显示阶段；错误可关闭并重选文件；相同文件可重试 | useReplay.handleFile、api.importDemo |
| 载入示例 | 停止播放并恢复示例，显著标明非模型输出 | 在导入过程中也可切回；旧回调不会覆盖示例 | useReplay.loadSample |
| 播放与时间轴 | 同步地图、回合摘要、当前胜率和游标 | 窗口等待不沿用旧概率；到结束自动停止，再播放从头开始 | useReplay、timeline.test.ts |
| 回合选择／曲线定位 | 跳到开打时间／所点时间；原生滑块提供键盘等价操作 | 冻结、已结束、无预测保留明确文字；图表缺口不连接 | WinReviewChart、winReview.ts |
| 复盘事件 | 回看所在事件组首事件前 3 秒；选择所属组 | 无对比的旧 v1 仍可回放，提示重导入生成补点 | eventReviewTime、comparisonStatusLabel |
| 前／后对比 | 展示双方数值及百分点差，按钮定位到准确采样 | 缺失不补差；回合结束不提供错误的后值跳转；连续事件不是个人贡献 | WinReviewChart、WinReview v2 |
| 复盘重试 | 重新请求当前 Demo 的复盘 | 加载／错误独立于模型状态；不重启 Demo 解析 | useReplay.reloadWinReview |
| 图层／主题 | 立即改变显示，保留真实数据 | 不发送 API 请求；原生 checkbox 支持 Space 与焦点 | App.vue、RadarMap.vue |

## 显示与导航

只有一个工作台；没有页面离开时的未保存表单。顶部文件名显示当前数据来源，示例数据有明确标签。完整事件日志直接定位事件时刻；复盘卡片中的事件定位前 3 秒，两处有文字解释。未来事件显示“后续”。经济表没有排序、行选择或批量操作。

原生按钮使用 Enter/Space；范围滑块保留方向键、Home/End；select 保留平台键盘行为。图层开关的隐藏输入保持可聚焦；`details` 的 summary 可切换诊断区。窄屏保持所有主要操作可达，表格自身滚动，页面不水平溢出。
