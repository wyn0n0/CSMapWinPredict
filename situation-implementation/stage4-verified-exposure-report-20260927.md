# 已验证暴露距离（2026-09-27）

本次按用户确认的有限探测方案增加距离说明，独立规则为 `situation-analysis-rules-v7-verified-exposure-1`，继承 v6 的 600 单位局部探测、连续斜坡和 1 秒预测。旧 v1–v6 规则文件、候选与人工决定不改写；本轮未提交或推送。

## 行为与距离含义

在现有 `Exposes` 成功分支保留起点和目标；仅当目标射线无遮挡且 `CanMoveLocal` 通过时，记录两点的水平世界距离。继续执行原来的候选顺序、缓存、600 单位上限和首次成功即返回，不增加地面、身体或可见性查询，也不搜索更短路径。

估算秒数 = 水平移动距离 / 200。斜坡使用水平投影而非坡面弧长，速度同样明确为水平速度；这是可达假设，不是实际移动或交火倒计时。某一组合先找到的结果不保证是该组合或全场最短距离。仅适用于现有候选范围内的单方探测，不扩大敌我组合初筛，不增加连续转弯、双人任意移动或导航网格。

局部探测成功时在 `contact-risk` evidence / 模板摘要中展示玩家槽位、距离、估算时间和非最短说明；诊断 Decisions 新增：

- `contact-local-peek-distance-units`
- `contact-local-peek-estimated-seconds`
- `contact-local-peek-estimated-speed`（200）
- `contact-local-peek-distance-metric`（horizontal）
- `contact-local-peek-moving-slot`

直接射线结果、未知/失败结果和 1 秒恒速预测不附加这一局部探测距离。预测原有时间说明保留。事实契约形状不变，新增候选 Schema 和版本绑定，避免覆盖旧内容哈希。

## 验证

- 独立 Release 输出 `datasets/verified-exposure-runtime-20260927-r1/`，0 警告、0 错误。
- 603 项定向检查通过：原射线/局部探测 360、预测 39、斜坡 140、600 单位 20、新距离 44。
- 新测试验证拒绝不可达目标、反向移动玩家、斜坡高度不计入水平距离、首次成功后不继续寻找更短目标；逐项比较新旧查询顺序及次数。
- v2/v3/v4/v5/v7 五版本复核派生、替换、保存、恢复、隔离与篡改拒绝通过。
- 阶段四契约 58 项、50 条范围门禁、复核 UI 11 项通过。
- 新版 300 条候选全部通过 Draft 2020-12 JSON Schema；HTTP 返回候选（范围内序号重新编号）与不可变工件一致，v7 UI 资源正常提供。
- AGENT.md、TODO.md、situation-implementation/TODO_4.md 只读。

相同 20 场 train / 6,960 场景重放，不重新全量导出。每条核对源场景、冻结 v1 哈希、新旧事实标签和局部/预测探测计数；每场首条重复计算确定性一致。

| 指标 | v6 对照 | v7 距离说明 |
|---|---:|---:|
| high | 338 | 338 |
| medium | 1,346 | 1,346 |
| low | 3,574 | 3,574 |
| unknown | 1,702 | 1,702 |
| 局部暴露场景 | 844 | 844（均有距离） |
| 局部射线探测计数 | 201,016 | 201,016 |
| 1 秒预测成功场景 | 28 | 28 |
| 同轮 p95 | 5.4851 ms | 5.5344 ms |

p95 差 0.0493 ms，约 +0.9%；此为单次同机顺序测量，包含文本生成等开销，不代表统计显著的性能变化。局部探测计数不包含全部地面/身体射线。未声称人工准确率提高。

输出 `datasets/contact-verified-exposure-sample20-20260927-r1/` 为 complete。v7 规则 SHA-256：`377b7be56afb53a7e21df1683826398ccc8590902c1c03d5968f9267fe2ae2c3`；comparison SHA-256：`ef471a961d4c1c2b8923413ac692e3ed3c165b8a0d5ebe3ca43ee7021b1d7a50`。

## 命令与独立复核

重放必须使用不存在的新目录：

```powershell
dotnet datasets/verified-exposure-runtime-20260927-r1/CsDemoMap.Cli.dll --sample-situation-verified-exposure datasets/situation-stage4-full-20260919-r1 <new-output-directory>
```

人工复核新入口使用独立 v7 候选和工作区，保留 50 条（36/7/7）；不迁移旧版批准：

已启动 http://127.0.0.1:5178，初始 0/50；风险分布 high 2、medium 10、low 28、unknown 10，8 条展示局部暴露距离。复核第 10 条（原候选 ordinal 69）示例：CT3 水平移动 128 单位，估算 0.64 秒。此分布变化相对旧 v5 来自已实现的 600 单位探测，距离记录本身相对 v6 不改变风险标签。

```powershell
dotnet datasets/verified-exposure-runtime-20260927-r1/CsDemoMap.Cli.dll --serve-situation-review datasets/situation-stage4-full-20260919-r1 datasets/situation-stage4-review-verified-exposure-work-20260927-r1 --candidates datasets/situation-stage4-review-candidates-20260919-r1 --producer-snapshot datasets/situation-stage4-review-source-snapshot-20260919-r1 --verified-exposure-candidates datasets/situation-stage4-review-verified-exposure-candidates-20260927-r1 --review-target 50 --port 5178
```

回退运行时可继续使用 `datasets/local-peek-3s-runtime-20260926-r1/` 的 v6 分析入口，或原 v5 候选和工作区；不混用新版候选与旧版工作区。原 checkpoint `ef387ca` 保留，完整回到该 checkpoint 会同时撤去此前未提交的斜坡及 600 单位源码，不能把它当成本次修改的单独回退。
