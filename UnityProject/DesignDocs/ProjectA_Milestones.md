# 《ProjectA》AI 实施里程碑（地球归还 / ER-M*）

> 对应产品权威：`ProjectA_GDD.md`（v3.0 地球归还，2026-09-18 Accepted）  
> 详细任务拆分与竖切片边界：`production/design/earth-reclamation/MILESTONES.md`  
> 实现映射：`production/design/earth-reclamation/SYSTEMS-SPEC.md`  
> 旧生物机械里程碑归档：`DesignDocs/Archive/ProjectA_Milestones_bio-mechanical_archived-2026-09-18.md`  
> **原则**：里程碑只规定顺序；一项任务交付一个玩家可验证的完整结果；禁止并行实现线

---

## 0. 产品转向门禁（2026-09-18）

### 0.1 立即生效

- **停止**领取任何生物机械主叙事 story（债兽、生态债务、锚点公开进化、生物器官幻想向新内容、M4-R00 生物产品返工等）。
- **允许**仅跨题材技术债（见 §1），做完后进入 ER-M0 → ER-M1。
- `DesignDocs/detailed/*` 中与战斗投射 / 编队 / 完整性契约相关的**技术**条款可作迁移输入；其中生物产品假设一律失效。

### 0.2 每次开始前必须读取

1. 仓库根 `AGENTS.md` / `CLAUDE.md`；
2. `production/session-state/DIGEST.md`；
3. `production/PROGRESS.md` §1；
4. `DesignDocs/README.md`；
5. `DesignDocs/ProjectA_GDD.md`；
6. 本文对应 ER 里程碑 + `production/design/earth-reclamation/MILESTONES.md`；
7. 涉及组合时读 `最新改动需求/组合引擎-正名与全阶段变化词宪法.md`（展示文案须机械）；
8. `detailed/00_Implementation_Completeness_Contract.md`（完整性门禁仍适用）。

### 0.3 通用完成定义

与旧里程碑相同底线：编译绿、需求可观察、正式入口 E2E、失败/中断/存档已覆盖或证明不适用、无第二份领域真相、热更无每帧 O(N) 扫敌。  
另加：玩家可见文案不得出现基因/器官/吞噬/代谢/谱系/债兽等禁用词（见 `TERM-MIGRATION.md`）。

---

## 1. 转向前仅允许的技术债（串行）

一次只领一项。全部 Done 或人明确跳过后再开 ER-M0 文档收尾 / ER-M1。

| ID | 目标 | 说明 |
|---|---|---|
| ER-TECH-01 | 战役/聚落最小存档方案设计并落地骨架 | 阻塞经营；可复用 Codex JSON 先例但不够直接抄 |
| ER-TECH-02 | `SimEntityId` 跨局稳定策略（ARCH-TASK-ENTITY-IDENTITY-01） | 阻塞存读档与编队 |
| ER-TECH-03 | 生产线/蓝图登记持久化（原 Lineage 持久化债，改语义） | 阻塞工厂与回厂改造 |
| ER-TECH-04 | 厘清 `DirectControlActions` vs `AbilitySystem` | 直控释放唯一真相 |

非目标：借技术债继续扩生物内容、债兽、Alpha 猎群产品。

---

## 2. ER 主线里程碑（摘要）

全文与验收见 `production/design/earth-reclamation/MILESTONES.md`。

| 里程碑 | 一句话结果 |
|---|---|
| **ER-M0** | 权威与队列已切到地球归还；生物叙事冻结写进 DIGEST |
| **ER-M1** | 竖切片：可见核心 + 轻聚落 + 2 蓝图生产 + 直控 |
| **ER-M2** | 电 / 仓 / 工作单 / 回厂改造 / 有预兆袭扰 |
| **ER-M3** | 双阵营远征 + 编制反制可读 + ≥2 个跨阵营组合 |
| **ER-M4** | 四阵营主核心可清；前哨改建；残余无主规则 |
| **ER-M5** | 返航终局 + 完整战役存档 |

竖切片阵营数 = **2**；首发 = **4**。经营深度 v1 = **轻经营**。

---

## 3. 当前 Next（唯一）

1. 完成 §1 技术债队列（或人点名跳过某项）；  
2. ER-M0 若 DIGEST/README 已对齐则可标 Done；  
3. 派 **ER-M1** 竖切片（禁止同时开生物返工）。

历史 M1～M4 生物机械任务的 Done **不自动**等于地球归还完成；只作可复用实现事实。
