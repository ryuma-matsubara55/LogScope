using Wpf.Ui.Controls;

namespace LogScope.Wpf;

/// <summary>
/// LogScope のすべてのウィンドウが継承する基準ウィンドウ（WPF-UI の FluentWindow 派生）。
///
/// なぜ直接 <c>ui:FluentWindow</c> を XAML ルートに使わないのか:
/// XAML コンパイラは <c>MainWindow.g.cs</c> に基底クラス参照を
/// <c>Wpf.Ui.Controls.FluentWindow</c> と global:: プレフィックス無しで出力する。
/// 本プロジェクトの名前空間が <c>LogScope.Wpf</c> であるため、C# の名前解決では
/// 先頭の <c>Wpf</c> はグローバルの <c>Wpf</c>（WPF-UI アセンブリ）ではなく
/// 親名前空間のメンバー <c>LogScope.Wpf</c> に束縛され、
/// CS0234（'Ui' が 'LogScope.Wpf' に存在しません）でビルドが失敗する。
///
/// 本クラスは名前空間 <c>LogScope.Wpf</c> 内に置くことで、生成コードの
/// <c>Wpf.Ui.Controls.FluentWindow</c> 参照が <c>LogScope.Wpf.AppWindow</c> に
/// 解決されるようにし、名前空間の改名なしでビルドを通すための回避策。
/// （新しいウィンドウを追加するときも XAML ルートはこの AppWindow を使うこと）
/// </summary>
public class AppWindow : FluentWindow
{
}
