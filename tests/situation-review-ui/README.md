# 本地复核页验证

静态入口：`apps/cli/SituationReviewUi/index.html`，由独立复核宿主提供 `/`；其余四个文件通过 `/review-ui/` 白名单加载，Mirage 底图通过 `/review-assets/mirage.webp` 加载。页面不访问外部资源、不使用 localStorage、不储存 session token。

运行纯 JavaScript 门禁：

```text
node --test tests/situation-review-ui/core.test.mjs
node --check apps/cli/SituationReviewUi/app.js
```

测试仅使用匿名合成 Narrative/DTO，不写真实 review workspace。覆盖必填评价、已存评价恢复、筛选和导航空态、过期 load/validation、冲突草稿、幂等 requestId、保存锁、非法 evidence、不同决定门禁、阻断状态、快捷键去重、坐标方向、重复 JSON 字段和 JSON 字段顺序等价。

宿主集成后的页面检查：

1. 在明确标识的合成工作区打开页面，核对身份/进度、雷达网格、匿名 slot/HP/方向、历史轨迹、C4、道具及烟火。移除底图时应显示缺图提示和坐标网格。
2. 点击候选要点，核对 Evidence 的 ID、ruleId 和 sourcePaths 高亮；编辑未知 ID 或重复 JSON 字段，确认禁止批准。合法编辑须经服务端校验后启用“修改后批准”。
3. 四项评价初始未选；填完后确认 approve/modify/reject。拒绝 reason/备注必填；拒绝成功后才出现单独的后备替换按钮，新样本仍未复核。
4. 编辑后切换样本、重读、刷新或关闭，应提示未保存状态。长按快捷键/在输入框敲 A/M/R 不应连存或误存。筛选仅改变导航列表，不改变全局进度。
5. 两标签页制造 revision 冲突，确认草稿保留且需显式重读；服务端阻断后 approve/modify/replace 关闭。断网/恢复后原载荷重试沿用 requestId；已确认提交后列表/详情读取失败应锁定写操作并提示重读。

真实工件只作获准的只读加载与一次本地视觉确认；不使用自动脚本替人填写真实评价。纯状态测试不代替浏览器视觉检查或宿主端到端测试。
