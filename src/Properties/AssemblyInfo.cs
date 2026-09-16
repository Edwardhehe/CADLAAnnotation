using System.Reflection;
using System.Runtime.InteropServices;
#if ZWCAD
using ZwSoft.ZwCAD.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif

[assembly: AssemblyTitle("GM批注")]
[assembly: AssemblyDescription("AutoCAD and ZWCAD drawing annotation plug-in")]
[assembly: AssemblyCompany("GM")]
[assembly: AssemblyProduct("GM批注")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: ComVisible(false)]
[assembly: ExtensionApplication(typeof(GMAnnotation.PluginEntry))]
[assembly: CommandClass(typeof(GMAnnotation.Commands))]
