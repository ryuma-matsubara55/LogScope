using System.Collections.Generic;
using System.Linq;
using System.Windows.Documents;
using LogScope.Wpf.Views;

namespace LogScope.Wpf.ViewModels;

public static class LogViewDirection
{
    public static void ApplyLines(LogView view, IEnumerable<string> lines, long lineNumber)
    {
        if (view is null)
        {
            return;
        }

        var list = lines.Take(2000).ToList();
        view.SetLineContent(list);
        view.ScrollToLine(lineNumber);
    }

    public static void ApplySingleLine(LogView view, string text, long lineNumber, int maxLength)
    {
        if (view is null)
        {
            return;
        }

        string display = text.Length > maxLength ? text[..maxLength] + "…" : text;
        view.SetLineContent(new[] { display });
        view.ScrollToLine(lineNumber);
    }
}
