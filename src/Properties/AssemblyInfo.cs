using System.Reflection;
using System.Runtime.InteropServices;
#if ZWCAD
using ZwSoft.ZwCAD.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif

[assembly: AssemblyTitle("LA批注")]
[assembly: AssemblyDescription("ZWCAD drawing annotation plug-in")]
[assembly: AssemblyCompany("LA")]
[assembly: AssemblyProduct("LA批注")]
[assembly: AssemblyVersion("0.3.3.0")]
[assembly: AssemblyFileVersion("0.3.3.0")]
[assembly: ComVisible(false)]
[assembly: ExtensionApplication(typeof(LAAnnotation.PluginEntry))]
[assembly: CommandClass(typeof(LAAnnotation.Commands))]
