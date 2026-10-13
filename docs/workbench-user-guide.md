# 翻译工作台使用说明

## 首次运行

1. 将翻译仓库克隆到本机：`git clone --recurse-submodules https://github.com/Azure-Sora/TSKHook-UITranslations.git`。
2. 解压 Windows 工作台，运行 `TSKHook-UITranslations.exe`。自包含发行包无需另装 .NET。
3. 在“模型与路径”选择刚克隆的仓库根目录和游戏根目录。捕获目录通常为 `游戏/BepInEx/plugins/TSKHook/ui_capture`。
4. 填写兼容 Chat Completions 的完整模型地址、模型名、API Key、每批数量、并发数和输出 token 上限，然后“保存设置并加载”。接口地址可使用 HTTPS；本机模型允许 loopback HTTP。

模型 API Key 与 GitHub Token 使用 Windows DPAPI 加密，存在当前账户的 `%LOCALAPPDATA%/TSKHook-UITranslations/credentials.bin`；路径及非秘密配置存在旁边的 `settings.json`。不要共享这个目录。GitHub Token 只用于发布，不需要提供给玩家。

## 日常流程

在游戏按 F1 打开配置管理器，开启捕获。工作台默认每五秒检查捕获目录，自动去重、按稳定身份归档并保留人工译文。工作台不启动时插件仍会捕获，下次打开会补处理。尚未写完的最后一行会等待后续数据。

“翻译待办”仅处理 new/stale，已审校、已验证、冲突、排除和待核对条目不会被模型覆盖。勾选“自动翻译”后，新条目会自动发送给配置的接口并产生用量。成功结果逐批保存，中途暂停后再次运行即可续跑。失败条目保留在本地重试列表，不会每五秒反复请求；修改接口设置后可使用“重试失败任务”。原始 token 用量记录在 `.local/model-usage.jsonl`，供应商未返回的字段为 null，不估算成零。

机器译文状态为 translated；标签、占位符、数值或换行异常会进入 needs_review。工作队列支持全文搜索和筛选；一次显示前 1,000 条以保持流畅，搜索仍覆盖全库。选择条目后可编辑译文，保存并标记已审校；只有自己进游戏确认后才使用“实机确认”。“保留原文 / 排除”使该条不进入运行包。

不要同时用两个程序编辑同一个工作目录。若外部工具修改了分类文件，工作台会要求重新加载，防止覆盖外部修改。归档的多文件事务可在下次加载时恢复。

## 本地预览

“编译并检查”产生 `.local/packages/<版本>/` 中的 JSONL、manifest 和完整校验报告。编译器使用主文件的 key/sourceHash，分类文件负责 translation/status；标签和占位符错误会阻止编译。new/stale/conflict/needs_review/excluded 不发布。

“安装到本机预览”将包原子写入游戏 `BepInEx/plugins/TSKHook/ui_preview.jsonl`。在游戏 F1 设置中开启“本地预览模式”后，插件每两秒检查文件变化，期间暂停公共翻译更新。已经打开的界面可能需要重新进入。此按钮不替换 DLL，也不修改正式远程包。

## 发布

发布前确认在翻译仓 `main` 分支，Git 凭据已配置，暂存区没有其他更改。在设置页填写对该仓具有 Contents 写权限的 GitHub Token。

填写未使用过的版本号，例如 `2026.10.13.1`。点击发布会先编译并展示待确认的条目数和警告数；确认后提交并推送 `translations/ui` 的更改，再创建 GitHub 草稿 Release，上传完整资产，最后公开该 Release。失败的上传保留为草稿，不对玩家开放；请检查草稿后使用新版本号重试。不会覆盖已有 Release。

翻译发布标签为 `translations-<版本>`，玩家默认读取最新正式 Release 的 `manifest.json`。工作台程序使用 `workbench-v*` 标签发布预览版，明确不占用正式翻译的 latest 通道。

## 既有数据的核对提示

迁移基线共有 46,042 条源记录、45,414 条运行译文、628 条未翻译条目。编译结果保留了原有的 894 个数字差异和 410 个换行差异提示，不表示全部都是错译。另外发现 17 条历史原文显示乱码、与保存的 sourceHash 不一致；不会擅自重算这些哈希，也不会将这些原文自动送给模型，重新捕获到同一原文哈希后可修复显示文本。

这套工具只处理实际捕获到的文本。按需加载的未拥有角色/技能以及图片文字仍是覆盖边界。

## 命令行与开发

开发需要 .NET 10 SDK；共享协议库固定引用 `vendor/TSKHook` 的 Git revision。

```powershell
dotnet run --project app/Workbench
dotnet run --project tools/CompileUiTranslations -- path/to/ui_texts_v2.jsonl .
dotnet run --project tools/CompileUiRuntime -- . .local/compiled/ui_translations.jsonl
dotnet run --project tools/BuildPackage -- 2026.10.13.1 . Azure-Sora/TSKHook-UITranslations
dotnet run --project tests/WorkbenchTests -c Release -- .
./scripts/Build-Workbench.ps1
```

`tools/BuildPackage` 的 `--publish` 开关用于显式命令行发布，凭据从进程环境变量 `TSKHOOK_GITHUB_TOKEN` 读取。普通使用建议直接操作工作台。
