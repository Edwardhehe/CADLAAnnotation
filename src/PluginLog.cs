using System;
using System.IO;
using System.Text;

namespace LAAnnotation
{
    internal static class PluginLog
    {
        private static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"LAAnnotation","Logs");
        public static void Error(string area,Exception ex){try{Directory.CreateDirectory(Folder);File.AppendAllText(Path.Combine(Folder,"LAAnnotation.log"),$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{area}] {ex}\r\n",Encoding.UTF8);}catch{}}
        public static void Warning(string area,string message){try{Directory.CreateDirectory(Folder);File.AppendAllText(Path.Combine(Folder,"LAAnnotation.log"),$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{area}] {message}\r\n",Encoding.UTF8);}catch{}}
    }
}
