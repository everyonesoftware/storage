using System.Runtime.InteropServices;
using System.Text.Json;
using Storage.Client;
namespace Storage.Service;
public sealed class NativeCore(string repositoryRoot, string libraryPath) : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ExecuteFn([MarshalAs(UnmanagedType.LPUTF8Str)] string root, [MarshalAs(UnmanagedType.LPUTF8Str)] string actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string command);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeFn(IntPtr response);
    private const string Actor = "{\"principalId\":\"local-owner\",\"capabilities\":[\"readItems\",\"createItems\",\"createVersions\",\"modifyItems\",\"manageAccess\",\"manageCollections\",\"executeQueries\",\"readDiagnostics\"]}";
    private IntPtr library;
    private readonly object gate = new();
    public JsonElement Execute(JsonElement command)
    {
        lock (gate)
        {
            if (library == IntPtr.Zero) library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
            var execute = Marshal.GetDelegateForFunctionPointer<ExecuteFn>(NativeLibrary.GetExport(library, "storage_execute"));
            var free = Marshal.GetDelegateForFunctionPointer<FreeFn>(NativeLibrary.GetExport(library, "storage_free"));
            IntPtr result = execute(Path.GetFullPath(repositoryRoot), Actor, command.GetRawText());
            if (result == IntPtr.Zero) throw new StorageException("InternalError", "Native engine returned no response.");
            try { using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(result) ?? "null"); return document.RootElement.Clone(); }
            finally { free(result); }
        }
    }
    public void Dispose() { lock (gate) { if (library != IntPtr.Zero) { NativeLibrary.Free(library); library = IntPtr.Zero; } } }
}
