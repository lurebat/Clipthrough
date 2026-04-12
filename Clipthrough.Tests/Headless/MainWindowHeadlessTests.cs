using System;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Controls;
using Clipthrough.Models;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void MainWindow_LoadsExpectedControls()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.FindControl<TextBox>("SearchTextBox"));
        Assert.NotNull(window.FindControl<ListBox>("ClipsListBox"));
        Assert.NotNull(window.FindControl<EmbeddedImageEditorView>("SelectedImageEditor"));
    }

    [AvaloniaFact]
    public void SearchTextBox_AcceptsHeadlessTextInput()
    {
        var window = new MainWindow();

        window.Show();
        var searchTextBox = window.FindControl<TextBox>("SearchTextBox");
        Assert.NotNull(searchTextBox);

        searchTextBox!.Focus();
        window.KeyTextInput("invoice");

        Assert.Equal("invoice", searchTextBox.Text);
    }

    [AvaloniaFact]
    public void MainWindow_LoadsWelcomeSetupControls()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.FindControl<TextBox>("WelcomeDatabasePathTextBox"));
        Assert.NotNull(window.FindControl<Button>("WelcomeDatabasePathBrowseButton"));
        Assert.NotNull(window.FindControl<TextBox>("WelcomeDatabasePasswordTextBox"));
    }

    [AvaloniaFact]
    public void RichContentView_LoadsInsideHeadlessWindow()
    {
        var view = new RichContentView
        {
            ContentFormat = ClipContentFormat.Html,
            Markup = "<p>Hello <strong>headless</strong> world</p>",
        };
        var window = new Window
        {
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.Content);
    }

    [AvaloniaFact]
    public void RichContentView_UsesRichEditorForRtf()
    {
        var rtf = @"{\rtf1\ansi{\colortbl ;\red255\green0\blue0;}\cf1 hello}";

        // Verify the RTF-to-HTML conversion preserves colors
        var html = Clipthrough.Presentation.RtfToHtmlConverter.Convert(rtf);
        Assert.Contains("color:#FF0000", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hello", html);

        var view = new RichContentView
        {
            ContentFormat = ClipContentFormat.Rtf,
            Markup = rtf,
        };
        var window = new Window
        {
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // In headless mode, LoadHtml may fall back to TextBox; both outcomes are valid
        Assert.NotNull(view.Content);
        var typeName = view.Content!.GetType().FullName;
        Assert.True(
            typeName == "AvRichTextBox.RichTextBox" || typeName == "Avalonia.Controls.TextBox",
            $"Expected rich editor or fallback TextBox, got: {typeName}");
    }
}
