using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AdvancedCsvSearch
{
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
}
