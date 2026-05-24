using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace AdvancedCsvSearch
{
    public class MainViewModel : INotifyPropertyChanged
    {
        #region Fields
        private string _targetFolder = "";
        private bool _isRecursive;
        private string _statusText = "";
        private bool _isSearching;
        private CancellationTokenSource? _cancellationTokenSource;
        private List<object> _resultsToExport = new List<object>();
        private List<string> _masterHeaders = new List<string>();
        private HashSet<string> _masterHeadersSet = new HashSet<string>();
        private double _searchProgress;
        private bool _isExportEnabled;
        private char _detectedDelimiter = ','; // Default
        #endregion

        #region Properties
        public ObservableCollection<SearchCriterion> SearchCriteria { get; set; }
        public ObservableCollection<string> DiscoveredColumns { get; } = new ObservableCollection<string>();
        public string TargetFolder { get => _targetFolder; private set => SetProperty(ref _targetFolder, value); }
        public bool IsRecursive { get => _isRecursive; set => SetProperty(ref _isRecursive, value); }
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        public bool IsSearching { get => _isSearching; private set => SetProperty(ref _isSearching, value); }
        public double SearchProgress { get => _searchProgress; private set => SetProperty(ref _searchProgress, value); }
        public bool IsExportEnabled { get => _isExportEnabled; private set => SetProperty(ref _isExportEnabled, value); }
        public bool IsSearchEnabled => !IsSearching && !string.IsNullOrEmpty(TargetFolder) && SearchCriteria.Any(c => !string.IsNullOrWhiteSpace(c.ColumnName) && !string.IsNullOrWhiteSpace(c.Value));
        #endregion

        #region Commands
        public ICommand SelectFolderCommand { get; }
        public ICommand DiscoverColumnsCommand { get; }
        public ICommand SaveQueryCommand { get; }
        public ICommand LoadQueryCommand { get; }
        public ICommand ShowAboutCommand { get; }
        public ICommand AddCriterionCommand { get; }
        public ICommand RemoveCriterionCommand { get; }
        public ICommand IndentCommand { get; }
        public ICommand OutdentCommand { get; }
        public ICommand StartSearchCommand { get; }
        public ICommand CancelSearchCommand { get; }
        public ICommand ExportResultsCommand { get; }
        #endregion

        public MainViewModel()
        {
            SearchCriteria = new ObservableCollection<SearchCriterion>();
            SearchCriteria.CollectionChanged += SearchCriteria_CollectionChanged;

            SelectFolderCommand = new AsyncRelayCommand(SelectFolderAsync);
            DiscoverColumnsCommand = new AsyncRelayCommand(DiscoverColumnsAsync, () => !string.IsNullOrEmpty(TargetFolder));
            SaveQueryCommand = new RelayCommand(SaveQuery, () => SearchCriteria.Any(c => !string.IsNullOrWhiteSpace(c.ColumnName)));
            LoadQueryCommand = new RelayCommand(LoadQuery);
            ShowAboutCommand = new RelayCommand(ShowAbout);
            AddCriterionCommand = new RelayCommand(AddCriterion);
            RemoveCriterionCommand = new RelayCommand<SearchCriterion>(RemoveCriterion);
            IndentCommand = new RelayCommand<SearchCriterion>(IndentCriterion, CanIndentCriterion);
            OutdentCommand = new RelayCommand<SearchCriterion>(OutdentCriterion, CanOutdentCriterion);
            StartSearchCommand = new AsyncRelayCommand(SearchAsync, () => IsSearchEnabled);
            CancelSearchCommand = new RelayCommand(CancelSearch);
            ExportResultsCommand = new RelayCommand(ExportResults, () => IsExportEnabled);
            
            AddCriterion();
            StatusText = "Ready. Please select a folder and define your search criteria.";
        }

        #region CSV Robust Parsing Logic
        private string[] ParseCsvLine(string line, char delimiter)
        {
            if (string.IsNullOrWhiteSpace(line)) return Array.Empty<string>();

            // Regex to split by delimiter while ignoring delimiters inside quotes
            string pattern = $"(?<=^|{delimiter})(\"(?:[^\"]|\"\")*\"|[^{delimiter}]*)";
            var matches = Regex.Matches(line, pattern);

            string[] fields = new string[matches.Count];
            for (int i = 0; i < matches.Count; i++)
            {
                string f = matches[i].Value.Trim();
                // Strip surrounding quotes and unescape double-quotes
                if (f.StartsWith("\"") && f.EndsWith("\""))
                {
                    f = f.Substring(1, f.Length - 2).Replace("\"\"", "\"");
                }
                fields[i] = f;
            }
            return fields;
        }

        private char DetectDelimiter(string headerLine)
        {
            char[] possibleDelimiters = { ',', ';', '\t', '|' };
            char bestChoice = ',';
            int maxCount = 0;

            foreach (var d in possibleDelimiters)
            {
                int count = headerLine.Split(d).Length;
                if (count > maxCount)
                {
                    maxCount = count;
                    bestChoice = d;
                }
            }
            return bestChoice;
        }
        #endregion

        #region Command Implementations
        private async Task SelectFolderAsync()
        {
            var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                TargetFolder = dialog.FileName;
                await DiscoverColumnsAsync();
                IsExportEnabled = false;
            }
        }

        private async Task DiscoverColumnsAsync()
        {
            StatusText = "Scanning for column headers...";
            DiscoveredColumns.Clear();
            var uniqueHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var searchOption = IsRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                
                await Task.Run(async () =>
                {
                    var zipFiles = Directory.EnumerateFiles(TargetFolder, "*.zip", searchOption);
                    foreach (var zipPath in zipFiles)
                    {
                        try
                        {
                            using var archive = ZipFile.OpenRead(zipPath);
                            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                            {
                                using var stream = entry.Open();
                                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                                var headerLine = await reader.ReadLineAsync();
                                if (headerLine != null)
                                {
                                    char delimiter = DetectDelimiter(headerLine);
                                    _detectedDelimiter = delimiter;
                                    var headers = ParseCsvLine(headerLine, delimiter);
                                    foreach (var h in headers) uniqueHeaders.Add(h);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            /* Skip corrupt zips */
                            Debug.WriteLine($"Skipping corrupt zip '{zipPath}': {ex.Message}");
                        }
                    }
                });

                foreach (var h in uniqueHeaders.OrderBy(x => x)) DiscoveredColumns.Add(h);
                
                if (DiscoveredColumns.Count > 0)
                    StatusText = $"Discovered {DiscoveredColumns.Count} unique columns. Ready.";
                else
                    StatusText = "No CSV columns found.";
            }
            catch (Exception ex) { StatusText = $"Error discovering columns: {ex.Message}"; }
        }

        private async Task SearchAsync()
        {
            IsSearching = true;
            IsExportEnabled = false;
            _resultsToExport.Clear();
            _masterHeaders.Clear();
            _masterHeadersSet.Clear();
            _masterHeadersSet.Add("Source ZIP"); _masterHeaders.Add("Source ZIP");
            _masterHeadersSet.Add("Source CSV"); _masterHeaders.Add("Source CSV");
            SearchProgress = 0;
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            IProgress<string> statusProgress = new Progress<string>(update => StatusText = update);
            IProgress<double> barProgress = new Progress<double>(update => SearchProgress = update);

            try
            {
                var criteria = new List<SearchCriterion>(SearchCriteria.Where(c => !string.IsNullOrWhiteSpace(c.ColumnName) && !string.IsNullOrWhiteSpace(c.Value)));
                var searchOption = IsRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                int totalMatches = 0;

                await Task.Run(async () =>
                {
                    var zipFiles = Directory.GetFiles(TargetFolder, "*.zip", searchOption);
                    int filesProcessed = 0;
                    foreach (var zipPath in zipFiles)
                    {
                        if (token.IsCancellationRequested) break;
                        statusProgress.Report($"Searching in: {Path.GetFileName(zipPath)}");
                        try
                        {
                            using var archive = ZipFile.OpenRead(zipPath);
                            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (token.IsCancellationRequested) break;
                                totalMatches += await ProcessCsvEntry(entry, Path.GetFileName(zipPath), criteria, token);
                            }
                        }
                        catch (Exception ex)
                        {
                            /* Skip corrupt zips */
                            Debug.WriteLine($"Skipping corrupt zip '{zipPath}': {ex.Message}");
                        }
                        filesProcessed++;
                        barProgress.Report((double)filesProcessed / zipFiles.Length * 100);
                    }
                }, token);

                StatusText = $"Search complete. Found {totalMatches} matching rows.";
            }
            catch (OperationCanceledException) { StatusText = "Search canceled."; }
            finally
            {
                IsSearching = false;
                IsExportEnabled = _resultsToExport.Any();
            }
        }

        private async Task<int> ProcessCsvEntry(ZipArchiveEntry entry, string zipName, List<SearchCriterion> criteria, CancellationToken token)
        {
            int localMatches = 0;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var headerLine = await reader.ReadLineAsync();
            if (headerLine == null) return 0;

            char delimiter = DetectDelimiter(headerLine);
            var headers = ParseCsvLine(headerLine, delimiter);

            int prevHeaderCount = _masterHeaders.Count;
            foreach (var h in headers)
            {
                if (_masterHeadersSet.Add(h)) _masterHeaders.Add(h);
            }
            bool schemaChanged = _masterHeaders.Count > prevHeaderCount;
            
            var colIndexMap = new Dictionary<string, int>();
            foreach (var criterion in criteria)
            {
                var index = Array.FindIndex(headers, h => h.Equals(criterion.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (index != -1) colIndexMap[criterion.ColumnName!] = index;
            }

            bool fileHeaderAdded = false;
            while (!reader.EndOfStream)
            {
                if (token.IsCancellationRequested) break;
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var values = ParseCsvLine(line, delimiter);
                if (DoesRowMatch(values, criteria, colIndexMap))
                {
                    if (!fileHeaderAdded)
                    {
                        if (_resultsToExport.Count > 0) _resultsToExport.Add(new[] { "" });
                        _resultsToExport.Add(new[] { "FILE_MARKER", $"--- START: {zipName} / {entry.FullName} ---" });
                        if (schemaChanged)
                        {
                            _resultsToExport.Add(new[] { "SCHEMA_CHANGE", "New columns detected in this file..." });
                        }
                        fileHeaderAdded = true;
                    }
                    
                    var rowData = new Dictionary<string, string>();
                    rowData["Source ZIP"] = zipName;
                    rowData["Source CSV"] = entry.FullName;
                    for (int i = 0; i < headers.Length && i < values.Length; i++) rowData[headers[i]] = values[i];
                    _resultsToExport.Add(rowData);
                    localMatches++;
                }
            }
            return localMatches;
        }

        private void SaveResultsToCsv(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", _masterHeaders.Select(EscapeCsv)));

            foreach (var item in _resultsToExport)
            {
                if (item is string[] marker)
                {
                    sb.AppendLine(string.Join(",", marker.Select(EscapeCsv)));
                }
                else if (item is Dictionary<string, string> row)
                {
                    var line = _masterHeaders.Select(h => row.TryGetValue(h, out var val) ? EscapeCsv(val) : "");
                    sb.AppendLine(string.Join(",", line));
                }
            }
            File.WriteAllText(filePath, sb.ToString());
        }

        private string EscapeCsv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n"))
                return $"\"{field.Replace("\"", "\"\"")}\"";
            return field;
        }

        private void SearchCriteria_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (SearchCriterion item in e.NewItems)
                    item.PropertyChanged += SearchCriterion_PropertyChanged;
            }
            if (e.OldItems != null)
            {
                foreach (SearchCriterion item in e.OldItems)
                    item.PropertyChanged -= SearchCriterion_PropertyChanged;
            }
            RaisePropertyChanged(nameof(IsSearchEnabled));
            ((RelayCommand)SaveQueryCommand).RaiseCanExecuteChanged();
        }

        private void SearchCriterion_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            RaisePropertyChanged(nameof(IsSearchEnabled));
        }

        private void SaveQuery()
        {
            var dialog = new SaveFileDialog { Filter = "JSON Files|*.json", FileName = "query.json" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = JsonSerializer.Serialize(SearchCriteria, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(dialog.FileName, json);
                    StatusText = "Query saved.";
                }
                catch (Exception ex) { StatusText = $"Error saving: {ex.Message}"; }
            }
        }

        private void LoadQuery()
        {
            var dialog = new OpenFileDialog { Filter = "JSON Files|*.json" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var items = JsonSerializer.Deserialize<ObservableCollection<SearchCriterion>>(json);
                    if (items != null)
                    {
                        SearchCriteria.Clear();
                        foreach (var item in items) SearchCriteria.Add(item);
                        StatusText = "Query loaded.";
                    }
                }
                catch (Exception ex) { StatusText = $"Error loading: {ex.Message}"; }
            }
        }

        private void ShowAbout() => MessageBox.Show("Advanced CSV Search\n\nSearch through CSVs inside ZIP files.", "About");

        private void AddCriterion()
        {
            var c = new SearchCriterion();
            if (SearchCriteria.Any()) c.IsNotFirst = true;
            SearchCriteria.Add(c);
        }

        private void RemoveCriterion(SearchCriterion c)
        {
            SearchCriteria.Remove(c);
            if (SearchCriteria.Any()) SearchCriteria[0].IsNotFirst = false;
        }

        private void IndentCriterion(SearchCriterion c) { if (CanIndentCriterion(c)) c.IndentLevel++; }
        private bool CanIndentCriterion(SearchCriterion c)
        {
            int index = SearchCriteria.IndexOf(c);
            if (index <= 0) return false;
            var prev = SearchCriteria[index - 1];
            return c.IndentLevel < 5 && c.IndentLevel <= prev.IndentLevel;
        }
        private void OutdentCriterion(SearchCriterion c) { if (c.IndentLevel > 0) c.IndentLevel--; }
        private bool CanOutdentCriterion(SearchCriterion c) => c.IndentLevel > 0;

        private void CancelSearch() => _cancellationTokenSource?.Cancel();

        private void ExportResults()
        {
            var dialog = new SaveFileDialog { Filter = "CSV Files|*.csv", FileName = "results.csv" };
            if (dialog.ShowDialog() == true) SaveResultsToCsv(dialog.FileName);
        }
        #endregion

        #region Fixed Search Evaluation Logic
        private int _evalIndex;
        private List<SearchCriterion> _criteriaForEval = new List<SearchCriterion>();
        private string[] _valuesForEval = new string[0];
        private Dictionary<string, int> _colIndexMapForEval = new Dictionary<string, int>();

        private bool DoesRowMatch(string[] values, List<SearchCriterion> criteria, Dictionary<string, int> colIndexMap)
        {
            if (!criteria.Any()) return true;
            _criteriaForEval = criteria;
            _valuesForEval = values;
            _colIndexMapForEval = colIndexMap;
            _evalIndex = 0;
            try { return ParseExpression(0); }
            catch { return false; }
        }

        private bool ParseExpression(int level)
        {
            bool left = ParseTerm(level);

            while (_evalIndex < _criteriaForEval.Count)
            {
                var currentCriterion = _criteriaForEval[_evalIndex];
                if (currentCriterion.IndentLevel < level) break;
                
                var op = currentCriterion.LogicalOperator;
                // Note: We do NOT increment _evalIndex here. 
                // ParseTerm consumes the next item and moves the pointer.
                bool right = ParseTerm(level);
                left = (op == LogicalOperator.AND) ? (left && right) : (left || right);
            }
            return left;
        }

        private bool ParseTerm(int level)
        {
            if (_evalIndex >= _criteriaForEval.Count) return false;
            var current = _criteriaForEval[_evalIndex];

            if (current.IndentLevel > level)
            {
                return ParseExpression(current.IndentLevel);
            }

            _evalIndex++;
            return EvaluateSingleCriterion(current);
        }

        private bool EvaluateSingleCriterion(SearchCriterion criterion)
        {
            if (string.IsNullOrEmpty(criterion.ColumnName)) return false;
            if (!_colIndexMapForEval.TryGetValue(criterion.ColumnName, out var colIndex)) return false;

            string cellValue = (colIndex < _valuesForEval.Length) ? _valuesForEval[colIndex] : "";
            bool result = CheckCriterion(cellValue, criterion);
            return criterion.IsNot ? !result : result;
        }

        private bool CheckCriterion(string cellValue, SearchCriterion criterion)
        {
            string val1 = criterion.Value ?? "";
            string val2 = criterion.Value2 ?? "";

            switch (criterion.SearchType)
            {
                case SearchType.ExactMatch: return cellValue.Equals(val1, StringComparison.OrdinalIgnoreCase);
                case SearchType.IsOneOf: return val1.Split(',').Any(v => cellValue.Equals(v.Trim(), StringComparison.OrdinalIgnoreCase));
                case SearchType.Regex: try { return Regex.IsMatch(cellValue, val1, RegexOptions.IgnoreCase); } catch { return false; }
                case SearchType.GreaterThan: if (double.TryParse(cellValue, out double n1) && double.TryParse(val1, out double n2)) return n1 > n2; return false;
                case SearchType.LessThan: if (double.TryParse(cellValue, out double n3) && double.TryParse(val1, out double n4)) return n3 < n4; return false;
                case SearchType.IsBetween: if (double.TryParse(cellValue, out double nb) && double.TryParse(val1, out double nb1) && double.TryParse(val2, out double nb2)) return nb >= nb1 && nb <= nb2; return false;
                default: return false;
            }
        }
        #endregion

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (propertyName == nameof(IsSearchEnabled))
            {
                Application.Current.Dispatcher.Invoke(() => ((AsyncRelayCommand)StartSearchCommand).RaiseCanExecuteChanged());
            }
        }
        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value)) return false;
            storage = value;
            RaisePropertyChanged(propertyName);
            return true;
        }
        #endregion
    }

}
