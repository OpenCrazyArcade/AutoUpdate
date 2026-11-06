using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenCrazyArcade.AutoUpdate.Managers;

namespace OpenCrazyArcade.AutoUpdate.UI.Windows;

/// <summary>
/// Dev-only CAPlay switches, rendered from the flag list inside CAPlay.dll (see <see cref="CaPlayFlags"/>), so a new
/// flag in CAPlay shows up here without touching the launcher. Opened with Ctrl+Alt+Shift + right click on the config button.
/// </summary>
internal sealed class DevConfigWindow : Window
{
    readonly Dictionary<string, string> _values;
    readonly Dictionary<string, ComboBox> _boxes = new();

    public IReadOnlyDictionary<string, string> Result => _values;

    public DevConfigWindow(IReadOnlyList<CaPlayFlag> flags, string loadError, Dictionary<string, string> saved)
    {
        _values = new Dictionary<string, string>(saved);
        Title = "Dev Config — CAPlay";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2A));
        Foreground = Brushes.White;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "Cờ dành cho dev. Các cờ \"hook\" sửa code game đang chạy và CÓ THỂ làm client crash im lặng.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x6B)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        if (flags.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = loadError ?? "CAPlay.dll không khai báo cờ nào.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.LightGray
            });
        }

        var list = new StackPanel();
        foreach (var flag in flags)
        {
            var box = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
            var options = flag.Type == "bool"
                ? new List<CaPlayFlagOption> { new("0", "Tắt"), new("1", "Bật") }
                : flag.Options ?? new List<CaPlayFlagOption>();
            foreach (var o in options)
            {
                bool hook = o.Risk == "hook";
                box.Items.Add(new ComboBoxItem
                {
                    Content = hook ? "⚠ " + o.Label : o.Label,
                    Tag = o.Value,
                    Foreground = hook ? Brushes.OrangeRed : Brushes.Black
                });
            }
            string current = _values.TryGetValue(flag.Env, out var v) ? v : flag.Default;
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current)
                               ?? box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == flag.Default);
            _boxes[flag.Env] = box;

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(new TextBlock { Text = flag.Label, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = $"{flag.Env} — {flag.Desc}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11
            });
            panel.Children.Add(box);
            list.Children.Add(panel);
        }
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var save = new Button { Content = "Lưu", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Đóng", Width = 90, IsCancel = true };
        save.Click += (_, _) => { Collect(flags); DialogResult = true; };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    void Collect(IReadOnlyList<CaPlayFlag> flags)
    {
        foreach (var flag in flags)
        {
            string value = (_boxes[flag.Env].SelectedItem as ComboBoxItem)?.Tag as string ?? flag.Default;
            // Defaults are not stored: a value equal to the default means "leave the variable unset".
            if (value == flag.Default) _values.Remove(flag.Env); else _values[flag.Env] = value;
        }
    }
}
