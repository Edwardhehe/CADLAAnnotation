using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using WinRegistry = Microsoft.Win32.Registry;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace GMAnnotation
{
    /// <summary>
    /// 自动加载管理器（对齐「批量打印」/ IFoxCAD）：
    /// 只写入当前正在运行的 CAD 的 <c>UserRegistryProductRootKey\Applications</c>，
    /// 不扫其它年份版本。Applications 不存在时 CreateSubKey 创建。
    /// 卸载时顺带清理旧名 LAAnnotation，避免改名后双加载。
    /// </summary>
    internal static class AutoloadManager
    {
        private const string AppKeyName = "GMAnnotation";
        private const string LegacyAppKeyName = "LAAnnotation";
        private const string AppDescription = "GM批注插件";

        public static string CurrentDllPath =>
            Assembly.GetExecutingAssembly().Location;

        public static bool IsInstalled(out string dllPath)
        {
            dllPath = string.Empty;
            var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
            if (applicationsRoot == null)
            {
                return false;
            }

            using (var key = WinRegistry.CurrentUser.OpenSubKey(
                applicationsRoot + "\\" + AppKeyName))
            {
                var loader = key?.GetValue("LOADER")?.ToString();
                if (string.IsNullOrWhiteSpace(loader))
                {
                    return false;
                }

                dllPath = loader;
                return true;
            }
        }

        public static IReadOnlyList<string> Install(string dllPath = null)
        {
            dllPath = string.IsNullOrWhiteSpace(dllPath)
                ? Path.GetFullPath(CurrentDllPath)
                : Path.GetFullPath(dllPath);
            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException("当前插件 DLL 不存在，无法安装自动加载。", dllPath);
            }

            var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: true)
                ?? throw new InvalidOperationException(
                    "未找到当前 CAD 的自动加载注册表位置。请先正常启动一次当前 CAD。");

            if (!WriteAutoloadKey(applicationsRoot, dllPath))
            {
                throw new InvalidOperationException("无法写入当前 CAD 的自动加载注册表项。");
            }

            // 清理同目录下的旧 LA 键，避免双加载
            TryDeleteKey(applicationsRoot, LegacyAppKeyName);

            return new[] { applicationsRoot };
        }

        public static int Uninstall()
        {
            var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
            if (applicationsRoot == null)
            {
                return 0;
            }

            var removed = 0;
            foreach (var keyName in new[] { AppKeyName, LegacyAppKeyName })
            {
                if (TryDeleteKey(applicationsRoot, keyName))
                {
                    removed++;
                }
            }

            return removed;
        }

        private static bool TryDeleteKey(string applicationsRoot, string keyName)
        {
            using (var parent = WinRegistry.CurrentUser.OpenSubKey(
                applicationsRoot,
                writable: true))
            {
                if (parent == null)
                {
                    return false;
                }

                try
                {
                    if (!parent.GetSubKeyNames().Any(name =>
                        string.Equals(name, keyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }

                    parent.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                    return true;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Autoload.DeleteKey", ex);
                    return false;
                }
            }
        }

        private static bool WriteAutoloadKey(string applicationsRoot, string dllPath)
        {
            using (var key = WinRegistry.CurrentUser.CreateSubKey(
                applicationsRoot + "\\" + AppKeyName))
            {
                if (key == null)
                {
                    return false;
                }

                key.SetValue("DESCRIPTION", AppDescription, RegistryValueKind.String);
                key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
                key.SetValue("LOADER", dllPath, RegistryValueKind.String);
                key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
                return true;
            }
        }

        /// <summary>
        /// 当前正在运行的 CAD 的 Applications 路径（IFoxCAD GetAcAppKey 写法）。
        /// </summary>
        private static string GetCurrentCadApplicationsRoot(bool createIfMissing)
        {
            var productRoot = HostApplicationServices.Current?.UserRegistryProductRootKey;
            if (string.IsNullOrWhiteSpace(productRoot))
            {
                return null;
            }

            productRoot = NormalizeHkcuRelativePath(productRoot);
            if (string.IsNullOrWhiteSpace(productRoot))
            {
                return null;
            }

            var applicationsPath = productRoot + "\\Applications";
            if (createIfMissing)
            {
                using (var created = WinRegistry.CurrentUser.CreateSubKey(applicationsPath))
                {
                    return created != null ? applicationsPath : null;
                }
            }

            using (var existing = WinRegistry.CurrentUser.OpenSubKey(applicationsPath))
            {
                return existing != null ? applicationsPath : null;
            }
        }

        private static string NormalizeHkcuRelativePath(string path)
        {
            var normalized = path.Replace('/', '\\').Trim('\\');
            const string hkcuPrefix = @"HKEY_CURRENT_USER\";
            if (normalized.StartsWith(hkcuPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(hkcuPrefix.Length).Trim('\\');
            }

            return normalized;
        }
    }
}