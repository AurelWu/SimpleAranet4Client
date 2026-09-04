using System.Diagnostics;
using System.Text;

namespace SimpleAranet4Client.Storage
{
    /// <summary>
    /// Writes a file somewhere the user can actually find it again.
    /// </summary>
    /// <remarks>
    /// This is not the same thing as sharing. Android's share sheet is ACTION_SEND: it only offers
    /// apps that can receive the file, and on a stock device none of them save it to storage. Saving
    /// needs the platform's own mechanism, which is what this wraps.
    /// </remarks>
    public static class CsvSaver
    {
        /// <summary>Whether <see cref="SaveAsync"/> can do anything on this platform.</summary>
        public static bool IsSupported =>
#if ANDROID
            OperatingSystem.IsAndroidVersionAtLeast(29);
#elif WINDOWS
            true;
#else
            false;   // iOS and Mac Catalyst have "Save to Files" in the share sheet already
#endif

        /// <summary>
        /// Saves <paramref name="content"/> as <paramref name="fileName"/>. Returns a description of
        /// where it went, or null if the user cancelled or the platform cannot do it.
        /// </summary>
        public static async Task<string?> SaveAsync(string fileName, string content)
        {
#if ANDROID
            // MediaStore writes into the shared Downloads collection without needing any storage
            // permission, from API 29 on.
            if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return null;

            try
            {
                var values = new Android.Content.ContentValues();
                values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
                values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, "text/csv");
                values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath,
                           Android.OS.Environment.DirectoryDownloads);

                var resolver = Android.App.Application.Context.ContentResolver;
                if (resolver == null) return null;

                var uri = resolver.Insert(Android.Provider.MediaStore.Downloads.ExternalContentUri, values);
                if (uri == null) return null;

                await using var stream = resolver.OpenOutputStream(uri);
                if (stream == null) return null;

                var bytes = Encoding.UTF8.GetBytes(content);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();

                return $"Downloads/{fileName}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MediaStore save failed: {ex.Message}");
                return null;
            }
#elif WINDOWS
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = fileName
                };
                picker.FileTypeChoices.Add("CSV file", new List<string> { ".csv" });

                // An unpackaged window has to be handed to the picker explicitly.
                var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView;
                if (window is Microsoft.UI.Xaml.Window native)
                {
                    var handle = WinRT.Interop.WindowNative.GetWindowHandle(native);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
                }

                var file = await picker.PickSaveFileAsync();
                if (file == null) return null; // cancelled

                await Windows.Storage.FileIO.WriteTextAsync(file, content);
                return file.Path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Save picker failed: {ex.Message}");
                return null;
            }
#else
            await Task.CompletedTask;
            return null;
#endif
        }
    }
}
