# 步骤八连续斜坡支持（2026-09-26）

## Checkpoint 与数据保留

实现前源码已提交到本地 checkpoint `ef387ca3f2da5d6442455f3f871a20be9a35c934`，标签 `checkpoint/pre-continuous-slope-20260926`，未推送。
6 个旧复核工作区的 76 个文件保存在 `datasets/checkpoint-pre-continuous-slope-20260926/review-workspaces.zip`，逐文件比对压缩包内容哈希通过，清单为同目录 `file-hashes.json`。
ZIP SHA-256：`b31fcaac8a9f3facefffe61504473e4d2ba022aeba3cbec42a18abfdf7120575`。
完成实现后再次核对上述 76 个源文件，哈希全部未变。旧 v4 工作区的 5 条 approved 均保留。

## 实现

新增独立规则 `situation-analysis-rules-v5-continuous-slope-1`，旧 v1–v4 分支保持原规则和行为。规则 SHA-256：`844f47abbe09d382582b6fd73cb26ad386cef588277df000537f9c8ecd459534`。

新增 `SituationContinuousSlope.cs`，在现有三角网格 BVH 上实现有界向下射线，返回最近命中位置和向上归一化法线；原布尔遮挡检测热路径不变。没有引入导航网格、物理引擎或外部依赖。

局部探测和 1 秒恒速预测共用坡面路径：

1. 起点仅在当前脚部高度上下 4 单位内寻找支撑，并验证站立净空。
2. 每段水平距离最多 16 单位，围绕上一地面高度，按最大坡度限制下一次地面搜索范围；更新实际 Z。
3. 坡度上限暂设 45°；地面高度变化必须能由相邻面法线解释，容差 0.5 单位，从而拒绝平坦踏面间的高度跳变。此配置是待人工验证的保守规则，不宣称等同 CS2 原生移动参数。
4. 按贴地路径检查身体两侧支撑、横向净空、分段身体射线与近脚射线；身体保持竖直。分段射线包含共享端点，避免恰位于地面采样点的墙体被两段端点容差同时略过。
5. 预测保留 0.25/0.5/0.75/1 秒同步时点及每秒 320 单位水平速度上限。允许与起点坡面切向速度一致的垂直分量，误差最多 4 单位/秒；不一致则跳过预测。沿途位置仍由地面确定。

预测及局部暴露最多 medium、置信度最多 medium，不成为当前交战中心。无未来帧或结局读取；当前直接中高风险的早返回保留。

这是有限采样和身体射线近似，不是完整角色碰撞体扫描：台阶、断崖、跳跃、姿态变化、极窄裂隙、复杂坡面交界仍不保证覆盖。0.5 单位容差和 16 单位采样间隔不能保证识别任意微小不连续；不得宣称已支持全部地形。旧 unknown 的聚合策略保持不变，斜坡支持不能消除所有 unknown。烟雾、穿透和历史地图兼容性限制继续存在。

## 验证

- Release 构建 0 警告、0 错误。
- 原射线/局部移动 360 项、预测 39 项、连续斜坡 140 项通过；包含 100 个多层倾斜平面最近命中的解析对照，另覆盖上下坡、反向面绕序、45° 边界、坡面交界、台阶、断层、墙体、低顶、窄面、速度一致性和并发确定性。
- v2/v3/v4/v5 四版本候选派生、替换、保存、恢复、隔离和篡改拒绝集成通过。
- 50 条复核范围测试、58 项阶段四契约、11 项复核 UI 测试通过。浏览器核验当前为 v5、0/50、36/7/7，未代替用户评价。
- 固定 20 场训练侧 / 6,960 场景抽样重放 complete，目录 `datasets/contact-continuous-slope-sample20-20260926-r1/`，未重新全量导出。每条先验证冻结 v1 源场景和事实/预标注，每场首条新结果重复计算一致。
- 本轮 p50 0.8550 ms，p95 v5 为 2.4866 ms、同轮 v4 对照为 2.5243 ms，耗时基本持平；不以单轮差异宣称稳定加速。

相对 v4 的接触风险变化：

| 变化 | 场景数 |
|---|---:|
| high → high | 338 |
| low → low | 3,672 |
| low → medium | 3 |
| low → unknown | 4 |
| medium → low | 34 |
| medium → medium | 849 |
| medium → unknown | 33 |
| unknown → low | 77 |
| unknown → medium | 21 |
| unknown → unknown | 1,929 |

共 172 条风险变化。98 条原 unknown 得到明确输出，同时 37 条因更严格检查转 unknown；总 unknown 从 2,027 降至 1,966，净减少 61。部分中风险降低或转未知，需人工确认，标签变化不等于准确率提升。
comparison SHA-256：`bf867511f11549fcb6b61eb21a6e08646ff665ee040cadaa6146560daaff829c`。
对照 v4 规则 SHA-256 保持 `139c2bf3f5d3b4efc8b52039604a36e8a9772661f1b9665945e9a6cc9d7f5216`。

## 当前复核与回退

来源候选目录 `datasets/situation-stage4-review-continuous-slope-candidates-20260926-r1/`，仍为原 300 个时点派生，manifest SHA-256：`ab9f73d34ef713b77fc6153328f37ddb3bdc3bc9c01175430370c92321f50f92`。
当前工作区 `datasets/situation-stage4-review-continuous-slope-work-20260926-r1/`，独立持久范围 50 条（36/7/7）。当前候选风险为 high 2、medium 6、low 30、unknown 12；这 50 条中的 unknown 数量未减少。类别沿用 v1，不按本次输出重新调抽样。

```powershell
dotnet apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll --serve-situation-review datasets/situation-stage4-full-20260919-r1 datasets/situation-stage4-review-continuous-slope-work-20260926-r1 --candidates datasets/situation-stage4-review-candidates-20260919-r1 --producer-snapshot datasets/situation-stage4-review-source-snapshot-20260919-r1 --continuous-slope-candidates datasets/situation-stage4-review-continuous-slope-candidates-20260926-r1 --review-target 50 --port 5177
```

回退复核时停止当前服务，改用 `--position-prediction-candidates datasets/situation-stage4-review-position-prediction-candidates-20260926-r1` 和旧工作区 `datasets/situation-stage4-review-position-prediction-work-20260926-r1` 即可恢复旧规则及 5 条批准；无须覆盖任何工件。完整源码回退点为上述 checkpoint，人工记录还有逐文件校验的 ZIP 备份。

斜坡实现尚未提交、未推送。`AGENT.md`、`TODO.md`、`situation-implementation/TODO_4.md` 保持只读。
