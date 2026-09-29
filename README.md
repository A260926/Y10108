# 消息结构兼容性实验室 (Message Schema Compatibility Lab)

在发布新 schema 之前，用一个 ASP.NET Core + SQLite 应用验证 **旧 writer / 新 reader** 及其反向组合的真实编码、解码与往返行为。

## 安装与运行

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet run --no-build --urls http://127.0.0.1:5238
```

打开 <http://127.0.0.1:5238>，页面标题为 **消息结构兼容性实验室**。

首次启动会预置一个含三个版本（`v1`/`v2`/`v3`）的事件消息家族，以及两次矩阵运行：
`seed-preserve`（保留未知字段）与 `seed-drop`（丢弃未知字段）。
其中 **`v1 → v3` 格子只在“保留未知字段”模式下可逆**（`region` 字段经 sidecar 往返保留）；
在“丢弃未知字段”模式下该字段在中间 reader 再写出时被永久丢弃，格子明确标记为不可逆并给出解释。

## 模型能力

- JSON 形式定义 `record` / `enum` / `union`，字段可声明 `required`、`default`、`alias`、`secret`。
- schema 以版本发布；**发布后不可变**，任何修改都必须形成新版本（PUT/DELETE 已发布版本返回 409）。
- 每个版本上传样例；样例入库前 secret 明文被替换为不可逆 token（`__SECRET__:<sha256>`），数据库与字节流不保存明文。

## 兼容矩阵

为所选 writer/reader 的全组合生成格子，每格保存：

- 编码摘要（字节长度、SHA-256、逐字段 `[offset,length)` 字节区间）
- 解码字段、**未知字段 sidecar**、默认值使用情况、错误代码、字段来源（direct / alias / default / unknown / union-order）
- schema 演变解释（不是简单布尔值）：
  - union 分支顺序变化（按 `__tag` 选分支，仍记录 `UNION_BRANCH_REORDERED` 与位置变化）
  - enum 新增/移除值（`ENUM_UNKNOWN_VALUE`）
  - required 增删、默认值及默认值类型变化
  - 字段重命名：**只有声明 alias 才继承**；名称相似但未声明 alias 明确不继承
- 中间 reader 再写出时按 unknownPolicy 保留/丢弃未知字段；**该选择进入矩阵身份**（参与幂等内容哈希）。
- 可逆性结论与人类可读解释；点击格子可下钻到字节高亮与字段来源。

## 运行语义

- 运行冻结当时的 schema 与样例摘要；相同幂等键 + 相同内容返回原运行，键相同但内容不同返回 409。
- 格子逐个提交：单格失败不阻止其它格；写格子与推进游标分离，进程在两者之间退出后恢复时不重复计算（见 `MatrixRunner` + `RunService.Reconcile`）。
- 两个页面并发修改 draft 基于 revision 的乐观并发，后写者收到 `DRAFT_REVISION_CONFLICT`。
- 导出包含 schema、摘要与矩阵结论；secret 字段只保留**存在性与 SHA-256 摘要**，不导出 sidecar 原始值。

## 关键代码

- `Engine/Encoder.cs` / `Decoder.cs` / `CompatibilityEngine.cs`：编码、解码与往返/可逆判定。
- `Engine/SchemaDiffer.cs`：逐类演变解释。
- `Engine/MatrixRunner.cs`：格子处理与“写格子→推进游标”崩溃注入点。
- `Store/LabStore.cs` / `RunStore.cs`：SQLite 持久化、幂等、游标恢复。
- `Tests/`：未知字段往返、alias、union 顺序、enum 未知值、部分失败、恢复、冻结隔离、导出脱敏、预置演示等 18 个测试。
