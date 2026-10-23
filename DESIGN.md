---
version: alpha
name: "DEMO/MAP — Light analysis workbench"
description: "Approved B showcase translated into the existing Vue replay application."
colors:
  background: "#edf3f8"
  surface: "#ffffff"
  ink: "#172d46"
  muted: "#62758c"
  line: "#dce5ef"
  primary: "#2959d5"
  t: "#a96512"
  ct: "#246cae"
typography:
  sans:
    fontFamily: '"Segoe UI", "Microsoft YaHei", "PingFang SC", sans-serif'
  mono:
    fontFamily: '"Bahnschrift", "Cascadia Mono", "Consolas", monospace'
rounded:
  DEFAULT: "18px"
  control: "9px"
spacing:
  panel-gap: "22px"
  page-max: "1640px"
components:
  button: {}
  panel: {}
  slider: {}
  switch: {}
  table: {}
---

# DEMO/MAP 前端设计约定

## 方向与范围

2026-10-22：依据用户确认的 B「浅色分析工作台」概念和独立展示页，改写现有 Vue 前端。使用冷瓷白底、雾蓝操作色、琥珀 T 方与蓝色 CT 方，强调地图、时间和局势变化。目标是中文桌面复盘工具，同时支持窄屏纵向浏览。保留 CS 标签与地图名称，不增加营销版块。

业务依据：[项目索引](PROJECT_INDEX.md)、[复盘操作与语义](docs/context/RUNBOOK.md#胜率-m5-补点与-m6-复盘)。展示页只提供视觉参考；真实页面的采样、回放与事件来自现有领域代码。不能复制展示页的虚构数值、模拟导入或假轨迹。

## 令牌权威

Model B：`apps/web/src/styles.css` 的 `:root` 为运行时权威，此文件镜像已确认基线。`--brand` 用于操作、选中态、焦点与时间轴；`--t/--ct` 为文字，`--t-bright/--ct-bright` 为地图标记和图表线。`--muted` 用于次要信息，不能用于隐藏错误或未知状态。

青玉变体由 `.app-frame[data-theme="jade"]` 覆盖背景、操作色、文字、分割线和滚动条，保留双方语义颜色。主题为当前页面会话的视觉偏好，刷新恢复雾蓝。没有远程字体、图标包或新增依赖。

## 字体、布局与密度

系统字体离线显示中英文，数据采用等宽数字。标题 16–32px；主操作、正文与表格 11–13px；次要时间/来源标注较小。完整文件名可悬停查看，长错误和来源可以换行。

顶部品牌及导入，随后对局摘要；主区域左雷达、右走势与事件比较；底部经济表、区域状态和完整事件日志。诊断信息用原生 details 收纳。桌面两列；1080px 以下雷达整行、分析双列；700px 以下纵向排列。摘要在 1080px/480px 切为三列/两列。

文档负责页面滚动，经济表仅水平滚动，事件列表限定高度。地图保持正方形、真实坐标与时间，不为装饰改变标记位置。全局滚动条由 thumb/track/hover 令牌统一，兼顾 standards 与 WebKit。

## 形状、动效与图标

18px 面板、9px 控件、柔和阴影；半透明顶栏与轻微环境渐变。`UiIcon.vue` 管理 1.7px 线条图标，`BrandMark.vue` 管理三斜线品牌标志。

首屏分组错峰淡入（700–850ms），曲线首次绘制（1.3s），按钮约 200ms 的悬停/按下反馈，事件面板约 350ms 的出现动效。动效不平滑或补猜概率值，不改变回放位置或模型数据。`prefers-reduced-motion: reduce` 关闭装饰动画和过渡；用户主动控制的回放保持可用。

## 交互与可访问性

行为权威见 [UX-CONTRACT.md](UX-CONTRACT.md)。原生按钮、checkbox、range、select 和 details；图层开关保留键盘输入。导入由真实按钮触发原生文件选择。全局可见焦点，跳转主内容链接，时间轴有中文名称。完整回合曲线含未来内容，明确标注；地图与当前胜率只跟随当前回放位置。

保留导入阶段、错误关闭、复盘重试、模型不可用、未知质量和缺失对比提示。未知不替换成概率 0%，模型不可用不阻止查看雷达。强制颜色模式使用系统边框；没有全量无障碍认证声明。

## 验证

按 [RUNBOOK](docs/context/RUNBOOK.md) 的前端路由运行类型检查、前端测试和构建；具体结果记录于[月度历史](docs/context/history/2026-10.md)。`premium-ui.json` 声明静态审计范围和控件归属；静态审计不能替代浏览器键盘、窄屏与实际状态验证。
