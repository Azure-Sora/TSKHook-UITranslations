# 验证记录（2026-10-13）

## 已完成

- 从原插件仓 `9c3ecdf` 复制 24 个受 Git 管理的文件，并在迁移前后逐文件比对 SHA-256。来源记录保存在 `migration-baseline.json`；工具随后改为共享工作台核心的薄入口，原始哈希保留用于追溯。
- 新编译器输出 45,414 条；在本机 Windows 与迁移基线运行文件 SHA-256 完全一致：`97250f420d1c58df670c14c3eee2c95f2011d7bc566a4f23f4af5fe251cedbe6`。
- 24 个工作台测试断言通过：增量去重、保留相同文本的不同身份、人工结果保护、stale、断行、冲突、编译排除、模型协议、401 失败保存、手动重试、用量记录、外部修改检测与基线编译。
- 模型测试使用本地 HTTP 替身，没有真实模型 API 调用或费用。
- 技能纯文本生成器自检通过。
- 自包含 Windows x64 程序成功启动，加载 46,042 条，默认显示 628 条；三个页面已渲染并检查，DPAPI 加解密往返验证通过。
- 首次源码推送 `6566ceb` 的 [GitHub Actions](https://github.com/Azure-Sora/TSKHook-UITranslations/actions/runs/38346866207) 已成功，包括工作台测试、WPF 构建与运行文件编译。
- 使用工作台共享 ReleasePublisher 实际发布 [translations-2026.10.13.1](https://github.com/Azure-Sora/TSKHook-UITranslations/releases/tag/translations-2026.10.13.1)：草稿创建、三个附件上传、正式发布均完成。它只包含迁移基线，未重新调用模型。
- 插件的真实 HttpClient 从公开 latest 通道下载该包，通过大小、SHA-256、JSONL、45,414 条记录校验，再次检查返回无需切换。没有将该测试包安装到游戏目录。

## 待实测

- 真实模型接口的鉴权、限流、响应格式与译文语义：NOT_RUN，需要维护者在工作台配置自己的接口。
- 插件已在后续实机测试中验证 F1、中文配置渲染、所测鼠标/F11 输入隔离、配置重载、F11 同步、20 条真实新增捕获、任务页中文显示及手动更新检查。详细范围与未覆盖项见 [插件验证记录](https://github.com/Azure-Sora/TSKHook-Plus/blob/codex/ui-translation-modernization/docs/verification.md)。工作台读取真实新增记录、不同内容包的游戏内切换和回退仍未验证。
- GitHub Actions 在远端的最终执行状态另行记录，本地通过不等于远端 CI 通过。

既有数据提示：894 数字、410 换行、17 原文显示/hash，共 1,321 个提示，涉及 1,214 条记录。当前不擅自改写这些译文。

## 工作台 0.2：重复任务与技能生成（2026-10-13）

- 保留每个来源 key、ID、原文及哈希，按分类/字段/原文和当前译文状态合并工作队列，显示关联位置数；模型请求按组发送，回填保留并发人工修订。不同既有译文提示分歧并跳过自动翻译/复用。
- 新增一致译文复用，支持当前待办与后续捕获。分组保存审校/排除；实机确认仍按具体位置记录。
- 将既有 GenerateSkillPlainText 接入共享工作台核心，GUI 增加按钮并在编译前执行，CLI 调用同一实现。仅从已翻译的彩色预览派生无色正式文本，不外推等级数值；保留人工审校与已有捕获文本。
- 新增分组模型请求、逐身份运行包、并发人工修改、原子组保存、复用分歧、受保护字段、技能生成幂等/同步/冲突/人工保留测试。模型用协议替身，不调用真实供应商。
- 从当前用户数据复制独立验证目录：71,498 个位置；旧视图 26,109 个待处理位置，新合并视图 7,057 组（含需核对/冲突；不同状态保留分组）。用户原数据未改写、未加入本次程序提交。
- 自包含 WPF 在副本上加载与三页渲染通过；生成器两次运行均识别 2,958 个可用彩色预览组，正式文本已存在，新增/更新/歧义均为 0。编译 45,347 条、1,296 项历史校验提示，符合本次捕获后的待审状态；不冒充全量实机验收。
- 本地 0.2 发行目录为 `.local/dist-v0.2/TSKHook-UITranslations`，与正在使用的 0.1 分开，防止覆盖运行中的程序。使用者应保存修订、关闭旧工作台后再打开新版；既有本机设置与凭据继续使用。

## 工作台 0.3：塔层编号模板（2026-10-13）

- 仅对同分类 stage_name 中明确的“某塔 + 1 至 4 位十进制编号 + F”名称启用模板，至少 3 个不同楼层才合组；普通数字句子、技能、多数字名称、受保护字段及哈希不匹配记录不参与。
- 请求模型时使用 `{floor}`；输出必须保留一个占位符且不含固定数字，再按每个成员的原编号展开。运行包仍逐条保留真实 key/sourceHash/文本，不包含模板标记。
- 人工组保存支持同样的模板展开；固定编号批量覆盖会被拒绝。排除时逐条保留原文，实机确认仍需逐位置操作。至少两个既有不同楼层的译文一致且结构合格时可免模型复用；有分歧时不复用。
- 54 项断言通过（含数据副本编译），覆盖 230 层只请求一条、逐层正确回填、全部运行时身份、人工组保存、错误编号拒绝、规则边界、模板复用及缺失占位符拒绝。真实模型接口未调用。
- 自包含 WPF 在本次用户数据副本上渲染通过：待翻译任务 6,070 → 4,746；“水神の塔{floor}F”一组覆盖 245 个来源位置。其他字段中的奖励/商品句子保持独立。原数据未改写、未随程序提交。
- 新版位于 `.local/dist-v0.3/TSKHook-UITranslations`。保存修订并关闭旧工作台后打开新版；设置、凭据与原数据路径继续使用。

## 工作台 0.3.1：统一翻译规范与响应协议（2026-10-13）

- 修订 LLM_TRANSLATION_GUIDE.md，删除旧 JSONL/整行回写/status 修改/大批次建议，统一为 JSON 数组三字符串输出；更新术语优先级、上下文边界、机制核对、分组/楼层模板/彩色技能规则及当前接口示例。
- 严格检查三个字段、类型、数量、顺序、key 与 sourceHash。允许无法确定的译文返回空字符串，写入 needs_review，保留同批有效结果；楼层模板空译文也正常进入待核对。
- 56 项测试通过，包括同批部分留空、模板留空和旧 status 协议拒绝。自包含 0.3.1 构建通过。模型请求参数（包括 temperature）、批量、并发与用户设置均未修改；未调用真实模型。
- 相似译文注入仅作方案研究，未接入检索或额外模型接口。用户的 translations/ui/source 数据仍保留为未提交修改。


## 0.4.0 收尾工作流（2026-10-13）

- `dotnet run --project tests/WorkbenchTests -c Release`：87 项断言通过。新增覆盖数字宽度与百分号误报、真实占位符/数值保护、重新校验、显式统一、过期快照拒绝、空译文拒绝、整组冲突排除与重开后的保护、编译排除冲突、选中项重译及无关失败记录保留。
- Windows self-contained 发布成功。构建仅报告既有 LIB 环境目录不存在警告。
- 使用当前数据中40条记录的隔离副本运行 WPF smoke，分别检查“译文待统一 / 译文待修正 / 捕获冲突”和右侧编辑、候选、冲突详情布局，退出码均为0。
- 当前数据只读检查：49个位置的非空待核对译文可通过新版重新校验。实际用户数据未执行状态变更、统一或重译；测试接口全部为fixture，未新增付费API请求。真实供应商下的新重译入口及游戏显示未作本轮验收。

## 0.4.1 敌人捕获身份修复（2026-10-13）

- 共享插件规则区分没有 unit_id 的 enemy_data 原文，插件版本 1.4.2；工作台通过同一构建器迁移旧 key。
- 工作台 101 项断言通过，覆盖迁移预览、自动备份哈希、可靠译文复用、排除状态保留、混合来源与错误原文拒绝、新旧译文竞争拒绝、相同译文合并、失败记录迁移、幂等、导入及编译。全部使用本地 fixture，没有调用付费模型。
- 插件运行时 19 项断言通过，包括同关卡两名敌人的不同精确匹配 key，fallback 为 0。插件和 Windows 自包含工作台构建成功；隔离 WPF smoke 退出码 0，修复按钮布局已检查。
- 当前真实数据在独占工作目录后迁移 8 个位置，复用 4 个已有译文。冲突位置从 163 减少至 155；所有非迁移记录逐条比对一致。露妮亚、萝露缇的 character_name/unit_name 各自独立。再次预览迁移数量为 0。
- 数据备份位于 `.local/backups/enemy-identity-20261013-092204-6173925`，含原文件 SHA-256 和 key 映射。编译 70,817 条，致命校验问题为 0。已备份并更新本机游戏插件两 DLL 及运行时译文；安装资产 SHA-256 为 `9dfe8bab59bf2aa2d690de6232b9141e778438af24db0594e5a61090d4ddb51d`。
- 游戏内画面本轮未实测；其他类型捕获冲突保持原状态。构建保留既有 LIB、net6 生命周期及插件 NuGet 依赖替代警告。
- 本机使用已有 packages/active.json，单独覆盖旧 ui_translations.jsonl 不会改变活动包。因此最终安装使用受支持的 `ui_preview.jsonl` 与 `LocalPreview = true`，原活动包及旧种子资产保持原样；配置和此前预览（如存在）也已备份。预览模式暂停公共包更新，避免本地未发布验收内容被覆盖。

## 0.5.0 捕获变体、多模型与自动核对（2026-10-13）

- 插件 1.4.3 新规则区分已证实的角色名称、姐妹技能说明、BossRush活动页签和MainStageDetail名称变体。运行时27项断言通过；工作台135项断言通过，覆盖候选约束、人工/排除保护、误报警告确认、修正、暂缓、空值与结构拒绝、过期响应/新增同原文位置跳过、取消、失败记录隐私、模型/供应商选择和旧配置迁移。测试模型均为本地fixture，未请求付费API。
- 实际工作目录在独占锁下迁移3630个位置，45个复用已有译文。冲突位置155→0；未迁移记录逐条一致，主动排除42→42；原文及哈希保留，同源重复项安全合并。再次预览迁移数0。备份 `.local/backups/capture-identity-20261013-095739-4612412` 含完整文件、SHA-256和key映射。
- 编译70906条译文，致命结构问题0。加载已安装预览资产并使用原来源路径/身份重建JSON，3522个已迁移可用译文均精确命中，fallback=0；这是数据与运行时代码联测，未作为真实游戏画面验收。
- 本机供应商保留原接口与加密凭据，新增多模型配置；初翻gemini-3.8-flash、核对claude-sonnet-5-5。每批32、并发4、输出上限32768不变。配置迁移前备份，credentials.bin字节未变。
- WPF隔离smoke退出0，检查工作队列、候选、核对按钮与供应商双模型布局；smoke同时验证切换供应商后模型和凭据仍正确配对，模型列表去重保存，且不读写真实凭据。
- 本机游戏插件已安装1.4.3双DLL，预览资产SHA-256为 `b829ff0140e38fd48ce126223f3a657cd708d1f02730eba0ab7d2caa3ebd45f5`。保持LocalPreview=true，公共活动包不变；原DLL和预览备份于游戏BepInEx/translation-backups/capture-variants-20261013-180028。
- 真实供应商下的新核对协议与实际游戏画面未作本轮验收；没有批量发送用户待统一/待修正译文。构建保留既有LIB目录、net6支持期、插件NuGet依赖替代警告。

## 2026-10-13 workbench 0.5.1 transport regression

User evidence: 86 failed review tasks,3 batches,9m06s,0saved; local failure ledger86 TransientModelException; provider screenshot repeated input sizes with billed outputs. Code used3min header timeout and3attempts+2/4sec backoff, exactly consistent with timing. Old exception collapse prevents conclusively excluding network/5xx.

Fix:15min linked deadline covers headers and body; desktop HttpClient disables its shorter independent timeout. Transport timeout/network and HTTP5xx never automatically retried; HTTP429 and completed invalid-output retries remain bounded. Safe actionable errors and failed-run UI title. No translation data/settings/credentials changed by this patch.

146 fixture assertions PASS, including delayed successful response, header/body deadlines, user cancellation, and exactly one request for timeout/network/504 without writes or secret leakage. No real provider call; end-to-end provider acceptance remains to be confirmed with one selected group. Old app remains open; new package is separate to preserve user state.

## 2026-10-13 workbench 0.5.2 review protocol audit

Evidence: user reported22success/64failure then canceled another run after output-validation retries. Local ledger32InvalidDataException+32JsonReaderException. Usage shows three72585-input requests with1output each, repeated52875-input requests, and subsequent16-item batches with28253/45340/39643input. Old rejected response bodies were never saved, so their exact content/rejection cannot be recovered; do not attribute every prior failure to a single guessed cause.

Confirmed implementation problems: conflicting initial3field vs review7field protocols; redundant full-text/key/hash transcription; representative-only validation warnings; all-or-nothing response validation with automatic paid batch repeat; default Unicode escaping inflating model-readable input; generic diagnostics.

New protocol: local snapshot-bound integer IDs; choose resolves candidate text locally and accept retains exact original; revise alone transmits replacement text. Strict per-item validation and identity mapping isolate invalid neighbours, with existing human/fatal/stale guards intact. Review makes one outbound call per batch without automatic retries. Maximum8items and40000textcharacters per request; oversize local rejection; direct Unicode request JSON. Received text/finish_reason/usage stored before validation; fixed local rejection categories logged and persisted without secrets.

Read-only actual-data audit:71447positions,64remainingunifytasks,8candidates had warning types not exposed by old representative-only payload;0fatalcandidates. Serialized task data155767chars old vs46371new (not measured token savings; excludes repeated prompt overhead). New8batches have13750-18665combinedinstruction/datachars. All64 synthetic defer responses validated;22previously saved real decisions converted to compact wire protocol validated22/22 (offline replay, not a fresh provider response). No user translation writes or paid API requests in this repair.

Regression:173fixture assertions pass including partial apply, out-of-order/missing/duplicate/unknown IDs, malformed JSON, invalid candidates, per-candidate warning payload, Unicode, independent size/item limits, response retention including length finish reason, single-request invalid-output handling and unchanged protected rows. Existing cancellation/human/stale/transport coverage retained. Real provider acceptance of the new prompt remains NOT_RUN.

## 2026-10-13 release validation: workbench 0.5.2 and translations 2026.10.13.2

- Current checkout regression: 174 workbench assertions PASS, skill plain-text generator self-test PASS, WPF Release build PASS, Windows self-contained package PASS. Packaged executable smoke on an isolated workspace exits 0; provider bindings and DPAPI roundtrip are checked without loading real settings or credentials.
- Current translation package: 71,405 entries, fatal errors 0, 421 retained historical warnings (253 numeric and 168 newline). Runtime asset size 21,868,623 bytes; SHA-256 `786b48d14e9b19595c5524d66b1bcd46412011bc69a09ae2ff25f3265483672b`. Complete legacy review and protected-field verification are recorded in [the quality audit](legacy-translation-quality-audit-2026-10-13.md).
- The released workbench 0.5.2 tag uses plugin shared-source commit `aea329a96041d12b5cd2cb9a257591a1a01aa5ff`, available on `codex/workbench-v0.5.2-dependency`. Current main follows the later plugin 1.4.4 consolidation on master, with the same authoritative library now under `Extensions/Core/`; the fixed submodule revision and project reference are updated together. Existing public tags are retained.
- Workbench release tag `workbench-v0.5.2` is a prerelease; it must not become the latest formal translation release. Translation release tag `translations-2026.10.13.2` publishes manifest.json, ui_translations.jsonl and validation.json through ReleasePublisher. Minimum plugin version is 1.4.3; older plugins must reject the package. LocalPreview mode continues to use the local preview until the user switches it.
- This release validation does not establish game-screen acceptance or real-provider acceptance of the new model-review prompt.
