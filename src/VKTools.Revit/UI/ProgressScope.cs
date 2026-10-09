using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using VKTools.Revit.Infrastructure;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// Окно прогресса с кнопкой «Отменить» на время долгой операции в контексте Revit API.
    /// Revit однопоточный: операция идёт в главном потоке, поэтому окно перерисовывается и принимает
    /// нажатие «Отменить» только когда операция отдаёт управление — в <see cref="Report"/> и в событии
    /// Revit ProgressChanged (долгие внутренние операции Revit). На это время главное окно Revit
    /// отключено, чтобы в нём нельзя было ничего нажать (docs/ARCHITECTURE.md, D-016).
    /// </summary>
    internal sealed class ProgressScope : IOperationProgress, IDisposable
    {
        private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(150);

        private readonly UIApplication _app;
        private readonly IntPtr _revitWindow;
        private readonly Window? _ownerWindow;
        private readonly Window _window;
        private readonly TextBlock _stage;
        private readonly TextBlock _revitStage;
        private readonly TextBlock _elapsed;
        private readonly ProgressBar _bar;
        private readonly Button _cancel;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private TimeSpan _lastPump = TimeSpan.MinValue;
        private bool _revitDisabled;
        private bool _disposed;

        public ProgressScope(UIApplication app, string title, Window? ownerWindow = null)
        {
            _app = app;
            _revitWindow = app.MainWindowHandle;
            _ownerWindow = ownerWindow;

            _window = new Window
            {
                Title = "VK Tools — " + title,
                Width = 560,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
            };
            _window.ApplyDefaultLook();
            var panel = new StackPanel { Margin = new Thickness(14) };
            _stage = new TextBlock { Text = "Начинаю…", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, MinHeight = 34 };
            _bar = new ProgressBar { Height = 16, Margin = new Thickness(0, 8, 0, 6), IsIndeterminate = true, Minimum = 0, Maximum = 1 };
            _revitStage = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
            _elapsed = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };
            _cancel = new Button { Content = "Отменить", Padding = new Thickness(14, 3, 14, 3), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            _cancel.Click += (s, e) => RequestCancel();
            panel.Children.Add(_stage);
            panel.Children.Add(_bar);
            panel.Children.Add(_revitStage);
            panel.Children.Add(_elapsed);
            panel.Children.Add(_cancel);
            _window.Content = panel;
            _window.Closing += (s, e) =>
            {
                // Крестик = «Отменить»: окно закроется само, когда операция остановится
                if (!_disposed)
                {
                    e.Cancel = true;
                    RequestCancel();
                }
            };

            try
            {
                if (_ownerWindow != null) _ownerWindow.IsEnabled = false;
                if (_revitWindow != IntPtr.Zero)
                {
                    new WindowInteropHelper(_window).Owner = _revitWindow;
                    NativeMethods.EnableWindow(_revitWindow, false);
                    _revitDisabled = true;
                }
                _window.Show();
                _app.Application.ProgressChanged += OnRevitProgress;
                Pump(force: true);
            }
            catch
            {
                // Окно прогресса не открылось — Revit не должен остаться отключённым
                Dispose();
                throw;
            }
        }

        public bool IsCancellationRequested { get; private set; }

        public void Report(string stage, int done = 0, int total = 0)
        {
            if (_disposed) return;
            _stage.Text = stage;
            if (total > 0)
            {
                _bar.IsIndeterminate = false;
                _bar.Maximum = total;
                _bar.Value = Math.Min(done, total);
            }
            else
            {
                _bar.IsIndeterminate = true;
            }
            _revitStage.Text = string.Empty;
            Pump(force: false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _app.Application.ProgressChanged -= OnRevitProgress;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Revit закрывается — отписываться уже не от чего
            }
            if (_revitDisabled) NativeMethods.EnableWindow(_revitWindow, true);
            if (_ownerWindow != null) _ownerWindow.IsEnabled = true;
            _window.Close();
        }

        private void RequestCancel()
        {
            if (IsCancellationRequested) return;
            IsCancellationRequested = true;
            _cancel.IsEnabled = false;
            _cancel.Content = "Отменяю…";
            _stage.Text = "Отмена: останавливаюсь на ближайшем шаге и откатываю изменения…";
            Host.Log.Info("Пользователь нажал «Отменить».");
        }

        /// <summary>Долгая внутренняя операция Revit (пересчёт, фиксация): показываем её и даём отменить, если Revit позволяет.</summary>
        private void OnRevitProgress(object? sender, ProgressChangedEventArgs e)
        {
            try
            {
                if (IsCancellationRequested && e.Cancellable && (e.Stage == ProgressStage.PositionChanged || e.Stage == ProgressStage.Unchanged))
                {
                    e.Cancel();
                    return;
                }
                if (e.Stage == ProgressStage.Finished)
                {
                    _revitStage.Text = string.Empty;
                }
                else if (!string.IsNullOrEmpty(e.Caption))
                {
                    _revitStage.Text = e.UpperRange > 0 ? $"Revit: {e.Caption} ({e.Position} из {e.UpperRange})" : "Revit: " + e.Caption;
                }
                Pump(force: false);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException)
            {
                // Прогресс — не повод ронять операцию
            }
        }

        /// <summary>Дать окну перерисоваться и принять нажатие «Отменить». Не чаще раза в 150 мс.</summary>
        private void Pump(bool force)
        {
            TimeSpan now = _watch.Elapsed;
            if (!force && now - _lastPump < PumpInterval) return;
            _lastPump = now;
            _elapsed.Text = "Прошло: " + OperationTimings.Seconds(now);
            try
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new DispatcherOperationCallback(f =>
                    {
                        ((DispatcherFrame)f).Continue = false;
                        return null;
                    }), frame);
                Dispatcher.PushFrame(frame);
            }
            catch (InvalidOperationException)
            {
                // Диспетчер сейчас нельзя прокачать (идёт разметка окна) — перерисуем на следующем шаге
            }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);
    }
}
