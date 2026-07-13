using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using WinRegistry = Microsoft.Win32.Registry;
#if !ZWCAD
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace LAAnnotation
{
    /// <summary>通过当前用户注册表切换当前 CAD 宿主的启动自动加载。</summary>
    internal static class AutoloadManager
    {
        private const string AppKeyName = "LAAnnotation";
        private const string AppDescription = "LA批注插件";
#if ZWCAD
        private const string ProductRoot = @"Software\ZWSOFT\ZWCAD";
#endif

        public static string CurrentDllPath =>
            Assembly.GetExecutingAssembly().Location;

        public static bool IsInstalled(out string dllPath)
        {
            dllPath = string.Empty;
            foreach (var applicationsRoot in GetApplicationRoots())
            {
                using (var key = WinRegistry.CurrentUser.OpenSubKey(
                    applicationsRoot + "\\" + AppKeyName))
                {
                    var loader = key?.GetValue("LOADER")?.ToString();
                    if (!string.IsNullOrWhiteSpace(loader))
                    {
                        dllPath = loader;
                        return true;
                    }
                }
            }

            return false;
        }

        public static IReadOnlyList<string> Install()
        {
            var dllPath = Path.GetFullPath(CurrentDllPath);
            var roots = GetApplicationRoots().ToList();
            if (roots.Count == 0)
            {
                throw new InvalidOperationException(
                    "未找到当前 CAD 的自动加载注册表位置。请先正常启动一次当前 CAD。");
            }

            foreach (var applicationsRoot in roots)
            {
                using (var key = WinRegistry.CurrentUser.CreateSubKey(
                    applicationsRoot + "\\" + AppKeyName))
                {
                    if (key == null)
                    {
                        continue;
                    }

                    key.SetValue(
                        "DESCRIPTION",
                        AppDescription,
                        RegistryValueKind.String);
                    key.SetValue(
                        "LOADCTRLS",
                        2,
                        RegistryValueKind.DWord);
                    key.SetValue(
                        "LOADER",
                        dllPath,
                        RegistryValueKind.String);
                    key.SetValue(
                        "MANAGED",
                        1,
                        RegistryValueKind.DWord);
                }
            }

            return roots;
        }

        public static int Uninstall()
        {
            var removed = 0;
            foreach (var applicationsRoot in GetApplicationRoots())
            {
                using (var parent = WinRegistry.CurrentUser.OpenSubKey(
                    applicationsRoot,
                    writable: true))
                {
                    if (parent == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (parent.GetSubKeyNames().Any(name =>
                            string.Equals(
                                name,
                                AppKeyName,
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            parent.DeleteSubKeyTree(
                                AppKeyName,
                                throwOnMissingSubKey: false);
                            removed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error("Autoload.Uninstall", ex);
                    }
                }
            }

            return removed;
        }

        private static IEnumerable<string> GetApplicationRoots()
        {
#if ZWCAD
            using (var root = WinRegistry.CurrentUser.OpenSubKey(ProductRoot))
            {
                if (root == null)
                {
                    yield break;
                }

                foreach (var version in root.GetSubKeyNames()
                    .OrderByDescending(
                        value => value,
                        StringComparer.OrdinalIgnoreCase))
                {
                    using (var versionKey = root.OpenSubKey(version))
                    {
                        if (versionKey == null)
                        {
                            continue;
                        }

                        foreach (var locale in versionKey.GetSubKeyNames()
                            .OrderBy(
                                value => value,
                                StringComparer.OrdinalIgnoreCase))
                        {
                            var applicationsPath = ProductRoot + "\\" +
                                version + "\\" + locale + "\\Applications";
                            using (var applications =
                                WinRegistry.CurrentUser.OpenSubKey(applicationsPath))
                            {
                                if (applications != null)
                                {
                                    yield return applicationsPath;
                                }
                            }
                        }
                    }
                }
            }
#else
            var productRoot = HostApplicationServices.Current
                .UserRegistryProductRootKey;
            if (string.IsNullOrWhiteSpace(productRoot))
            {
                yield break;
            }

            var applicationsPath = productRoot + "\\Applications";
            using (var applications =
                WinRegistry.CurrentUser.OpenSubKey(applicationsPath))
            {
                if (applications != null)
                {
                    yield return applicationsPath;
                }
            }
#endif
        }
    }
}
