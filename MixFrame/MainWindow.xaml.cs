using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MixFrame.Pages;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MixFrame;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const uint WmDropFiles = 0x0233;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const nuint DropSubclassId = 1;

    private readonly SubclassProc _subclassProc;
    private IntPtr _windowHandle;
    private bool _initialNavigationFocusNormalized;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr dropHandle, uint fileIndex, char[]? fileName, uint fileNameSize);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr dropHandle);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam);

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData);

    public static MainWindow? AppMainWindow { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        AppMainWindow = this;
        _subclassProc = WindowSubclassProc;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!SetWindowSubclass(_windowHandle, _subclassProc, DropSubclassId, 0))
            throw new InvalidOperationException("无法初始化窗口拖放接收器。");
        DragAcceptFiles(_windowHandle, true);
        Closed += OnWindowClosed;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Activated += OnWindowActivated;

        AppWindow.Resize(new SizeInt32(1000, 720));

        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 241, 245, 249);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(64, 255, 255, 255);
        }

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        RootFrame.Navigate(typeof(ImageWorkspacePage));
        RootNavigation.Loaded += OnRootNavigationLoaded;
    }

    private void OnRootNavigationLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialNavigationFocusNormalized) return;
        _initialNavigationFocusNormalized = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (RootNavigation.SelectedItem is Control selectedItem)
                selectedItem.Focus(FocusState.Pointer);
        });
    }

    private IntPtr WindowSubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        if (message != WmDropFiles)
            return DefSubclassProc(hwnd, message, wParam, lParam);

        var dropHandle = (IntPtr)wParam;
        var paths = new List<string>();
        try
        {
            var count = DragQueryFile(dropHandle, uint.MaxValue, null, 0);
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(dropHandle, index, null, 0);
                var buffer = new char[length + 1];
                if (DragQueryFile(dropHandle, index, buffer, (uint)buffer.Length) > 0)
                    paths.Add(new string(buffer, 0, (int)length));
            }
        }
        finally
        {
            DragFinish(dropHandle);
        }

        if (paths.Count > 0)
            DispatcherQueue.TryEnqueue(async () => await DispatchDroppedPathsAsync(paths));
        return IntPtr.Zero;
    }

    private async Task DispatchDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (RootFrame.Content is IDroppedPathHandler handler)
            await handler.ImportDroppedPathsAsync(paths);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_windowHandle == IntPtr.Zero) return;
        DragAcceptFiles(_windowHandle, false);
        RemoveWindowSubclass(_windowHandle, _subclassProc, DropSubclassId);
        _windowHandle = IntPtr.Zero;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        ApplyWindowCorners();
    }

    private void ApplyWindowCorners()
    {
        var hwnd = _windowHandle;
        if (hwnd == IntPtr.Zero) return;

        var cornerPreference = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag?.ToString() == "videos")
        {
            RootFrame.Navigate(typeof(VideoWorkspacePage));
            return;
        }

        RootFrame.Navigate(typeof(ImageWorkspacePage));
    }
}
