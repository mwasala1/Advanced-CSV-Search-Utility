using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AdvancedCsvSearch
{
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
}
