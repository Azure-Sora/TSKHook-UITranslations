# UI Inspector：漏捕获文本诊断与本机控制

这是可选的 BepInEx 6 / IL2CPP 开发工具，独立生成 `TSKHook.UiInspector.dll`。它借鉴 MaaDotAbyss 的 Canvas 遍历、主线程队列和事件射线点击思路，与正式 `TSKHook.dll` 分开安装，不加入正式插件的单 DLL 发行流程。

## 为什么需要它

当前正式插件的捕获入口为 `HttpClient.FormatData` 的 JSON 响应。TMP `text` setter 和 `OnEnable` 能应用已有译文，但不收集新的原文；旧 `UnityEngine.UI.Text` 也没有这条渲染翻译路径。因此，未进入网络响应的 UI 文本可能始终无法在工作台出现。

预制体初始文本也可能通过字段反序列化进入组件，而没有走属性 setter；Unity 官方说明序列化直接操作字段：[Script serialization](https://docs.unity3d.com/2022.3/Documentation/Manual/script-Serialization.html)。仅凭截图无法确定菜单是文本还是图片，需要导出并对照实际位置。

| 导出结果 | 后续汉化路径 |
| --- | --- |
| TMP / UI.Text / TextMesh 中能读到日文 | 先用本工具预览，再为正式插件补充渲染文本采集和对应生命周期处理 |
| 同一屏幕位置只有 Image / RawImage | 根据 Sprite、Texture 信息确认图片；文字像素需要单独的图片汉化方案 |
| 标准组件和图片都无法解释画面 | 结合 components 字段排查自定义组件、材质或其他渲染方式；不能直接判定没有文本 |

工具只读取已加载场景中的 Canvas 层级。`IncludeInactive` 可以包含其未激活节点，不会加载尚未打开的页面，也不会导出磁盘中所有 prefab。`activeInHierarchy`、组件 `enabled` 和屏幕包围盒都不等同于“实际可见”：透明度、遮挡和裁剪仍需对照截图。

## 图片导出与临时替换（0.1.4）

从当前 `ui` 输出取得 Image 节点的 `scene`、`path`、`instanceId`，以及图片组件的 `componentIndex` 和 `sprite`。以下参数仅为示例，必须用当前进程读到的完整身份：

```powershell
python $inspectorCli image-export --scene Main --path '/Canvas[0]/InformationBtn[0]' --instance-id -123 --component-index 4 --sprite btn_information --output '.local/notification-original.png'
python $inspectorCli image-apply --scene Main --path '/Canvas[0]/InformationBtn[0]' --instance-id -123 --component-index 4 --sprite btn_information --file '.local/notification-zh.png'
python $inspectorCli image-restore
```

`image-export` 返回带真实透明通道的 PNG。通过 GPU 读回不可读图集，再用 Sprite 网格顶点和 UV 还原原始矩形中的像素，支持紧密或旋转打包；不调用 `textureRect`，也不猜测 `spriteRect` 是图集位置。当前仅支持 UI `Image`，不支持 RawImage、Spine 或材质文字。输出为源素材，不包含组件 tint、材质效果、数量角标或屏幕缩放。

`image-apply` 将 PNG 内容通过本机桥发送，游戏不读取任意客户端文件路径；要求当前资源名和节点身份精确匹配、PNG 至多 4 MiB、像素尺寸与原 Sprite 完全相同。通过 `overrideSprite` 进行一次内存预览，保留 pivot、pixelsPerUnit、border 和组件属性。同一组件再次替换前先恢复。`image-restore` 只恢复仍引用本工具替换 Sprite 的组件，并释放其纹理和 Sprite；游戏已修改或销毁的目标会跳过。

预览不会持久保存或自动覆盖新创建的页面。重新进入主页或重启后，重新读树取得身份再应用。当前是开发工具实验，未加入正式插件的自动图片翻译路径。

2026-10-14 实机试验：主页 `btn_information`、`btn_mission`、`btn_present` 均为 88×92；从运行游戏导出，经内置 imagegen 分别生成“通知／任务／礼物”，保留 alpha 并确定性缩回源尺寸后，三次替换全部验证成功，实机截图中文字可读、数量角标正常。恢复报告 3 成功、0 跳过；恢复后重新导出的三个 PNG 与原图 SHA-256 完全一致。错误尺寸 PNG 被拒绝，再次应用通过。正式 `TSKHook.dll` 哈希未改变。生成图仍有字体粗细、底板轮廓和礼盒颜色变化，属于效果试验，尚未达到仅修改文字的保真标准。原图、生成图、游戏尺寸图、提示词和截图保存在本机 `.local/home-icon-localization/`。构建通过，36 项托管断言及 7 项 CLI 测试通过。

用户随后确认任务、礼物首版满足需求，允许近似保留风格及多次生成择优，不要求仅修改文字。通知另生成 v2，修正圆形底板；实际主页截图保存为 `preview-v2.png`。

## SkeletonGraphic 只读研究（0.1.5）

```powershell
python $inspectorCli spine --filter 'QuestTopView' --output '.local/quest-spine.json'
python $inspectorCli spine-export --scene Main --path '/Canvas[0]/Card[0]/Card_skeleton[0]' --instance-id -123 --texture btn_quest_main --page 0 --output '.local/quest-atlas.png'
```

`spine` 返回当前激活 SkeletonGraphic 的完整节点身份、SkeletonDataAsset、骨骼导出版本、当前动画、动画名、材质和图集来源、atlas 文本、当前 slot/attachment 和 AtlasRegion 几何。`--filter` 是对已转义节点路径进行大小写敏感的子串匹配，不会打开页面。Skeleton 源为 JSON 时返回文本；二进制 `.skel` 只记录名称和字节数，不误解码成 JSON。每个读取失败独立记录在 `errors`。

`spine-export` 要求精确场景、路径、实例 ID、页号和原始图集纹理名，将对应图集页 GPU 读回为 RGBA PNG。只读原图集，不替换贴图、不改 slot、骨骼或动画；主线程操作及 PNG 清理方式同图片导出。当前没有 Spine 替换命令。

2026-10-14 实机读取任务 6、星骑士 3、故事 3、商店 2 个大按钮，共 14 个，零错误；14 张图集导出通过，按 atlas 坐标、旋转和裁剪偏移提取 27 份独立标题素材。TeamBtn 悬停实测动画由 `cut_01` 切换到 `cut_02`。构建通过、8 项 CLI 检查通过。详细方案和替换尚未实测的边界见 [Spine 图片汉化研究](https://github.com/Azure-Sora/TSKHook-Plus/blob/master/Extensions/docs/spine-ui-localization-research.md)。

### 同尺寸图集替换预览（0.1.6）

```powershell
python $inspectorCli spine-apply --scene Main --path '/Canvas[0]/Card[0]/Card_skeleton[0]' --instance-id -123 --texture btn_quest_main --page 0 --file '.local/quest-atlas-zh.png'
python $inspectorCli spine-restore
```

0.1.6 补充了 `spine-apply` 与 `spine-restore`，上述“没有替换命令”为 0.1.5 的历史边界。PNG 至多 2 MiB（Base64 后仍在桥的 4 MiB 请求上限内），要求尺寸与原图集完全相同；节点和原纹理身份必须匹配。单渲染器通过 `OverrideTexture`，多渲染器通过 `CustomTextureOverride`，按实际 Spine 4.0 渲染路径分别应用。不会修改共享材质、SkeletonData、Skin、骨骼和动画。恢复只处理仍属于本次预览的设置，保留先前映射并释放替换纹理；同一 graphic 再次应用前必须先恢复。

`spine` 同时返回 slot alpha、渲染材质的关键词和 `_StraightAlphaInput`。`renderTextures` 读取的是子 Graphic 的 `mainTexture` getter；它可能返回 `UnityWhite`，不能据此否定 CanvasRenderer 中已由 Spine 更新的贴图，应结合实际截图确认。预览仍为一次性修改，切换销毁页面或重启后需要重新读取身份并应用。

实机首轮：主任务和联合演习分别验证多、单渲染器，原图集替换、恢复、中文显示、实际悬停以及点击进入列表再返回均通过。两个材质均为 Straight Alpha，普通与白色高亮标题原 alpha 完全相同；汉化高亮按中文图 alpha 同步。生成和回填只改标题及 1px 图集边缘，其他像素逐像素不变。构建和 10 项 CLI 测试通过。当前未验证禁用状态或闪白精确帧，未执行任何关卡、挑战或跳过操作。素材、完整提示词、脚本和截图位于 `.local/spine-ui-localization/trial-01/`。

## 构建与安装

```powershell
dotnet build tools/UiInspector/TSKHook.UiInspector.csproj -c Release '-p:GamePath=E:\Games\DMM\Twinkle_StarKnightsX'
```

仅把 `bin/Release/net6.0/TSKHook.UiInspector.dll` 放进游戏 `BepInEx/plugins/`。使用游戏已有的 BepInEx/core 与 interop；不要复制构建目录中的其他程序集。新插件需重启游戏才能加载。停止使用时，关闭游戏后移除这个开发 DLL 即可。

启动后生成 `BepInEx/config/local.tskhook.uiinspector.cfg`。可在 ConfigurationManager 中使用 Commands / Action，也可以使用：

- **F9**：导出树和一份空译文模板。
- **Ctrl+F9**：重读 `overrides.json` 并应用一次预览。
- **Shift+F9**：恢复仍保持为本次预览值的文本，跳过已被游戏修改或销毁的组件。

导出目录为 `BepInEx/plugins/TSKHook/ui_inspector/`。树中的 `screenBox` 为游戏帧缓冲左上角起算的 `[left, top, right, bottom]`；尺寸来自 `Screen.width/height`，不包括窗口标题栏。`path` 转义节点名并带 sibling index，`componentIndex` 是组件在该节点上的序号。路径用于当前版本诊断，层级变化后必须重新导出，不能直接作为长期发行翻译键。

每个节点记录场景、路径、激活状态、组件类型、文本/字体、图片资源名等。`currentText` 可能已经被正式插件翻译，不能当作可靠的原始日文。`spriteRect` 是 Sprite 原始矩形，不是打包纹理中的裁剪位置；这里不调用会在部分 packed Sprite 上异常的 `textureRect`，也不导出图片像素。

## 命令行：无需逐步手动操作

需要 Python 3.10+，无第三方依赖。桥只监听 `127.0.0.1`，默认端口 **27125**，每次启动生成令牌写到上述目录的 `bridge.json`。CLI 自动读取该文件，不需要把令牌粘到命令行或日志。`bridge.json` 仅作本机发现，不应提交或分享。

```powershell
$env:TSK_GAME_PATH = 'E:\Games\DMM\Twinkle_StarKnightsX'
$inspectorCli = 'E:\Codes\TSKHook-UITranslations\tools\UiInspector\ui_inspector.py'
python $inspectorCli ping
python $inspectorCli ui --output '.local/menu-ui.json'
python $inspectorCli catalog --output '.local/static-text-catalog.json'
python $inspectorCli ui --canvas 'MenuCanvas' --include-inactive --max-nodes 20000 --output '.local/menu-ui-full.json'
python $inspectorCli screenshot --output '.local/menu.png'
python $inspectorCli dump
python $inspectorCli click --scene Home --path '/Canvas[0]/Menu[2]/Close[0]' --instance-id 12345
python $inspectorCli click --xy 1000 650
python $inspectorCli scroll --xy 1000 450 --delta -3
python $inspectorCli hover --scene Home --path '/Canvas[0]/Choice[2]' --instance-id 12345
python $inspectorCli unhover
python $inspectorCli apply --file '.local/menu-overrides.json'
python $inspectorCli restore
python $inspectorCli status '此前输出的 requestId'
```

示例路径、场景、ID 和坐标是占位值，必须从当前游戏导出选择。也可用 `--game-path` 或 `--discovery` 替代环境变量。`ui --output`、`screenshot --output` 将结果写在 CLI 所在进程可访问的本机路径；`dump` 将树和模板写在游戏目录。即使游戏以管理员权限运行，CLI 也不依赖窗口按键模拟。

`ui`、`dump`、`apply` 均可使用 `--canvas`、`--include-inactive` / `--no-include-inactive` 和 `--max-nodes` 临时调整扫描范围，不需要先手动打开配置窗口，也不会更改持久配置。导出出现 `truncated=true` 时应先缩小到目标 Canvas 或增大节点上限，再判断是否缺少组件。

0.1.2 的 `catalog` 在主线程遍历游戏 `TextType` 枚举，通过只读 `TextManager.GetText` 获取系统文本；参数使用原样占位符，不打开页面，不调用游戏业务操作。它用于在危险页面之外补齐常用标题/按钮。读取到模板不代表可执行任意文本替换，仍需核对参数类型和实际显示位置。

0.1.3 的 `hover` 同样检查最上层射线和节点实例，仅派发 pointerEnter，保持到下一次 hover、click、unhover 或工具退出。`unhover` 只派发 pointerExit。二者不发送 down/up/click，适合检查选项的悬停说明和角色台词。它不移动系统鼠标；真实鼠标的后续事件仍由游戏处理。

点击与滚动使用 EventSystem 的最上层 Raycast 命中，再沿父级寻找对应事件处理器；按路径操作时还验证目标处理器一致，不会绕过遮挡直接 `Button.onClick.Invoke()`。0.1.1 的点击发送 enter/down，间隔两帧后发送 up/click，并在抬起前重新验证命中目标。`dispatched=true` 只表示事件已派发，页面是否切换应通过下一次 UI / 截图确认。依赖物理鼠标、拖拽或自定义输入循环的控件可能不响应，这一版没有模拟任意按键或输入文本。

所有 Unity 操作均从游戏主线程执行。请求有唯一 ID；近期最多 256 条记录用于拒绝重复提交并查询状态，旧的已完成记录会淘汰。排队超过截止时间或桥关闭后尚未开始的操作会取消；已开始的操作无法撤回，超时返回相应状态，CLI 不自动重试。不要根据一次超时盲目重发点击，应先查询 `status` 并重新读取画面。

## 本地翻译预览

将导出的 `*.overrides.template.json` 复制到本地工作文件，保留所需条目，填写 `translation` 后通过 CLI `apply --file` 发送。也可保存为游戏导出目录下的 `overrides.json`，使用 Ctrl+F9 或 CLI `apply`。

```json
{
  "schemaVersion": 1,
  "entries": [
    {
      "scene": "Home",
      "path": "/Canvas[0]/Menu[2]/Close[0]",
      "component": "TMPro.TextMeshProUGUI",
      "componentIndex": 1,
      "source": "閉じる",
      "translation": "关闭"
    }
  ]
}
```

必须同时精确匹配场景、路径、组件类型、组件序号和当前原文。空译文跳过；重复身份或格式错误拒绝整份规则。CLI 可直接传入文件内容，游戏不需要读取 CLI 的文件路径。规则保留富文本标签、占位符和换行；本工具不负责自动翻译或格式修复。

预览为一次性内存修改，不改 prefab、缓存或网络数据，也不自动反复扫描覆盖游戏更新。重新进入页面后需再次应用。恢复只处理值仍与本工具写入值一致的组件；正式插件的翻译钩子仍可能再次改变恢复值，日志会如实报告验证结果。工具不替换字体，旧 Text/TextMesh 若缺少中文字形，需要进一步适配对应字体。

## 验证边界

```powershell
dotnet run --project tests/UiInspectorSmokeTest/UiInspectorSmokeTest.csproj -c Release
python tests/UiInspectorSmokeTest/test_cli.py
```

托管检查覆盖原文和场景隔离、无效规则拒绝、重复命令、排队取消、异步完成、关闭行为、HTTP 长度及实际本机认证请求；Python 检查验证 CLI 的 UTF-8 传输、认证头、二进制响应和参数构造。这些不等同于真实游戏的 UI 树、截图方向、点击、滚动或汉化显示验收，需安装后逐项检查。

2026-10-14 首次交付：使用本机游戏实际 BepInEx/core 与 interop 构建成功；32 项托管断言和 3 项 Python CLI 测试通过。包括实际 HTTP 请求等待 15 秒后取消，并验证队列恢复时不执行该点击。编译保留本机已有 LIB 路径告警；测试项目另有 net6.0 生命周期提示。开发 DLL 已安装，正式插件哈希保持不变；当时游戏仍在运行，尚未加载新工具，游戏内验收为 **NOT_RUN**。菜单日文是文本还是图片，仍需首次导出后确认。

同日第一次实机检查（0.1.0）：命令桥、正确方向的 1280×720 截图和滚动通过；遮挡目标被拒绝。菜单树导出了 306 个节点，没有截断。菜单标题、按钮标签节点包含 `TMPro.TextMeshProUGUI` 和游戏包装组件 `TKSTextTMPGUI`，确认有文本组件可走汉化路径。发现旧 `TextMesh` 在实际 IL2CPP Player 中被裁剪，但包装程序集仍有类型；0.1.0 对每个组件提前探测该类型，产生 1,238 项读取错误，文本/图片详情不可用。另有关闭事件虽已派发、画面却未关闭的问题，点击验收为 **FAILED**。

0.1.1 改为先读取并缓存组件的实际 native 类型继承链，只访问匹配的文本或图片包装类型，避免触碰被裁剪的无关类型；点击补齐完整事件序列，托管专用辅助方法标记为不注入 IL2CPP。新增类型分类回归检查。这些修正需要重启后验收，不能用旧进程的截图或托管测试宣布修复生效。

### 0.1.1 第二次实机检查（2026-10-14）

用户再次重启后，ping 确认为 `0.1.1.0`，Unity `2021.3.25f1`，游戏 PID `27196`。本轮结果取代上述两项失败的当前状态，原记录保留为问题证据。

- **读取通过**：主页和菜单共导出 307 个节点、33 个文本组件、127 个图片组件，零读取错误、未截断。菜单的 15 处文字均在 `TMPro.TextMeshProUGUI` 中读到确切日文，另附 `TKSTextTMPGUI` 游戏包装组件。
- **菜单汉化通过**：标题、13 个菜单项和关闭按钮共 15 处，精确规则全部命中；重新读取 UI 和真实截图均为中文，所观察文字无缺字方框或明显裁切。
- **实际点击通过**：关闭菜单后 UI 树中 `PopupHomeMenu` 节点数归零，截图回到主页；再次点击 MenuBtn 并等候页面加载，菜单正常打开。点击设置打开真实设置弹窗，关闭设置后弹窗节点归零。
- **设置当前页汉化通过**：没有调整任何设置项，仅为当前可见的战斗标签页预览新增 12 处译文；与底层菜单合计 27 次写入全部验证成功。设置标题、标签、战斗速度、恢复默认设置、取消及确定文字均在截图中显示中文。其他设置标签页尚未验收。
- **恢复通过**：27 处全部恢复原文；通过独立的下一次 UI 读取，按场景、路径、组件和序号逐条核对原文，27 项一致。再次应用后 27 处重新显示译文。
- **导出与滚动通过**：`dump --canvas PopupCanvasFirst` 在游戏目录生成有效树和空译文模板（146 节点、28 文本、57 图片、零错误）；新版本滚动可见改变菜单位置，已滚回顶部。
- **日志**：检查时没有 Error、Fatal、Exception 或辅助方法返回类型不支持的记录。正式插件有一条跳过不可读 TMP atlas 字形插入的正常信息，所观察中文显示正常。

本机证据位于 `.local/ui-localization-investigation/`：`v011-before.json`、`v011-menu-zh.png`、`v011-after-close.json/png`、`v011-settings.json/png`、`v011-settings-zh.png`、`v011-restored.json` 和 `v011-final-menu.json` 等。当前规则为 `menu-overrides.json`（15 条）和 `menu-settings-overrides.json`（27 条）。最终游戏留在中文菜单；预览没有写入正式翻译包，重新创建页面后需要重新应用。未执行领取、消费、战斗或设置变更。

### 0.1.3 与正式包验收（2026-10-14）

之后的版本加入只读系统目录和独立悬停命令，实机已加载 0.1.3。目录返回 169 个非空文本，零读取错误；任务 6、星骑士 3、故事 3、商店 2 个选项逐一仅发送 hover，14 处气泡均读回预期中文并保存截图。文字由正式插件 1.4.6 自动应用合并翻译包，本轮没有使用工具 apply 规则。个人资料、恢复体力说明和抽卡确认等补漏也已入包；完整范围与限制见 [正式插件验证记录](https://github.com/Azure-Sora/TSKHook-Plus/blob/master/Extensions/docs/verification.md)。

工具构建、5 项 CLI 检查与上述实际悬停通过。切换页面时 UI 树可能早于加载遮罩出现；前置拒绝代表没有派发输入，仍需重新读树和截图再操作。原始树和命令令牌仅留在本机忽略目录，不提交到翻译仓。


### 0.1.7：翻译配置重读

`python $inspectorCli reload-tskhook-config` 在 Unity 主线程调用已加载 TSKHook 的公开配置重读入口；不接受任意路径、设置或代码，也不硬依赖插件程序集。不存在 TSKHook 或旧版本未暴露入口时返回错误。维护者先备份并编辑 TSKHook.cfg 后，可借此应用配置及已选择的 Maintenance 操作；一般玩家仍使用 F1。

本机游戏 1.6.0 / Inspector 0.1.7 实测配置重读、菜单 15 处简繁切换与原样显示、独立图片回退及热下载通过。CLI 检查增至 11 项。实时业务操作只包含开始游戏、菜单开关与主页/招募页导航，没有购买、招募、领奖或战斗。详情见 [插件验收记录](https://github.com/Azure-Sora/TSKHook-Plus/blob/master/Extensions/docs/verification.md)。原配置与资源状态已恢复，临时本机 HTTPS 服务、证书与私钥已清理；本轮未发布。
