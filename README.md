# LA批注

当前测试版本：v0.3.3。

同时支持 AutoCAD 与 ZWCAD 的本地批注插件。两种宿主共用同一套业务源码、DWG 数据格式、界面和命令，无需登录或联网。

## 构建与加载

1. 在 PowerShell 执行 `./build.ps1`。
2. 按宿主选择 DLL：
   - ZWCAD 2025：`bin/Release/net48/LAAnnotation.ZWCAD.dll`
   - AutoCAD 2019+：`bin/Release/net48/LAAnnotation.AutoCAD.dll`
3. 在对应 CAD 中执行 `NETLOAD` 加载。不要在 AutoCAD 中加载 ZWCAD DLL，反之亦然。

## 命令

- `LA_PZ_NOTE`：创建矩形云线批注；
- `LA_PZ_EDIT`：选择并编辑批注；
- `LA_PZ_SETTINGS`：设置图层、颜色、文字和云线尺寸；
- `LA_PZ_DELETE`：删除整条批注。
- `LA_PZ_MENU`：创建或恢复顶部“LA批注”菜单。

插件加载后会自动尝试在 CAD 顶部菜单栏创建“LA批注”菜单。如果菜单栏被隐藏，可先把 CAD 的 `MENUBAR` 设置为 `1`，再运行 `LA_PZ_MENU`。

创建顺序：先指定云线区域两个角点，再指定批注框位置，最后填写内容。内容窗口取消时不会创建任何图元或占用编号。

从 v0.3.0 开始，第二角点移动过程中会通过 DrawJig 实时预览云线；确认范围后，移动鼠标会继续实时预览云线、引线和批注框。按 Esc 取消不会写入任何临时图元。云线自适应按框选周长计算弧瓣间距，矩形和菱形每边至少 4 瓣，总顶点不超过 400。

创建、编辑、设置界面已全部迁移为标准 XAML WPF，不再引用 WinForms。源码结构为 `Views/AnnotationWindow.xaml(.cs)`、`Views/SettingsWindow.xaml(.cs)` 和 `Themes/LATheme.xaml`。

设置窗口包含外形（矩形/菱形/椭圆）、云线样式、半径与线宽、自适应与比例、分行字高、人员角色、固定宽度、图层与文字样式、分项颜色、日期/姓名图层规则、连接符、打印、正交/捕捉恢复及双击编辑。文字框按 MText 实际排版尺寸生成，修改内容后同步调整边框。

批注创建后，双击云线、引线、文字或文字框会尝试打开编辑窗口。若当前 CAD 的原生双击行为发生冲突，请使用 `LA_PZ_EDIT`。

设置保存在 `%APPDATA%\LAAnnotation\settings.xml`；完整批注数据保存在 DWG 的 Group 扩展字典中。

启用“字体自适应”后，插件按当前 CAD 视图高度计算实际字高，默认占视图高度 1.8%。首行、正文、状态行、云线半径、线宽、边框留白和固定宽度会作为一个整体同步缩放；命令行会显示本次采用的实际字高。每条批注会保存自己的实际绘制尺寸，之后编辑不会因全局设置变化而突然缩放。

运行日志位于 `%APPDATA%\LAAnnotation\Logs\LAAnnotation.log`。

## 第一版已知限制

- COPY 会复制批注 UUID，尚未自动重新编号；
- 暂不提供批注列表、Excel 导出、多区域批注和图片批注；
- 双击行为需要在实际 ZWCAD 环境中验证与其他插件的兼容性。
