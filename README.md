# TSKHook-UITranslations

TSKHook 的文字、图片翻译资源、Windows 翻译工作台及可选调试桥。游戏插件位于 [TSKHook-Plus](https://github.com/Azure-Sora/TSKHook-Plus)。

## 目录

- `app/`：翻译工作台；导入捕获、模型翻译、审校、编译、预览与发布。
- `tools/`：独立编译与验证入口。
- `tools/UiInspector/`：可选游戏调试桥与 CLI；不属于玩家插件依赖。
- `translations/images/ui_images/`：独立版本的常驻 UI 图片资源。
- `translations/ui/`：已有源文、分类译文、术语和翻译规则。
- `vendor/TSKHook/`：固定版本的插件协议库，通过 Git submodule 引用。
- `docs/migration-baseline.json`：迁移源提交与逐文件 SHA-256。

克隆时使用 `git clone --recurse-submodules`。工作目录中的原始捕获、凭据、运行日志和任务缓存不应提交。

本仓由 TSKHook-Plus 的 `9c3ecdf` 迁移已有公开资料。旧历史继续在原仓保留；本仓保留原许可证，来源清单见 `docs/migration-baseline.json`。

实施方案见 [docs/design.md](docs/design.md)。

## 使用

- [工作台使用说明](docs/workbench-user-guide.md)
- [迁移和验证记录](docs/verification.md)
- [图片包构建、版本和更新渠道](docs/image-resource-packages.md)
- 开发启动：`dotnet run --project app/Workbench`（.NET 10 SDK）。
- 构建无需另装运行时的 Windows 程序：`./scripts/Build-Workbench.ps1`。
- 默认数据已迁移，日常关注新增和需要核对的条目即可。

插件只下载正式翻译包，玩家不需要模型 API Key 或安装工作台。


## 当前资源版本

- [文字 2026.10.14.2](https://github.com/Azure-Sora/TSKHook-UITranslations/releases/tag/translations-2026.10.14.2)：71,892 条运行时记录，最低插件 1.4.6；合并静态 UI、系统目录和有界模板，保留剧情曲名原文及“持有数”短标签。
- [图片 2026.10.14.3](https://github.com/Azure-Sora/TSKHook-UITranslations/releases/tag/images-2026.10.14.3)：41 个 Sprite、16 张 Spine 卡片的 31 个标题区域，最低插件 1.6.0；ui_images.zip 与 image-manifest.json 分别作为资源和更新清单。
- 推荐 [插件 v1.6.0](https://github.com/Azure-Sora/TSKHook-Plus/releases/tag/v1.6.0)。图片与工作台 Release 不占用文字 latest，图片更新入口为固定 image-channel。

玩家启用自动更新并关闭本地预览模式后即可检查公共包；本地预览是维护者功能。图片文字按资源包字形显示，不随逐字简繁配置改变。
