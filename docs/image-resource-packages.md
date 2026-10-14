# 独立图片资源包

资源源目录：`translations/images/ui_images/`，包含最终 PNG 和运行时源匹配 manifest.json。图片版本 `2026.10.14.3`，独立标签 `images-2026.10.14.3`，最低插件版本 1.6.0。

调试桥及 CLI 在 [tools/UiInspector](../tools/UiInspector/README.md)，仅维护时独立安装。插件无需调试桥；安装与替换图片的细节见 [插件图片说明](https://github.com/Azure-Sora/TSKHook-Plus/blob/master/Extensions/docs/ui-image-localization.md)。

在本仓运行：

```powershell
dotnet run --project tools/BuildImagePackage -c Release -- .
dotnet run --project tests/UiInspectorSmokeTest -c Release
python tests/UiInspectorSmokeTest/test_cli.py
dotnet build tools/UiInspector/TSKHook.UiInspector.csproj -c Release '-p:GamePath=E:\Games\DMM\Twinkle_StarKnightsX'
```

BuildImagePackage 复用 vendor/TSKHook 的纯托管协议，只构建和校验本地包，不访问发布接口。与插件联调时可使用 `-p:TSKHookSourceRoot=E:\Codes\TSKHook-Plus` 指向正在开发的插件核心；正式提交前固定 vendor 子模块到通过验证的插件提交。相同输入采用固定 ZIP 时间与文件次序，产出可重复。

输出 `.local/image-packages/<图片版本>/ui_images.zip`、`image-manifest.json`。外层清单包含 resourceType=ui-images、独立版本、minimumPluginVersion=1.6.0、ZIP 大小及 SHA-256、唯一版本附件 URL。ZIP 内 manifest.json 仍是 Sprite/Spine 的运行时规则，版本必须与外层相同。

确认发布时显式运行 `./scripts/Publish-Images.ps1 -Version <图片版本>`；它要求 main 已提交所有更改，将图片作为 `images-<版本>` 发布，完成版本附件上传后更新固定 `image-channel/image-manifest.json`。所有图片 Release 使用 latest=false，因此不抢占文字更新的 latest 通道。上传结果不明时先查 GitHub 状态，不盲目重试。

文字仍按 `translations-<版本>` 单独发布，保留 manifest.json、ui_translations.jsonl、validation.json 与工作台既有流程；工作台预览继续为 prerelease/latest=false。不要把图片加入文字包，版本号无需同步。

玩家手动离线安装时，从 ui_images.zip 解出 manifest.json 和 PNG，放到 `BepInEx/plugins/TSKHook/ui_images/`。插件首次启动会保留此包作为远程更新的回退基线，之后使用 `image-packages/active.json`；已有远程状态时以该指针为准。无需覆盖字体、配置或文字包。手动替换旧离线目录后，可通过插件维护菜单回退/重载或管理对应图片状态；不要在游戏运行中改 active.json。

迁移来源及逐文件原始哈希：[inspector-image-migration.json](inspector-image-migration.json)。生图原件、导出、截图、游戏原始资产与命令令牌均不提交。实机图片验收证据留在插件仓忽略的 `.local/common-ui-images`，不是公开资源包的一部分。
