using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
        private List<string[]> _resultsToExport = new List<string[]>();
        private double _searchProgress;
        private bool _isExportEnabled;
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

        #region Event Handlers
        private void SearchCriteria_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (SearchCriterion item in e.NewItems) item.PropertyChanged += Criterion_PropertyChanged;
            if (e.OldItems != null) foreach (SearchCriterion item in e.OldItems) item.PropertyChanged -= Criterion_PropertyChanged;
            RaisePropertyChanged(nameof(IsSearchEnabled));
        }

        private void Criterion_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SearchCriterion.ColumnName) || e.PropertyName == nameof(SearchCriterion.Value))
            {
                RaisePropertyChanged(nameof(IsSearchEnabled));
            }
        }
        #endregion

        #region Command Implementations
        private void ShowAbout()
        {
            var aboutWindow = new AboutWindow
            {
                Owner = Application.Current.MainWindow
            };
            aboutWindow.ShowDialog();
        }

        private async Task SelectFolderAsync()
        {
            var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                TargetFolder = dialog.FileName;
                StatusText = $"Folder selected. Discovering columns...";
                await DiscoverColumnsAsync();
                IsExportEnabled = false;
            }
        }

        private async Task DiscoverColumnsAsync()
        {
            StatusText = "Scanning for column headers...";
            DiscoveredColumns.Clear();
            try
            {
                var searchOption = IsRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var firstZip = Directory.EnumerateFiles(TargetFolder, "*.zip", searchOption).FirstOrDefault();
                if (firstZip == null) { StatusText = "No ZIP files found."; return; }

                using var archive = ZipFile.OpenRead(firstZip);
                var firstCsv = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
                if (firstCsv == null) { StatusText = "No CSV files found in first ZIP."; return; }

                using var stream = firstCsv.Open();
                using var reader = new StreamReader(stream);
                var headerLine = await reader.ReadLineAsync();

                if (headerLine != null)
                {
                    headerLine.Split(',').ToList().ForEach(h => DiscoveredColumns.Add(h.Trim()));
                    StatusText = $"Discovered {DiscoveredColumns.Count} columns. Ready.";
                }
                else { StatusText = "First CSV file appears to be empty."; }
            }
            catch (Exception ex) { StatusText = $"Error discovering columns: {ex.Message}"; }
        }

        private void SaveQuery()
        {
            var sfd = new SaveFileDialog { Filter = "JSON Query File (*.json)|*.json", FileName = "MyQuery.json", Title = "Save Search Query" };
            if (sfd.ShowDialog() == true)
            {
                try
                {
                    var json = JsonSerializer.Serialize(SearchCriteria, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(sfd.FileName, json);
                    StatusText = $"Query saved to {Path.GetFileName(sfd.FileName)}";
                }
                catch (Exception ex) { MessageBox.Show($"Failed to save query: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }

        private void LoadQuery()
        {
            var ofd = new OpenFileDialog { Filter = "JSON Query File (*.json)|*.json", Title = "Load Search Query" };
            if (ofd.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(ofd.FileName);
                    var loadedCriteria = JsonSerializer.Deserialize<ObservableCollection<SearchCriterion>>(json);
                    if (loadedCriteria != null)
                    {
                        foreach (var item in SearchCriteria) item.PropertyChanged -= Criterion_PropertyChanged;
                        SearchCriteria = loadedCriteria;
                        SearchCriteria.CollectionChanged += SearchCriteria_CollectionChanged;
                        foreach (var item in SearchCriteria) item.PropertyChanged += Criterion_PropertyChanged;
                        RaisePropertyChanged(nameof(SearchCriteria));
                        StatusText = $"Query loaded from {Path.GetFileName(ofd.FileName)}";
                    }
                }
                catch (Exception ex) { MessageBox.Show($"Failed to load query: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }

        private void AddCriterion()
        {
            var newCriterion = new SearchCriterion();
            if (SearchCriteria.Any()) newCriterion.IsNotFirst = true;
            SearchCriteria.Add(newCriterion);
        }

        private void RemoveCriterion(SearchCriterion? criterion)
        {
            if (criterion != null)
            {
                SearchCriteria.Remove(criterion);
                if (SearchCriteria.Any())
                {
                    var firstCriterion = SearchCriteria.First();
                    firstCriterion.IsNotFirst = false;
                    if (firstCriterion.IndentLevel > 0) firstCriterion.IndentLevel = 0;
                }
            }
        }

        private void IndentCriterion(SearchCriterion? criterion)
        {
            if (criterion != null)
            {
                criterion.IndentLevel++;
                RaisePropertyChanged(nameof(IndentCommand));
                RaisePropertyChanged(nameof(OutdentCommand));
            }
        }

        private bool CanIndentCriterion(SearchCriterion? criterion)
        {
            if (criterion == null) return false;
            int index = SearchCriteria.IndexOf(criterion);
            if (index <= 0) return false;
            var previousCriterion = SearchCriteria[index - 1];
            return criterion.IndentLevel <= previousCriterion.IndentLevel;
        }

        private void OutdentCriterion(SearchCriterion? criterion)
        {
            if (criterion != null)
            {
                criterion.IndentLevel--;
                RaisePropertyChanged(nameof(IndentCommand));
                RaisePropertyChanged(nameof(OutdentCommand));
            }
        }

        private bool CanOutdentCriterion(SearchCriterion? criterion) => criterion != null && criterion.IndentLevel > 0;

        private async Task SearchAsync()
        {
            IsSearching = true;
            IsExportEnabled = false;
            _resultsToExport.Clear();
            SearchProgress = 0;
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            StatusText = "Starting search...";
            IProgress<string> statusProgress = new Progress<string>(update => StatusText = update);
            IProgress<double> barProgress = new Progress<double>(update => SearchProgress = update);

            try
            {
                var searchFolder = TargetFolder;
                var criteria = new List<SearchCriterion>(SearchCriteria.Where(c => !string.IsNullOrWhiteSpace(c.ColumnName) && !string.IsNullOrWhiteSpace(c.Value)));
                var searchOption = IsRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                await Task.Run(async () =>
                {
                    var zipFiles = Directory.GetFiles(searchFolder, "*.zip", searchOption);
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
                                await ProcessCsvEntry(entry, Path.GetFileName(zipPath), criteria, token);
                            }
                        }
                        catch (Exception ex) { statusProgress.Report($"Error reading {Path.GetFileName(zipPath)}: {ex.Message}"); }
                        filesProcessed++;
                        if (zipFiles.Length > 0) barProgress.Report((double)filesProcessed / zipFiles.Length * 100);
                    }
                }, token);

                token.ThrowIfCancellationRequested();
                StatusText = $"Search complete. Found {_resultsToExport.Count} matching rows.";
            }
            catch (OperationCanceledException) { StatusText = "Search was canceled."; }
            catch (Exception ex) { StatusText = $"An error occurred: {ex.Message}"; }
            finally
            {
                IsSearching = false;
                if (_cancellationTokenSource != null) _cancellationTokenSource.Dispose();
                IsExportEnabled = _resultsToExport.Any();
            }
        }
        
        private async Task ProcessCsvEntry(ZipArchiveEntry entry, string zipName, List<SearchCriterion> criteria, CancellationToken token)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var headerLine = await reader.ReadLineAsync();
            if (headerLine == null) return;
            var headers = headerLine.Split(',');
            var colIndexMap = new Dictionary<string, int>();
            foreach(var criterion in criteria)
            {
                if (string.IsNullOrEmpty(criterion.ColumnName)) continue;
                var index = Array.FindIndex(headers, h => h.Trim().Equals(criterion.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (index != -1) colIndexMap[criterion.ColumnName] = index;
            }
            if (!colIndexMap.Any()) return;
            bool headerAdded = false;
            while (!reader.EndOfStream)
            {
                if (token.IsCancellationRequested) break;
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var values = line.Split(',');
                if (DoesRowMatch(values, criteria, colIndexMap))
                {
                    if (!headerAdded)
                    {
                        _resultsToExport.Add(new[] { "Source ZIP Archive", "Source CSV File" }.Concat(headers).ToArray());
                        headerAdded = true;
                    }
                    _resultsToExport.Add(new[] { zipName, entry.FullName }.Concat(values).ToArray());
                }
            }
        }

        private void CancelSearch() => _cancellationTokenSource?.Cancel();

        private void ExportResults()
        {
            var sfd = new SaveFileDialog { Filter = "CSV File (*.csv)|*.csv", FileName = $"SearchResults_{DateTime.Now:yyyyMMdd_HHmmss}.csv", Title = "Export Search Results" };
            if (sfd.ShowDialog() == true)
            {
                try
                {
                    SaveResultsToCsv(sfd.FileName);
                    StatusText = $"Results successfully exported to {Path.GetFileName(sfd.FileName)}.";
                }
                catch (Exception ex) { StatusText = $"Export failed: {ex.Message}"; MessageBox.Show($"Could not export the file. Reason: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }
        
        private void SaveResultsToCsv(string filePath)
        {
            var sb = new StringBuilder();
            foreach (var rowArray in _resultsToExport)
            {
                var line = string.Join(",", rowArray.Select(field =>
                {
                    var sanitizedField = field.Replace("\"", "\"\"");
                    if (sanitizedField.Contains(',') || sanitizedField.Contains('\"') || sanitizedField.Contains('\n'))
                    {
                        return $"\"{sanitizedField}\"";
                    }
                    return sanitizedField;
                }));
                sb.AppendLine(line);
            }
            File.WriteAllText(filePath, sb.ToString());
        }
        #endregion

        #region Search Evaluation Logic
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
            catch (Exception) { return false; }
        }

        private bool ParseExpression(int level)
        {
            bool left = ParseTerm(level);
            while (_evalIndex < _criteriaForEval.Count)
            {
                var currentCriterion = _criteriaForEval[_evalIndex];
                if (currentCriterion.IndentLevel < level) break;
                if (currentCriterion.IndentLevel > level) throw new InvalidOperationException("Malformed expression.");
                var op = currentCriterion.LogicalOperator;
                _evalIndex++;
                bool right = ParseTerm(level);
                if (op == LogicalOperator.AND) left = left && right; else left = left || right;
            }
            return left;
        }

        private bool ParseTerm(int level)
        {
            if (_evalIndex >= _criteriaForEval.Count) throw new InvalidOperationException("Unexpected end of expression.");
            var currentCriterion = _criteriaForEval[_evalIndex];
            if (currentCriterion.IndentLevel > level) return ParseExpression(currentCriterion.IndentLevel);
            _evalIndex++;
            return EvaluateSingleCriterion(currentCriterion);
        }

        private bool EvaluateSingleCriterion(SearchCriterion criterion)
        {
            if (string.IsNullOrEmpty(criterion.ColumnName) || !_colIndexMapForEval.TryGetValue(criterion.ColumnName, out var colIndex) || colIndex >= _valuesForEval.Length)
            {
                return false;
            }
            bool result = CheckCriterion(_valuesForEval[colIndex].Trim(), criterion);
            return criterion.IsNot ? !result : result;
        }

        private bool CheckCriterion(string cellValue, SearchCriterion criterion)
        {
            string val1 = criterion.Value!;
            string? val2 = criterion.Value2;
            switch (criterion.SearchType)
            {
                case SearchType.ExactMatch: return cellValue.Equals(val1, StringComparison.OrdinalIgnoreCase);
                case SearchType.IsOneOf: return val1.Split(',').Any(v => cellValue.Equals(v.Trim(), StringComparison.OrdinalIgnoreCase));
                case SearchType.Regex: try { return Regex.IsMatch(cellValue, val1, RegexOptions.IgnoreCase); } catch { return false; }
                case SearchType.GreaterThan: if (double.TryParse(cellValue, out double cellNumGt) && double.TryParse(val1, out double compareNumGt)) return cellNumGt > compareNumGt; return false;
                case SearchType.LessThan: if (double.TryParse(cellValue, out double cellNumLt) && double.TryParse(val1, out double compareNumLt)) return cellNumLt < compareNumLt; return false;
                case SearchType.IsBetween: if (double.TryParse(cellValue, out double cellNumB) && double.TryParse(val1, out double compareNumB1) && double.TryParse(val2, out double compareNumB2)) return cellNumB >= compareNumB1 && cellNumB <= compareNumB2; return false;
                case SearchType.OnDate: if (DateTime.TryParse(cellValue, out DateTime cellDateO) && DateTime.TryParse(val1, out DateTime compareDateO)) return cellDateO.Date == compareDateO.Date; return false;
                case SearchType.BeforeDate: if (DateTime.TryParse(cellValue, out DateTime cellDateB) && DateTime.TryParse(val1, out DateTime compareDateB)) return cellDateB.Date < compareDateB.Date; return false;
                case SearchType.AfterDate: if (DateTime.TryParse(cellValue, out DateTime cellDateA) && DateTime.TryParse(val1, out DateTime compareDateA)) return cellDateA.Date > compareDateA.Date; return false;
                case SearchType.IsBetweenDates: if (DateTime.TryParse(cellValue, out DateTime cellDateBd) && DateTime.TryParse(val1, out DateTime compareDateBd1) && DateTime.TryParse(val2, out DateTime compareDateBd2)) return cellDateBd.Date >= compareDateBd1.Date && cellDateBd.Date <= compareDateBd2.Date; return false;
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

    #region Data Models
    public class SearchCriterion : INotifyPropertyChanged
    {
        private string? _columnName;
        private SearchType _searchType;
        private string? _value;
        private string? _value2;
        private bool _isNot;
        private LogicalOperator _logicalOperator;
        private bool _isNotFirst;
        private int _indentLevel;

        public string? ColumnName { get => _columnName; set => SetProperty(ref _columnName, value); }
        public SearchType SearchType { get => _searchType; set => SetProperty(ref _searchType, value); }
        public string? Value { get => _value; set => SetProperty(ref _value, value); }
        public string? Value2 { get => _value2; set => SetProperty(ref _value2, value); }
        public bool IsNot { get => _isNot; set => SetProperty(ref _isNot, value); }
        public LogicalOperator LogicalOperator { get => _logicalOperator; set => SetProperty(ref _logicalOperator, value); }
        public bool IsNotFirst { get => _isNotFirst; set => SetProperty(ref _isNotFirst, value); }
        public int IndentLevel { get => _indentLevel; set => SetProperty(ref _indentLevel, value); }

        [JsonIgnore]
        public List<LogicalOperator> AvailableOperators => new List<LogicalOperator> { LogicalOperator.AND, LogicalOperator.OR };
        
        public SearchCriterion() => LogicalOperator = LogicalOperator.AND;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (!Equals(storage, value))
            {
                storage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public enum SearchType 
    { 
        [Description("Exact Match")] ExactMatch, 
        [Description("Is One Of (List)")] IsOneOf, 
        [Description("Regex")] Regex,
        [Description("Greater Than (>)")] GreaterThan,
        [Description("Less Than (<)")] LessThan,
        [Description("Is Between")] IsBetween,
        [Description("On Date")] OnDate,
        [Description("Before Date")] BeforeDate,
        [Description("After Date")] AfterDate,
        [Description("Is Between Dates")] IsBetweenDates
    }
    public enum LogicalOperator { AND, OR }
    #endregion

    #region Command Helpers
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        public event EventHandler? CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute ?? (() => true);
        }
        public bool CanExecute(object? parameter) => _canExecute();
        public void Execute(object? parameter) => _execute();
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Predicate<T> _canExecute;
        public event EventHandler? CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
        public RelayCommand(Action<T> execute, Predicate<T>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute ?? (p => true);
        }
        public bool CanExecute(object? parameter) => _canExecute((T)parameter!);
        public void Execute(object? parameter) => _execute((T)parameter!);
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private bool _isExecuting;
        public event EventHandler? CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute ?? (() => true);
        }
        public bool CanExecute(object? parameter) => !_isExecuting && _canExecute();
        public async void Execute(object? parameter)
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            try { await _execute(); }
            finally { _isExecuting = false; RaiseCanExecuteChanged(); }
        }
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
    #endregion
}
