# 核心 Scene 输入与 5120 token 配置

日期：2026年10月5日。本文统一定义字段范围与预算；阶段六执行入口为[TODO_6](../TODO_6.md)。核心投影、Schema、基础prompt和独立预算入口已实现；[步骤三 A报告](../situation-implementation/stage6-core-context-report-20261005.md)确认基础输入全量准入通过，A–D完整渲染预算和生成/训练资源仍留后续实测。

## 输入范围

采用十样本测试的`key_scene_table`核心组合：`{"facts":完整原Facts,"sceneContext":核心Scene紧凑表格}`。Facts及其原evidence全部保留；核心Scene从同一条配对记录投影，不按sampleId拼接不同来源。

| 部分 | 保留字段 |
| --- | --- |
| 公共信息 | map、sceneBuilderVersion、geometryVersion、dataQuality |
| 每个存活人员 | slot、side、region、regionSource、floor、position、health、armor、weaponCategory、heading、utilityCounts |
| 回合 | phase、remainingSeconds、elapsedSeconds、clockSource、score |
| C4 | 现有Scene.bomb全部字段：state、carrierSlot、defuserSlot、site、position、region、regionSource、secondsToExplosion、secondsToDefuse |
| T/CT队伍资源 | defuserCount、equipmentValue、totalMoney、helmetCount、weaponCounts、utilityCounts |

沿用测量脚本的紧凑编码：同字段对象数组转换为显式columns与rows，递归处理，字段顺序确定，能无损还原选定核心对象。坐标和数值保持原精度，null、unknown与质量标记保留。匿名slot仅表示当前Scene人员，不能当作跨样本稳定身份。

本轮不新增烟火效果/区域形状、当前投掷物、几何距离表、速度、近期移动摘要或完整轨迹。原Facts中已经包含的相关派生判断继续保留；“仅核心”限制新增Scene字段，不删改完整Facts。后续错误分析先在此范围内改进提示词、规划和标签，扩大字段范围另行形成明确决策。

新表示为`facts-core-scene-table-v1`；[独立配置](../situation-implementation/situation-core-context-v1.json)绑定[Schema](../schemas/situation/situation-core-context-v1.schema.json)，[渲染器](../tools/situation_core_context.py)逐条核验同源投影、完整Facts和表格还原。新增引用采用`scene:<JSON Pointer>`，指向还原后核心Scene的原值，同路径映射到配对记录的`input.scene`；映射规则与字段说明加入system并计入token预算。逐条来源哈希、展开引用审计哈希留在报告侧，不传入prompt。旧Facts evidence ID集合及验证器语义不变，Scene引用不得冒充旧ID。

支持检查只接受同源、同类型、同值的直接观测`equals`，null/unknown不能转成已知值；排除字段、错误值和来源错配必须失败。中文结论或派生关系的语义支持仍须步骤四/六的评价规则，引用存在或观测通过不代表整段解说正确。Narrative v1的`evidenceIds`仍只用于原Facts；Scene支持明细在独立评价工件中绑定，不擅自扩展原Schema。

## 预算口径

| 项目 | 新配置 |
| --- | --- |
| 完整序列上限 | **5120 tokens** |
| 推理输出预留/上限 | **512 tokens，含结束预算** |
| 推理最大输入 | **4608 tokens**，包括system、user、chat template和生成前缀 |
| 训练约束 | 实际system/user/assistant/EOS完整序列≤5120；目标输出预算仍为512，超限需修订标签/配置并重新核验，不静默截断 |
| 输入完整性 | 完整Facts及已选核心字段均保留；教学示例、内容计划、来源映射和特殊token全部入账 |
| 模型与批量 | 固定Qwen3-1.7B revision，非thinking；拟沿用NF4 rank16、micro batch1、累积16，资源可行性待新实测 |

5120是总序列预算，不是“5120输入再加512输出”，也不要求将每条短样本填充到5120。采用实际长度统计耗时，资源检查另覆盖上限压力。

[十样本实测](../situation-implementation/scene-context-token-probe-20261005.md)的核心紧凑输入平均3010.7、最大3354 tokens；加512预留后平均3522.7、最大3866，十条均低于5120，最紧样本剩余1254 tokens。这些数字使用旧system且未添加新教学示例/证据映射，不代表全量新输入准入。

## 实施顺序与验证

1. 在阶段六步骤三 A完成核心投影、无损还原、输入Schema、引用映射及独立配置；复用原配对Scene，无需默认重解析DEM。
2. 对实际新train/dev及全部A–D渲染输入核算5120预算；A–D都使用相同核心输入，仅提示词策略不同。A保留摘要任务风格，但以最小修改允许读取核心信息；原Facts-only原prompt单列历史对照，不混算输入变化与prompt收益。B0仍为Facts模板参照，明确其输入差异。
3. 冻结评价协议后，做10条新输入的真实生成校准，再完成50×4对照。用新长度/吞吐更新时长预算；原Facts-only的2至6小时首轮、27至55分钟小试估算不作为当前预算。
4. 分析标签绑定完整新输入及支持关系；在基线、标签和预算完成后做新训练短资源检查，覆盖真实最长样本、5120压力及512监督输出，合格后才进入200条限定小试。
5. test仍通过独立入口按冻结配置核验，不用其内容/失败反馈选择表示、prompt或模型。超限、OOM、未知值和失败如实记录。

## 资源与历史兼容

新5120配置的训练/推理显存、prefill时延及持续吞吐尚未测量。历史2560训练reserved峰值4.590 GiB及旧4.6至5.2 GiB规划均不能直接沿用；旧Facts-only吞吐与完整序列1600至2200的时长估算也不能直接沿用。当前不宣称本机能通过5120资源检查。

可复算部分只有BF16 KV cache：batch1、28层、8个KV头、head_dim128、L=5120时约0.547 GiB；不含权重、激活、工作区及驱动占用，不能当作总显存。训练保持use_cache=false，生成缓存独立核验。模型仍按一个进程、一个常驻实例调度。

原Facts-only/2560配置、26项准备完成记录、冻结语料、报告和资源接受记录保留为历史事实。新配置单独版本化、单独准入，trainingExecutionReady仍为false。新5120配置和输入报告单独保存，旧冻结JSON与生产默认入口保持原样；本步仅CPU离线tokenizer核验，不执行新生成或训练。
