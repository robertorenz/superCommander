using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SuperCommander.Models;

namespace SuperCommander.Views.Dialogs;

public enum ViewerMode
{
    Text,
    Hex,
    Image
}

/// <summary>Internal F3 viewer: text with encoding choice, hex dump and images.</summary>
public partial class ViewerWindow : ThemedWindow
{
    private const long MaxLoadBytes = 96L * 1024 * 1024;
    private const int HexBytesPerRow = 16;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp"
    };

    private byte[] _bytes = Array.Empty<byte>();
    private ViewerMode _mode = ViewerMode.Text;
    private int _lastFindIndex = -1;
    private bool _suppressEvents;

    public ViewerWindow()
    {
        InitializeComponent();

        _suppressEvents = true;
        EncodingBox.ItemsSource = new[] { "UTF-8", "UTF-16 LE", "UTF-16 BE", "Latin-1", "ASCII" };
        EncodingBox.SelectedIndex = 0;
        _suppressEvents = false;
    }

    /// <summary>
    /// Loads a file, or the supplied bytes when the row lives inside an archive.
    /// </summary>
    public void Load(FileItem item, byte[]? contents = null)
    {
        Title = $"{item.Name} - Viewer";
        PathText.Text = item.FullPath;

        try
        {
            if (contents is not null)
            {
                _bytes = contents;
            }
            else
            {
                var info = new FileInfo(item.FullPath);
                if (info.Length > MaxLoadBytes)
                {
                    // Show the head of very large files rather than refusing.
                    _bytes = new byte[MaxLoadBytes];
                    using var stream = File.OpenRead(item.FullPath);
                    int read = stream.Read(_bytes, 0, (int)MaxLoadBytes);
                    if (read < _bytes.Length) Array.Resize(ref _bytes, read);
                }
                else
                {
                    _bytes = File.ReadAllBytes(item.FullPath);
                }
            }
        }
        catch (Exception ex)
        {
            _bytes = Encoding.UTF8.GetBytes($"Could not read the file:{Environment.NewLine}{ex.Message}");
        }

        var extension = Path.GetExtension(item.Name);
        SetMode(ImageExtensions.Contains(extension) ? ViewerMode.Image
            : LooksBinary(_bytes) ? ViewerMode.Hex
            : ViewerMode.Text);
    }

    // ------------------------------------------------------------------ modes

    private void SetMode(ViewerMode mode)
    {
        _mode = mode;
        _suppressEvents = true;
        TextMode.IsChecked = mode == ViewerMode.Text;
        HexMode.IsChecked = mode == ViewerMode.Hex;
        ImageMode.IsChecked = mode == ViewerMode.Image;
        _suppressEvents = false;

        TextView.Visibility = mode == ViewerMode.Image ? Visibility.Collapsed : Visibility.Visible;
        ImageScroller.Visibility = mode == ViewerMode.Image ? Visibility.Visible : Visibility.Collapsed;
        EncodingBox.IsEnabled = mode == ViewerMode.Text;
        WrapToggle.IsEnabled = mode == ViewerMode.Text;

        switch (mode)
        {
            case ViewerMode.Text:
                TextView.Text = Decode(_bytes);
                InfoText.Text = $"{FileItem.FormatBytesShort(_bytes.Length)} - text";
                break;

            case ViewerMode.Hex:
                TextView.Text = BuildHexDump(_bytes);
                TextView.TextWrapping = TextWrapping.NoWrap;
                InfoText.Text = $"{FileItem.FormatBytesShort(_bytes.Length)} - hex";
                break;

            case ViewerMode.Image:
                ShowImage();
                break;
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        var mode = ReferenceEquals(sender, HexMode) ? ViewerMode.Hex
            : ReferenceEquals(sender, ImageMode) ? ViewerMode.Image
            : ViewerMode.Text;

        if (mode == ViewerMode.Text) TextView.TextWrapping =
            WrapToggle.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;

        SetMode(mode);
    }

    private void OnEncodingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _mode != ViewerMode.Text) return;
        TextView.Text = Decode(_bytes);
    }

    private void OnWrapChanged(object sender, RoutedEventArgs e) =>
        TextView.TextWrapping = WrapToggle.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private void ShowImage()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(_bytes);
            image.EndInit();
            image.Freeze();

            ImageView.Source = image;
            InfoText.Text = $"{image.PixelWidth} x {image.PixelHeight} - {FileItem.FormatBytesShort(_bytes.Length)}";
        }
        catch (Exception)
        {
            ImageView.Source = null;
            InfoText.Text = "Not a readable image";
            SetMode(ViewerMode.Hex);
        }
    }

    // -------------------------------------------------------------- decoding

    private Encoding SelectedEncoding => EncodingBox.SelectedIndex switch
    {
        1 => Encoding.Unicode,
        2 => Encoding.BigEndianUnicode,
        3 => Encoding.Latin1,
        4 => Encoding.ASCII,
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    };

    private string Decode(byte[] bytes) => SelectedEncoding.GetString(bytes);

    private static bool LooksBinary(byte[] bytes)
    {
        int limit = Math.Min(bytes.Length, 8000);
        int suspicious = 0;

        for (int i = 0; i < limit; i++)
        {
            byte b = bytes[i];
            if (b == 0) return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) suspicious++;
        }

        return limit > 0 && suspicious * 100 / limit > 5;
    }

    private static string BuildHexDump(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 4);

        for (int offset = 0; offset < bytes.Length; offset += HexBytesPerRow)
        {
            builder.Append(offset.ToString("X8")).Append("  ");

            int count = Math.Min(HexBytesPerRow, bytes.Length - offset);
            for (int i = 0; i < HexBytesPerRow; i++)
            {
                builder.Append(i < count ? bytes[offset + i].ToString("X2") : "  ");
                builder.Append(i == 7 ? "  " : " ");
            }

            builder.Append(" |");
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[offset + i];
                builder.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            builder.Append('|').Append('\n');
        }

        return builder.ToString();
    }

    // ----------------------------------------------------------------- search

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FindNext();
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNext();

    private void FindNext()
    {
        var needle = FindBox.Text;
        if (needle.Length == 0 || _mode == ViewerMode.Image) return;

        int start = _lastFindIndex + 1;
        if (start >= TextView.Text.Length) start = 0;

        int index = TextView.Text.IndexOf(needle, start, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0 && start > 0)
            index = TextView.Text.IndexOf(needle, 0, StringComparison.CurrentCultureIgnoreCase);

        if (index < 0)
        {
            InfoText.Text = $"\"{needle}\" not found";
            _lastFindIndex = -1;
            return;
        }

        _lastFindIndex = index;
        TextView.Focus();
        TextView.Select(index, needle.Length);
        TextView.ScrollToLine(Math.Max(0, TextView.GetLineIndexFromCharacterIndex(index) - 3));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F3:
                SetMode(ViewerMode.Text);
                e.Handled = true;
                break;
            case Key.F4:
                SetMode(ViewerMode.Hex);
                e.Handled = true;
                break;
            case Key.F5:
                SetMode(ViewerMode.Image);
                e.Handled = true;
                break;
            case Key.F7:
                FindBox.Focus();
                FindBox.SelectAll();
                e.Handled = true;
                break;
        }
    }
}
