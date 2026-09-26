# 局部移动探测实施与回退（2026-09-26）

## 人工复核目标更新

用户随后将人工复核目标改为 50 条，当前工作区使用 `--review-target 50`：36 train / 7 dev / 7 test。
保留已有 1 条批准，页面显示 1/50、剩余 49 条；不改写原始 300 条候选及人工决定。
工作区内 `review-scope.json` 绑定来源身份、创建 revision、50 个原始序号及哈希，重启自动恢复；页面重编号为 1–50，后备替换沿用原始序号跟随，范围外 API 请求被拒绝。
选择优先保留已有评价/替换，再补足 split 数量并尽量覆盖类别；不再宣称覆盖原 300 条的全部比赛和配额。
定向检查覆盖 36/7/7、已有进度保留、输入乱序确定性、恢复、范围边界和篡改拒绝；Release 构建 0 警告/错误。
下文 300 条统计属于完整来源工件，不代表当前人工工作量。

## Checkpoint

修复前源码 checkpoint：`f6be0cc95009b05912285f32f585a78872b15d19`，标签 `checkpoint/pre-local-peek-20260926`，本地未推送。包含此前射线实现、复核功能及用户已删除的旧并行文档这一状态。

人工工作区属于被 Git 忽略的数据，另存于 `datasets/checkpoint-pre-local-peek-f6be0cc/review-workspaces.zip`，SHA-256 `cc1ba005b62d7feec2ae6ab34cbb284e88ea78bfe00a9144c49a00a871354bac`。32 个持久文件已逐个与压缩包内条目校验 SHA-256；清单为同目录 `review-file-hashes.json`。旧射线工作区现场为 revision 5、5 条决定，未改写。

需要回退时可从上述标签建立独立 Git 工作树并重新构建，再使用旧 `--raycast-candidates` 入口及旧工作区。旧网格和数据目录保留；备份恢复也应解压到新目录，避免覆盖后续人工进度。不要直接对当前未提交成果执行破坏性 reset。

## 实现

新增独立规则 `situation-analysis-rules-v3-local-peek-1`，旧 v1/v2 规则及候选继续可用。没有使用导航网格、速度预测或全局寻路。

1. 保留直接射线查询；已有中/高风险无遮挡组合时不追加局部探测。
2. 对原规则中达到中/高候选门限、但当前被遮挡的组合，分别检查单方移动。每人固定 8 个水平方向，终点距离 64 个地图单位；不组合双方同时移动，不执行连续转弯。
3. 起点必须有地面支撑和站立净空。潜在终点先检查射击视线，仍被遮挡时立即跳过移动验证。
4. 移动通过 16 单位身体半径、16/36/64 单位高度的多条射线近似检查，沿路每 16 单位检查地面，地面容差 4 单位，并检查终点两侧支撑和竖直净空。有限射线并非完整胶囊扫掠，不保证所有狭小障碍都能识别。
5. 发现有效暴露点即停止，最多判 medium，置信度不超过 medium；证据与摘要明确“可能暴露”，不推断意图或时间，也不把假设位置作为当前交战中心。
6. 起点几何/地面/站立空间不足且没有找到有效通路时返回 unknown；未命中时只陈述有限采样范围内未发现通路，不宣称绝对安全。

复用现有 BVH、短路遮挡查询；候选点和移动合法性检查结果仅在当前场景内缓存，避免跨帧状态影响确定性。核心改动集中于 Visibility 与 Facts 分析器，其余是规则版本、复核契约接入和测试。

## 验证与性能

- Release 构建 0 警告、0 错误。
- 360 项射线/局部移动检查通过，覆盖短墙拐角、长墙分隔、远处拐角、窄窗、悬空、缺少地面、直接接触短路及确定性。
- v2 与 v3 复核集成均通过：生成、后备替换、批准、保存恢复、版本隔离、候选篡改拒绝（合成数据）。
- 存储 75 项、工作流 31 项、消费端 21 项、HTTP、阶段四契约 58 项、UI 核心 11 项通过；这些验证覆盖本轮相应修改。
- 真实数据仅固定训练侧 20 场、6,960 场景；没有全量 Demo 重新解析或导出。r1 为初始实现，r2 为调整检查顺序后的实现，二者全部 Facts/Narrative 哈希一致。

| v2 → v3 接触风险 | 场景数 |
|---|---:|
| high → high | 338 |
| medium → medium | 474 |
| low → low | 3,682 |
| low → medium（局部暴露） | 435 |
| low → unknown（无法可靠验证） | 2,031 |

r1 p95 为 3.2656 ms；r2 p50 为 0.947 ms、p95 为 2.7659 ms，同轮 v2 基线 p95 为 1.4242 ms。优化降低开销，但新功能仍增加约 1.34 ms 的 p95 耗时，不能声称没有性能损失。结果为本机测量，不是跨机器性能保证。

r2 工件：`datasets/contact-local-peek-sample20-20260926-r2`。
规则哈希：`b817b7288a7190e6c199ca8f9fd5a7146135d17d6d5a1d6d83a559a492de0303`。
comparison 哈希：`29a73eda275f6af740e61a8c57a8b9ae5b6cbaf378053aecee63bb04a26dee3a`。
比较文件包含探测计数，优化会改变该计数；事实和摘要哈希另已逐条确认完全一致。

**限制：** 2,031 条 unknown 是显著覆盖缺口，不能视为准确率提升。该保守实现不处理蹲姿通行、跳跃、台阶/斜坡路径、复杂转弯或双方同步移动；静态网格与历史比赛版本的匹配仍未认证。435 条新 medium 表示规则发现潜在通路，仍需人工判断，而非已验证真实绕角交火。

## 新复核工作区

候选：`datasets/situation-stage4-review-local-peek-candidates-20260926-r1`。
工作区：`datasets/situation-stage4-review-local-peek-work-20260926-r1`。
本轮地址：`http://127.0.0.1:5177`；0/300、revision 0，无自动人工评价。

300 条候选分布：high 24、medium 41、low 147、unknown 88；其中 28 条 medium 来自局部移动探测。浏览器已核对 #16 的新版本、可能暴露证据及摘要。保留原 v1 抽样时点和筛选类别。
候选 manifest SHA-256：`78bc871fe54569565b17c5fb1104e88b4b9c8b5087ff55ac896229280342783f`。

```powershell
dotnet apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll --serve-situation-review datasets/situation-stage4-full-20260919-r1 datasets/situation-stage4-review-local-peek-work-20260926-r1 --candidates datasets/situation-stage4-review-candidates-20260919-r1 --producer-snapshot datasets/situation-stage4-review-source-snapshot-20260919-r1 --local-peek-candidates datasets/situation-stage4-review-local-peek-candidates-20260926-r1 --review-target 50
```

重启端口以实际输出为准。`--local-peek-candidates` 与 `--raycast-candidates` 互斥；旧工件和人工评价不可混入新工作区。AGENT.md、TODO.md、TODO_4.md 未改动。本轮实现位于 checkpoint 之后，尚未提交。
