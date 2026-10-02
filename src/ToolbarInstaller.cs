using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#if ZWCAD
using UiApplication = ZwSoft.ZwCAD.ApplicationServices.Application;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#elif ACAD_CORE
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation
{
    /// <summary>「GM批注」CAD 原生工具栏：单字按钮直达常用命令（浮动快捷栏已取消，这里是唯一入口）。
    /// 与 <see cref="MenuInstaller"/> 同一套路——全部通过 COM 反射操作宿主菜单组，任何一步失败都只记日志并返回 false，
    /// 绝不因为"工具栏建不出来"影响插件其它功能。
    /// 按钮图标由 WPF 现场生成：白底深蓝单字（观感与原浮动快捷栏按钮一致），先在 64×64 高清画好再缩到 16×16。</summary>
    internal static class ToolbarInstaller
    {
        /// <summary>工具栏名称；重复加载时按此名复用，不重复创建（避免宿主报"已存在"）。</summary>
        internal const string ToolbarName = "GM批注";

        /// <summary>单个工具栏按钮：图标单字 / 按钮名 / 悬停提示 / 命令。</summary>
        private sealed class ToolbarButtonSpec
        {
            public string Glyph;
            public string Name;
            public string Help;
            public string Command;
        }

        /// <summary>按钮清单（快捷栏已取消，工具栏是唯一入口）。
        /// 注意"批"指向 GM_PZ_DRAW（直接绘制、不弹出批注面板），与菜单里的"绘制批注"（GM_PZ_NOTE，弹面板）不同。</summary>
        private static readonly ToolbarButtonSpec[] Buttons =
        {
            new ToolbarButtonSpec { Glyph = "批", Name = "批注", Help = "绘制批注：框选云线范围 → 放文字框 → 填写内容（不弹出批注面板）", Command = "GM_PZ_DRAW" },
            new ToolbarButtonSpec { Glyph = "移", Name = "移动", Help = "移动批注文字框、文字和引线（云线不动）", Command = "GM_PZ_MOVE" },
            new ToolbarButtonSpec { Glyph = "编", Name = "编辑", Help = "编辑批注（双击批注也可）", Command = "GM_PZ_EDIT" },
            new ToolbarButtonSpec { Glyph = "删", Name = "删除", Help = "删除批注（支持 UNDO）", Command = "GM_PZ_DELETE" },
            new ToolbarButtonSpec { Glyph = "隐", Name = "隐藏", Help = "隐藏批注（未预选时隐藏全图批注）", Command = "GM_PZ_HIDE" },
            new ToolbarButtonSpec { Glyph = "显", Name = "显示", Help = "显示批注（未预选时显示全图批注）", Command = "GM_PZ_SHOW" },
            new ToolbarButtonSpec { Glyph = "合", Name = "合并", Help = "合并批注：内容完全一致的批注并成一条（保留编号最小的一条，其余云线并入）", Command = "GM_PZ_MERGE" },
            new ToolbarButtonSpec { Glyph = "滤", Name = "过滤", Help = "过滤批注：只显示内容完全一致的批注，其余隐藏（按内容分组选择）", Command = "GM_PZ_FILTER" },
            new ToolbarButtonSpec { Glyph = "刷", Name = "格式刷", Help = "格式刷：选一条批注提取样式 → 调整对话框（字高/文字样式/云线样式/图层颜色）→ 连续刷到其他批注", Command = "GM_PZ_FORMAT" },
            new ToolbarButtonSpec { Glyph = "汇", Name = "汇总", Help = "批注汇总", Command = "GM_PZ_SUMMARY" },
            new ToolbarButtonSpec { Glyph = "例", Name = "清单", Help = "批注清单：把本图全部批注生成清单表画在图上（列与批注列表一致，不框选、不画引线）", Command = "GM_PZ_LEGEND" },
            new ToolbarButtonSpec { Glyph = "出", Name = "导出", Help = "导出批注 (CSV)", Command = "GM_PZ_EXPORT" },
            new ToolbarButtonSpec { Glyph = "入", Name = "导入", Help = "导入批注 (CSV)", Command = "GM_PZ_IMPORT" },
            new ToolbarButtonSpec { Glyph = "历", Name = "历史", Help = "批注历史记录（留痕）", Command = "GM_PZ_HISTORY" },
        };

        private static bool _retryAttached;

        // ---- 用户关闭/打开工具栏的检测 ----
        // CAD 的 COM 工具栏对象没有"关闭"事件：工具栏就绪后挂一个 Idle 回调，每 2 秒读一次 Visible，
        // 与上次已知值不同就写回 settings.xml（下次启动按此显隐）。插件卸载/CAD 退出时（Terminate）再查一次。
        private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);
        private static bool _watchAttached;
        private static DateTime _nextWatchUtc;
        private static bool? _lastKnownVisible;
        private static int _watchFailures;
        /// <summary>本次会话是否已把工具栏停靠过（启动时隐藏、之后首次打开时再停靠到顶部）。</summary>
        private static bool _dockedThisSession;

        /// <summary>按上次记录应显示还是隐藏（供加载提示使用）。</summary>
        public static bool SavedVisible => SettingsStore.LoadToolbarVisible();

        /// <summary>创建或刷新工具栏，并按上次记录的显隐状态显示或隐藏（工具栏总会被创建，隐藏时也在，GMPANEL 可随时打开）。失败时安排一次 Idle 重试（NETLOAD 时机过早、CAD 界面尚未就绪时很有用）。</summary>
        /// <returns>工具栏已就绪返回 <c>true</c>。</returns>
        public static bool EnsureWithRetry()
        {
            if (Ensure(out _)) return true;
            if (_retryAttached) return false;
            try
            {
                CadApplication.Idle += OnRetryIdle;
                _retryAttached = true;
            }
            catch (Exception ex) { _retryAttached = false; PluginLog.Error("Toolbar.Retry", ex); }
            return false;
        }

        /// <summary>卸载插件 / CAD 退出时：最后记录一次工具栏显隐，并摘掉 Idle 回调（工具栏本身保留在 CAD 界面，不随卸载消失）。</summary>
        public static void Detach()
        {
            if (_watchAttached)
            {
                CheckUserVisibility("卸载/退出");
                _watchAttached = false;
                try { CadApplication.Idle -= OnWatchIdle; }
                catch (Exception ex) { PluginLog.Error("Toolbar.Detach", ex); }
            }
            if (!_retryAttached) return;
            _retryAttached = false;
            try { CadApplication.Idle -= OnRetryIdle; }
            catch (Exception ex) { PluginLog.Error("Toolbar.Detach", ex); }
        }

        private static void StartWatching()
        {
            _watchFailures = 0;
            if (_watchAttached) return;
            try
            {
                _nextWatchUtc = DateTime.UtcNow + WatchInterval;
                CadApplication.Idle += OnWatchIdle;
                _watchAttached = true;
            }
            catch (Exception ex) { PluginLog.Error("Toolbar.Watch", ex); }
        }

        private static void OnWatchIdle(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            if (now < _nextWatchUtc) return;
            _nextWatchUtc = now + WatchInterval;
            CheckUserVisibility("Idle");
        }

        /// <summary>读取工具栏当前 Visible，与上次已知值不同则记住（用户点了工具栏的 ×，或在 CAD 工具栏右键菜单里勾选/取消）。
        /// CAD 主窗口最小化/不可见时跳过，避免把"随主窗口一起隐藏"误记成用户关闭。</summary>
        private static void CheckUserVisibility(string source)
        {
            try
            {
                if (MainWindowHidden()) return;
                var toolbars = GetToolbarsCollection();
                if (toolbars == null) return;
                var toolbar = TryGetToolbar(toolbars);
                if (toolbar == null) return; // 被 CUI 删除等情况：不改记录，下次启动按记录重建
                var visible = Convert.ToBoolean(toolbar.GetProperty("Visible"));
                _watchFailures = 0;
                if (_lastKnownVisible == visible) return;
                _lastKnownVisible = visible;
                SettingsStore.SaveToolbarVisible(visible);
                PluginLog.Info("Toolbar", "检测到 GM批注工具栏被" + (visible ? "打开" : "关闭") + "（" + source + "），已记住：下次启动" + (visible ? "显示" : "保持隐藏") + "。");
            }
            catch (Exception ex)
            {
                // 连续失败 5 次（约 10 秒）就停止轮询，只记一次日志，避免刷屏。
                if (++_watchFailures == 5)
                {
                    PluginLog.Warning("Toolbar.Watch", "读取工具栏显隐连续失败，停止检测：" + Describe(ex));
                    if (_watchAttached) { _watchAttached = false; try { CadApplication.Idle -= OnWatchIdle; } catch { } }
                }
            }
        }

        private static bool MainWindowHidden()
        {
            try
            {
                var handle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (handle == IntPtr.Zero) return true;
                return IsIconic(handle) || !IsWindowVisible(handle);
            }
            catch { return false; }
        }

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

        private static void OnRetryIdle(object sender, EventArgs e)
        {
            Detach();
            var ready = Ensure(out var message);
            if (!ready) PluginLog.Warning("Toolbar.Retry", message);
        }

        /// <summary>创建（或复用）「GM批注」工具栏，按当前代码重建按钮，并按上次记录的显隐状态显示或隐藏。
        /// 记录为隐藏时不强制 Visible=true（也不停靠，避免宿主停靠时顺带显示），但工具栏仍会创建好，供 GMPANEL 打开。</summary>
        public static bool Ensure(out string message)
        {
            try
            {
                var toolbars = GetToolbarsCollection();
                if (toolbars == null) { message = "当前 CAD 未公开工具栏接口。"; return false; }

                var created = false;
                var toolbar = TryGetToolbar(toolbars);
                if (toolbar == null)
                {
                    toolbars.InvokeMethod("Add", ToolbarName);
                    toolbar = TryGetToolbar(toolbars);
                    created = true;
                }
                if (toolbar == null) { message = "工具栏创建失败：宿主未返回工具栏对象。"; return false; }

                // 重复加载（或手动刷新）时先清掉旧按钮，再按当前代码重建，保证与版本一致。
                while (Convert.ToInt32(toolbar.GetProperty("Count")) > 0)
                {
                    toolbar.InvokeMethod("Item", 0).InvokeMethod("Delete");
                }

                for (var i = 0; i < Buttons.Length; i++) AddButton(toolbar, i, Buttons[i]);

                var visible = SettingsStore.LoadToolbarVisible();
                if (visible)
                {
                    SetProperty(toolbar, "Visible", true);
                    TryDockToTop(toolbar);
                    _dockedThisSession = true;
                }
                else
                {
                    SetProperty(toolbar, "Visible", false);
                }
                _lastKnownVisible = visible;
                StartWatching();

                message = "GM批注工具栏已" + (created ? "创建" : "刷新") + "（" + Buttons.Length + " 个按钮）" +
                    (visible ? "。" : "，按上次关闭状态保持隐藏（GMPANEL 可重新打开）。");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Toolbar.Ensure", ex);
                message = "工具栏创建失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>切换工具栏显示/隐藏并记住；工具栏还不存在时创建并显示。</summary>
        public static bool ToggleVisible(out string message)
        {
            try
            {
                var toolbars = GetToolbarsCollection();
                if (toolbars == null) { message = "当前 CAD 未公开工具栏接口。"; return false; }
                var toolbar = TryGetToolbar(toolbars);
                if (toolbar == null) return SetVisible(true, out message);
                var visible = Convert.ToBoolean(toolbar.GetProperty("Visible"));
                return SetVisible(!visible, out message);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Toolbar.Toggle", ex);
                message = "工具栏操作失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>显示或隐藏工具栏并写入 settings.xml（下次启动按此状态）。工具栏不存在时先按目标状态创建。</summary>
        public static bool SetVisible(bool visible, out string message)
        {
            try
            {
                SettingsStore.SaveToolbarVisible(visible);
                _lastKnownVisible = visible;
                var toolbars = GetToolbarsCollection();
                if (toolbars == null) { message = "当前 CAD 未公开工具栏接口。"; return false; }
                var toolbar = TryGetToolbar(toolbars);
                if (toolbar == null) return Ensure(out message); // Ensure 按刚写入的状态创建

                SetProperty(toolbar, "Visible", visible);
                if (visible && !_dockedThisSession)
                {
                    TryDockToTop(toolbar);
                    _dockedThisSession = true;
                }
                StartWatching();
                message = visible
                    ? "GM批注工具栏已显示（下次启动保持显示）。"
                    : "GM批注工具栏已隐藏（下次启动保持隐藏，输入 GMPANEL 可重新打开）。";
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Toolbar.SetVisible", ex);
                message = "工具栏操作失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>取当前宿主菜单组下的 Toolbars 集合 COM 对象；宿主不支持时返回 <c>null</c>。</summary>
        private static object GetToolbarsCollection()
        {
            var menuGroups = MenuInstaller.GetStatic(typeof(UiApplication), "MenuGroups");
            if (menuGroups == null) return null;
#if ZWCAD
            var group = menuGroups.InvokeMethod("Item", 0);
#else
            var group = menuGroups.InvokeMethod("Item", "Acad");
#endif
            return group?.GetProperty("Toolbars");
        }

        /// <summary>按名称找工具栏；不存在或宿主查询抛异常时返回 <c>null</c>（等价于"还没有"）。</summary>
        private static object TryGetToolbar(object toolbars)
        {
            try
            {
                return toolbars.InvokeMethod("Item", ToolbarName);
            }
            catch (TargetInvocationException)
            {
                // COM 反射把宿主内部异常包成 TargetInvocationException：对按名查询来说等价于"不存在"。
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>加一个按钮：宏前缀与菜单一致。图标用返回的按钮对象的 <c>SetBitmaps(小, 大)</c> 设置。
        /// <para><b>血泪教训：AddToolbarButton 的第 5 个参数是 Boolean（ShowImage）而不是位图路径。</b>
        /// ActiveX 签名是 <c>AddToolbarButton(Index, Name, HelpString, Macro [, ShowImage])</c>，
        /// 往第 5 个位置传字符串会被 COM 判为类型不匹配，抛 TargetInvocationException——
        /// 症状是 11 个按钮全部只有文字没有图标（每次加载 11 条 Toolbar.Button.Icon 日志），
        /// 而失败信息被 ex.Message 吞成一句"调用的目标发生了异常"，根本看不出是参数类型错。</para></summary>
        private static void AddButton(object toolbar, int index, ToolbarButtonSpec spec)
        {
            short buttonIndex = (short)index;
            var macro = MenuInstaller.CreateMenuMacro(spec.Command);
            object button;
            try
            {
                button = toolbar.InvokeMethod("AddToolbarButton", buttonIndex, spec.Name, spec.Help, macro, false);
            }
            catch (Exception ex)
            {
                // 退化路径：某些宿主不认第 5 个参数，用 4 参重试（此时按钮没有图标，但功能可用）。
                PluginLog.Warning("Toolbar.Button.Bool", Describe(ex));
                button = toolbar.InvokeMethod("AddToolbarButton", buttonIndex, spec.Name, spec.Help, macro);
            }
            TrySetIcon(button, spec.Glyph);
        }

        /// <summary>给刚建好的按钮设图标：SetBitmaps 只认**完整文件路径**（不同于 AddToolbarButton 的位图参数，
        /// 那个要的是"支持路径下的资源名"）。大图标位图缺失时用 16×16 顶替，避免因为大小图标不全都放弃设置。</summary>
        private static void TrySetIcon(object button, string glyph)
        {
            if (button == null) return;
            var small = TryCreateIcon(glyph, 16);
            if (string.IsNullOrEmpty(small)) return;
            var large = TryCreateIcon(glyph, 32);
            if (string.IsNullOrEmpty(large)) large = small;
            try
            {
                button.InvokeMethod("SetBitmaps", small, large);
                return;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Toolbar.Button.Icon", Describe(ex));
            }
            // 兜底：个别宿主只认"支持路径下的资源名"，这时把 icons 目录挂进 CAD 支持路径，再用裸文件名设一次。
            try
            {
                var folder = Path.GetDirectoryName(small);
                if (!EnsureSupportPath(folder)) return;
                button.InvokeMethod("SetBitmaps", Path.GetFileName(small), Path.GetFileName(large));
            }
            catch (Exception ex) { PluginLog.Warning("Toolbar.Button.Icon.Name", Describe(ex)); }
        }

        /// <summary>把图标目录加进 CAD 支持路径（Preferences → Files → SupportPath），已存在则不重复加。
        /// 只在"用完整路径设图标"失败后才会走到这里，属于兜底路径；改动会写进当前 CAD 配置，日志里有记录。</summary>

        private static object ResolveAcadApplication()
        {
#if ZWCAD
            try { return UiApplication.ZcadApplication; } catch { return null; }
#else
            var applicationTypeNames = new[]
            {
                "Autodesk.AutoCAD.ApplicationServices.Application, AcMgd",
                "Autodesk.AutoCAD.ApplicationServices.Core.Application, AcCoreMgd"
            };
            foreach (var typeName in applicationTypeNames)
            {
                var type = Type.GetType(typeName, throwOnError: false);
                if (type == null) continue;
                var prop = type.GetProperty("AcadApplication", BindingFlags.Public | BindingFlags.Static);
                var value = prop?.GetValue(null, null);
                if (value != null) return value;
            }
            return null;
#endif
        }
        private static bool EnsureSupportPath(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            try
            {
                var app = ResolveAcadApplication();
                if (app == null) return false;
                var files = app.GetProperty("Preferences").GetProperty("Files");
                var current = Convert.ToString(files.GetProperty("SupportPath")) ?? "";
                if (current.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                SetProperty(files, "SupportPath", current.TrimEnd(';') + ";" + folder);
                PluginLog.Warning("Toolbar.SupportPath", "已把图标目录加入 CAD 支持路径: " + folder);
                return true;
            }
            catch (Exception ex) { PluginLog.Warning("Toolbar.SupportPath", Describe(ex)); return false; }
        }

        /// <summary>尽量把工具栏停靠到顶部工具栏区（失败保持浮动，用户可自己拖）。</summary>
        private static void TryDockToTop(object toolbar)
        {
            try { toolbar.InvokeMethod("Dock", 1); }
            catch (Exception ex) { PluginLog.Warning("Toolbar.Dock", ex.Message); }
        }

        private static void SetProperty(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }

        /// <summary>异常摘要：把内层异常链一并展开。COM 反射失败时外层永远只是
        /// "调用的目标发生了异常(TargetInvocationException)"，真正的原因（HRESULT / 类型不匹配）只在内层，
        /// 只记 ex.Message 等于什么都没记。</summary>
        private static string Describe(Exception ex)
        {
            var text = "";
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (text.Length > 0) text += " <- ";
                text += current.GetType().Name + ": " + current.Message;
            }
            return text;
        }

        // ============ 图标：单字 → 白底蓝字 16×16 BMP ============

        /// <summary>把单字画成工具栏图标（BMP 文件，缓存在数据目录 icons 下），size 传 16 或 32。
        /// 观感对齐原浮动快捷栏按钮：**白底 + 深藏青单字**，整张不透明（不用 192,192,192 掩膜灰，
        /// 那样浅色/深色主题下都是同一块白底，字永远看得清）。
        /// **缓存文件名带样式版本号（tb_v3_）且按"字的码位+尺寸"命名**：改画风只须升版本号即可让旧缓存全部失效；
        /// 码位命名保证按钮增删移位不会出现"例"按钮显示"出"字这类错位；小/大图标要分别有文件（SetBitmaps 两个参数各要一个路径）。</summary>
        private static string TryCreateIcon(string glyph, int size)
        {
            try
            {
                if (size != 16 && size != 32) size = 16;
                var folder = Path.Combine(AppPaths.DataFolder, "icons");
                Directory.CreateDirectory(folder);
                var code = string.IsNullOrEmpty(glyph) ? 0 : char.ConvertToUtf32(glyph, 0);
                var path = Path.Combine(folder, "tb_v3_" + code.ToString("X4", CultureInfo.InvariantCulture) + "_" + size.ToString(CultureInfo.InvariantCulture) + ".bmp");
                if (File.Exists(path)) return path;

                // 高清渲染再缩小：直接在 16×16 上画 11px 汉字会糊成一团。
                // 先在 64×64 上以约 46px 的字号画字，再用线性插值整体缩到目标尺寸，笔画干净得多。
                const int render = 64;
                var typeface = ResolveCjkTypeface(glyph);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, render, render));
                    var glyphBrush = new SolidColorBrush(Color.FromRgb(23, 42, 108)); // 深藏青，接近快捷栏按钮的字色
                    var fontSize = render * 0.72;
#if WPF_NO_PIXELSPERDIP
                    // net45（AutoCAD 2015~2024）：FormattedText 还没有 pixelsPerDip 重载（.NET Framework 4.6.2 才加入），
                    // 它的第 7 个参数是 NumberSubstitution，所以这里必须用 6 参构造（见 LAAnnotation.AutoCAD.csproj 的符号定义）。
                    var text = new FormattedText(
                        glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, glyphBrush);
#else
                    // net472 / net8 必须显式给 pixelsPerDip，而且必须是 1.0：
                    // 它表示"每个 DIP 对应多少像素"，位图按 96 DPI 渲染时就是 1.0。
                    // 千万别顺手填 96 —— 那会把字号放大 96 倍，字画到画布之外，图标只剩背景色。
                    var text = new FormattedText(
                        glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, glyphBrush, 1.0);
#endif
                    dc.DrawText(text, new Point((render - text.Width) / 2.0, (render - text.Height) / 2.0));
                }

                // RenderTargetBitmap 只接受 Pbgra32，传 Bgra32 会抛
                // ArgumentException「此操作不支持"Bgra32"PixelFormat」——
                // 症状是工具栏所有按钮全部没有图标（异常被下面的 catch 吞成一条日志），
                // 极易被当成"图标画不出来"去查画法，其实是格式参数不对。
                // 本图背景是不透明矩形，alpha 恒为 255，预乘与直通一致，24 位 BMP 直接丢 alpha 即可。
                var hi = new RenderTargetBitmap(render, render, 96, 96, PixelFormats.Pbgra32);
                hi.Render(visual);
                var scaled = new TransformedBitmap(hi, new ScaleTransform((double)size / render, (double)size / render));
                var pixels = new byte[size * size * 4];
                scaled.CopyPixels(pixels, size * 4, 0);
                WriteBmp24bpp(path, pixels, size, size);
                return path;
            }
            catch (Exception ex)
            {
                // 图标属美化项：生成失败不影响工具栏本身（按钮只是没有图标）。
                PluginLog.Warning("Toolbar.Icon", Describe(ex));
                return null;
            }
        }

        /// <summary>选一个**真正含该汉字字形**的字体。CAD 宿主进程里按名解析字体可能失败，
        /// 落到 WPF 的默认回退字体后没有中文字形，汉字会被替换成"？"——图标全部变成问号块（已实际发生）。
        /// 所以不能只写一个 FontFamily 名：这里逐个候选字体校验"字形表里确实有这个字"，
        /// 全部失败再用字体文件兜底（黑体是各版本 Windows 中文环境都自带的 TTF）。</summary>
        private static Typeface ResolveCjkTypeface(string glyph)
        {
            var ch = string.IsNullOrEmpty(glyph) ? '\0' : glyph[0];
            var names = new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun", "NSimSun", "DengXian", "KaiTi" };
            foreach (var name in names)
            {
                try
                {
                    var tf = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                    if (HasGlyph(tf, ch)) return tf;
                }
                catch { /* 字体不存在等情况，换下一个候选 */ }
            }

            // 文件兜底：按文件 URI 构造 FontFamily（"./文件名" 是 WPF 支持的写法）。
            var files = new[] { "simhei.ttf", "msyh.ttc", "simsun.ttc" };
            foreach (var file in files)
            {
                try
                {
                    var tf = new Typeface(new FontFamily(new Uri("file:///C:/Windows/Fonts/"), "./" + file), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                    if (HasGlyph(tf, ch)) return tf;
                }
                catch { /* 文件不存在等情况，换下一个 */ }
            }

            // 都不行就退回候选表第一个——至少行为与旧版一致，日志里能看到图标生成失败的原因。
            return new Typeface(new FontFamily(names[0]), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        }

        private static bool HasGlyph(Typeface typeface, char ch)
        {
            if (ch == 0) return true;
            try
            {
                return typeface.TryGetGlyphTypeface(out var gt) && gt.CharacterToGlyphMap.ContainsKey(ch);
            }
            catch { return false; }
        }

        /// <summary>把 BGRA 像素（自上而下）写成 24 位 BMP（自下而上、每行 4 字节对齐）。不依赖 System.Drawing。</summary>
        private static void WriteBmp24bpp(string path, byte[] bgra, int width, int height)
        {
            var stride = (width * 3 + 3) / 4 * 4;
            var dataSize = stride * height;
            var buffer = new byte[54 + dataSize];

            buffer[0] = (byte)'B'; buffer[1] = (byte)'M';
            WriteInt(buffer, 2, 54 + dataSize);
            WriteInt(buffer, 6, 0);
            WriteInt(buffer, 10, 54);

            WriteInt(buffer, 14, 40);
            WriteInt(buffer, 18, width);
            WriteInt(buffer, 22, height);
            WriteShort(buffer, 26, 1);
            WriteShort(buffer, 28, 24);
            WriteInt(buffer, 30, 0);
            WriteInt(buffer, 34, dataSize);
            WriteInt(buffer, 38, 2835);
            WriteInt(buffer, 42, 2835);
            WriteInt(buffer, 46, 0);
            WriteInt(buffer, 50, 0);

            for (var y = 0; y < height; y++)
            {
                var sourceRow = y * width * 4;
                var targetRow = 54 + (height - 1 - y) * stride;
                for (var x = 0; x < width; x++)
                {
                    buffer[targetRow + x * 3 + 0] = bgra[sourceRow + x * 4 + 0];
                    buffer[targetRow + x * 3 + 1] = bgra[sourceRow + x * 4 + 1];
                    buffer[targetRow + x * 3 + 2] = bgra[sourceRow + x * 4 + 2];
                }
            }

            File.WriteAllBytes(path, buffer);
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8 & 0xFF);
            buffer[offset + 2] = (byte)(value >> 16 & 0xFF);
            buffer[offset + 3] = (byte)(value >> 24 & 0xFF);
        }

        private static void WriteShort(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8 & 0xFF);
        }
    }
}
