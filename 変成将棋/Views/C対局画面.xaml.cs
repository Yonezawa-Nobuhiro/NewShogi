using System.Windows;
using Microsoft.Win32;
using 変成将棋.ViewModels;

namespace 変成将棋.Views;

public partial class C対局画面 : Window
{
    public C対局画面()
    {
        InitializeComponent();

        if (DataContext is C対局VM vm)
        {
            vm.成り選択要求 = () =>
                MessageBox.Show("成りますか？", "成り確認",
                    MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes;
        }
    }

    private void OnAIモデル読み込みClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "ONNX モデルを選択",
            Filter = "ONNX モデル (*.onnx)|*.onnx|すべてのファイル (*.*)|*.*",
        };
        if (dlg.ShowDialog() is not true) return;

        if (DataContext is C対局VM vm)
        {
            try
            {
                vm.AIモデルを読み込む(dlg.FileName);
                MessageBox.Show($"モデルを読み込みました:\n{dlg.FileName}",
                    "AI 読み込み完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"読み込みに失敗しました:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
