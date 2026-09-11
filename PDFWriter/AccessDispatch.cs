using System.Reflection;
using System.Runtime.ExceptionServices;

namespace PDFWriter;

// Invoke named Automation properties without the C# dynamic binder's type-info lookup.
internal static class AccessDispatch
{
    internal static object Get(object target, string name, params object[] args)
    => Invoke(target, name, BindingFlags.GetProperty, args);

    internal static object Call(object target, string name, params object[] args)
        => Invoke(target, name, BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding, args);

    private static object Invoke(object target, string name, BindingFlags operation, object[] args)
    {
        try
        {
            return target.GetType().InvokeMember(name,
                operation | BindingFlags.Public | BindingFlags.Instance,
                null, target, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
