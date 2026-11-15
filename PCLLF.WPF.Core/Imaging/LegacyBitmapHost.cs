using System;
using System.Windows;
using DrawingBitmap = System.Drawing.Bitmap;

namespace PCLCS;

/// <summary>The actual LF application resource owner and logger primitives.</summary>
public sealed class LegacyBitmapHost {
    public readonly string ResourceAssemblyName;
    public readonly Func<string?, DrawingBitmap?> FindBitmapResource;
    public readonly Action<Exception, string> Warn;
    public readonly Action? PrepareWebP;

    public LegacyBitmapHost(string resourceAssemblyName, Func<string?, DrawingBitmap?> findBitmapResource,
        Action<Exception, string> warn, Action? prepareWebP = null) {
        ResourceAssemblyName = resourceAssemblyName;
        FindBitmapResource = findBitmapResource;
        Warn = warn;
        PrepareWebP = prepareWebP;
    }

    // Native C# owners provide the same actual resource dictionary and warning sink.
    // The legacy adapter preserves My.Application's original cast in its delegate.
    public static LegacyBitmapHost ForApplication(Type resourceOwner, Action<Exception, string> warn) =>
        new(resourceOwner.Assembly.GetName().Name!, key => (DrawingBitmap?)Application.Current.TryFindResource(key!), warn);
}
