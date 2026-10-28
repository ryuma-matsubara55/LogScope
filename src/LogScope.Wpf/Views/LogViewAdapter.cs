using System.Windows;
using LogScope.Wpf.Views;

namespace LogScope.Wpf;

public static class LogViewAdapter
{
    public static void BindLineNumbers(LogView view, Func<LogView, bool, Visibility> visibilityFunc)
    {
        view.SetShowLineNumbersHandler((v, show) => visibilityFunc(v, show));
    }
}
