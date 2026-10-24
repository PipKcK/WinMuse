using Microsoft.UI.Xaml;

namespace WinMuse.Helpers;

public static class Lifetime
{
    /// <summary>Attach while the element is in the tree so short-lived pages don't leak through service events.</summary>
    public static void WhileLoaded(this FrameworkElement el, Action attach, Action detach)
    {
        el.Loaded += (_, _) => attach();
        el.Unloaded += (_, _) => detach();
    }
}
