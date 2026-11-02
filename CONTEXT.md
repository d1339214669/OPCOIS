# OPCOIS — 肿瘤信息系统（Oncology Information System）

肿瘤信息管理系统：对放疗/肿瘤患者的诊疗全流程进行数据管理——患者登记、诊断、治疗计划、处方、治疗执行、随访与质控。与放疗计划系统（RTPSystem）同属放疗/肿瘤领域，共享临床物理闸门（单位/坐标/DICOM/容差），但 OIS 侧重**信息流与流程管理**，而非剂量计算与勾画。

> 领域背景与临床正确性判据见 `D:\MyOpc\DOMAIN.md`；团队宪法见 `D:\MyOpc\TEAM.md`。

## Language

**患者（patient）**: 接受诊疗的唯一实体，用 Patient ID（全局唯一）标识。
_Avoid_: 客户、病例（case 是「针对某疾病的就诊记录」，非患者本体）

**就诊/病例（case / encounter）**: 患者一次疾病诊治的记录单元。一个患者可多个 case，一个 case 对应一个 diagnosis。
_Avoid_: 会话、session

**诊疗流程（care path / clinical workflow）**: 从登记→诊断→计划→执行→随访的标准步骤序列。状态机驱动，每步有明确入口与出口。
_Avoid_: 路由、pipeline（那是实现细节）

**处方（prescription）**: 医生对某一治疗方案的书面指令，含处方剂量（Gy）、分次（fx）、给量方式（SIB/常规）等。
_Avoid_: 医嘱（order 特指具体检验/用药指令）

**治疗计划（treatment plan）**: 放疗物理师/计划系统（如 RTPSystem）产出的可执行方案，含射束、靶区、剂量分布。OIS 管理计划的生命周期与版本，不负责剂量计算。
_Avoid_: 方案、scheme（那是 UI 术语）

**分次（fraction / fx）**: 处方中的单次照射单元。分次次数 × 单次剂量 = 总剂量。
_Avoid_: 次、疗程（course 是一段连续治疗）

**靶区（target volume / PTV·CTV·GTV）**: 勾画结构：GTV=可见肿瘤、CTV=临床靶区、PTV=计划靶区（含摆位误差 margin）。术语与 ICRU 50/62/83 一致。
_Avoid_: 肿瘤区域、region

**危及器官（OAR, Organ at Risk）**: 受照后产生不可接受并发症风险的正常组织。
_Avoid_: 正常组织（泛称）、organs at risk（首字母缩写固定 OAR）

**治疗执行（treatment delivery / administration）**: 患者在治疗机上按计划实际照射的过程，记录实际给量、设备、时间。
_Avoid_: 治疗、RT 治疗（那是泛称）

**质控（QA, Quality Assurance）**: 对设备/计划/流程的校验活动，确保治疗符合预期（如设备晨检、计划独立核对）。
_Avoid_: 检查、验收（acceptance 特指设备到货验收）

**工作清单（worklist / task list）**: 按角色/状态筛选的待办事项列表（如「待医师确认计划」「待物理师审核」）。
_Avoid_: 待办、todo、队列（queue 是实现细节）

## DICOM RT / 数据交换

**DICOM RT**: 放疗数据的标准交换格式。OIS 需保证导出/导入与 RTPSystem、治疗设备、影像设备互操作。

**RT Structure Set / RT Plan / RT Dose**: 三类核心放疗对象：勾画结构集、治疗计划、剂量分布。OIS 管理其存储与版本，UID 全局唯一，Referenced Frame of Reference 正确。

**影像（image / RT Image）**: 放疗定位/验证影像（CT/MR/DRR/EPID）。OIS 关联影像到患者/计划，不负责渲染。

## Flagged ambiguities

**患者 vs 病例**: 患者是物理个体（唯一 ID），病例是疾病记录。一个患者多病例。勿混用 PatientID 与 CaseID。

**处方 vs 计划**: 处方是医生的剂量指令（Gy·fx），计划是物理师的可执行方案（射束/靶区/剂量分布）。医嘱按处方执行，管理按计划管理。

**分次 vs 疗程**: 分次是一次照射，疗程是一段连续治疗（多分次）。给量涉及 fx 与 course 时务必区分。

## Example dialogue

> **Dev**: 处方剂量和计划总剂量对不上，该以哪个为准？
> **Clinician**: 以处方为准——医生开的 Gy·fx 是治疗依据，计划是执行方案。核对时两者差异必须显式报出，不能静默取其一。
> **Dev**: 一个患者能同时有两份待确认的计划吗？
> **Clinician**: 能，但同一 case 同一时刻只能有一个「生效中」的计划。状态机保证：新计划必须结束旧计划才能生效。

## Related

- 团队宪法：`D:\MyOpc\TEAM.md`
- 专业背景：`D:\MyOpc\DOMAIN.md`
- 代码仓库：`D:\OIS`（OPCOIS），远程 `https://github.com/d1339214669/OPCOIS.git`
