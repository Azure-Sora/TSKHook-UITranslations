# UI 翻译工作文件

日常操作请使用独立工作台，见 [使用说明](../../docs/workbench-user-guide.md)。

## 数据归属

- `source/ui_source.jsonl`：key、category、identity、source、sourceHash 等原文事实。
- `source/by_category/*.jsonl`：译文与状态的工作文件，只编辑 translation/status。
- `source/ui_conflicts.jsonl`：身份冲突报告。
- `glossary.md`、`TskNames.json`、`LLM_TRANSLATION_GUIDE.md`：术语、专名与翻译规范。
- `runtime/`：迁移时的运行包基线，保留用于验证；日常新包生成到 `.local/packages/` 并通过 Release 发布。

状态为 new、translated、reviewed、verified、stale、conflict、needs_review、excluded。机器输出不能标记为人工审校或实机验证；needs_review、冲突与过期译文不发布。

## 命令行

在仓库根目录运行：

```powershell
dotnet run --project tools/CompileUiTranslations -- path/to/ui_texts_v2.jsonl .
dotnet run --project tools/CompileUiRuntime -- . .local/compiled/ui_translations.jsonl
dotnet run --project tools/BuildPackage -- 2026.10.13.1 . Azure-Sora/TSKHook-UITranslations
```

前两个工具与桌面工作台使用同一套归档、审校保护和编译逻辑。捕获文件需为 UTF-8 JSONL，完整记录以换行结束；最后的未完成行在后续扫描继续处理。原始捕获放在游戏目录或 `.local/`，不提交账号相关数据。

## 技能预览去色标签

兼容原有生成器：

```powershell
dotnet run --project tools/GenerateSkillPlainText -- --self-test
dotnet run --project tools/GenerateSkillPlainText -- translations/ui/source/ui_source.jsonl translations/ui/source/by_category/skill.jsonl
```

只移除成对 color 标签，不改文字、数字或其他富文本。运行此类外部工具前暂停工作台自动任务，完成后重新加载工作目录。
