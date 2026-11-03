using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using FastFind.App.Services;

namespace FastFind.App.Views;

public partial class BulkRenameWindow : Window
{
    public sealed class PreviewRow
    {
        public required string Path { get; init; }
        public required string OldName { get; init; }
        public string NewName { get; set; } = "";
        public string Status { get; set; } = "";
        public bool IsProblem { get; set; }
        public bool IsUnchanged { get; set; }
        public bool IsDirectory { get; init; }
    }

    private readonly List<PreviewRow> _rows;
    private bool _loading = true;

    public int RenamedCount { get; private set; }

    public BulkRenameWindow(IEnumerable<string> paths)
    {
        InitializeComponent();
        _rows = paths.Select(p => new PreviewRow { Path = p, OldName = System.IO.Path.GetFileName(p), IsDirectory = Directory.Exists(p) }).ToList();
        PreviewGrid.ItemsSource = _rows;
        _loading = false;
        Recalculate();
        Loaded += (_, _) => FindBox.Focus();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) { if (!_loading) Recalculate(); }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _loading = true;
        FindBox.Clear(); ReplaceBox.Clear(); PrefixBox.Clear(); SuffixBox.Clear(); ExtBox.Clear();
        RegexBox.IsChecked = false; CaseBox.IsChecked = false;
        NumberBox.SelectedIndex = 0; CaseModeBox.SelectedIndex = 0; ExtModeBox.SelectedIndex = 0;
        StartBox.Text = "1"; PadBox.Text = "1";
        _loading = false;
        Recalculate();
    }

    private void Recalculate()
    {
        Regex? regex = null;
        string find = FindBox.Text;
        string replace = ReplaceBox.Text;
        bool useRegex = RegexBox.IsChecked == true;
        var comparison = CaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string? regexError = null;
        if (useRegex && find.Length > 0)
        {
            try { regex = new Regex(find, (CaseBox.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException ex) { regexError = ex.Message; }
        }
        int.TryParse(StartBox.Text, out int number);
        int.TryParse(PadBox.Text, out int pad);
        pad = Math.Clamp(pad, 1, 10);
        int numberMode = NumberBox.SelectedIndex;
        int caseMode = CaseModeBox.SelectedIndex;
        int extMode = ExtModeBox.SelectedIndex;
        string newExt = ExtBox.Text.Trim().TrimStart('.');

        var seen = new Dictionary<string, PreviewRow>(StringComparer.OrdinalIgnoreCase);
        int changes = 0, problems = 0;
        foreach (var row in _rows)
        {
            string ext = row.IsDirectory ? "" : System.IO.Path.GetExtension(row.OldName);
            string stem = row.IsDirectory ? row.OldName : row.OldName[..^ext.Length];
            bool includeExt = extMode == 2 && !row.IsDirectory;
            string work = includeExt ? row.OldName : stem;

            if (find.Length > 0)
            {
                if (regex != null)
                {
                    try { work = regex.Replace(work, replace); }
                    catch (RegexMatchTimeoutException) { }
                }
                else if (!useRegex) work = work.Replace(find, replace, comparison);
            }
            work = caseMode switch
            {
                1 => work.ToLowerInvariant(),
                2 => work.ToUpperInvariant(),
                3 => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(work.ToLowerInvariant()),
                _ => work,
            };
            string num = number.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0');
            work = numberMode switch
            {
                1 => num + "_" + work,
                2 => work + "_" + num,
                3 => num,
                _ => work,
            };
            work = PrefixBox.Text + work + SuffixBox.Text;
            string finalExt = includeExt ? "" : extMode == 1 && !row.IsDirectory ? (newExt.Length > 0 ? "." + newExt : "") : ext;
            string newName = work + finalExt;
            if (numberMode != 0) number++;

            row.NewName = newName;
            row.IsProblem = false;
            row.IsUnchanged = false;
            string dir = System.IO.Path.GetDirectoryName(row.Path) ?? "";
            string target = System.IO.Path.Combine(dir, newName);
            if (regexError != null) { row.Status = "Invalid regex"; row.IsProblem = true; }
            else if (newName.Length == 0 || newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || newName is "." or "..") { row.Status = "Invalid name"; row.IsProblem = true; }
            else if (string.Equals(newName, row.OldName, StringComparison.Ordinal)) { row.Status = "Unchanged"; row.IsUnchanged = true; }
            else if (seen.TryGetValue(target, out _)) { row.Status = "Duplicate new name"; row.IsProblem = true; }
            else if (!string.Equals(target, row.Path, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)) && !_rows.Any(r => r.Path.Equals(target, StringComparison.OrdinalIgnoreCase)))
            { row.Status = "Already exists"; row.IsProblem = true; }
            else row.Status = "Will rename";
            if (!row.IsProblem && !row.IsUnchanged) { seen[target] = row; changes++; }
            if (row.IsProblem) problems++;
        }
        PreviewGrid.Items.Refresh();
        SummaryText.Text = $"{_rows.Count:N0} items · {changes:N0} will change" + (problems > 0 ? $" · {problems:N0} with problems (skipped)" : "") + (regexError != null ? $" · {regexError}" : "");
        ApplyButton.IsEnabled = changes > 0;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        // Two phases so swaps (a→b, b→a) work: first move everything to a temporary name, then to the final name.
        var todo = _rows.Where(r => !r.IsProblem && !r.IsUnchanged).ToList();
        var temps = new List<(PreviewRow Row, string Temp)>();
        var errors = new List<string>();
        foreach (var row in todo)
        {
            string dir = System.IO.Path.GetDirectoryName(row.Path) ?? "";
            string temp = System.IO.Path.Combine(dir, $"~ff-rename-{Guid.NewGuid():N}");
            try
            {
                if (row.IsDirectory) Directory.Move(row.Path, temp); else File.Move(row.Path, temp);
                temps.Add((row, temp));
            }
            catch (Exception ex) { errors.Add($"{row.OldName}: {ex.Message}"); }
        }
        foreach (var (row, temp) in temps)
        {
            string dir = System.IO.Path.GetDirectoryName(row.Path) ?? "";
            string target = System.IO.Path.Combine(dir, row.NewName);
            try
            {
                if (row.IsDirectory) Directory.Move(temp, target); else File.Move(temp, target);
                RenamedCount++;
            }
            catch (Exception ex)
            {
                errors.Add($"{row.OldName} → {row.NewName}: {ex.Message}");
                try { if (row.IsDirectory) Directory.Move(temp, row.Path); else File.Move(temp, row.Path); } catch (Exception) { }
            }
        }
        if (errors.Count > 0)
        {
            Logger.Warn("Bulk rename errors: " + string.Join("; ", errors));
            MessageBox.Show(this, string.Join(Environment.NewLine, errors.Take(15)) + (errors.Count > 15 ? $"{Environment.NewLine}… and {errors.Count - 15} more" : ""),
                $"{RenamedCount} renamed, {errors.Count} failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        DialogResult = true;
        Close();
    }
}
