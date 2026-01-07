#pragma warning disable CS0618 

using Avalonia.Controls;
using Avalonia.Input;
using VMAFLite.ViewModels;

namespace VMAFLite.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCloseAbout(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && DataContext is MainWindowViewModel vm)
            vm.ToggleAboutCommand.Execute(null);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files == null) return;

        var fileList = files.Select(f => f.Path.LocalPath).Where(IsVideo).ToList();
        if (fileList.Count == 0 || DataContext is not MainWindowViewModel vm || vm.IsRunning) return;

        if (fileList.Count == 1)
        {
            if (string.IsNullOrEmpty(vm.ReferencePath)) vm.SetReferenceFile(fileList[0]);
            else vm.SetDistortedFile(fileList[0]);
        }
        else if (fileList.Count >= 2)
        {
            vm.SetReferenceFile(fileList[0]);
            vm.SetDistortedFile(fileList[1]);
        }
    }

    private static bool IsVideo(string path)
    {
        return Path.GetExtension(path).ToLower() is ".mkv" or ".mp4" or ".avi" or ".mov" or
               ".webm" or ".mts" or ".m2ts" or ".mxf" or ".wmv" or ".mpg" or ".mpeg" or
               ".vob" or ".flv" or ".m4v" or ".ts";
    }
}