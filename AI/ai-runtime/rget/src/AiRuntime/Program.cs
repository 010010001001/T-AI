using System.Reflection;
using System.Runtime.InteropServices;

var baseDir = AppContext.BaseDirectory;
var nativeDir = Path.Combine(baseDir, "native");

NativeLibrary.SetDllImportResolver(
    Assembly.GetExecutingAssembly(),
    (name, asm, path) =>
    {
        var candidate = Path.Combine(nativeDir, name + ".dll");
        return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
    });

Native.ai_backend_init(Path.Combine(baseDir, "backends"));