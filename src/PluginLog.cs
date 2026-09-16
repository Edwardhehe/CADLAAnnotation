using System;
using System.IO;
using System.Text;

namespace GMAnnotation
{
    internal static class PluginLog
    {
        private static readonly string Folder=Path.Combine(AppPaths.DataFolder,"Logs");
        public static void Error(string area,Exception ex){try{Directory.CreateDirectory(Folder);File.AppendAllText(Path.Combine(Folder,"GMAnnotation.log"),$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{area}] {ex}\r\n",Encoding.UTF8);}catch{}}
        public static void Warning(string area,string message){try{Directory.CreateDirectory(Folder);File.AppendAllText(Path.Combine(Folder,"GMAnnotation.log"),$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{area}] {message}\r\n",Encoding.UTF8);}catch{}}
        public static void Info(string area,string message){try{Directory.CreateDirectory(Folder);File.AppendAllText(Path.Combine(Folder,"GMAnnotation.log"),$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{area}] {message}\r\n",Encoding.UTF8);}catch{}}
    }
}
