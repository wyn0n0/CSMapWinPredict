# 步骤八：1 秒位置预测（2026-09-26）

## 实现与边界

独立规则 `situation-analysis-rules-v4-position-prediction-1` 在 v3 局部移动探测上增加恒速外推。只使用目标时点的归一化位置和速度，不读取未来帧、回合结局或导航网格。按照用户要求，预测时点固定为 0.25、0.5、0.75、1.0 秒；双方必须使用同一时点。

仅在没有已确认直接中高风险组合时，对现有近距离筛选中被遮挡的敌我组合追加预测。优先检查预测位置距离与射线；发现无遮挡通路后，复用身体半径、净空、起点支撑和每至多 16 单位的路径地面检查。按玩家/时点缓存预测，按起终点缓存移动检查；首次可靠暴露即可结束。当前直接高风险不会承担预测开销。

速度上限为每秒 320 地图单位，1 秒最大位移 320 单位；异常值不裁剪成可用值。垂直速度超过 4 单位/秒、速度缺失、默认零歧义、过期帧、无效位置或越出雷达范围时跳过预测。地面约束为平地近似，不模拟楼梯、跳跃、重力、转向和急停；预测失败继续使用原有 64 单位局部探测。未发现暴露不代表未来一秒绝无接触，也未覆盖当前距离筛选之外的组合或两个采样时点之间的短暂暴露。

预测产生 `contact.position-prediction` evidence，注明恒速假设、预测时点和双方位置/速度来源；接触风险最多 medium，置信度最多 medium，不把预测位置作为当前交战中心。静态地图版本、身体射线近似、烟雾和穿透的既有限制继续存在。

## 验证结果

- Release 构建成功，0 警告、0 错误。
- 原射线/局部移动 360 项、新预测 39 项通过，覆盖一秒端点、同步时点、移动方向、穿墙、途中地面缺口、缺失/异常速度、默认零、越界和回退。
- v2/v3/v4 候选派生、替换、保存、恢复、版本隔离、篡改拒绝集成通过；50 条范围测试、58 项阶段四契约、11 项复核 UI 测试通过。
- 固定训练侧 20 场、6,960 场景，输出 `datasets/contact-position-prediction-sample20-20260926-r1/`，complete。未重新全量导出。每个源场景的冻结 v1 事实和预标注哈希先重放核对；每场首个新结果重复计算一致。
- 对照 v3：35 个场景采用预测 evidence，其中 28 个原本已是 medium；新增 3 个 low→medium、4 个 unknown→medium，其余风险等级不变。仍有 2,027 个 unknown。
- 本轮 p50 0.8059 ms；p95 新版 2.4720 ms、v3 2.3485 ms，增加约 5.26%。这是本机同轮观测，不构成延迟保证或人工准确率结论。
- v3 规则 SHA-256 保持 `b817b7288a7190e6c199ca8f9fd5a7146135d17d6d5a1d6d83a559a492de0303`。
- v4 规则 SHA-256：`139c2bf3f5d3b4efc8b52039604a36e8a9772661f1b9665945e9a6cc9d7f5216`。
- comparison SHA-256：`90c6c473703d6c84ea557d8f93d3c09d3b4b2f252d568392a7f9c4541ae24052`。

## 人工复核与回退

新版来源候选为 `datasets/situation-stage4-review-position-prediction-candidates-20260926-r1/`，沿用原始 300 个时点，其中 6 条含预测 evidence。manifest SHA-256：`65a235c74f8130a87b4d70015dc25aad06c83a714c9126ec0353a35e6e3195b3`。

当前工作区为 `datasets/situation-stage4-review-position-prediction-work-20260926-r1/`，持久范围仍为 50 条（36/7/7）；其中 1 条包含预测 evidence。浏览器已核验新版本与 0/50 初始进度，未代替用户评价。预测样例原 ordinal 141：`sample-46b3bdaa6e3044db3d1f8f30d7a0e93df774288e537d4a110d1b0e7a59e75c90`，属于 train。类别仍沿用 v1，不能宣称这是重新按预测风险分层的样本。

旧 v3 工作区 `datasets/situation-stage4-review-local-peek-work-20260926-r1/` 的 3 条 approved 已保留。新规则和候选哈希改变，旧批准未自动迁移。回退时停止当前服务，再以 `--local-peek-candidates` 和旧工作区启动即可；无需覆盖数据或重置 Git。前序 checkpoint `f6be0cc` 是加入局部探测之前的源码状态，本轮未创建新的提交。

启动新版：

```powershell
dotnet apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll --serve-situation-review datasets/situation-stage4-full-20260919-r1 datasets/situation-stage4-review-position-prediction-work-20260926-r1 --candidates datasets/situation-stage4-review-candidates-20260919-r1 --producer-snapshot datasets/situation-stage4-review-source-snapshot-20260919-r1 --position-prediction-candidates datasets/situation-stage4-review-position-prediction-candidates-20260926-r1 --review-target 50 --port 5177
```

重新抽样须使用不存在的新输出目录：

```powershell
dotnet apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll --sample-situation-position-prediction datasets/situation-stage4-full-20260919-r1 <new-output-directory>
```

`AGENT.md`、`TODO.md`、`situation-implementation/TODO_4.md` 保持只读。代码未提交、未推送；工作区与大型数据继续本地保存。
