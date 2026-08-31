using System.Runtime.InteropServices;
using Windows.Storage.Pickers;

namespace MixFrame.Services;

internal static class OutputFolderPickerService
{
    private const int CancelledHResult = unchecked((int)0x800704C7);
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static async Task<string?> PickAsync(
        IntPtr ownerWindow,
        string? initialDirectory,
        PickerLocationId fallbackLocation)
    {
        try
        {
            return PickNative(ownerWindow, initialDirectory);
        }
        catch (COMException)
        {
            return await PickWithWinUiFallbackAsync(ownerWindow, fallbackLocation);
        }
        catch (InvalidCastException)
        {
            return await PickWithWinUiFallbackAsync(ownerWindow, fallbackLocation);
        }
    }

    private static string? PickNative(IntPtr ownerWindow, string? initialDirectory)
    {
        var dialogType = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), throwOnError: true)!;
        var dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;
        IShellItem? initialFolder = null;
        IShellItem? result = null;

        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FileOpenOptions.PickFolders | FileOpenOptions.ForceFileSystem | FileOpenOptions.PathMustExist);

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                var itemId = ShellItemId;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, ref itemId, out initialFolder));
                dialog.SetFolder(initialFolder);
            }

            var showResult = dialog.Show(ownerWindow);
            if (showResult == CancelledHResult) return null;
            Marshal.ThrowExceptionForHR(showResult);

            dialog.GetResult(out result);
            result.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var pathPointer);
            try
            {
                return Marshal.PtrToStringUni(pathPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
        finally
        {
            if (result is not null) Marshal.FinalReleaseComObject(result);
            if (initialFolder is not null) Marshal.FinalReleaseComObject(initialFolder);
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    private static async Task<string?> PickWithWinUiFallbackAsync(IntPtr ownerWindow, PickerLocationId fallbackLocation)
    {
        var picker = new FolderPicker { SuggestedStartLocation = fallbackLocation };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid shellItemId,
        out IShellItem shellItem);

    [Flags]
    private enum FileOpenOptions : uint
    {
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800
    }

    private enum ShellItemDisplayName : uint
    {
        FileSystemPath = 0x80058000
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindingContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(ShellItemDisplayName displayName, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem shellItem, uint hint, out int order);
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr filterSpecs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(FileOpenOptions options);
        void GetOptions(out FileOpenOptions options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid clientGuid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }
}
