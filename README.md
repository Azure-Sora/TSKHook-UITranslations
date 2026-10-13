# TSKHook-UITranslations

TSKHook 的 UI 翻译数据与 Windows 翻译工作台。游戏插件位于 [TSKHook-Plus](https://github.com/Azure-Sora/TSKHook-Plus)。

## 目录

- `app/`：翻译工作台；导入捕获、模型翻译、审校、编译、预览与发布。
- `tools/`：独立编译与验证入口。
- `translations/ui/`：已有源文、分类译文、术语和翻译规则。
- `vendor/TSKHook/`：固定版本的插件协议库，通过 Git submodule 引用。
- `docs/migration-baseline.json`：迁移源提交与逐文件 SHA-256。

克隆时使用 `git clone --recurse-submodules`。工作目录中的原始捕获、凭据、运行日志和任务缓存不应提交。

本仓由 TSKHook-Plus 的 `9c3ecdf` 迁移已有公开资料。旧历史继续在原仓保留；本仓保留原许可证，来源清单见 `docs/migration-baseline.json`。

实施方案见 [docs/design.md](docs/design.md)。

## 使用

- [工作台使用说明](docs/workbench-user-guide.md)
- [迁移和验证记录](docs/verification.md)
- 开发启动：`dotnet run --project app/Workbench`（.NET 10 SDK）。
- 构建无需另装运行时的 Windows 程序：`./scripts/Build-Workbench.ps1`。
- 默认数据已迁移，日常关注新增和需要核对的条目即可。

插件只下载正式翻译包，玩家不需要模型 API Key 或安装工作台。
