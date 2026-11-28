using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace PCLLF.Native.Facade.Tests;
internal static class Native
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static readonly Assembly Main = typeof(PCL.Application).Assembly;
    internal static Type Type(string name) => Main.GetType(name, true)!;
    internal static PCL.GlassMessageDialog Dialog(PCL.ModMain.MyMsgBoxConverter request)
    {
        if (request.ContentFactory is not null) request.UseNativeContent = true;
        return new PCL.GlassMessageDialog(request, () => { }, _ => { });
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static FieldInfo Field(Type type, string name) => type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name);
    // State moved from module fields to forwarding properties. Keep Field() strict for all ABI/metadata assertions.
    internal static StateSlot State(Type type, string name) => new(type, name);
    internal sealed class StateSlot {
        private readonly FieldInfo? field;
        private readonly PropertyInfo? property;
        internal StateSlot(Type type, string name) {
            field = type.GetField(name, Flags); property = type.GetProperty(name, Flags);
            if ((field is null) == (property is null)) throw new MissingMemberException(type.FullName, name);
        }
        internal object? GetValue(object? owner) => field is not null ? field.GetValue(owner) : property!.GetValue(owner);
        internal void SetValue(object? owner, object? value) {
            if (field is not null) field.SetValue(owner, value); else property!.SetValue(owner, value);
        }
    }
    internal static object? Get(object target, string name) => Field(target.GetType(), name).GetValue(target);
    internal static void Set(object target, string name, object? value) => Field(target.GetType(), name).SetValue(target, value);
    internal static MethodInfo Method(Type type, string name) => type.GetMethod(name, Flags) ?? throw new MissingMethodException(type.FullName, name);
    internal static object? Call(Type type, string name, object? instance, params object? [] arguments)
    {
        try
        {
            return Method(type, name).Invoke(instance, arguments);
        }
        catch (TargetInvocationException error)when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    internal static object? Static(string type, string name, params object? [] arguments) => Call(Type(type), name, null, arguments);
    internal static PropertyInfo Property(Type type, string name) => type.GetProperty(name, Flags) ?? throw new MissingMemberException(type.FullName, name);
    internal static object? Enum(string type, string name) => System.Enum.Parse(Type(type), name);
    internal static IDictionary Entries => (IDictionary)(Field(Type("PCL.Settings"), "Entries").GetValue(null) ?? throw new InvalidOperationException("Real Settings.Entries is unavailable."));

    internal static void Cache(string key, object value)
    {
        object entry = Entries[key] ?? throw new InvalidOperationException("Missing real setting " + key);
        ((IDictionary)Get(entry, "ValueCache")!)[""] = value;
    }

    internal static string Full(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
