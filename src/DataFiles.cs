using System;
using System.IO;
using System.Text;
using System.Threading;

namespace GMAnnotation
{
    /// <summary>
    /// 本机数据文件（settings.xml / history.json / knowledge.json / drawingnos.json）的公共读写工具：
    /// <list type="bullet">
    /// <item>跨进程互斥：同一用户同时开着 AutoCAD 与 ZWCAD（或两个 CAD 进程）时共用 %AppData%\GMAnnotation，
    /// 读-改-写必须串行，否则后写的一方会把先写的一方覆盖掉。</item>
    /// <item>原子写：先写同目录 .tmp，再 File.Replace 替换；任何时刻磁盘上都是完整的旧文件或完整的新文件。</item>
    /// <item>文本编码探测：UTF-8 BOM → 严格 UTF-8 → GBK(936) → 系统默认（Excel 中文环境"另存为 CSV"默认是 GBK）。</item>
    /// </list>
    /// </summary>
    internal static class DataFiles
    {
        private const string MutexName = "Local\\GMAnnotation.DataFiles";
        private const int LockTimeoutMs = 5000;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static int _encodingsReady;

        /// <summary>注册代码页编码（.NET 8 默认不带 GBK 等代码页；.NET Framework 自带，无需注册）。</summary>
        public static void EnsureEncodings()
        {
            if (Interlocked.Exchange(ref _encodingsReady, 1) == 1) return;
#if ACAD_CORE
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch (Exception ex) { PluginLog.Warning("Encoding.Register", ex.Message); }
#endif
        }

        /// <summary>GBK(936) 编码；取不到时返回 null。</summary>
        public static Encoding Gbk()
        {
            EnsureEncodings();
            try { return Encoding.GetEncoding(936); }
            catch (Exception ex) { PluginLog.Warning("Encoding.GBK", ex.Message); return null; }
        }

        /// <summary>读文本并猜编码：UTF-8 BOM → 严格 UTF-8 → GBK(936) → 系统默认。</summary>
        public static string ReadAllTextDetect(string filePath)
        {
            return DecodeDetect(File.ReadAllBytes(filePath), out _);
        }

        /// <summary>按 UTF-8 BOM → 严格 UTF-8 → GBK → 系统默认 解码，并给出采用的编码名称。</summary>
        public static string DecodeDetect(byte[] bytes, out string encodingName)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            { encodingName = "UTF-8(BOM)"; return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); }
            try { var text = new UTF8Encoding(false, true).GetString(bytes); encodingName = "UTF-8"; return text; }
            catch (Exception) { /* 不是合法 UTF-8，继续往下试 */ }
            var gbk = Gbk();
            if (gbk != null) { encodingName = "GBK"; return gbk.GetString(bytes); }
            encodingName = Encoding.Default.WebName;
            return Encoding.Default.GetString(bytes);
        }

        /// <summary>
        /// 获取跨进程数据文件锁（同线程可重入）。<paramref name="required"/> 为 true 时等不到锁抛 IOException（读-改-写场景，
        /// 宁可本次不保存也不覆盖别人刚写的数据）；为 false 时等不到也继续（只追加一行之类的低风险写入）。
        /// </summary>
        public static IDisposable Lock(bool required = true)
        {
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(false, MutexName);
                bool acquired;
                try { acquired = mutex.WaitOne(LockTimeoutMs); }
                catch (AbandonedMutexException) { acquired = true; } // 上一个持有者异常退出：锁已归本线程
                if (acquired) return new Releaser(mutex);
                mutex.Dispose();
                if (required) throw new IOException("GM批注数据文件正被另一个 CAD 进程占用，请稍后重试。");
                PluginLog.Warning("DataFiles.Lock", "等待数据文件锁超时，继续执行（低风险写入）。");
                return new Releaser(null);
            }
            catch (IOException) { throw; }
            catch (Exception ex)
            {
                mutex?.Dispose();
                if (required) throw new IOException("无法获取 GM批注数据文件锁：" + ex.Message, ex);
                PluginLog.Warning("DataFiles.Lock", ex.Message);
                return new Releaser(null);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private Mutex _mutex;
            public Releaser(Mutex mutex) { _mutex = mutex; }
            public void Dispose()
            {
                var m = Interlocked.Exchange(ref _mutex, null);
                if (m == null) return;
                try { m.ReleaseMutex(); } catch (Exception ex) { PluginLog.Warning("DataFiles.Unlock", ex.Message); }
                m.Dispose();
            }
        }

        /// <summary>原子写文本（UTF-8 无 BOM，除非指定编码）：写 .tmp → File.Replace；替换失败时重试，最后退回覆盖写。</summary>
        public static void WriteAllTextAtomic(string path, string text, Encoding encoding = null)
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text ?? "", encoding ?? Utf8NoBom);
            ReplaceWithTemp(temp, path);
        }

        /// <summary>把已写好的临时文件替换到目标位置。</summary>
        public static void ReplaceWithTemp(string temp, string path)
        {
            Exception last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null, true);
                    else File.Move(temp, path);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    last = ex;
                    Thread.Sleep(100);
                }
            }
            // 某些文件系统（网络盘等）不支持 Replace：退回"覆盖复制"，至少不会出现文件缺失的空窗。
            try
            {
                File.Copy(temp, path, true);
                File.Delete(temp);
                PluginLog.Warning("DataFiles.Replace", "原子替换失败，已改用覆盖写入：" + (last?.Message ?? ""));
            }
            catch (Exception ex)
            {
                PluginLog.Error("DataFiles.Replace", ex);
                throw;
            }
        }
    }
}
