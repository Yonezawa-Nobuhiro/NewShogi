using System.IO;
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

    private void OnKifuSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not C対局VM vm) return;
        if (vm.棋譜SFENs.Count <= 1)
        {
            MessageBox.Show("保存できる棋譜がありません。", "棋譜保存",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title            = "棋譜を保存",
            Filter           = "変成将棋棋譜 (*.kf)|*.kf|すべてのファイル (*.*)|*.*",
            DefaultExt       = ".kf",
            FileName         = $"棋譜_{DateTime.Now:yyyyMMdd_HHmmss}",
        };
        if (dlg.ShowDialog() is not true) return;

        var lines = new List<string>
        {
            $"# 変成将棋棋譜",
            $"# 日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"# 手数: {vm.棋譜SFENs.Count - 1}",
            "",
        };
        lines.AddRange(vm.棋譜SFENs);
        File.WriteAllLines(dlg.FileName, lines, System.Text.Encoding.UTF8);
        MessageBox.Show($"棋譜を保存しました:\n{dlg.FileName}",
            "棋譜保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnKifuLoadClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not C対局VM vm) return;

        var dlg = new OpenFileDialog
        {
            Title  = "棋譜を開く",
            Filter = "変成将棋棋譜 (*.kf)|*.kf|すべてのファイル (*.*)|*.*",
        };
        if (dlg.ShowDialog() is not true) return;

        try
        {
            var lines = File.ReadAllLines(dlg.FileName, System.Text.Encoding.UTF8);
            vm.棋譜を読み込む(lines);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"読み込みに失敗しました:\n{ex.Message}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
