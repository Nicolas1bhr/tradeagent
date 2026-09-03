using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// THE ATAS ASSEMBLIES ARE NOT COPIED BESIDE THIS, AND THAT IS THE BRIDGE'S RULE RATHER THAN AN
// OVERSIGHT: TradeAgent.AtasBridge references them with Private=false because the platform provides
// them at runtime, and copying them next to a strategy is what breaks its loading. A console that
// runs OUTSIDE ATAS therefore has to say where they live.
//
// Installed before anything ATAS-typed is touched — hence Gate.Run() in another file, so the JIT has
// not had to resolve a single one of those types by the time this handler exists.
var install = Environment.GetEnvironmentVariable("TA_ATAS_DIR")
              ?? @"C:\Program Files (x86)\ATAS Platform";
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var name = new AssemblyName(e.Name).Name;
    var candidate = Path.Combine(install, name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

return TradeAgent.AtasGate.Gate.Run();
